using System.IO;
using System.Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MusicVisualizer.NowPlaying;

/// <summary>
/// Reads now-playing info from the Windows media session (the same source as the
/// volume flyout). Works with the Spotify desktop app with no sign-in, on free accounts too.
/// Falls back to whichever app Windows considers current if Spotify isn't running.
/// </summary>
public sealed class MediaSessionProvider : INowPlayingProvider
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string? _artKey;
    private ImageSource? _art;

    public string Name => "Windows media";

    private async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sessions = _manager.GetSessions();
        return sessions.FirstOrDefault(s => IsSpotify(s.SourceAppUserModelId)) ?? _manager.GetCurrentSession();
    }

    private static bool IsSpotify(string? aumid) =>
        aumid?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true;

    private static string FriendlyName(string aumid)
    {
        if (IsSpotify(aumid)) return "Spotify";
        string name = aumid.Split('!')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.ToLowerInvariant() switch
        {
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "zunemusic" => "Media Player",
            _ => name,
        };
    }

    public async Task<TrackInfo?> GetCurrentAsync(CancellationToken ct = default)
    {
        var session = await GetSessionAsync();
        if (session is null) return null;

        var props = await session.TryGetMediaPropertiesAsync();
        if (props is null || string.IsNullOrWhiteSpace(props.Title)) return null;

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        string key = $"{props.Title}|{props.Artist}|{props.AlbumTitle}";
        if (key != _artKey)
        {
            _artKey = key;
            _art = await LoadArtAsync(props.Thumbnail);
        }

        // Timeline position is only updated occasionally; bring it up to "now".
        var now = DateTime.UtcNow;
        var position = timeline.Position;
        if (playing && timeline.LastUpdatedTime.Year > 2000)
            position += now - timeline.LastUpdatedTime.UtcDateTime;
        var duration = timeline.EndTime - timeline.StartTime;

        return new TrackInfo(
            props.Title,
            props.Artist ?? "",
            props.AlbumTitle ?? "",
            playing,
            position,
            duration,
            _art,
            FriendlyName(session.SourceAppUserModelId),
            now);
    }

    private static async Task<ImageSource?> LoadArtAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            return ImageUtil.Load(ms);
        }
        catch
        {
            return null;
        }
    }

    public async Task PlayPauseAsync()
    {
        var s = await GetSessionAsync();
        if (s is not null) await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        var s = await GetSessionAsync();
        if (s is not null) await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        var s = await GetSessionAsync();
        if (s is not null) await s.TrySkipPreviousAsync();
    }

    public void Dispose() { }
}
