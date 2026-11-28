using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MusicVisualizer.NowPlaying;

/// <summary>
/// Polls the Windows media session for the current track and exposes it to the UI.
/// Runs on the UI thread (DispatcherTimer), so <see cref="Changed"/> is raised there.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private readonly MediaSessionProvider _media = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;

    public TrackInfo? Current { get; private set; }

    /// <summary>Apps to show, most preferred first, or null for everything (see MediaSessionProvider.PreferredApps).</summary>
    public Func<IReadOnlyList<string>?>? PreferredApps
    {
        get => _media.PreferredApps;
        set => _media.PreferredApps = value;
    }

    /// <summary>
    /// The two main colours of the latest album cover. Kept when playback stops or a track has
    /// no cover, so the visuals keep the last song's colours instead of switching to something else.
    /// </summary>
    public AlbumColors? AlbumColors { get; private set; }

    public event EventHandler? Changed;

    public NowPlayingService()
    {
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    public async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            TrackInfo? track;
            try { track = await _media.GetCurrentAsync(); }
            catch { track = null; } // media session gone, etc.

            bool artChanged = !ReferenceEquals(track?.Art, Current?.Art);
            Current = track;
            if (artChanged && track?.Art is BitmapSource bmp && ImageUtil.Palette(bmp) is { } colors)
                AlbumColors = colors;

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _busy = false;
        }
    }

    public Task PlayPauseAsync() => CommandAsync(p => p.PlayPauseAsync());
    public Task NextAsync() => CommandAsync(p => p.NextAsync());
    public Task PreviousAsync() => CommandAsync(p => p.PreviousAsync());

    private async Task CommandAsync(Func<INowPlayingProvider, Task> command)
    {
        await command(_media);
        await Task.Delay(350);
        await PollAsync();
    }

    public void Dispose()
    {
        _timer.Stop();
        _media.Dispose();
    }
}
