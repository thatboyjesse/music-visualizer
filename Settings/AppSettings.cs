using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Settings;

public enum ListenMode { AllAudio, SelectedApps }

/// <summary>What moves the style cycle on: elapsed time or finished songs.</summary>
public enum CycleBy { Time, Songs }

/// <summary>User preferences, stored as JSON in %AppData%\MusicVisualizer\settings.json.</summary>
public sealed class AppSettings
{
    public static string Folder => InstallInfo.DataDir;

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string Mode { get; set; } = "Bars";

    /// <summary>The current style's look: colours, position, size, intensity, glow, background picture...</summary>
    public Appearance Look { get; set; } = new();

    /// <summary>
    /// Each style's own saved look, by style name. A style that's never been used starts from a copy
    /// of whichever style was showing. See <see cref="Appearance.CopySharedFrom"/> for what's shared.
    /// </summary>
    public Dictionary<string, Appearance> Looks { get; set; } = new();

    // Cycling through styles
    public bool CycleEnabled { get; set; }
    /// <summary>Style names to cycle through.</summary>
    public List<string> CycleModes { get; set; } = new();
    public CycleBy CycleBy { get; set; } = CycleBy.Time;
    /// <summary>Change style every this many seconds (when cycling by time).</summary>
    public int CycleSeconds { get; set; } = 300;
    /// <summary>Change style every this many songs (when cycling by songs).</summary>
    public int CycleSongs { get; set; } = 1;
    public bool CycleShuffle { get; set; }

    /// <summary>Run the live wallpaper whenever the app is running.</summary>
    public bool WallpaperEnabled { get; set; }

    /// <summary>Device name of the monitor the wallpaper covers (e.g. \\.\DISPLAY2). Null means the main screen.</summary>
    public string? WallpaperScreen { get; set; }

    /// <summary>What to visualize: everything the PC plays, or only <see cref="Apps"/>.</summary>
    public ListenMode Listen { get; set; } = ListenMode.AllAudio;

    /// <summary>Process names (without ".exe") to listen to when <see cref="Listen"/> is SelectedApps.</summary>
    public List<string> Apps { get; set; } = new();

    /// <summary>Stop drawing while a maximized or full-screen window covers the wallpaper's screen.</summary>
    public bool PauseWhenCovered { get; set; } = true;

    /// <summary>Show the normal desktop wallpaper while nothing is playing (the visualizer comes back with the music).</summary>
    public bool HideWhenSilent { get; set; } = true;

    /// <summary>How long it must be quiet before switching to the normal wallpaper, in seconds.</summary>
    public double SilentSeconds { get; set; } = 5;

    /// <summary>Start capturing automatically when the visualizer window opens or a track starts playing.</summary>
    public bool AutoSystemAudio { get; set; } = true;

    /// <summary>False until the first-run welcome has been shown.</summary>
    public bool SetupDone { get; set; }

    /// <summary>The "still running in the tray" balloon is shown once.</summary>
    public bool TrayHintShown { get; set; }

    private static string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Reads settings.json, or the backup made by the previous save if the main file is missing or
    /// damaged (e.g. the PC lost power mid-write), and only then falls back to defaults.
    /// Holds the settings mutex, so it never reads while the engine is swapping the file in (when the
    /// main file is briefly missing and the backup is the previous version).
    /// </summary>
    public static AppSettings Load()
    {
        using var _ = new SettingsLock();
        foreach (var path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) is { } settings)
                {
                    settings._baseline = settings.ToNode();
                    return settings;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Damaged or unreadable: try the next one.
            }
        }
        return new AppSettings();
    }

    /// <summary>
    /// True when another program writes settings.json too (the settings app next to the engine).
    /// Then <see cref="Save"/> writes only what changed here since this copy was loaded, on top of
    /// the file as it is now, so it never puts back a style or look the engine has just changed.
    /// </summary>
    public static bool MergeOnSave { get; set; }

    /// <summary>These settings as they were loaded or last saved: what <see cref="Save"/> compares against.</summary>
    private JsonObject? _baseline;

    /// <summary>
    /// Saves without ever leaving a half-written file: the JSON goes to a temporary file first,
    /// which then replaces settings.json in one step, keeping the previous version as settings.json.bak.
    /// The named settings mutex (also taken by the engine) is held from reading to writing.
    /// </summary>
    public void Save()
    {
        using var _ = new SettingsLock();
        try
        {
            Directory.CreateDirectory(Folder);
            JsonObject current = ToNode();
            JsonObject result = MergeOnSave && _baseline is not null && ReadFile() is { } file
                ? Merge(_baseline, current, file)
                : current;
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, result.ToJsonString(Json));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
            else File.Move(temp, FilePath);
            // Compare against what this copy holds, not the merged file: values the engine changed
            // must not count as edits next time. The file watcher reloads the merged version.
            _baseline = current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Settings are a convenience; never crash over them. The next save tries again.
        }
    }

    /// <summary>The named mutex the engine also takes around reading and writing settings.json (waits at most 1 s).</summary>
    private sealed class SettingsLock : IDisposable
    {
        private readonly Mutex _mutex = new(false, InstallInfo.SettingsMutex);
        private readonly bool _locked;

        public SettingsLock()
        {
            try { _locked = _mutex.WaitOne(1000); }
            catch (AbandonedMutexException) { _locked = true; }
        }

        public void Dispose()
        {
            if (_locked) _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private static JsonObject? ReadFile()
    {
        try { return File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The file with this copy's changes (current vs baseline) applied, value by value. The current
    /// look (<c>Look</c>) belongs to a style: if the file now shows another style, only the settings
    /// shared by all styles are applied to it; the edits are already in that style's entry in <c>Looks</c>.
    /// </summary>
    private static JsonObject Merge(JsonObject baseline, JsonObject current, JsonObject file)
    {
        var result = (JsonObject)file.DeepClone();
        foreach (var (key, value) in current)
            if (key != nameof(Look)) MergeValue(result, key, baseline[key], value);

        bool sameStyle = result[nameof(Mode)]?.ToString() == current[nameof(Mode)]?.ToString();
        if (current[nameof(Look)] is JsonObject look && result[nameof(Look)] is JsonObject fileLook)
        {
            var baseLook = baseline[nameof(Look)] as JsonObject;
            foreach (var (key, value) in look)
                if (sameStyle || Appearance.SharedKeys.Contains(key)) MergeValue(fileLook, key, baseLook?[key], value);
        }
        else if (sameStyle)
        {
            result[nameof(Look)] = current[nameof(Look)]?.DeepClone();
        }
        return result;
    }

    private static void MergeValue(JsonObject target, string key, JsonNode? before, JsonNode? now)
    {
        if (JsonNode.DeepEquals(before, now)) return; // not changed here: keep the file's value
        if (before is JsonObject b && now is JsonObject n && target[key] is JsonObject t)
        {
            foreach (var (k, v) in n) MergeValue(t, k, b[k], v);
            foreach (var (k, _) in b)
                if (!n.ContainsKey(k)) t.Remove(k);
            return;
        }
        target[key] = now?.DeepClone();
    }

    private JsonObject ToNode() => (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
