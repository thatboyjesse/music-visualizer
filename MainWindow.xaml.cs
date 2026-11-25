using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicVisualizer.Audio;
using MusicVisualizer.Core;
using MusicVisualizer.Visualizer;
using MusicVisualizer.Wallpaper;

namespace MusicVisualizer;

/// <summary>
/// The optional visualizer window (tray → Open visualizer). Audio, settings and the wallpaper
/// belong to <see cref="AppController"/>; closing this window leaves the app running.
/// </summary>
public partial class MainWindow : Window
{
    private const string AudioFilter =
        "Audio files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif|All files|*.*";

    private readonly AppController _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _updatingSeek;
    private bool _userSeeking;

    private bool _fullscreen;
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;

    private AudioEngine Engine => _app.Engine;

    public MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Viz.Attach(Engine);
        Viz.Bind(_app.NowPlaying);

        Card.ShowControls = true;
        Card.Bind(_app.NowPlaying);

        _app.Changed += OnAppChanged;
        _app.AppearanceChanged += OnAppearanceChanged;
        Viz.Look = _app.Settings.Look;
        _timer.Tick += (_, _) => RefreshTransport();
        _timer.Start();

        StateChanged += (_, _) => UpdatePaused();
        OnAppChanged(this, EventArgs.Empty);
    }

    private void OnAppChanged(object? sender, EventArgs e)
    {
        ModeBtn.Content = $"Style: {_app.Mode}";
        Viz.Mode = _app.Mode;

        bool wallpaper = _app.WallpaperRunning;
        WallpaperBtn.IsChecked = wallpaper;
        WallpaperNotice.Visibility = wallpaper ? Visibility.Visible : Visibility.Collapsed;

        var screens = Screens.All();
        ScreenBtn.Visibility = screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var screen = _app.WallpaperScreen;
        ScreenBtn.Content = screen.Label;
        ScreenBtn.ToolTip = $"Wallpaper shows on {screen.Label}, {screen.Bounds.Width}×{screen.Bounds.Height}. Click for the next screen (S).";

        ApplyTheme();
        UpdatePaused();
        RefreshTransport();
    }

    public void ApplyTheme() => Viz.AlbumColors = _app.AlbumColors;

    private void OnAppearanceChanged(object? sender, EventArgs e) => Viz.Look = _app.Settings.Look;

    // ------------------------------------------------------------ transport

    private void RefreshTransport()
    {
        bool file = Engine.Source == AudioSource.File;
        bool live = Engine.Source is AudioSource.Loopback or AudioSource.Apps;
        Intro.Visibility = Engine.Source == AudioSource.None && !_app.WallpaperRunning ? Visibility.Visible : Visibility.Collapsed;
        LoopbackBtn.IsChecked = live;
        PlayBtn.IsEnabled = file;
        Seek.IsEnabled = file;
        PlayBtn.Content = Engine.IsPlaying && file ? "" : "";
        TrackName.Text = Engine.Source switch
        {
            AudioSource.Apps => $"Listening to {string.Join(", ", _app.Settings.Apps)}",
            _ => Engine.TrackName ?? "No audio loaded",
        };

        var pos = Engine.Position;
        var dur = Engine.Duration;
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
            Engine.OpenFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that file:\n\n{ex.Message}", "Music Visualizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = AudioFilter, Title = "Open audio file" };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    private void OnLoopbackClick(object sender, RoutedEventArgs e)
    {
        if (Engine.Source is AudioSource.Loopback or AudioSource.Apps)
        {
            Engine.Stop();
            _app.Settings.AutoSystemAudio = false; // user turned it off on purpose; don't fight them
        }
        else
        {
            _app.Settings.AutoSystemAudio = true;
            _app.StartLiveAudio();
        }
        _app.Settings.Save();
        RefreshTransport();
    }

    private void OnIntroLoopbackClick(object sender, RoutedEventArgs e)
    {
        _app.Settings.AutoSystemAudio = true;
        _app.Settings.Save();
        if (Engine.Source is not (AudioSource.Loopback or AudioSource.Apps)) _app.StartLiveAudio();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => Engine.TogglePlay();

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e) => _userSeeking = true;

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        _userSeeking = false;
        Engine.Seek(Seek.Value);
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek) return;
        Engine.Seek(e.NewValue);
    }

    // ------------------------------------------------------------ modes, wallpaper, settings

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        var modes = Enum.GetValues<VisualMode>();
        _app.SetMode(modes[(Array.IndexOf(modes, _app.Mode) + 1) % modes.Length]);
    }

    private void OnWallpaperClick(object sender, RoutedEventArgs e) =>
        _app.SetWallpaper(!_app.WallpaperRunning, showErrors: true);

    private void OnScreenClick(object sender, RoutedEventArgs e) => _app.NextScreen();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _app.ShowSettings();

    /// <summary>Don't render twice: the in-window view pauses while hidden or while the wallpaper runs.</summary>
    private void UpdatePaused() =>
        Viz.IsPaused = WindowState == WindowState.Minimized || _app.WallpaperRunning;

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
            case Key.Space: Engine.TogglePlay(); break;
            case >= Key.D1 and <= Key.D7: _app.SetMode((VisualMode)(e.Key - Key.D1)); break;
            case >= Key.NumPad1 and <= Key.NumPad7: _app.SetMode((VisualMode)(e.Key - Key.NumPad1)); break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.W: _app.SetWallpaper(!_app.WallpaperRunning, showErrors: true); break;
            case Key.S: _app.NextScreen(); break;
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
        _timer.Stop();
        _app.Changed -= OnAppChanged;
        _app.AppearanceChanged -= OnAppearanceChanged;
        // A file only plays while its window is open; live audio is up to the controller.
        if (Engine.Source == AudioSource.File) Engine.Stop();
    }
}
