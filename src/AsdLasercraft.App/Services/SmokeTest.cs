using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json.Nodes;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Views;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Imaging;
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

            // ---------------------------------------------------------------- AI (fake client: no key, no network)
            Step(log, "assistant setup card", () =>
            {
                main!.RightTabs.SelectedItem = main.AssistantTab;
                Pump(250);
                Save(main, Path.Combine(outDir, "assistant-setup.png"));
            });

            Step(log, "assistant chat", () =>
            {
                var fake = FakeAiClient.Sequence(
                    new AiResponse
                    {
                        StopReason = "tool_use",
                        Content =
                        {
                            new AiText("I'll add that for you."),
                            new AiToolUse("t1", "add_text", JsonNode.Parse("{\"text\":\"Happy Birthday\",\"size_mm\":14,\"font\":\"Segoe Script\",\"x_mm\":40,\"y_mm\":200}")!.AsObject()),
                        },
                    },
                    AiResponse.FromText("Done – **\"Happy Birthday\"** is on the Engrave layer below the coaster, in Segoe Script at 14 mm.\n\n" +
                                        "For 3 mm birch ply I'd start with:\n- Engrave: `3000 mm/min` at 35 %\n- Cut: `300 mm/min` at 100 %, 2 passes with air assist\n\n" +
                                        "Run a test grid first if it's a new sheet."));
                main!.SmokeUseFakeAi(fake);
                var chat = main.SmokeChat("Add \"Happy Birthday\" under the coaster in a script font");
                PumpUntil(chat, 5000);
                Pump(250);
                Save(main, Path.Combine(outDir, "assistant.png"));
            });

            Step(log, "ai settings", () =>
            {
                var dlg = new AiSettingsWindow(main!, settings);
                dlg.Show();
                Pump(200);
                Save(dlg, Path.Combine(outDir, "ai-settings.png"));
                dlg.Close();
            });

            Step(log, "design window", () =>
            {
                var request = new DesignRequest { WidthMm = 90, Style = DesignStyle.EngraveAndCut };
                var design = DesignGenerator.Parse(DemoDesignReply, request, WpfTextOutliner.Instance);
                var dlg = new DesignWindow(main!, main!.SmokeAi);
                dlg.Show();
                dlg.SmokeShow("A round coaster with mountains under a crescent moon, \"WILD\" across the top and a cut outline", design, DesignStyle.EngraveAndCut, 90);
                Pump(300);
                Save(dlg, Path.Combine(outDir, "ai-design.png"));
                dlg.Close();
            });

            Step(log, "test grid reader", () =>
            {
                var grid = new MaterialTestOptions();
                var dlg = new TestGridWindow(main!, main!.SmokeAi, settings, MaterialLibrary.CreateDefault(), Path.Combine(Path.GetTempPath(), "asd-smoke-materials.json"), main.SmokeActiveLayer);
                var (jpeg, bmp) = FakeGridPhoto(grid);
                dlg.SetPhoto(jpeg, bmp);
                dlg.ShowReading(TestGridReader.Parse(
                    "{\"row\":2,\"column\":2,\"confidence\":0.82,\"reasoning\":\"Rich, even brown with crisp edges. The higher-power cells in this row start to char and show a halo.\",\"alternative\":{\"row\":1,\"column\":2},\"advice\":\"Use air assist to keep the edges clean.\"}",
                    grid), grid, GridGoal.Engrave);
                dlg.Show();
                Pump(300);
                Save(dlg, Path.Combine(outDir, "ai-test-grid.png"));
                dlg.Close();
            });

            Step(log, "smart image tools", () =>
            {
                var img = main!.SmokeImage();
                var mask = BackgroundRemoval.PlainBackgroundMask(img.Gray, img.PixelWidth, img.PixelHeight);
                BackgroundRemoval.ApplyMask(img.Gray, mask);
                AutoTune.Tune(img.Gray, "Plywood (birch) 3 mm – Photo");
                var (scene, sw, sh) = FakeScenePhoto();
                var dlg = new LineArtWindow(main, scene, sw, sh);
                dlg.Show();
                dlg.SmokeSet(0.6, 0.55, 0.2);
                Pump(300);
                Save(dlg, Path.Combine(outDir, "ai-line-art.png"));
                dlg.Close();
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

    /// <summary>Pumps the dispatcher until the task finishes (UI-thread async work such as the assistant).</summary>
    private static void PumpUntil(Task task, int timeoutMs)
    {
        var start = DateTime.UtcNow;
        while (!task.IsCompleted)
        {
            if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for async UI work.");
            Pump(30);
        }
        task.GetAwaiter().GetResult(); // surface any exception
    }

    private const string DemoDesignReply = """
        A round coaster with a mountain range under a crescent moon and WILD across the top.
        ```svg
        <svg xmlns="http://www.w3.org/2000/svg" width="90mm" height="90mm" viewBox="0 0 90 90">
          <g id="cut"><circle cx="45" cy="45" r="43"/></g>
          <path d="M14 62 L30 38 L38 48 L50 28 L62 46 L68 40 L78 62 Z M44 36 L50 28 L55 36 L52 34 L50 37 L47 34 Z"/>
          <path d="M64 30 A8 8 0 1 0 72 42 A6.5 6.5 0 1 1 64 30 Z"/>
          <path d="M18 70 L72 70 M24 75 L66 75"/>
          <text x="45" y="22" font-family="Arial Black" font-size="10" text-anchor="middle">WILD</text>
        </svg>
        ```
        """;

    /// <summary>A soft, photo-like greyscale scene (cottage, tree, sun, hills) for the line art screenshot.</summary>
    private static (byte[] Gray, int W, int H) FakeScenePhoto()
    {
        int w = 720, h = 540;
        var visual = new DrawingVisual();
        Brush G(byte v) => new SolidColorBrush(Color.FromRgb(v, v, v));
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(225, 225, 225), Color.FromRgb(170, 170, 170), 90), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(G(245), null, new Point(590, 110), 55, 55);
            var hills = Geometry.Parse("M0,380 C150,300 260,330 360,360 C480,395 600,300 720,330 L720,540 L0,540 Z");
            dc.DrawGeometry(G(120), null, hills);
            dc.DrawRectangle(G(200), new Pen(G(60), 3), new Rect(170, 250, 220, 160));                       // walls
            dc.DrawGeometry(G(80), new Pen(G(40), 3), Geometry.Parse("M150,255 L280,160 L410,255 Z"));      // roof
            dc.DrawRectangle(G(70), null, new Rect(255, 330, 50, 80));                                       // door
            foreach (var x in new[] { 195.0, 325.0 }) dc.DrawRectangle(G(110), new Pen(G(50), 2), new Rect(x, 280, 40, 36));
            dc.DrawRectangle(G(70), null, new Rect(330, 175, 22, 45));                                       // chimney
            dc.DrawRectangle(G(60), null, new Rect(520, 300, 18, 110));                                      // trunk
            foreach (var (cx, cy, r) in new[] { (529.0, 260.0, 60.0), (495.0, 290.0, 40.0), (565.0, 290.0, 42.0) })
                dc.DrawEllipse(G(95), null, new Point(cx, cy), r, r * 0.9);
            var rnd = new Random(5);
            for (int i = 0; i < 60; i++) // grass
            {
                double x = rnd.Next(w), y = 420 + rnd.Next(110);
                dc.DrawLine(new Pen(G(70), 1.5), new Point(x, y), new Point(x + rnd.Next(-4, 5), y - 8 - rnd.Next(8)));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var gray = new FormatConvertedBitmap(rtb, PixelFormats.Gray8, null, 0);
        var px = new byte[w * h];
        gray.CopyPixels(px, w, 0);
        // Soften like a real photo so the filter has gradients to work with.
        var soft = Filters.ToBytes(Filters.GaussianBlur(Filters.ToFloat(px), w, h, 1.6));
        return (soft, w, h);
    }

    /// <summary>A pretend photo of a burned 5 × 5 test grid on wood, for the grid reader screenshot.</summary>
    private static (byte[] Jpeg, BitmapSource Bitmap) FakeGridPhoto(MaterialTestOptions g)
    {
        int w = 640, h = 480;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xD9, 0xBF, 0x93)), null, new Rect(0, 0, w, h));
            var rnd = new Random(3);
            for (int i = 0; i < 40; i++) // wood grain
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 0x8A, 0x63, 0x3A)), 1 + rnd.NextDouble() * 2),
                            new Point(0, rnd.Next(h)), new Point(w, rnd.Next(h)));
            double cell = 62, gap = 18, left = 150, top = 70;
            for (int r = 0; r < g.SpeedSteps; r++)
                for (int c = 0; c < g.PowerSteps; c++)
                {
                    double heat = (c + 1.0) / g.PowerSteps * (1 - 0.55 * r / (g.SpeedSteps - 1));
                    byte v = (byte)(200 - 180 * heat);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(v + 20), (byte)(v * 0.75), (byte)(v * 0.45))), null,
                                     new Rect(left + c * (cell + gap), top + r * (cell + gap), cell, cell));
                }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return (ms.ToArray(), rtb);
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
