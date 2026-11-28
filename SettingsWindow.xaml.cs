using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicVisualizer.Audio;
using MusicVisualizer.Core;
using MusicVisualizer.Settings;
using MusicVisualizer.Visualizer;
using MusicVisualizer.Wallpaper;

namespace MusicVisualizer;

/// <summary>
/// Settings and first-run setup: a live preview and the main "Run as wallpaper" button on the
/// left, and Appearance / Audio / General tabs on the right. Every change applies at once.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] Presets =
    {
        "#7A5CFF", "#5B4BFF", "#2E7BFF", "#2EC4FF", "#3FD0C9", "#00E58F",
        "#B6FF3B", "#FFD23F", "#FF8A3D", "#FF4D6D", "#FF3BD4", "#FFFFFF",
    };

    private static readonly (VisualMode Mode, string Tip)[] Modes =
    {
        (VisualMode.Bars, "Classic spectrum bars"),
        (VisualMode.Mirror, "Bars growing up and down from a centre line"),
        (VisualMode.Blocks, "LED-meter blocks"),
        (VisualMode.Spectrum, "A smooth filled curve"),
        (VisualMode.Radial, "Bars around a pulsing circle"),
        (VisualMode.Ring, "The waveform bent into a circle"),
        (VisualMode.Wave, "The raw waveform, like an oscilloscope"),
    };

    private static readonly (TitlePosition Position, string Label)[] Titles =
    {
        (TitlePosition.BottomRight, "Bottom right"), (TitlePosition.BottomCenter, "Bottom center"), (TitlePosition.BottomLeft, "Bottom left"),
        (TitlePosition.TopRight, "Top right"), (TitlePosition.TopCenter, "Top center"), (TitlePosition.TopLeft, "Top left"),
        (TitlePosition.Hidden, "Hidden"),
    };

    /// <summary>Choices for "change every" when cycling by time, in seconds.</summary>
    private static readonly int[] CycleTimes = { 15, 30, 60, 120, 180, 300, 600, 900, 1800, 3600 };

    private readonly AppController _app;
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Appearance _look;
    private TitlePosition _cardBeforeHiding = TitlePosition.BottomRight; // restored when "Hide the song title card" is unticked
    private bool _syncing = true; // controls raise change events while the window is built; ignore them

    public SettingsWindow(AppController app)
    {
        _app = app;
        _look = app.Settings.Look.Clone();
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height;

        // WPF shows an .ico's smallest frame by default, which looks blurry when scaled up.
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Logo.Source = icon.Frames.OrderByDescending(f => f.PixelWidth).First();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        Welcome.Visibility = app.Settings.SetupDone ? Visibility.Collapsed : Visibility.Visible;

        BuildPresets(Color1Presets, 1);
        BuildPresets(Color2Presets, 2);

        foreach (var (mode, tip) in Modes)
        {
            var b = new RadioButton { Content = mode.ToString(), GroupName = "Mode", Tag = mode, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6) };
            b.Checked += (_, _) => { if (!_syncing) _app.SetMode(mode); };
            ModeButtons.Children.Add(b);
        }
        foreach (var (place, label) in new[] { ("Below", "Below the cover"), ("Above", "Above the cover"), ("Left", "Left of the cover"), ("Right", "Right of the cover"), ("Hidden", "Hidden") })
        {
            var b = new RadioButton { Content = label, GroupName = "Caption", Tag = place, Margin = new Thickness(0, 0, 6, 6) };
            b.Checked += (_, _) =>
            {
                if (_syncing) return;
                _look.AlbumCaption = place != "Hidden";
                if (place != "Hidden") _look.CaptionPlace = Enum.Parse<CaptionPlace>(place);
                Apply();
            };
            CaptionButtons.Children.Add(b);
        }
        foreach (var (mode, _) in Modes)
        {
            var box = new CheckBox { Content = mode.ToString(), Tag = mode, Margin = new Thickness(0, 0, 18, 8) };
            box.Checked += OnCycleChanged;
            box.Unchecked += OnCycleChanged;
            CycleModeBoxes.Children.Add(box);
        }
        foreach (var (position, label) in Titles)
        {
            var b = new RadioButton { Content = label, GroupName = "Title", Tag = position, Margin = new Thickness(0, 0, 6, 6) };
            b.Checked += (_, _) => { if (_syncing) return; _look.Title = position; Apply(); };
            TitleButtons.Children.Add(b);
        }

        Preview.Attach(app.Engine);
        Preview.Bind(app.NowPlaying);
        PreviewCard.ShowControls = false;
        PreviewCard.Scale = 1.25; // same as on the wallpaper
        PreviewCard.Bind(app.NowPlaying);
        StateChanged += (_, _) => Preview.IsPaused = WindowState == WindowState.Minimized;

        _app.Changed += OnAppChanged;
        _app.AppearanceChanged += OnAppearanceChanged;
        _app.LookSwitched += OnLookSwitched;
        Closed += (_, _) =>
        {
            _app.LookSwitched -= OnLookSwitched;
            _app.Changed -= OnAppChanged;
            _app.AppearanceChanged -= OnAppearanceChanged;
            _errorTimer.Stop();
        };
        _errorTimer.Tick += (_, _) => { ShowCaptureError(); Preview.AlbumColors = _app.AlbumColors; };
        _errorTimer.Start();

        _syncing = false;
        Sync();
        SyncLook();
        _ = RefreshAppsAsync();
    }

    private void OnAppChanged(object? sender, EventArgs e) => Sync();

    /// <summary>Another style was picked (here, in the visualizer window, or by the cycle): show its own settings.</summary>
    private void OnLookSwitched(object? sender, EventArgs e)
    {
        // A slider still being dragged would carry on editing the new style with the old one's value.
        if (Mouse.Captured is System.Windows.Controls.Primitives.Thumb) Mouse.Capture(null);
        _look = _app.Settings.Look.Clone();
        SyncLook();
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        _look = _app.Settings.Look.Clone();
        Preview.Look = _app.Settings.Look;
        PreviewCard.Place(_app.Settings.Look.Title, _app.Settings.Look.PeekCard);
    }

    /// <summary>Wallpaper state, screens, mode, listen mode and startup.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            bool wallpaper = _app.WallpaperRunning;
            var screen = _app.WallpaperScreen;
            RunBtn.Content = wallpaper ? "■  Stop wallpaper" : "▶  Run as wallpaper";
            RunBtn.Background = new SolidColorBrush(wallpaper ? Color.FromRgb(0x26, 0x26, 0x36) : Color.FromRgb(0x5B, 0x4B, 0xFF));
            RunHint.Text = wallpaper
                ? "It's running behind your desktop icons. Close this window to see it."
                : "Puts the visualizer behind your desktop icons and closes this window.";
            StatusText.Text = wallpaper ? $"Wallpaper on {screen.Label}" : "Running in the system tray";

            // The preview is the wallpaper screen at full size, scaled down, so everything sits where it will on the desktop.
            PreviewSurface.Width = Math.Max(1, screen.Bounds.Width);
            PreviewSurface.Height = Math.Max(1, screen.Bounds.Height);
            PreviewFrame.Width = 350;
            PreviewFrame.Height = 350.0 * screen.Bounds.Height / Math.Max(1, screen.Bounds.Width) + 2;

            var screens = Screens.All();
            ScreenRow.Visibility = screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            ScreenButtons.Children.Clear();
            foreach (var s in screens)
            {
                var button = new RadioButton
                {
                    Content = $"{s.Label}  ·  {s.Bounds.Width}×{s.Bounds.Height}",
                    GroupName = "Screen",
                    IsChecked = s.DeviceName == screen.DeviceName,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                button.Checked += (_, _) => { if (!_syncing) _app.SetScreen(s.DeviceName); };
                ScreenButtons.Children.Add(button);
            }

            Preview.Mode = _app.Mode;
            Preview.AlbumColors = _app.AlbumColors;
            foreach (RadioButton b in ModeButtons.Children) b.IsChecked = (VisualMode)b.Tag == _app.Mode;
            bool bars = _app.Mode is not (VisualMode.Wave or VisualMode.Ring);
            PeaksBox.IsEnabled = ReflectionBox.IsEnabled = BarWidthSlider.IsEnabled = BarCountSlider.IsEnabled = bars;

            var cs = _app.Settings;
            CycleBox.IsChecked = cs.CycleEnabled;
            CycleOptions.Visibility = cs.CycleEnabled ? Visibility.Visible : Visibility.Collapsed;
            foreach (CheckBox b in CycleModeBoxes.Children) b.IsChecked = cs.CycleModes.Contains(b.Tag.ToString()!);
            CycleByTime.IsChecked = cs.CycleBy == CycleBy.Time;
            CycleBySongs.IsChecked = cs.CycleBy == CycleBy.Songs;
            CycleShuffleBox.IsChecked = cs.CycleShuffle;
            if (cs.CycleBy == CycleBy.Time)
            {
                CycleSlider.Maximum = CycleTimes.Length - 1;
                int i = Array.FindIndex(CycleTimes, t => t >= cs.CycleSeconds);
                CycleSlider.Value = i < 0 ? CycleTimes.Length - 1 : i;
            }
            else
            {
                CycleSlider.Maximum = 9;
                CycleSlider.Value = Math.Clamp(cs.CycleSongs, 1, 10) - 1;
            }
            UpdateCycleText();
            CycleHint.Text = cs.CycleModes.Count < 2 ? "Tick at least two styles to switch between." : "";
            CycleHint.Visibility = cs.CycleModes.Count < 2 ? Visibility.Visible : Visibility.Collapsed;

            IdleBox.IsChecked = _app.Settings.HideWhenSilent;
            IdleSlider.Value = _app.Settings.SilentSeconds;
            IdleText.Text = $"{_app.Settings.SilentSeconds:0} s";
            IdleDelayRow.IsEnabled = _app.Settings.HideWhenSilent;

            bool apps = _app.Settings.Listen == ListenMode.SelectedApps;
            ListenAll.IsChecked = !apps;
            ListenApps.IsChecked = apps;
            AppsPanel.IsEnabled = apps;
            AppsPanel.Opacity = apps ? 1 : 0.5;

            StartupBox.IsChecked = StartWithWindows.IsEnabled;
            CoveredBox.IsChecked = _app.Settings.PauseWhenCovered;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Appearance controls from <see cref="_look"/>.</summary>
    private void SyncLook()
    {
        _syncing = true;
        try
        {
            Preview.Look = _app.Settings.Look;
            PreviewCard.Place(_look.Title, _look.PeekCard);
            foreach (RadioButton b in TitleButtons.Children) b.IsChecked = (TitlePosition)b.Tag == _look.Title;
            AlbumBox.IsChecked = _look.AlbumArt;
            AlbumOptions.Visibility = _look.AlbumArt ? Visibility.Visible : Visibility.Collapsed;
            AlbumSizeSlider.Value = _look.AlbumSize;
            AlbumBounceSlider.Value = _look.AlbumBounce;
            AlbumSharpnessSlider.Value = _look.AlbumSharpness;
            string caption = _look.AlbumCaption ? _look.CaptionPlace.ToString() : "Hidden";
            foreach (RadioButton b in CaptionButtons.Children) b.IsChecked = (string)b.Tag == caption;
            PeekBox.IsChecked = _look.PeekCard;
            PeekBox.IsEnabled = _look.Title == TitlePosition.Hidden;
            HideCardBox.IsChecked = _look.Title == TitlePosition.Hidden;
            foreach (var b in new[] { Fps15, Fps24, Fps30, Fps60 })
                b.IsChecked = int.Parse((string)b.Tag) == _look.FrameRate;
            BarCountSlider.Value = _look.BarCount;
            BarCountText.Text = _look.BarCount.ToString();
            WidthSlider.Value = _look.Width;
            EqBassSlider.Value = _look.EqBass;
            EqLowMidsSlider.Value = _look.EqLowMids;
            EqHighMidsSlider.Value = _look.EqHighMids;
            EqTrebleSlider.Value = _look.EqTreble;
            PeaksBox.IsChecked = _look.PeakCaps;
            ReflectionBox.IsChecked = _look.Reflection;
            BarWidthSlider.Value = _look.BarWidth;
            PosXSlider.Value = _look.PositionX;
            PosYSlider.Value = _look.PositionY;
            SizeSlider.Value = _look.Size;
            IntensitySlider.Value = Math.Log2(_look.Intensity);
            GlowBox.IsChecked = _look.Glow;
            GlowSlider.Value = _look.GlowStrength;
            GlowSlider.IsEnabled = _look.Glow;
            DimSlider.Value = _look.BackgroundDim;
            DimRow.Visibility = RemovePictureBtn.Visibility =
                _look.BackgroundImage is null ? Visibility.Collapsed : Visibility.Visible;

            ColorArt.IsChecked = _look.Colors == ColorMode.AlbumArt;
            ColorRainbow.IsChecked = _look.Colors == ColorMode.Rainbow;
            ColorCustom.IsChecked = _look.Colors == ColorMode.Custom;
            CustomColors.Visibility = _look.Colors == ColorMode.Custom ? Visibility.Visible : Visibility.Collapsed;
            ColorHint.Text = _look.Colors switch
            {
                ColorMode.AlbumArt => "Uses the two main colors of the album cover that's playing, for every song (black-and-white covers give greys and white). Keeps the last song's colors when the music stops.",
                ColorMode.Rainbow => "Cycles through the rainbow, faster when the bass hits.",
                _ => "A gradient from the first color to the second, across the spectrum.",
            };
            Color1Box.Text = _look.Color1;
            Color2Box.Text = _look.Color2;
            Color1Swatch.Background = new SolidColorBrush(VisualizerCanvas.Parse(_look.Color1, Colors.Gray));
            Color2Swatch.Background = new SolidColorBrush(VisualizerCanvas.Parse(_look.Color2, Colors.Gray));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Apply()
    {
        _app.SetAppearance(_look);
        SyncLook();
    }

    // ------------------------------------------------------------ main action

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_app.WallpaperRunning) _app.SetWallpaper(false, showErrors: true);
        else _app.RunAsWallpaper();
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (LookPage is null) return; // during InitializeComponent
        LookPage.Visibility = TabLook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AudioPage.Visibility = TabAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------ appearance

    private void OnLookChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _look.PeakCaps = PeaksBox.IsChecked == true;
        _look.Reflection = ReflectionBox.IsChecked == true;
        _look.Glow = GlowBox.IsChecked == true;
        Apply();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _app is null) return;
        if (sender == BarWidthSlider) _look.BarWidth = e.NewValue;
        else if (sender == PosXSlider) _look.PositionX = e.NewValue;
        else if (sender == PosYSlider) _look.PositionY = e.NewValue;
        else if (sender == SizeSlider) _look.Size = e.NewValue;
        else if (sender == IntensitySlider) _look.Intensity = Math.Pow(2, e.NewValue); // slider is log2: −2..2 = ¼×..4×
        else if (sender == GlowSlider) _look.GlowStrength = e.NewValue;
        else if (sender == DimSlider) _look.BackgroundDim = e.NewValue;
        else if (sender == WidthSlider) _look.Width = e.NewValue;
        else if (sender == AlbumSizeSlider) _look.AlbumSize = e.NewValue;
        else if (sender == AlbumBounceSlider) _look.AlbumBounce = e.NewValue;
        else if (sender == AlbumSharpnessSlider) _look.AlbumSharpness = e.NewValue;
        else if (sender == EqBassSlider) _look.EqBass = e.NewValue;
        else if (sender == EqLowMidsSlider) _look.EqLowMids = e.NewValue;
        else if (sender == EqHighMidsSlider) _look.EqHighMids = e.NewValue;
        else if (sender == EqTrebleSlider) _look.EqTreble = e.NewValue;
        else if (sender == BarCountSlider)
        {
            _look.BarCount = (int)e.NewValue;
            BarCountText.Text = _look.BarCount.ToString();
        }
        _app.SetAppearance(_look); // no SyncLook(): don't fight the slider being dragged
    }

    private void OnCenterClick(object sender, RoutedEventArgs e)
    {
        _look.PositionX = 0.5;
        _look.PositionY = 0.5;
        Apply();
    }

    private void OnAlbumChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _look.AlbumArt = AlbumBox.IsChecked == true;
        _look.PeekCard = PeekBox.IsChecked == true;
        if (sender == HideCardBox)
        {
            if (HideCardBox.IsChecked == true)
            {
                if (_look.Title != TitlePosition.Hidden) _cardBeforeHiding = _look.Title;
                _look.Title = TitlePosition.Hidden;
            }
            else if (_look.Title == TitlePosition.Hidden)
            {
                _look.Title = _cardBeforeHiding;
            }
        }
        Apply();
    }

    private void UpdateCycleText()
    {
        var s = _app.Settings;
        if (s.CycleBy == CycleBy.Songs)
            CycleText.Text = s.CycleSongs == 1 ? "song" : $"{s.CycleSongs} songs";
        else
            CycleText.Text = s.CycleSeconds < 60 ? $"{s.CycleSeconds} s"
                : s.CycleSeconds < 3600 ? $"{s.CycleSeconds / 60} min" : "1 hour";
    }

    /// <summary>Any cycle control changed: read them all and hand them to the controller.</summary>
    private void OnCycleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        var s = _app.Settings;
        var modes = CycleModeBoxes.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (VisualMode)b.Tag);
        bool byChanged = (CycleBySongs.IsChecked == true) != (s.CycleBy == CycleBy.Songs);
        _app.SetCycle(CycleBox.IsChecked == true, modes, CycleBySongs.IsChecked == true ? CycleBy.Songs : CycleBy.Time,
            s.CycleSeconds, s.CycleSongs, CycleShuffleBox.IsChecked == true);
        if (byChanged || sender == CycleBox || sender is CheckBox) Sync(); // slider range / hint / visibility
    }

    private void OnCycleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _app is null) return;
        var s = _app.Settings;
        int i = (int)Math.Round(e.NewValue);
        int seconds = s.CycleBy == CycleBy.Time ? CycleTimes[Math.Clamp(i, 0, CycleTimes.Length - 1)] : s.CycleSeconds;
        int songs = s.CycleBy == CycleBy.Songs ? i + 1 : s.CycleSongs;
        _app.SetCycle(s.CycleEnabled, CycleList(), s.CycleBy, seconds, songs, s.CycleShuffle);
        UpdateCycleText();
    }

    private IEnumerable<VisualMode> CycleList() =>
        _app.Settings.CycleModes.Select(n => Enum.Parse<VisualMode>(n));

    private void OnFpsChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _look.FrameRate = int.Parse((string)((RadioButton)sender).Tag);
        Apply();
    }

    private void OnCoveredChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _app.SetPauseWhenCovered(CoveredBox.IsChecked == true);
    }

    private void OnResetEq(object sender, RoutedEventArgs e)
    {
        _look.EqBass = _look.EqLowMids = _look.EqHighMids = _look.EqTreble = 0;
        Apply();
    }

    private void OnIdleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _app.SetHideWhenSilent(IdleBox.IsChecked == true, _app.Settings.SilentSeconds);
        Sync();
    }

    private void OnIdleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _app is null) return;
        _app.SetHideWhenSilent(_app.Settings.HideWhenSilent, e.NewValue);
        IdleText.Text = $"{e.NewValue:0} s";
    }

    private void OnResetLook(object sender, RoutedEventArgs e)
    {
        string? picture = _look.BackgroundImage;
        _look = new Appearance { BackgroundImage = picture }; // keep the picture; there's a Remove button for it
        Apply();
    }

    private void OnColorModeChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _app is null) return;
        _look.Colors = sender == ColorRainbow ? ColorMode.Rainbow : sender == ColorCustom ? ColorMode.Custom : ColorMode.AlbumArt;
        Apply();
    }

    private void BuildPresets(Panel panel, int slot)
    {
        foreach (string hex in Presets)
        {
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(VisualizerCanvas.Parse(hex, Colors.Gray)),
                ToolTip = hex,
            };
            swatch.Click += (_, _) =>
            {
                if (slot == 1) _look.Color1 = hex; else _look.Color2 = hex;
                Apply();
            };
            panel.Children.Add(swatch);
        }
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnHexCommitted(sender, e);
    }

    /// <summary>Accepts "#RRGGBB" or "RRGGBB"; anything else is put back to the current colour.</summary>
    private void OnHexCommitted(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        string text = box.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        bool valid = text.Length == 7 && text.Skip(1).All(Uri.IsHexDigit);
        if (valid)
        {
            if (box == Color1Box) _look.Color1 = text.ToUpperInvariant();
            else _look.Color2 = text.ToUpperInvariant();
        }
        Apply();
    }

    private void OnChoosePicture(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a picture for behind the visualizer",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.jfif;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) SetPicture(dlg.FileName);
    }

    private void OnUseDesktopWallpaper(object sender, RoutedEventArgs e)
    {
        var path = AppController.CurrentDesktopWallpaper();
        if (path is null)
        {
            MessageBox.Show(this, "Your desktop background isn't a picture file (it may be a solid color or Windows Spotlight). Choose a picture instead.",
                "Music Visualizer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetPicture(path);
    }

    private void OnRemovePicture(object sender, RoutedEventArgs e) => SetPicture(null);

    private void SetPicture(string? path)
    {
        try
        {
            _app.SetBackgroundImage(path);
        }
        catch (Exception ex)
        {
            _app.ShowError($"Couldn't use that picture:\n\n{ex.Message}");
        }
        _look = _app.Settings.Look.Clone();
        SyncLook();
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
        var rows = found.Select(a => (a.Name, Label: a.DisplayName, Detail: $"{a.Name}.exe")).ToList();
        foreach (var name in chosen.Where(n => !found.Any(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            rows.Add((name, name, $"{name}.exe · not running"));

        AppList.Children.Clear();
        foreach (var (name, label, detail) in rows.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var content = new TextBlock();
            content.Inlines.Add(new System.Windows.Documents.Run(label));
            content.Inlines.Add(new System.Windows.Documents.Run("   " + detail) { Foreground = Brushes.Gray, FontSize = 11 });
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

    private void OnQuit(object sender, RoutedEventArgs e) => _app.Stop();
}
