using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicVisualizer.Audio;
using MusicVisualizer.Core;
using MusicVisualizer.Settings;
using MusicVisualizer.Visualizer;
using MusicVisualizer.Wallpaper;

namespace MusicVisualizer;

/// <summary>
/// Settings and first-run setup. Opened from the tray icon, the Start menu (a second launch
/// signals the running copy), or the gear in the visualizer window. Every change applies at once.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppController _app;
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _syncing;

    public SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height - 24; // scrolls on small screens

        // WPF shows an .ico's smallest frame by default, which looks blurry when scaled up.
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Logo.Source = icon.Frames.OrderByDescending(f => f.PixelWidth).First();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        Welcome.Visibility = app.Settings.SetupDone ? Visibility.Collapsed : Visibility.Visible;

        _app.Changed += OnAppChanged;
        Closed += (_, _) => { _app.Changed -= OnAppChanged; _errorTimer.Stop(); };
        _errorTimer.Tick += (_, _) => ShowCaptureError();
        _errorTimer.Start();

        Sync();
        _ = RefreshAppsAsync();
    }

    private void OnAppChanged(object? sender, EventArgs e) => Sync();

    /// <summary>Makes every control match the current settings.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            bool wallpaper = _app.WallpaperRunning;
            WallpaperToggle.IsChecked = wallpaper;
            WallpaperToggle.Content = wallpaper ? "On" : "Off";
            StatusText.Text = wallpaper
                ? $"Running in the system tray · wallpaper on {_app.WallpaperScreen.Label}"
                : "Running in the system tray";

            var screens = Screens.All();
            ScreenRow.Visibility = screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            ScreenButtons.Children.Clear();
            string current = _app.WallpaperScreen.DeviceName;
            foreach (var s in screens)
            {
                var button = new RadioButton
                {
                    Content = $"{s.Label}  ·  {s.Bounds.Width}×{s.Bounds.Height}",
                    GroupName = "Screen",
                    IsChecked = s.DeviceName == current,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                button.Checked += (_, _) => { if (!_syncing) _app.SetScreen(s.DeviceName); };
                ScreenButtons.Children.Add(button);
            }

            switch (_app.Mode)
            {
                case VisualMode.Radial: ModeRadial.IsChecked = true; break;
                case VisualMode.Wave: ModeWave.IsChecked = true; break;
                default: ModeBars.IsChecked = true; break;
            }

            bool apps = _app.Settings.Listen == ListenMode.SelectedApps;
            ListenAll.IsChecked = !apps;
            ListenApps.IsChecked = apps;
            AppsPanel.IsEnabled = apps;
            AppsPanel.Opacity = apps ? 1 : 0.5;

            StartupBox.IsChecked = StartWithWindows.IsEnabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ------------------------------------------------------------ wallpaper & style

    private void OnWallpaperToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _app.SetWallpaper(WallpaperToggle.IsChecked == true, showErrors: true);
        Sync(); // the attach may have failed
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _app.SetMode(sender == ModeRadial ? VisualMode.Radial : sender == ModeWave ? VisualMode.Wave : VisualMode.Bars);
    }

    // ------------------------------------------------------------ listen to

    private void OnListenChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _app.SetListen(sender == ListenApps ? ListenMode.SelectedApps : ListenMode.AllAudio, _app.Settings.Apps);
    }

    private void OnRefreshApps(object sender, RoutedEventArgs e) => _ = RefreshAppsAsync();

    /// <summary>Lists apps that have audio sessions, plus any chosen earlier that aren't running now.</summary>
    private async Task RefreshAppsAsync()
    {
        List<AudioApp> found;
        try
        {
            found = await Task.Run(AudioApps.WithAudioSessions);
        }
        catch (Exception ex)
        {
            found = new List<AudioApp>();
            AppsError.Text = $"Couldn't list apps: {ex.Message}";
            AppsError.Visibility = Visibility.Visible;
        }

        var chosen = new HashSet<string>(_app.Settings.Apps, StringComparer.OrdinalIgnoreCase);
        var rows = found.Select(a => (a.Name, Label: $"{a.DisplayName}", Detail: $"{a.Name}.exe")).ToList();
        foreach (var name in chosen.Where(n => !found.Any(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            rows.Add((name, name, $"{name}.exe · not running"));

        AppList.Children.Clear();
        foreach (var (name, label, detail) in rows.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var content = new TextBlock();
            content.Inlines.Add(new System.Windows.Documents.Run(label));
            content.Inlines.Add(new System.Windows.Documents.Run("   " + detail) { Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11 });
            var box = new CheckBox { Content = content, Tag = name, IsChecked = chosen.Contains(name) };
            box.Checked += OnAppToggled;
            box.Unchecked += OnAppToggled;
            AppList.Children.Add(box);
        }
        AppsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppToggled(object sender, RoutedEventArgs e)
    {
        var names = AppList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag);
        _app.SetListen(ListenMode.SelectedApps, names);
    }

    private void ShowCaptureError()
    {
        string? error = _app.Engine.Source == AudioSource.Apps ? _app.Engine.AppCaptureError : null;
        if (error is null && AppsError.Text.StartsWith("Couldn't list")) return;
        AppsError.Text = error is null ? "" : $"Couldn't capture an app: {error} (needs Windows 11 or Windows 10 build 20348+)";
        AppsError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------ general

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        try
        {
            StartWithWindows.Set(StartupBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            _app.ShowError($"Couldn't change the startup setting:\n\n{ex.Message}");
        }
        Sync();
    }

    private void OnOpenVisualizer(object sender, RoutedEventArgs e) => _app.ShowVisualizer();

    private void OnQuit(object sender, RoutedEventArgs e) => _app.Quit();
}
