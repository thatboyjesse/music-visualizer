using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicVisualizer.Audio;
using MusicVisualizer.NowPlaying;
using MusicVisualizer.Settings;
using MusicVisualizer.Visualizer;
using MusicVisualizer.Wallpaper;

namespace MusicVisualizer;

public partial class MainWindow : Window
{
    private const string AudioFilter =
        "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif|All files|*.*";

    private readonly AudioEngine _engine = new();
    private readonly NowPlayingService _nowPlaying = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private WallpaperWindow? _wallpaper;
    private bool _updatingSeek;
    private bool _userSeeking;

    private bool _fullscreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;

    public MainWindow()
    {
        InitializeComponent();
        Viz.Attach(_engine);

        if (Enum.TryParse<VisualMode>(_settings.Mode, out var mode)) SetMode(mode);

        Card.ShowControls = true;
        Card.Bind(_nowPlaying);
        _nowPlaying.Changed += OnNowPlayingChanged;
        _nowPlaying.Start();

        _engine.StateChanged += (_, _) => Dispatcher.BeginInvoke(() => { RefreshTransport(); ApplyTheme(); });
        _timer.Tick += (_, _) => RefreshTransport();
        _timer.Start();

        StateChanged += (_, _) => UpdatePaused();
        Loaded += OnLoaded;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        RefreshScreenButton();
        RefreshTransport();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // "--wallpaper" is used by the "start with Windows" option in the installer.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--wallpaper", StringComparison.OrdinalIgnoreCase)))
        {
            SetWallpaper(true);
            WindowState = WindowState.Minimized;
        }
    }

    // ------------------------------------------------------------ now playing

    private void OnNowPlayingChanged(object? sender, EventArgs e)
    {
        var track = _nowPlaying.Current;
        if (track is { IsPlaying: true } && _engine.Source == AudioSource.None && _settings.AutoSystemAudio)
            TryStartLoopback(showErrors: false);
        ApplyTheme();
    }

    /// <summary>Tint visuals with the album art colour while visualizing system audio.</summary>
    private void ApplyTheme()
    {
        double? hue = _engine.Source == AudioSource.Loopback ? _nowPlaying.ThemeHue : null;
        Viz.ThemeHue = hue;
        if (_wallpaper is not null) _wallpaper.ThemeHue = hue;
    }

    // ------------------------------------------------------------ transport

    private void RefreshTransport()
    {
        bool file = _engine.Source == AudioSource.File;
        Intro.Visibility = _engine.Source == AudioSource.None && _wallpaper is null ? Visibility.Visible : Visibility.Collapsed;
        LoopbackBtn.IsChecked = _engine.Source == AudioSource.Loopback;
        PlayBtn.IsEnabled = file;
        Seek.IsEnabled = file;
        PlayBtn.Content = _engine.IsPlaying && file ? "" : "";
        TrackName.Text = _engine.TrackName ?? "No audio loaded";

        var pos = _engine.Position;
        var dur = _engine.Duration;
        PositionText.Text = Format(pos);
        DurationText.Text = Format(dur);

        if (!_userSeeking)
        {
            _updatingSeek = true;
            Seek.Value = dur.TotalSeconds > 0 ? pos.TotalSeconds / dur.TotalSeconds : 0;
            _updatingSeek = false;
        }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OpenFile(string path)
    {
        try
        {
            _engine.OpenFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that file:\n\n{ex.Message}", "Music Visualizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TryStartLoopback(bool showErrors = true)
    {
        try
        {
            _engine.StartLoopback();
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show(this, $"Couldn't capture system audio:\n\n{ex.Message}", "Music Visualizer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshTransport();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = AudioFilter, Title = "Open audio file" };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    private void OnLoopbackClick(object sender, RoutedEventArgs e)
    {
        if (_engine.Source == AudioSource.Loopback)
        {
            _engine.Stop();
            _settings.AutoSystemAudio = false; // user turned it off on purpose; don't fight them
        }
        else
        {
            _settings.AutoSystemAudio = true;
            TryStartLoopback();
        }
        RefreshTransport();
    }

    private void OnIntroLoopbackClick(object sender, RoutedEventArgs e)
    {
        _settings.AutoSystemAudio = true;
        if (_engine.Source != AudioSource.Loopback) TryStartLoopback();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _engine.TogglePlay();

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e) => _userSeeking = true;

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        _userSeeking = false;
        _engine.Seek(Seek.Value);
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek) return;
        _engine.Seek(e.NewValue);
    }

    // ------------------------------------------------------------ modes

    private void SetMode(VisualMode mode)
    {
        switch (mode)
        {
            case VisualMode.Radial: ModeRadial.IsChecked = true; break;
            case VisualMode.Wave: ModeWave.IsChecked = true; break;
            default: ModeBars.IsChecked = true; break;
        }
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (Viz is null) return; // fires during InitializeComponent
        var mode = sender == ModeRadial ? VisualMode.Radial
                 : sender == ModeWave ? VisualMode.Wave
                 : VisualMode.Bars;
        Viz.Mode = mode;
        if (_wallpaper is not null) _wallpaper.Mode = mode;
        _settings.Mode = mode.ToString();
    }

    // ------------------------------------------------------------ wallpaper

    private void OnWallpaperClick(object sender, RoutedEventArgs e) => SetWallpaper(_wallpaper is null);

    private void OnScreenClick(object sender, RoutedEventArgs e) => NextScreen();

    /// <summary>Switches the wallpaper to the next monitor (wrapping around) and remembers the choice.</summary>
    private void NextScreen()
    {
        var all = Screens.All();
        if (all.Count < 2) return;
        var current = Screens.Find(_settings.WallpaperScreen);
        int index = all.ToList().FindIndex(s => s.DeviceName == current.DeviceName);
        var next = all[(index + 1) % all.Count];
        _settings.WallpaperScreen = next.DeviceName;
        RefreshScreenButton();
        MoveWallpaper();
    }

    private void MoveWallpaper()
    {
        if (_wallpaper is null) return;
        // No RestoreStaticWallpaper() here: re-applying the wallpaper makes Explorer raise
        // its WorkerW above our window. The screen we left repaints on its own.
        _wallpaper.MoveTo(Screens.Find(_settings.WallpaperScreen).Bounds);
    }

    /// <summary>The screen picker only shows with two or more monitors.</summary>
    private void RefreshScreenButton()
    {
        var all = Screens.All();
        var screen = Screens.Find(_settings.WallpaperScreen);
        ScreenBtn.Visibility = all.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScreenBtn.Content = screen.Label;
        ScreenBtn.ToolTip = $"Wallpaper shows on {screen.Label}, {screen.Bounds.Width}×{screen.Bounds.Height}. Click for the next screen (S).";
    }

    // Monitors plugged in or out, or resolution changed. Raised on a system thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { RefreshScreenButton(); MoveWallpaper(); });

    private void SetWallpaper(bool on)
    {
        if (on && _wallpaper is null)
        {
            if (_engine.Source == AudioSource.None) TryStartLoopback(showErrors: false);

            var window = new WallpaperWindow(_engine, _nowPlaying, Viz.Mode);
            try
            {
                window.ShowAsWallpaper(Screens.Find(_settings.WallpaperScreen).Bounds);
                _wallpaper = window;
            }
            catch (Exception ex)
            {
                try { window.Close(); } catch (InvalidOperationException) { }
                MessageBox.Show(this, $"Couldn't attach to the desktop:\n\n{ex.Message}", "Music Visualizer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else if (!on && _wallpaper is not null)
        {
            _wallpaper.Close();
            _wallpaper = null;
            DesktopWallpaper.RestoreStaticWallpaper();
        }

        WallpaperBtn.IsChecked = _wallpaper is not null;
        WallpaperNotice.Visibility = _wallpaper is not null ? Visibility.Visible : Visibility.Collapsed;
        ApplyTheme();
        UpdatePaused();
        RefreshTransport();
    }

    /// <summary>Don't render twice: the in-window view pauses while hidden or while the wallpaper runs.</summary>
    private void UpdatePaused() =>
        Viz.IsPaused = WindowState == WindowState.Minimized || _wallpaper is not null;

    // ------------------------------------------------------------ input & window

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            OpenFile(files[0]);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;
        switch (e.Key)
        {
            case Key.Space: _engine.TogglePlay(); break;
            case Key.D1 or Key.NumPad1: ModeBars.IsChecked = true; break;
            case Key.D2 or Key.NumPad2: ModeRadial.IsChecked = true; break;
            case Key.D3 or Key.NumPad3: ModeWave.IsChecked = true; break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.W: SetWallpaper(_wallpaper is null); break;
            case Key.S: NextScreen(); break;
            case Key.H:
                ControlBar.Visibility = ControlBar.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevStyle = WindowStyle;
            _prevResize = ResizeMode;
            WindowState = WindowState.Normal; // required so Maximized covers the taskbar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _prevStyle;
            ResizeMode = _prevResize;
            WindowState = _prevState;
        }
        _fullscreen = !_fullscreen;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_wallpaper is not null)
        {
            _wallpaper.Close();
            DesktopWallpaper.RestoreStaticWallpaper();
        }
        _timer.Stop();
        _settings.Save();
        _nowPlaying.Dispose();
        _engine.Dispose();
    }
}
