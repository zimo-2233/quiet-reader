using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuietReader
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietReader");
            string initialFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--data-dir" && i + 1 < args.Length) root = Path.GetFullPath(args[++i]);
                else if (args[i].EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) initialFile = args[i];
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\QuietReader_" + TextDecoder.Hash(root.ToLowerInvariant()), out created))
            {
                if (!created) { MessageBox.Show("闲页已在运行，请在任务栏中打开现有窗口。", "闲页"); return; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var reader = new ReaderWindow(new LibraryStore(root), initialFile);
                    app.Run(reader.Window);
                    GC.KeepAlive(reader);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法启动闲页：\n" + ex.Message, "闲页", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
    public class ReaderWindow
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        public readonly Window Window;
        readonly LibraryStore store;
        readonly TextBox reader;
        readonly ListBox books;
        readonly ListBox chapters;
        readonly Slider progress;
        readonly DispatcherTimer saveTimer;
        Book current;
        string document = "";
        TextWindow textWindow;
        HwndSource source;
        bool restoring, listUpdating, progressUpdating, progressDragging, closing, ready, busy, dirty;
        int restoreGeneration;
        double normalWidth = 960, normalHeight = 680;
        DateTime lastSave = DateTime.MinValue;
        string statusNote = "";
        string bossShortcut = "Ctrl+Alt+Q";
        T Get<T>(string name) where T : class { return Window.FindName(name) as T; }
        void Click(string name, Action action) { Get<Button>(name).Click += delegate { action(); }; }
        static double Clamp(double value, double min, double max) { return Double.IsNaN(value) || Double.IsInfinity(value) ? min : Math.Max(min, Math.Min(max, value)); }

        public ReaderWindow(LibraryStore store, string initialFile)
        {
            this.store = store;
            using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml"))
                Window = (Window)XamlReader.Load(xaml);
            WindowChrome.SetWindowChrome(Window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), CornerRadius = new CornerRadius(0), GlassFrameThickness = new Thickness(0) });
            try
            {
                using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("App.ico"))
                    if (icon != null) Window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(icon);
            }
            catch { }
            reader = Get<TextBox>("Reader"); books = Get<ListBox>("BookList"); chapters = Get<ListBox>("ChapterList"); progress = Get<Slider>("ProgressSlider");
            var p = store.State.Preferences;
            Window.Width = Clamp(p.Width, 380, SystemParameters.VirtualScreenWidth);
            Window.Height = Clamp(p.Height, 220, SystemParameters.VirtualScreenHeight);
            normalWidth = p.Compact ? 960 : Window.Width; normalHeight = p.Compact ? 680 : Window.Height;
            // Only restore bounds if they still intersect a currently connected screen.
            bool visible = Forms.Screen.AllScreens.Any(s =>
            {
                return p.Left < s.WorkingArea.Right && p.Left + Window.Width > s.WorkingArea.Left + 80 && p.Top < s.WorkingArea.Bottom - 48 && p.Top + 48 > s.WorkingArea.Top;
            });
            if (visible && p.Left > -90000)
            {
                Window.Left = p.Left; Window.Top = p.Top;
                if (Window.Top < SystemParameters.VirtualScreenTop) Window.Top = SystemParameters.VirtualScreenTop;
            }
            else Window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Get<Slider>("FontSlider").Value = Clamp(p.FontSize, 12, 36);
            Get<Slider>("OpacitySlider").Value = Clamp(p.Opacity, .3, 1);
            ApplyAppearance(); ApplyCompact(false); UpdatePin();
            Click("ImportButton", ChooseFile); Click("WelcomeImport", ChooseFile);
            Click("LibraryToggle", delegate { ToggleSidebar(); });
            Click("BooksTab", delegate { ShowTab(false); }); Click("ChaptersTab", delegate { ShowTab(true); });
            Click("PinButton", delegate { p.Topmost = !p.Topmost; UpdatePin(); ScheduleSave(); });
            Click("SettingsButton", delegate { if (Get<Popup>("SettingsPopup").IsOpen) Get<Popup>("SettingsPopup").IsOpen = false; else OpenSettings(); });
            Click("BossButton", BossExit); Click("MinimizeButton", delegate { Window.WindowState = WindowState.Minimized; });
            Click("PreviousButton", delegate { Page(false); }); Click("NextButton", delegate { Page(true); });
            Click("PaperTheme", delegate { Theme("#F7F5EF", "#333B36"); });
            Click("GreenTheme", delegate { Theme("#DDE7D6", "#354431"); });
            Click("DarkTheme", delegate { Theme("#1E1E1E", "#D4D4D4"); });
            Click("GrayTheme", delegate { Theme("#E7E9EB", "#37434B"); });
            Click("CustomBackground", delegate { ChooseColor(true); }); Click("CustomForeground", delegate { ChooseColor(false); });
            Click("CompactButton", delegate { ToggleCompact(); });
            Get<Slider>("FontSlider").ValueChanged += delegate { Reflow(delegate { p.FontSize = Get<Slider>("FontSlider").Value; ApplyAppearance(); }); };
            Get<Slider>("OpacitySlider").ValueChanged += delegate
            {
                p.Opacity = Get<Slider>("OpacitySlider").Value; Window.Opacity = p.Opacity;
                Get<TextBlock>("OpacityLabel").Text = Window.Opacity.ToString("P0"); ScheduleSave();
            };
            books.SelectionChanged += async delegate
            {
                if (!listUpdating && books.SelectedItem is Book) await OpenBook((Book)books.SelectedItem, null);
            };
            chapters.SelectionChanged += delegate
            {
                if (!listUpdating && chapters.SelectedItem is Chapter) { Jump(((Chapter)chapters.SelectedItem).Position); reader.Focus(); }
            };
            reader.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(delegate
            {
                if (!restoring && ready) { CapturePosition(); UpdateProgress(); ScheduleSave(); RecenterIfNeeded(); }
            }));
            progress.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate { progressDragging = true; }));
            progress.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate { progressDragging = false; Seek(); }));
            progress.ValueChanged += delegate { if (!progressUpdating && !progressDragging) Seek(); };
            Window.PreviewKeyDown += HandleKeys;
            Get<StackPanel>("SettingsRoot").PreviewKeyDown += HandleKeys;
            reader.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                { Get<Slider>("FontSlider").Value += e.Delta > 0 ? 1 : -1; e.Handled = true; }
            };
            Get<Border>("CompactDragArea").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (p.Compact && e.ButtonState == MouseButtonState.Pressed)
                {
                    e.Handled = true;
                    reader.Focus();
                    Window.DragMove();
                }
            };
            BuildReadingMenu();
            Get<Border>("TitleBar").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2) Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else Window.DragMove();
            };
            Window.PreviewDragOver += delegate(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            Window.Drop += async delegate(object sender, DragEventArgs e)
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0) await Import(paths[0]);
            };
            Window.SizeChanged += delegate { if (ready) { if (!restoring && current != null) Restore(current.Position); UpdateResponsive(); ScheduleSave(); } };
            Get<Thumb>("ResizeCorner").DragDelta += delegate(object sender, DragDeltaEventArgs e)
            {
                if (Window.WindowState != WindowState.Normal) return;
                Window.Width = Math.Max(Window.MinWidth, Window.ActualWidth + e.HorizontalChange);
                Window.Height = Math.Max(Window.MinHeight, Window.ActualHeight + e.VerticalChange);
            };
            Window.LocationChanged += delegate { if (ready) ScheduleSave(); };
            Window.SourceInitialized += delegate
            {
                source = (HwndSource)PresentationSource.FromVisual(Window); source.AddHook(WndProc);
                UpdateNativeFrame();
                bool registered = RegisterHotKey(source.Handle, 0x519, 0x4000 | 0x0002 | 0x0001, 0x51);
                if (!registered)
                {
                    registered = RegisterHotKey(source.Handle, 0x519, 0x4000 | 0x0002 | 0x0001, 0x7B);
                    if (registered) bossShortcut = "Ctrl+Alt+F12";
                }
                if (!registered)
                {
                    registered = RegisterHotKey(source.Handle, 0x519, 0x4000 | 0x0002 | 0x0004, 0x7B);
                    if (registered) bossShortcut = "Ctrl+Shift+F12";
                }
                if (!registered)
                {
                    Get<TextBlock>("BossHint").Text = "全局快捷键被占用 · Esc 退出";
                    Get<TextBlock>("HotkeyStatus").Text = "全局快捷键注册失败（错误 " + Marshal.GetLastWin32Error() + "）。请用退出按钮或窗口内 Esc。";
                }
                else
                {
                    Get<TextBlock>("BossHint").Text = "老板键  " + bossShortcut;
                    Get<TextBlock>("HotkeyStatus").Text = bossShortcut + " 全局退出；Esc 窗口内退出。";
                    Get<Button>("BossButton").ToolTip = "老板键：立即退出并保存 · " + bossShortcut + "（全局） / Esc";
                }
            };
            Window.Closing += delegate
            {
                closing = true; saveTimer.Stop();
                CapturePosition(); SaveNow();
                if (source != null) { UnregisterHotKey(source.Handle, 0x519); source.RemoveHook(WndProc); }
            };
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += delegate
            {
                if (!dirty || restoring) return;
                CapturePosition();
                if (DateTime.UtcNow - lastSave > TimeSpan.FromMilliseconds(900)) SaveNow();
            };
            Window.Loaded += async delegate
            {
                ready = true; UpdateResponsive(); RefreshBooks();
                if (!String.IsNullOrEmpty(initialFile)) await Import(initialFile);
                else
                {
                    var last = store.State.Books.Find(b => b.Id == store.State.CurrentId);
                    if (last != null) await OpenBook(last, null);
                }
                if (!String.IsNullOrEmpty(store.RecoveryNotice)) Get<TextBlock>("StatusText").Text = store.RecoveryNotice;
            };
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == 0x519) { handled = true; BossExit(); }
            return IntPtr.Zero;
        }
        void BossExit()
        {
            if (closing) return;
            // Remove all reading UI immediately; persistence runs after the window disappears.
            Get<Popup>("SettingsPopup").IsOpen = false;
            if (reader.ContextMenu != null) reader.ContextMenu.IsOpen = false;
            Window.Hide(); Window.Close();
        }
        void HandleKeys(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.Escape) { e.Handled = true; BossExit(); return; }
            if (mods == ModifierKeys.Control)
            {
                if (e.Key == Key.O) { e.Handled = true; ChooseFile(); }
                if (e.Key == Key.T) { e.Handled = true; store.State.Preferences.Topmost = !Window.Topmost; UpdatePin(); ScheduleSave(); }
                if (e.Key == Key.M) { e.Handled = true; ToggleCompact(); }
                if (e.Key == Key.Home) { e.Handled = true; Jump(0); }
                if (e.Key == Key.End) { e.Handled = true; Jump(document.Length); }
            }
            if (mods == ModifierKeys.None && !Get<Popup>("SettingsPopup").IsOpen)
            {
                if (e.Key == Key.PageDown || e.Key == Key.Right || e.Key == Key.Space) { e.Handled = true; Page(true); }
                if (e.Key == Key.PageUp || e.Key == Key.Left) { e.Handled = true; Page(false); }
                if (e.Key == Key.Down) { e.Handled = true; reader.LineDown(); }
                if (e.Key == Key.Up) { e.Handled = true; reader.LineUp(); }
            }
        }
        async void ChooseFile()
        {
            if (busy) return;
            Get<Popup>("SettingsPopup").IsOpen = false;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 TXT 小说", Filter = "TXT 文本文件 (*.txt)|*.txt", CheckFileExists = true };
            if (dialog.ShowDialog(Window) == true) await Import(dialog.FileName);
        }
        async Task Import(string path)
        {
            if (busy || closing) return;
            SetBusy(true); Get<TextBlock>("StatusText").Text = "正在导入，请稍候…";
            try
            {
                var imported = await Task.Run(delegate { return store.PrepareImport(path); });
                if (closing) return;
                var book = store.Add(imported);
                await LoadBook(book, imported.Text);
            }
            catch (Exception ex) { if (!closing) Notice("导入失败", ex.Message); }
            finally { if (!closing) SetBusy(false); }
        }
        async Task OpenBook(Book book, string text)
        {
            if (busy || closing || book == current) return;
            SetBusy(true);
            try
            {
                var content = text ?? await Task.Run(delegate { return store.ReadBook(book); });
                if (!closing) await LoadBook(book, content);
            }
            catch (Exception ex) { if (!closing) { Notice("无法打开书籍", ex.Message); RefreshBooks(); } }
            finally { if (!closing) SetBusy(false); }
        }
        async Task LoadBook(Book book, string text)
        {
            var entries = await Task.Run(delegate { return TextDecoder.Chapters(text); });
            if (closing) return;
            CapturePosition();
            current = book; current.Length = text.Length; store.State.CurrentId = book.Id;
            current.LastRead = DateTime.UtcNow.ToString("o");
            int position = Math.Max(0, Math.Min(book.Position, text.Length));
            document = text; textWindow = null;
            restoring = true;
            reader.Visibility = Visibility.Visible;
            Get<StackPanel>("Welcome").Visibility = Visibility.Collapsed;
            Get<Grid>("ReadingFooter").Visibility = store.State.Preferences.Compact ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("BookTitle").Text = book.Title;
            Get<TextBlock>("BookCaption").Text = "本地 TXT  /  " + book.Encoding + "  /  " + text.Length.ToString("N0") + " 字符";
            Window.Title = "闲页 · " + book.Title;
            listUpdating = true; chapters.ItemsSource = entries; chapters.SelectedIndex = -1; listUpdating = false;
            Get<TextBlock>("ShelfHint").Text = entries.Count > 0 ? entries.Count + " 个章节 · 自动保存进度" : "未识别到章节 · 可直接翻页阅读";
            RefreshBooks();
            statusNote = "已自动保存";
            Restore(position);
            reader.Focus(); ScheduleSave();
        }
        void SetBusy(bool value)
        {
            busy = value;
            Get<Button>("ImportButton").IsEnabled = !value; Get<Button>("WelcomeImport").IsEnabled = !value;
            books.IsEnabled = !value;
        }
        void Notice(string title, string message)
        {
            Get<TextBlock>("StatusText").Text = title + "：" + message;
            MessageBox.Show(Window, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        void RefreshBooks()
        {
            listUpdating = true;
            books.ItemsSource = store.State.Books.OrderByDescending(b => b.LastRead).ToList();
            books.SelectedItem = current;
            listUpdating = false;
        }
        void CapturePosition()
        {
            if (current == null || restoring || reader.Text.Length == 0) return;
            int line = reader.GetFirstVisibleLineIndex();
            if (line < 0) return;
            int anchor = reader.GetCharacterIndexFromLineIndex(line);
            if (anchor >= 0) current.Position = Math.Min((textWindow == null ? 0 : textWindow.Start) + anchor, document.Length);
            current.Progress = textWindow != null && textWindow.End == document.Length &&
                (reader.ExtentHeight <= reader.ViewportHeight + 1 || reader.VerticalOffset >= reader.ExtentHeight - reader.ViewportHeight - 1)
                ? 1 : (double)current.Position / Math.Max(1, current.Length);
        }
        void Restore(int position)
        {
            restoring = true; int generation = ++restoreGeneration;
            position = Math.Max(0, Math.Min(position, document.Length));
            if (textWindow == null || position < textWindow.Start || position > textWindow.End)
                SetTextWindow(position);
            Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate
            {
                if (closing || generation != restoreGeneration) return;
                try
                {
                    reader.UpdateLayout();
                    int index = Math.Max(0, Math.Min(position - textWindow.Start, reader.Text.Length));
                    reader.Select(index, 0);
                    reader.UpdateLayout();
                    int line = reader.GetLineIndexFromCharacterIndex(index);
                    // ScrollToLine only makes a line visible, which can restore it at the
                    // bottom. A plain-text box uses constant-height visual lines in Ideal mode.
                    if (line >= 0) reader.ScrollToVerticalOffset(line * reader.FontFamily.LineSpacing * reader.FontSize);
                }
                finally
                {
                    Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate
                    {
                        if (generation != restoreGeneration || closing) return;
                        restoring = false; CapturePosition(); UpdateProgress(); ScheduleSave();
                    }));
                }
            }));
        }
        void SetTextWindow(int anchor)
        {
            textWindow = new TextWindow(document, anchor);
            reader.Text = textWindow.Text;
        }
        void RecenterIfNeeded()
        {
            if (current == null || textWindow == null || restoring) return;
            int anchor = current.Position;
            if ((textWindow.Start > 0 && anchor - textWindow.Start < 1024) ||
                (textWindow.End < document.Length && textWindow.End - anchor < 4096))
            {
                restoring = true; SetTextWindow(anchor); Restore(anchor);
            }
        }
        void Jump(int position)
        {
            if (current == null) return;
            restoring = true;
            if (textWindow == null || position < textWindow.Start + 1024 || position > textWindow.End - 4096)
                SetTextWindow(position);
            Restore(position);
        }
        void Seek() { if (current != null && !restoring) Jump((int)Math.Round(progress.Value / 100 * document.Length)); }
        void Page(bool down) { if (current == null) return; if (down) reader.PageDown(); else reader.PageUp(); }
        void UpdateProgress()
        {
            if (current == null) return;
            progressUpdating = true;
            if (!progressDragging) progress.Value = current.Progress * 100;
            progressUpdating = false;
            Get<TextBlock>("StatusText").Text = String.Format("已读 {0:P1}  ·  {1} / {2} 字符  ·  {3}", current.Progress, current.Position.ToString("N0"), current.Length.ToString("N0"), statusNote);
        }
        void ScheduleSave() { if (closing || !ready) return; dirty = true; saveTimer.Start(); }
        void SaveNow()
        {
            var p = store.State.Preferences;
            if (Window.WindowState == WindowState.Normal)
            { p.Width = Window.Width; p.Height = Window.Height; p.Left = Window.Left; p.Top = Window.Top; }
            try
            {
                store.Save(); dirty = false; saveTimer.Stop(); lastSave = DateTime.UtcNow; statusNote = "已自动保存"; UpdateProgress();
            }
            catch (Exception ex)
            {
                statusNote = "保存失败：" + ex.Message; UpdateProgress();
                if (closing)
                {
                    // Avoid a modal that would reveal the app after a boss-key exit.
                    try { LibraryStore.AtomicWrite(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recovery-state.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(store.State), false); }
                    catch { }
                }
                else if (current == null) Get<TextBlock>("StatusText").Text = statusNote;
            }
        }
        void Reflow(Action action)
        {
            if (!ready) { action(); return; }
            CapturePosition(); int position = current == null ? 0 : current.Position;
            restoring = true; action();
            if (current != null) Restore(position); else restoring = false;
            ScheduleSave();
        }
        void Theme(string background, string foreground)
        {
            store.State.Preferences.Background = background; store.State.Preferences.Foreground = foreground;
            ApplyAppearance(); ScheduleSave();
        }
        void ApplyAppearance()
        {
            var p = store.State.Preferences;
            try { Window.Background = (Brush)new BrushConverter().ConvertFromString(p.Background); }
            catch { p.Background = "#1E1E1E"; Window.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)); }
            try { reader.Foreground = (Brush)new BrushConverter().ConvertFromString(p.Foreground); }
            catch { p.Foreground = "#D4D4D4"; reader.Foreground = Brushes.LightGray; }
            var background = ((SolidColorBrush)Window.Background).Color;
            var foreground = ((SolidColorBrush)reader.Foreground).Color;
            Window.Resources["PaperBrush"] = Window.Background;
            Window.Resources["TextBrush"] = reader.Foreground;
            Window.Resources["FrameBrush"] = Window.Background;
            Window.Resources["MutedBrush"] = Blend(background, foreground, .65);
            Window.Resources["SelectionBrush"] = Blend(background, foreground, .16);
            Window.Resources["HoverBrush"] = Blend(background, foreground, .10);
            Window.Resources["AccentBrush"] = reader.Foreground;
            Get<TextBlock>("BookTitle").Foreground = reader.Foreground;
            reader.FontSize = Clamp(p.FontSize, 12, 36);
            Window.Opacity = Clamp(p.Opacity, .3, 1);
            Get<TextBlock>("FontLabel").Text = reader.FontSize.ToString("0") + " px";
            Get<TextBlock>("OpacityLabel").Text = Window.Opacity.ToString("P0");
            UpdatePin(); UpdateNativeFrame();
        }
        static Brush Blend(Color a, Color b, double ratio)
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(a.R + (b.R - a.R) * ratio), (byte)(a.G + (b.G - a.G) * ratio), (byte)(a.B + (b.B - a.B) * ratio)));
            brush.Freeze(); return brush;
        }
        void UpdateNativeFrame()
        {
            if (source == null) return;
            try
            {
                var color = ((SolidColorBrush)Window.Background).Color;
                int border = store.State.Preferences.Compact ? -2 : color.R | color.G << 8 | color.B << 16;
                DwmSetWindowAttribute(source.Handle, 34, ref border, 4);
                int policy = store.State.Preferences.Compact ? 1 : 2;
                DwmSetWindowAttribute(source.Handle, 2, ref policy, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        void BuildReadingMenu()
        {
            var menu = new ContextMenu();
            menu.PreviewKeyDown += HandleKeys;
            menu.SetResourceReference(Control.BackgroundProperty, "PaperBrush");
            menu.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            Action<string, Action> add = delegate(string title, Action action)
            {
                var item = new MenuItem { Header = title }; item.Click += delegate { action(); }; menu.Items.Add(item);
            };
            add("阅读外观…", delegate { Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(OpenSettings)); });
            add("切换完整 / 无边框小窗    Ctrl+M", ToggleCompact);
            add("切换置顶    Ctrl+T", delegate { store.State.Preferences.Topmost = !Window.Topmost; UpdatePin(); ScheduleSave(); });
            add("导入 TXT    Ctrl+O", ChooseFile);
            add("立即退出    Esc", BossExit);
            reader.ContextMenu = menu;
            Get<Border>("CompactDragArea").ContextMenu = menu;
        }
        void OpenSettings()
        {
            var popup = Get<Popup>("SettingsPopup");
            bool compact = store.State.Preferences.Compact;
            popup.PlacementTarget = compact ? (UIElement)reader : Get<Button>("SettingsButton");
            popup.Placement = compact ? PlacementMode.MousePoint : PlacementMode.Bottom;
            popup.HorizontalOffset = compact ? 0 : -240;
            popup.IsOpen = true;
            Get<Button>("PaperTheme").Focus();
        }
        void ChooseColor(bool background)
        {
            Get<Popup>("SettingsPopup").IsOpen = false;
            using (var dialog = new Forms.ColorDialog { FullOpen = true })
            {
                var p = store.State.Preferences;
                try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(background ? p.Background : p.Foreground); } catch { }
                var owner = new NativeOwner(new WindowInteropHelper(Window).Handle);
                if (dialog.ShowDialog(owner) == Forms.DialogResult.OK)
                {
                    string color = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                    if (background) p.Background = color; else p.Foreground = color;
                    ApplyAppearance(); ScheduleSave();
                }
            }
        }
        class NativeOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public NativeOwner(IntPtr handle) { Handle = handle; }
        }
        void UpdatePin()
        {
            Window.Topmost = store.State.Preferences.Topmost;
            Get<Button>("PinButton").Content = Window.Topmost ? "已置顶" : "置顶";
            Get<Button>("PinButton").Background = Window.Topmost ? (Brush)Window.Resources["SelectionBrush"] : Brushes.Transparent;
        }
        void ToggleSidebar()
        {
            Reflow(delegate
            {
                bool show = Get<Border>("Sidebar").Visibility != Visibility.Visible;
                Get<Border>("Sidebar").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Get<ColumnDefinition>("SideColumn").Width = new GridLength(show ? Math.Min(208, Window.ActualWidth * .4) : 0);
            });
        }
        void ShowTab(bool chapter)
        {
            books.Visibility = chapter ? Visibility.Collapsed : Visibility.Visible;
            chapters.Visibility = chapter ? Visibility.Visible : Visibility.Collapsed;
            if (chapter)
            {
                Get<Button>("BooksTab").Background = Brushes.Transparent;
                Get<Button>("ChaptersTab").SetResourceReference(Control.BackgroundProperty, "SelectionBrush");
            }
            else
            {
                Get<Button>("ChaptersTab").Background = Brushes.Transparent;
                Get<Button>("BooksTab").SetResourceReference(Control.BackgroundProperty, "SelectionBrush");
            }
        }
        void ToggleCompact()
        {
            Get<Popup>("SettingsPopup").IsOpen = false;
            Reflow(delegate
            {
                var p = store.State.Preferences;
                if (!p.Compact) { normalWidth = Window.Width; normalHeight = Window.Height; }
                p.Compact = !p.Compact; ApplyCompact(true);
            });
        }
        void ApplyCompact(bool resize)
        {
            bool compact = store.State.Preferences.Compact;
            Get<Border>("OuterBorder").BorderThickness = new Thickness(compact ? 0 : 1);
            Get<RowDefinition>("HeaderRow").Height = new GridLength(compact ? 0 : 48);
            Get<RowDefinition>("StatusRow").Height = new GridLength(compact ? 0 : 33);
            Get<Border>("TitleBar").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Get<Border>("StatusBar").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Get<Thumb>("ResizeCorner").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Window.MinWidth = compact ? 220 : 380; Window.MinHeight = compact ? 80 : 220;
            UpdateNativeFrame();
            Get<StackPanel>("BookHeading").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Get<Grid>("ReadingFooter").Visibility = !compact && current != null ? Visibility.Visible : Visibility.Collapsed;
            Get<Border>("CompactDragArea").Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            reader.Margin = compact ? new Thickness(14, 28, 10, 6) : new Thickness(30, 0, 20, 6);
            reader.Cursor = compact ? Cursors.Arrow : Cursors.IBeam;
            reader.ForceCursor = compact;
            Get<Button>("CompactButton").Content = compact ? "返回完整窗口   Ctrl+M" : "进入无边框小窗   Ctrl+M";
            if (resize)
            {
                Window.WindowState = WindowState.Normal;
                Window.Width = compact ? 480 : normalWidth; Window.Height = compact ? 300 : normalHeight;
            }
            UpdateResponsive();
        }
        void UpdateResponsive()
        {
            bool small = Window.Width < 740 || store.State.Preferences.Compact;
            Get<Border>("Sidebar").Visibility = small ? Visibility.Collapsed : Visibility.Visible;
            Get<ColumnDefinition>("SideColumn").Width = new GridLength(small ? 0 : 208);
            Get<TextBlock>("Brand").Visibility = Window.Width < 500 ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("BossHint").Visibility = Window.Width < 580 ? Visibility.Collapsed : Visibility.Visible;
            if (Window.Width < 500) Get<Button>("ImportButton").Content = "＋"; else Get<Button>("ImportButton").Content = "＋ 导入";
        }
    }
}
