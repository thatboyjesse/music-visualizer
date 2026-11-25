using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MusicVisualizer.NowPlaying;

namespace MusicVisualizer.Controls;

/// <summary>Album art, title/artist, a progress line and (optionally) transport buttons.</summary>
public partial class NowPlayingCard : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private NowPlayingService? _service;
    private ImageSource? _shownArt;

    public NowPlayingCard()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateProgress();
        Unloaded += (_, _) => Unbind();
    }

    public bool ShowControls
    {
        get => Buttons.Visibility == Visibility.Visible;
        set => Buttons.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Scales the card up (used on the wallpaper, where it's viewed from further away).</summary>
    public double Scale
    {
        set => LayoutTransform = new ScaleTransform(value, value);
    }

    public void Bind(NowPlayingService service)
    {
        Unbind();
        _service = service;
        service.Changed += OnChanged;
        _tick.Start();
        Refresh();
    }

    private void Unbind()
    {
        if (_service is not null) _service.Changed -= OnChanged;
        _service = null;
        _tick.Stop();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var t = _service?.Current;
        Visibility = t is null ? Visibility.Collapsed : Visibility.Visible;
        if (t is null) return;

        TitleText.Text = t.Title;
        ArtistText.Text = t.Artist;
        SourceText.Text = "via " + t.SourceName;
        PlayBtn.Content = t.IsPlaying ? "" : "";

        if (!ReferenceEquals(t.Art, _shownArt))
        {
            _shownArt = t.Art;
            ArtBorder.Background = t.Art is null
                ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x36))
                : new ImageBrush(t.Art) { Stretch = Stretch.UniformToFill };
            ArtPlaceholder.Visibility = t.Art is null ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var t = _service?.Current;
        if (t is null || t.Duration <= TimeSpan.Zero)
        {
            ProgressFill.Width = 0;
            return;
        }
        double frac = t.PositionAt(DateTime.UtcNow).TotalSeconds / t.Duration.TotalSeconds;
        ProgressFill.Width = Math.Clamp(frac, 0, 1) * ProgressTrack.ActualWidth;
    }

    private async void Run(Func<NowPlayingService, Task> action)
    {
        if (_service is null) return;
        try
        {
            await action(_service);
        }
        catch (Exception ex)
        {
            SourceText.Text = ex.Message;
        }
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => Run(s => s.PlayPauseAsync());
    private void OnNext(object sender, RoutedEventArgs e) => Run(s => s.NextAsync());
    private void OnPrevious(object sender, RoutedEventArgs e) => Run(s => s.PreviousAsync());
}
