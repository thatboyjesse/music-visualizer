using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicVisualizer.NowPlaying;

namespace MusicVisualizer.Controls;

/// <summary>Album art, title/artist, a progress line and (optionally) transport buttons.</summary>
public partial class NowPlayingCard : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private NowPlayingService? _service;
    private ImageSource? _shownArt;
    private bool _hidden;

    // "Peek": while the card is hidden, a new song shows it in the bottom-right for a few seconds.
    private bool _peekOnNewSong;
    private bool _peeking;
    private string? _songKey;
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    public NowPlayingCard()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateProgress();
        _peekTimer.Tick += (_, _) => EndPeek();
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

    /// <summary>
    /// Places the card in a corner or edge of its parent (the wallpaper uses this for the
    /// "Song title" setting). Bottom positions leave room for the taskbar.
    /// </summary>
    /// <param name="peekOnNewSong">While hidden, briefly show the card in the bottom-right when a new song starts.</param>
    public void Place(Settings.TitlePosition position, bool peekOnNewSong = false)
    {
        _hidden = position == Settings.TitlePosition.Hidden;
        _peekOnNewSong = peekOnNewSong;
        if (!_hidden && _peeking) StopPeek();
        if (_hidden) position = Settings.TitlePosition.BottomRight; // where a peek appears
        string p = position.ToString();
        VerticalAlignment = p.StartsWith("Top") ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        HorizontalAlignment = p.EndsWith("Left") ? HorizontalAlignment.Left
                            : p.EndsWith("Center") ? HorizontalAlignment.Center
                            : HorizontalAlignment.Right;
        Margin = VerticalAlignment == VerticalAlignment.Top ? new Thickness(56, 56, 56, 0) : new Thickness(56, 0, 56, 88);
        Refresh();
    }

    private void StartPeek()
    {
        _peeking = true;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4)));
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void EndPeek()
    {
        _peekTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.6));
        fade.Completed += (_, _) => { if (_peeking) StopPeek(); };
        BeginAnimation(OpacityProperty, fade);
    }

    private void StopPeek()
    {
        _peeking = false;
        _peekTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Refresh();
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

        // A different song (not the first one seen) while hidden: peek.
        string? key = t is null ? null : $"{t.Title}|{t.Artist}";
        if (key is not null && key != _songKey)
        {
            if (_songKey is not null && _hidden && _peekOnNewSong) StartPeek();
            _songKey = key;
        }

        Visibility = t is null || (_hidden && !_peeking) ? Visibility.Collapsed : Visibility.Visible;
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
