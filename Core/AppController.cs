using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicVisualizer.Audio;
using MusicVisualizer.NowPlaying;
using MusicVisualizer.Settings;
using MusicVisualizer.Visualizer;
using MusicVisualizer.Wallpaper;

namespace MusicVisualizer.Core;

/// <summary>How the app was started.</summary>
public enum LaunchMode
{
    /// <summary>Normal launch (Start menu, desktop shortcut): run as the wallpaper, no window.</summary>
    Wallpaper,
    /// <summary>"Music Visualizer Settings" shortcut: open settings.</summary>
    Settings,
    /// <summary>Start with Windows: restore the saved state quietly.</summary>
    Background,
}

/// <summary>
/// Owns everything that lives as long as the app: settings, audio, now-playing, the wallpaper,
/// the tray icon and the (optional) windows. The app keeps running with no window open;
/// it only exits through <see cref="Quit"/> (tray menu, settings window, installer, or sign-out).
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly TrayIcon _tray = new();
    private readonly DispatcherTimer _reattachTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HwndSource _shellWatcher;
    private readonly int _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
    private int _reattachAttempts;

    private WallpaperWindow? _wallpaper;
    private DateTime _attachedAt;
    private bool _closingWallpaper;
    private MainWindow? _visualizer;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public AppSettings Settings { get; } = AppSettings.Load();
    public AudioEngine Engine { get; } = new();
    public NowPlayingService NowPlaying { get; } = new();

    /// <summary>Raised on the UI thread whenever something the windows show may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when <see cref="AppSettings.Look"/> changes (often, while a slider is dragged).</summary>
    public event EventHandler? AppearanceChanged;

    public AppController()
    {
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.VisualizerRequested += (_, _) => ShowVisualizer();
        _tray.WallpaperToggled += (_, _) => SetWallpaper(!WallpaperRunning, showErrors: true);
        _tray.QuitRequested += (_, _) => Quit();

        NowPlaying.Changed += (_, _) => { UpdateAudio(); ApplyTheme(); };
        Engine.StateChanged += (_, _) => Dispatcher.BeginInvoke(() => { ApplyTheme(); RaiseChanged(); });

        _reattachTimer.Tick += (_, _) => RetryWallpaper();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Settings.Save(); };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // A hidden top-level window, only so we receive the "TaskbarCreated" broadcast
        // that Explorer sends when it restarts (the wallpaper has to be re-attached then).
        _shellWatcher = new HwndSource(new HwndSourceParameters("Music Visualizer shell watcher")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            Width = 0,
            Height = 0,
        });
        _shellWatcher.AddHook(ShellWatcherHook);
    }

    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public VisualMode Mode => Enum.TryParse<VisualMode>(Settings.Mode, out var m) ? m : VisualMode.Bars;
    public bool WallpaperRunning => _wallpaper is not null;
    public ScreenInfo WallpaperScreen => Screens.Find(Settings.WallpaperScreen);

    /// <summary>Colours of the latest album cover (used by the "Song art colors" setting).</summary>
    public AlbumColors? AlbumColors => NowPlaying.AlbumColors;

    public void Start(LaunchMode launch)
    {
        StartWithWindows.Repair();
        NowPlaying.Start();
        if (launch == LaunchMode.Wallpaper) Settings.WallpaperEnabled = true;
        if (Settings.WallpaperEnabled && !TryAttachWallpaper(out _))
            StartReattachRetries(); // Explorer may not be ready yet right after sign-in
        if (launch == LaunchMode.Settings) ShowSettings();
        else if (launch == LaunchMode.Wallpaper) ShowTrayHintOnce();
        UpdateTray();
    }

    /// <summary>
    /// The main action: run as the wallpaper with no window in the way. Used by the big button in
    /// settings and by launching the app again while it runs (if the wallpaper is already on,
    /// that opens settings instead, so the click always does something visible).
    /// </summary>
    public void RunAsWallpaper(bool openSettingsIfRunning = false)
    {
        if (WallpaperRunning)
        {
            if (openSettingsIfRunning) ShowSettings();
            return;
        }
        SetWallpaper(true, showErrors: true);
        if (!WallpaperRunning) return;
        _settingsWindow?.Close();
        ShowTrayHintOnce();
    }

    // ------------------------------------------------------------ windows

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                Settings.SetupDone = true;
                Settings.Save();
                UpdateAudio();
                ShowTrayHintOnce();
            };
            _settingsWindow.Show();
            UpdateAudio();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void ShowVisualizer()
    {
        if (_visualizer is null)
        {
            _visualizer = new MainWindow(this);
            _visualizer.Closed += (_, _) =>
            {
                _visualizer = null;
                UpdateAudio();
                ShowTrayHintOnce();
            };
            _visualizer.Show();
            UpdateAudio();
        }
        if (_visualizer.WindowState == WindowState.Minimized) _visualizer.WindowState = WindowState.Normal;
        _visualizer.Activate();
    }

    private void ShowTrayHintOnce()
    {
        if (Settings.TrayHintShown || _disposed) return;
        Settings.TrayHintShown = true;
        Settings.Save();
        _tray.ShowBalloon(WallpaperRunning ? "Music Visualizer is your wallpaper now" : "Music Visualizer is still running",
            "It lives in the system tray. Click the icon to change how it looks, or right-click to quit.");
    }

    // ------------------------------------------------------------ settings

    public void SetMode(VisualMode mode)
    {
        Settings.Mode = mode.ToString();
        Settings.Save();
        if (_wallpaper is not null) _wallpaper.Mode = mode;
        RaiseChanged();
    }

    public void SetAppearance(Appearance look)
    {
        Settings.Look = look.Clone();
        SaveSoon();
        if (_wallpaper is not null) _wallpaper.Look = Settings.Look;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copies a picture into %AppData%\MusicVisualizer (so it survives the original being moved)
    /// and uses it as the background. Each copy gets a new name so every view reloads it.
    /// </summary>
    public void SetBackgroundImage(string? sourcePath)
    {
        var look = Settings.Look.Clone();
        string? copy = null;
        if (sourcePath is not null)
        {
            Directory.CreateDirectory(InstallInfoDataDir);
            copy = Path.Combine(InstallInfoDataDir, $"background-{DateTime.Now.Ticks}{Path.GetExtension(sourcePath)}");
            File.Copy(sourcePath, copy, overwrite: true);
        }
        foreach (var old in Directory.Exists(InstallInfoDataDir)
                     ? Directory.GetFiles(InstallInfoDataDir, "background-*") : Array.Empty<string>())
        {
            if (old != copy) try { File.Delete(old); } catch (IOException) { }
        }
        look.BackgroundImage = copy;
        SetAppearance(look);
        Settings.Save();
    }

    /// <summary>The current Windows desktop wallpaper file, if it is a picture.</summary>
    public static string? CurrentDesktopWallpaper()
    {
        var path = new StringBuilder(520);
        return SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)path.Capacity, path, 0)
               && File.Exists(path.ToString()) ? path.ToString() : null;
    }

    private static string InstallInfoDataDir => Shared.InstallInfo.DataDir;

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SetHideWhenSilent(bool hide, double seconds)
    {
        Settings.HideWhenSilent = hide;
        Settings.SilentSeconds = Math.Clamp(seconds, 1, 120);
        SaveSoon();
    }

    /// <summary>
    /// While nothing the app listens to makes a sound for <see cref="AppSettings.SilentSeconds"/>,
    /// show the normal desktop wallpaper; bring the visualizer back as soon as sound returns.
    /// </summary>
    private void CheckIdle()
    {
        if (_wallpaper is null) return;
        // Give capture a moment after the wallpaper appears, so it doesn't fade out before the first sound arrives.
        double quiet = Math.Min(Engine.SecondsSinceSound, (DateTime.UtcNow - _attachedAt).TotalSeconds);
        if (!Settings.HideWhenSilent) _wallpaper.SetIdle(false);
        else if (!_wallpaper.IsIdle && quiet >= Settings.SilentSeconds) _wallpaper.SetIdle(true);
        else if (_wallpaper.IsIdle && quiet < 0.3) _wallpaper.SetIdle(false);
    }

    /// <summary>True while the wallpaper is switched off because nothing is playing.</summary>
    public bool WallpaperIdle => _wallpaper?.IsIdle == true;

    public void SetListen(ListenMode mode, IEnumerable<string> apps)
    {
        Settings.Listen = mode;
        Settings.Apps = apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Settings.Save();
        if (Engine.Source is AudioSource.Loopback or AudioSource.Apps) StartLiveAudio();
        else UpdateAudio();
        RaiseChanged();
    }

    public void SetScreen(string deviceName)
    {
        Settings.WallpaperScreen = deviceName;
        Settings.Save();
        // No RestoreStaticWallpaper() here: re-applying the wallpaper makes Explorer raise
        // its WorkerW above our window. The screen we left repaints on its own.
        _wallpaper?.MoveTo(WallpaperScreen.Bounds);
        RaiseChanged();
    }

    /// <summary>Moves the wallpaper to the next monitor, wrapping around.</summary>
    public void NextScreen()
    {
        var all = Screens.All();
        if (all.Count < 2) return;
        int index = all.ToList().FindIndex(s => s.DeviceName == WallpaperScreen.DeviceName);
        SetScreen(all[(index + 1) % all.Count].DeviceName);
    }

    // ------------------------------------------------------------ audio

    /// <summary>Live audio runs only while something shows it: the wallpaper, the visualizer window, or the settings preview.</summary>
    private bool NeedsLiveAudio =>
        _wallpaper is not null || _settingsWindow is not null || (_visualizer is not null && Settings.AutoSystemAudio);

    /// <summary>Starts or stops live capture to match what's on screen. Leaves file playback alone.</summary>
    public void UpdateAudio()
    {
        if (Engine.Source == AudioSource.File) return;
        bool live = Engine.Source is AudioSource.Loopback or AudioSource.Apps;
        if (NeedsLiveAudio && !live) StartLiveAudio();
        else if (!NeedsLiveAudio && live) Engine.Stop();
    }

    /// <summary>Captures what the settings say: all system audio, or the chosen apps.</summary>
    public void StartLiveAudio()
    {
        try
        {
            if (Settings.Listen == ListenMode.SelectedApps)
            {
                if (Settings.Apps.Count > 0) Engine.StartApps(Settings.Apps);
                else Engine.Stop();
            }
            else
            {
                Engine.StartLoopback();
            }
        }
        catch (Exception ex)
        {
            Engine.Stop();
            ShowError($"Couldn't capture audio:\n\n{ex.Message}");
        }
    }

    private void ApplyTheme()
    {
        if (_wallpaper is not null) _wallpaper.AlbumColors = AlbumColors;
        _visualizer?.ApplyTheme();
    }

    // ------------------------------------------------------------ wallpaper

    public void SetWallpaper(bool on, bool showErrors)
    {
        Settings.WallpaperEnabled = on;
        Settings.Save();
        _reattachTimer.Stop();

        if (on && _wallpaper is null)
        {
            if (!TryAttachWallpaper(out var error))
            {
                Settings.WallpaperEnabled = false;
                Settings.Save();
                if (showErrors) ShowError($"Couldn't attach to the desktop:\n\n{error}");
            }
        }
        else if (!on && _wallpaper is not null)
        {
            CloseWallpaper();
            DesktopWallpaper.RestoreStaticWallpaper();
        }
        UpdateAudio();
        ApplyTheme();
        UpdateTray();
        RaiseChanged();
    }

    private bool TryAttachWallpaper(out string? error)
    {
        error = null;
        var window = new WallpaperWindow(Engine, NowPlaying, Mode, Settings.Look);
        try
        {
            window.ShowAsWallpaper(WallpaperScreen.Bounds);
        }
        catch (Exception ex)
        {
            try { window.Close(); } catch (InvalidOperationException) { }
            error = ex.Message;
            return false;
        }

        _wallpaper = window;
        _attachedAt = DateTime.UtcNow;
        window.Closed += OnWallpaperClosed;
        window.AlbumColors = AlbumColors;
        UpdateAudio();
        UpdateTray();
        RaiseChanged();
        return true;
    }

    private void CloseWallpaper()
    {
        if (_wallpaper is null) return;
        _closingWallpaper = true;
        _wallpaper.Close();
        _closingWallpaper = false;
        _wallpaper = null;
    }

    /// <summary>The wallpaper window also dies when Explorer restarts (its parent window is destroyed).</summary>
    private void OnWallpaperClosed(object? sender, EventArgs e)
    {
        if (_closingWallpaper || sender != _wallpaper) return;
        _wallpaper = null;
        UpdateTray();
        RaiseChanged();
        if (Settings.WallpaperEnabled) StartReattachRetries();
    }

    private void StartReattachRetries()
    {
        _reattachAttempts = 0;
        _reattachTimer.Start();
    }

    private void RetryWallpaper()
    {
        if (!Settings.WallpaperEnabled || _wallpaper is not null || ++_reattachAttempts > 40)
        {
            _reattachTimer.Stop();
            return;
        }
        if (TryAttachWallpaper(out _)) _reattachTimer.Stop();
    }

    private IntPtr ShellWatcherHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg && Settings.WallpaperEnabled)
        {
            // Explorer restarted. Our old window went with it (or is orphaned); attach a fresh one.
            CloseWallpaper();
            StartReattachRetries();
        }
        return IntPtr.Zero;
    }

    // Monitors plugged in or out, or resolution changed. Raised on a system thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            _wallpaper?.MoveTo(WallpaperScreen.Bounds);
            RaiseChanged();
        });

    // ------------------------------------------------------------ misc

    private void UpdateTray()
    {
        _tray.SetWallpaperChecked(WallpaperRunning);
        _tray.SetTooltip(WallpaperRunning ? "Music Visualizer: wallpaper on" : "Music Visualizer");
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void ShowError(string message)
    {
        Window? owner = _settingsWindow ?? (Window?)_visualizer;
        if (owner is not null)
            MessageBox.Show(owner, message, "Music Visualizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, "Music Visualizer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Quit() => Application.Current.Shutdown();

    /// <summary>Called on exit: puts the normal wallpaper back and removes the tray icon.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _reattachTimer.Stop();
        _saveTimer.Stop();
        _idleTimer.Stop();
        _visualizer?.Close();
        _settingsWindow?.Close();
        if (_wallpaper is not null)
        {
            CloseWallpaper();
            DesktopWallpaper.RestoreStaticWallpaper();
        }
        _tray.Dispose();
        _shellWatcher.Dispose();
        Settings.Save();
        NowPlaying.Dispose();
        Engine.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pvParam, uint winIni);
}
