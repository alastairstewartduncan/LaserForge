using System.IO;
using System.Windows;
using System.Windows.Threading;
using AsdLasercraft.App.Services;
using AsdLasercraft.App.Views;

namespace AsdLasercraft.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // CI runs "AsdLasercraft.exe --smoke-test <dir>": build every window, render screenshots, exit 0/1.
        int smoke = Array.FindIndex(e.Args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (smoke >= 0)
        {
            string dir = smoke + 1 < e.Args.Length ? e.Args[smoke + 1] : Path.Combine(Environment.CurrentDirectory, "smoke");
            Shutdown(SmokeTest.Run(dir));
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        // Double-clicking an .asdl file (installer file association) passes its path as the first argument.
        string? openPath = e.Args.FirstOrDefault(a => a.EndsWith(".asdl", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        if (!settings.ShowSplash || e.Args.Contains("--no-splash"))
        {
            ShowMain(settings, openPath);
            return;
        }

        var splash = new SplashWindow();
        splash.Show();
        // Build the main window while the splash animates; show it once both are ready.
        Dispatcher.InvokeAsync(() =>
        {
            var main = new MainWindow(settings);
            if (openPath != null) main.OpenFromCommandLine(openPath);
            splash.Status = "Ready";
            splash.WhenFinished(() =>
            {
                MainWindow = main;
                main.Show();
                main.Activate();
                splash.FadeOutAndClose();
                ShutdownMode = ShutdownMode.OnMainWindowClose;
            });
        }, DispatcherPriority.Background);
    }

    private void ShowMain(AppSettings settings, string? openPath)
    {
        var main = new MainWindow(settings);
        if (openPath != null) main.OpenFromCommandLine(openPath);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "A·S·D Lasercraft – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
