using System.IO;
using System.Windows;
using System.Windows.Threading;
using MusicVisualizer.Core;
using MusicVisualizer.Shared;

namespace MusicVisualizer;

/// <summary>
/// Entry point of MusicVisualizerSettings.exe. Installed next to the engine (MusicVisualizer.exe, Rust),
/// it's the <b>settings helper</b>: the engine starts it with --settings or --visualizer while one of
/// those windows is open, and it exits when they close (see <see cref="AppController.HelperMode"/>).
/// Run on its own (a dev build, no engine next to it) it is the whole app, as before 2.0. Command line:
///   (none)          run as the wallpaper with no window (the main way to start it). If it's already
///                   running, start the wallpaper, or open settings when the wallpaper is already on.
///   --settings      open settings (the "Music Visualizer Settings" Start-menu shortcut)
///   --background    restore the saved state quietly in the tray (used by "Start with Windows")
///   --wallpaper     same as no arguments (kept for older shortcuts)
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

        if (AppController.HelperMode)
        {
            StartHelper(Has(InstallInfo.VisualizerArg));
            return;
        }

        var launch = Has(InstallInfo.SettingsArg) ? LaunchMode.Settings
                   : Has(InstallInfo.BackgroundArg) ? LaunchMode.Background
                   : LaunchMode.Wallpaper;

        // One copy only. A second launch passes its request to the running copy.
        _instance = new Mutex(true, InstallInfo.InstanceMutex, out bool first);
        if (!first)
        {
            string? signal = launch switch
            {
                LaunchMode.Settings => InstallInfo.ShowEvent,
                LaunchMode.Wallpaper => InstallInfo.WallpaperEvent,
                _ => null,
            };
            if (signal is not null && EventWaitHandle.TryOpenExisting(signal, out var ev))
                using (ev) ev.Set();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ListenForSignals();

        _controller = new AppController();
        _controller.Start(launch);
    }

    /// <summary>
    /// Settings helper: one copy (a second one passes its request on), windows only, no tray or wallpaper.
    /// The engine signals it to show a window or to quit.
    /// </summary>
    private void StartHelper(bool visualizer)
    {
        _instance = new Mutex(true, InstallInfo.HelperMutex, out bool first);
        if (!first)
        {
            string signal = visualizer ? InstallInfo.HelperVisualizerEvent : InstallInfo.HelperSettingsEvent;
            if (EventWaitHandle.TryOpenExisting(signal, out var ev))
                using (ev) ev.Set();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.HelperSettingsEvent);
        var viz = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.HelperVisualizerEvent);
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.HelperQuitEvent);
        new Thread(() =>
        {
            while (true)
            {
                switch (WaitHandle.WaitAny(new WaitHandle[] { show, viz, quit }))
                {
                    case 0: Dispatcher.BeginInvoke(() => _controller?.ShowSettings()); break;
                    case 1: Dispatcher.BeginInvoke(() => _controller?.ShowVisualizer()); break;
                    default: Dispatcher.BeginInvoke(Shutdown); return;
                }
            }
        }) { IsBackground = true, Name = "Helper signal listener" }.Start();

        _controller = new AppController();
        _controller.StartHelper(visualizer);
    }

    /// <summary>Other processes signal us through named events: show settings, start the wallpaper, or quit (installer/uninstaller).</summary>
    private void ListenForSignals()
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.ShowEvent);
        var wallpaper = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.WallpaperEvent);
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstallInfo.QuitEvent);
        new Thread(() =>
        {
            while (true)
            {
                switch (WaitHandle.WaitAny(new WaitHandle[] { show, wallpaper, quit }))
                {
                    case 0: Dispatcher.BeginInvoke(() => _controller?.ShowSettings()); break;
                    case 1: Dispatcher.BeginInvoke(() => _controller?.RunAsWallpaper(openSettingsIfRunning: true)); break;
                    default: Dispatcher.BeginInvoke(Shutdown); return;
                }
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
