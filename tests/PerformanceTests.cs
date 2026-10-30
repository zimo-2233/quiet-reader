using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuietReader;

class PerformanceTests
{
    static int result;
    static bool settled;
    static ReaderWindow controller;
    static Stopwatch clock = Stopwatch.StartNew();
    static long lastTick, maxGap;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS: " + name); }
    static async Task WaitReady()
    {
        for (int i = 0; i < 1500; i++)
        {
            var field = typeof(ReaderWindow).GetField("restoring", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var text = (TextBox)controller.Window.FindName("Reader");
            if (text.Text.Length > 0 && !(bool)field.GetValue(controller)) { await Task.Delay(35); settled = true; return; }
            await Task.Delay(20);
        }
        throw new Exception("load timed out");
    }
    static void Report(string stage, Stopwatch time) { Console.WriteLine("METRIC " + stage + "_ms=" + time.ElapsedMilliseconds + " dispatcher_max_gap_ms=" + maxGap); maxGap = 0; }
    [STAThread]
    static int Main(string[] args)
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "perf-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fixture = Path.Combine(root, "long.txt");
            var builder = new StringBuilder(3300000);
            int paragraph = 0;
            while (builder.Length < 3300000) builder.Append("【").Append(paragraph++).Append("】清晨的街道还很安静，林简推开旧书店的木门，听见门楣上的铜铃轻轻响了一声。窗外的银杏刚刚变黄，几片叶子贴着玻璃缓缓落下。\n\n");
            if (args.Length > 0) File.Copy(args[0], fixture);
            else File.WriteAllText(fixture, builder.ToString(), new UTF8Encoding(false));
            var store = new LibraryStore(Path.Combine(root, "library"));
            var input = store.PrepareImport(fixture); var book = store.Add(input);
            book.Position = input.Text.Length * 3 / 4; store.State.CurrentId = book.Id; store.Save();
            Console.WriteLine("FIXTURE characters=" + input.Text.Length + " utf8_bytes=" + new FileInfo(fixture).Length);
            input = null; builder = null;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            lastTick = clock.ElapsedMilliseconds;
            timer.Tick += delegate { long now = clock.ElapsedMilliseconds; maxGap = Math.Max(maxGap, now - lastTick); lastTick = now; }; timer.Start();
            var stage = Stopwatch.StartNew(); controller = new ReaderWindow(store, null); var win = controller.Window;
            win.Loaded += async delegate
            {
                try
                {
                    await WaitReady(); Report("startup_75_percent", stage);
                    var text = (TextBox)win.FindName("Reader");
                    Check(Math.Abs(book.Position - book.Length * .75) < 150, "long-book restore uses absolute position");
                    Console.WriteLine("METRIC rendered_characters=" + text.Text.Length + " working_set_mb=" + (Process.GetCurrentProcess().WorkingSet64 / 1048576));
                    stage.Restart(); ((Slider)win.FindName("ProgressSlider")).Value = 50; await WaitReady(); Report("seek_50_percent", stage);
                    Check(Math.Abs(book.Position - book.Length * .5) < 150, "global progress seeks beyond local text window");
                    stage.Restart(); win.Width = 510; await WaitReady(); Report("resize", stage);
                    stage.Restart(); ((Slider)win.FindName("FontSlider")).Value = 24; await WaitReady(); Report("font_change", stage);
#if !BASELINE
                    Check(text.Text.Length <= TextWindow.Capacity, "rendering is bounded for a 3.3-million-character novel");
                    int before = book.Position;
                    for (int i = 0; i < 90; i++) { text.PageDown(); await Task.Delay(12); await WaitReady(); }
                    Check(book.Position > before + TextWindow.Capacity, "forward scrolling crosses text-window boundaries");
                    int after = book.Position;
                    for (int i = 0; i < 90; i++) { text.PageUp(); await Task.Delay(12); await WaitReady(); }
                    Check(book.Position < after - TextWindow.Capacity, "backward scrolling crosses text-window boundaries");
                    ((Slider)win.FindName("ProgressSlider")).Value = 100; await WaitReady();
                    Check(book.Progress == 1, "end of full book is reported as 100 percent");
                    ((Slider)win.FindName("ProgressSlider")).Value = 0; await WaitReady();
                    Check(book.Position == 0, "start of full book remains reachable");
#endif
                    settled = true; win.Close();
                }
                catch (Exception ex) { Console.WriteLine("FAIL: " + ex); result = 1; win.Close(); }
            };
            app.Run(win); timer.Stop();
            return settled ? result : 1;
        }
        finally { Directory.Delete(root, true); }
    }
}
