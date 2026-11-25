using System.IO;
using System.Windows;
using System.Windows.Threading;
using MusicVisualizer.Core;
using MusicVisualizer.Shared;

namespace MusicVisualizer;

/// <summary>
/// Entry point. Command line:
///   (none)          start, or bring up settings if already running
///   --background    start in the tray with no window (used by "Start with Windows")
///   --wallpaper     like --background and turns the wallpaper on (older shortcut)
///   --uninstall     remove the app (Settings → Apps → Uninstall); add --quiet for no prompts
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool Has(string arg) => e.Args.Any(a => a.Equals(arg, StringComparison.OrdinalIgnoreCase));

        if (Has(InstallInfo.UninstallArg))
        {
            Uninstaller.Run(quiet: Has(InstallInfo.QuietArg));
            Shutdown();
            return;
        }

        // One copy only. A second launch (Start menu, desktop shortcut) asks the running copy to show settings.
        _instance = new Mutex(true, InstallInfo.InstanceMutex, out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(InstallInfo.ShowEvent, out var show))
                using (show) show.Set();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ListenForSignals();

        bool wallpaper = Has("--wallpaper");
        bool background = wallpaper || Has(InstallInfo.BackgroundArg);
        _controller = new AppController();
        _controller.Start(showSettings: !background, forceWallpaper: wallpaper);
    }

    /// <summary>Other processes signal us through named events: show settings, or quit (installer/uninstaller).</summary>
    private void ListenForSignals()
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.ShowEvent);
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.QuitEvent);
        new Thread(() =>
        {
            while (true)
            {
                int which = WaitHandle.WaitAny(new WaitHandle[] { show, quit });
                if (which == 0) Dispatcher.BeginInvoke(() => _controller?.ShowSettings());
                else { Dispatcher.BeginInvoke(Shutdown); return; }
            }
        }) { IsBackground = true, Name = "Signal listener" }.Start();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _controller?.Dispose(); // signing out / shutting down: restore the desktop
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A background app shouldn't vanish over one bad frame: log it, tell the user, keep running.</summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(InstallInfo.DataDir);
            File.AppendAllText(Path.Combine(InstallInfo.DataDir, "error.log"), $"{DateTime.Now:u}\n{e.Exception}\n\n");
        }
        catch (IOException) { }
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to %AppData%\\MusicVisualizer\\error.log.",
            InstallInfo.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
