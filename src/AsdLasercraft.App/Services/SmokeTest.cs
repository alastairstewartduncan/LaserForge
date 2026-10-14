using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Views;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.App.Services;

/// <summary>
/// "AsdLasercraft.exe --smoke-test &lt;dir&gt;": run by CI on a Windows runner. Opens every window, loads a demo
/// project, exercises the preview and dialogs, and saves PNG screenshots. Any XAML/resource error or exception
/// fails the build (exit code 1) instead of crashing on a user's PC.
/// </summary>
public static class SmokeTest
{
    private static readonly List<Exception> DispatcherErrors = new();

    public static int Run(string outDir)
    {
        var log = new List<string>();
        // Exceptions inside event handlers (render, layout, timers) must fail the test too, not vanish.
        Application.Current.DispatcherUnhandledException += (_, e) => { DispatcherErrors.Add(e.Exception); e.Handled = true; };
        try
        {
            Directory.CreateDirectory(outDir);
            var settings = new AppSettings { CheckForUpdates = false, ShowSplash = false };

            Step(log, "splash", () =>
            {
                var splash = new SplashWindow();
                splash.Show();
                Pump(1050); // mid-engrave
                Save(splash, Path.Combine(outDir, "splash.png"));
                splash.Close();
            });

            MainWindow? main = null;
            Step(log, "main window", () =>
            {
                main = new MainWindow(settings, smoke: true) { Width = 1480, Height = 920, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                main.Show();
                Pump(300);
                Save(main, Path.Combine(outDir, "empty.png"));
                main.SmokeLoadDemo();
                Pump(300);
                Save(main, Path.Combine(outDir, "design.png"));
            });

            Step(log, "running job", () =>
            {
                main!.SmokeShowRunning();
                Pump(300);
                Save(main, Path.Combine(outDir, "laser-running.png"));
            });

            Step(log, "simulated preview", () =>
            {
                main!.SmokeShowPreview(0.55);
                Pump(300);
                Save(main, Path.Combine(outDir, "preview.png"));
            });

            Step(log, "pre-flight dialog", () =>
            {
                var (job, issues) = main!.SmokeJob();
                var dlg = new JobConfirmWindow(main, job, issues, "3 layers · demo project · absolute coordinates");
                dlg.Show();
                Pump(250);
                Save(dlg, Path.Combine(outDir, "preflight.png"));
                dlg.Close();
            });

            Step(log, "material library", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "asd-smoke-materials.json");
                var dlg = new MaterialLibraryWindow(main!, MaterialLibrary.CreateDefault(), path, main!.SmokeActiveLayer);
                dlg.Show();
                Pump(250);
                Save(dlg, Path.Combine(outDir, "materials.png"));
                dlg.Close();
            });

            Step(log, "about", () =>
            {
                var dlg = new AboutWindow(main!);
                dlg.Show();
                Pump(200);
                Save(dlg, Path.Combine(outDir, "about.png"));
                dlg.Close();
            });

            Step(log, "form dialog", () =>
            {
                var form = new FormDialog("Machine settings", main!).Section("Machine").Text("Name", "Demo").Number("Bed width (mm)", 400).Check("Flip Y", true)
                    .Hint("Smoke test – built but not shown modally.");
                form.Measure(new Size(800, 800));
            });

            Step(log, "close", () =>
            {
                main!.Close();
                Pump(100);
            });

            File.WriteAllLines(Path.Combine(outDir, "smoke.log"), log);
            Console.WriteLine($"::notice title=UI smoke test::{log.Count} steps passed, screenshots in {outDir}");
            return 0;
        }
        catch (Exception ex)
        {
            log.Add("FAILED: " + ex);
            try { File.WriteAllLines(Path.Combine(outDir, "smoke.log"), log); } catch { /* ignore */ }
            var first = ex is StepException se ? se : ex;
            Console.WriteLine($"::error title=UI smoke test failed::{Escape(first.Message + " – " + (ex.InnerException ?? ex))}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private sealed class StepException : Exception
    {
        public StepException(string step, Exception inner) : base($"Step '{step}' failed: {inner.Message}", inner) { }
    }

    private static void Step(List<string> log, string name, Action action)
    {
        try
        {
            action();
            if (DispatcherErrors.Count > 0)
            {
                var err = DispatcherErrors[0];
                DispatcherErrors.Clear();
                throw new InvalidOperationException("Unhandled exception on the UI thread: " + err.Message, err);
            }
            log.Add("ok   " + name);
            Console.WriteLine("ok   " + name);
        }
        catch (Exception ex)
        {
            throw new StepException(name, ex);
        }
    }

    /// <summary>Runs the dispatcher (layout, rendering, animations) for the given time.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        // Windows render their content, not their (dark) background, so paint that first.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = (element as Window)?.Background ?? Brushes.Transparent;
            dc.DrawRectangle(bg is SolidColorBrush ? bg : new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1C)), null, new Rect(0, 0, w, h));
        }
        rtb.Render(visual);
        rtb.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static string Escape(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
}
