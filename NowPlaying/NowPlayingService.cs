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

    /// <summary>Most prominent hue of the current album art, used to tint the visuals.</summary>
    public double? ThemeHue { get; private set; }

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
            if (artChanged)
                ThemeHue = track?.Art is BitmapSource bmp ? ImageUtil.DominantHue(bmp) : null;

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
