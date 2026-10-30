using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuietReader;

class WpfTests
{
    static int passed, result;
    static int expectedFinal;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS: " + name); }
    static int First(TextBox text) { return text.GetCharacterIndexFromLineIndex(text.GetFirstVisibleLineIndex()); }
    static int LineStart(TextBox text, int anchor) { return text.GetCharacterIndexFromLineIndex(text.GetLineIndexFromCharacterIndex(anchor)); }
    [STAThread]
    static int Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wpf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LibraryStore(root);
            var input = store.PrepareImport(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tests", "示例小说.txt"));
            var book = store.Add(input); book.Position = 2370; store.State.CurrentId = book.Id; store.Save();
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var controller = new ReaderWindow(store, null);
            var win = controller.Window;
            win.Loaded += async delegate
            {
                try
                {
                    var text = (TextBox)win.FindName("Reader");
                    for (int i = 0; i < 200 && text.Text.Length == 0; i++) await Task.Delay(50);
                    await Task.Delay(250);
                    Check(text.Text.Length == input.Text.Length, "startup reopens last imported book");
                    Check(!win.Topmost && ((Button)win.FindName("PinButton")).Content.ToString() == "置顶", "window starts without always-on-top");
                    Check(((SolidColorBrush)win.Background).Color == Color.FromRgb(30, 30, 30) && ((SolidColorBrush)text.Foreground).Color == Color.FromRgb(212, 212, 212), "default colors match VS Code Dark");
                    Check(First(text) == LineStart(text, 2370), "startup restores saved anchor at top");
                    int anchor = First(text);
                    win.Width = 610; win.Height = 480; await Task.Delay(300);
                    Check(First(text) == LineStart(text, anchor), "window resize preserves anchor");
                    anchor = First(text);
                    ((Button)win.FindName("CompactButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(300);
                    Check(win.Width == 480 && win.Height == 300 && ((FrameworkElement)win.FindName("BookHeading")).Visibility == Visibility.Collapsed, "compact mode resizes and hides heading");
                    Check(((Border)win.FindName("OuterBorder")).BorderThickness == new Thickness(0) &&
                        ((FrameworkElement)win.FindName("TitleBar")).Visibility == Visibility.Collapsed &&
                        ((FrameworkElement)win.FindName("StatusBar")).Visibility == Visibility.Collapsed &&
                        ((FrameworkElement)win.FindName("ResizeCorner")).Visibility == Visibility.Collapsed, "compact mode has no border, toolbar, footer or resize decoration");
                    Check(First(text) == LineStart(text, anchor), "compact mode preserves anchor");
                    anchor = First(text);
                    ((Slider)win.FindName("FontSlider")).Value = 25;
                    await Task.Delay(300);
                    Check(text.FontSize == 25 && First(text) == LineStart(text, anchor), "font changes preserve anchor");
                    ((Button)win.FindName("GreenTheme")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(store.State.Preferences.Background == "#DDE7D6", "theme button changes background");
                    Check(((SolidColorBrush)((Border)win.FindName("OuterBorder")).BorderBrush).Color == ((SolidColorBrush)win.Background).Color &&
                        ((SolidColorBrush)((Border)win.FindName("TitleBar")).Background).Color == ((SolidColorBrush)win.Background).Color,
                        "theme changes synchronize frame and toolbar colors");
                    var opacity = (Slider)win.FindName("OpacitySlider");
                    Check(opacity.IsMoveToPointEnabled && opacity.LargeChange == .05, "opacity track click uses pointer position with safe step size");
                    foreach (double value in new[] { .9, .7, .5, 1.0 })
                    {
                        opacity.Value = value;
                        Check(Math.Abs(win.Opacity - value) < .001, "opacity follows slider continuously at " + value);
                    }
                    ((Button)win.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(win.Topmost, "pin button enables always-on-top");
                    ((Button)win.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!win.Topmost, "pin button disables always-on-top");
                    text.PageDown(); await Task.Delay(200); expectedFinal = First(text);
                    Check(expectedFinal > anchor, "page navigation advances reading position");
                    ((Button)win.FindName("BossButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); result = 1; win.Close(); }
            };
            app.Run(win);
            if (result != 0) return result;
            var restored = new LibraryStore(root);
            Check(restored.State.Books[0].Position == expectedFinal, "boss button flushes final reading position");
            Check(!restored.State.Preferences.Topmost && restored.State.Preferences.FontSize == 25 && restored.State.Preferences.Compact, "boss exit persists appearance and window settings");
            Console.WriteLine("All " + passed + " WPF checks passed.");
            return result;
        }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex.ToString()); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
