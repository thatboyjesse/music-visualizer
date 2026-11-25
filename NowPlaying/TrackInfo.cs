using System.Windows.Media;

namespace MusicVisualizer.NowPlaying;

/// <summary>A snapshot of the track that is currently playing.</summary>
/// <param name="FetchedAt">UTC time the snapshot was taken; used to extrapolate the position between polls.</param>
public sealed record TrackInfo(
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Art,
    string SourceName,
    DateTime FetchedAt)
{
    public TimeSpan PositionAt(DateTime utcNow)
    {
        var pos = IsPlaying ? Position + (utcNow - FetchedAt) : Position;
        if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }
}

/// <summary>Something that can report and control what is playing.</summary>
public interface INowPlayingProvider : IDisposable
{
    string Name { get; }
    Task<TrackInfo?> GetCurrentAsync(CancellationToken ct = default);
    Task PlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
}
