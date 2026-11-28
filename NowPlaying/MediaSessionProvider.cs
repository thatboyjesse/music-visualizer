using System.IO;
using System.Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MusicVisualizer.NowPlaying;

/// <summary>
/// Reads now-playing info from the Windows media session (the same source as the volume flyout).
/// Works with the Spotify desktop app with no sign-in, on free accounts too. Shows one app at a
/// time: the app the visualizer listens to (see <see cref="PickSession"/>, the same rules as the
/// engine's <c>pick_session</c> in native/src/nowplaying.rs).
/// </summary>
public sealed class MediaSessionProvider : INowPlayingProvider
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string? _artKey;
    private ImageSource? _art;

    /// <summary>The session last shown, so the card's buttons control the app it shows.</summary>
    private GlobalSystemMediaTransportControlsSession? _shown;

    public string Name => "Windows media";

    /// <summary>
    /// Apps to show, in order of preference (lowercase process names without ".exe"), or null for
    /// "everything on this PC". Set by the app from the Listen-to setting and the audio engine.
    /// </summary>
    public Func<IReadOnlyList<string>?>? PreferredApps { get; set; }

    private async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _shown = PickSession(_manager, PreferredApps?.Invoke());
        return _shown;
    }

    /// <summary>
    /// Chosen apps: the first app in <paramref name="prefer"/> (the most recently sounding comes
    /// first) that has a media session, preferring one that's playing; none if no chosen app has one,
    /// so the card never shows an app the visualizer isn't listening to. Everything: a playing
    /// Spotify, else the current session if playing, else any playing one, else Spotify, else current.
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager m, IReadOnlyList<string>? prefer)
    {
        var sessions = m.GetSessions().ToList();
        static bool Playing(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }
        if (prefer is not null)
        {
            foreach (var name in prefer)
            {
                var mine = sessions.Where(s => MatchesApp(s.SourceAppUserModelId, name)).ToList();
                var pick = mine.FirstOrDefault(Playing) ?? mine.FirstOrDefault();
                if (pick is not null) return pick;
            }
            return null;
        }
        var current = m.GetCurrentSession();
        var spotify = sessions.FirstOrDefault(s => IsSpotify(s.SourceAppUserModelId));
        if (spotify is not null && Playing(spotify)) return spotify;
        if (current is not null && Playing(current)) return current;
        return sessions.FirstOrDefault(Playing) ?? spotify ?? current;
    }

    /// <summary>
    /// Whether a session (by its AppUserModelID) belongs to the app with this process name (lowercase,
    /// no ".exe"). Desktop apps' IDs are their exe name (<c>Spotify.exe</c>) or a name of their own
    /// (<c>Chrome</c>, <c>MSEdge</c>); Store apps' are <c>Publisher.App_hash!App</c>. Firefox uses a
    /// hash, so it can't be matched.
    /// </summary>
    public static bool MatchesApp(string? aumid, string name)
    {
        string a = (aumid ?? "").ToLowerInvariant().Split('!')[0];
        if (name == "spotify" && a.Contains("spotify")) return true;
        string b = a.EndsWith(".exe") ? a[..^4] : a;
        return b == name || b.Split('.', '_')[0] == name;
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

    // The buttons act on the app the card shows (falling back to picking one if nothing was shown yet).
    public async Task PlayPauseAsync()
    {
        var s = _shown ?? await GetSessionAsync();
        if (s is not null) await s.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        var s = _shown ?? await GetSessionAsync();
        if (s is not null) await s.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        var s = _shown ?? await GetSessionAsync();
        if (s is not null) await s.TrySkipPreviousAsync();
    }

    public void Dispose() { }
}
