using System.IO;
using System.Text.Json;
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
    /// </summary>
    public static AppSettings Load()
    {
        foreach (var path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) is { } settings)
                    return settings;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Damaged or unreadable: try the next one.
            }
        }
        return new AppSettings();
    }

    /// <summary>
    /// Saves without ever leaving a half-written file: the JSON goes to a temporary file first,
    /// which then replaces settings.json in one step, keeping the previous version as settings.json.bak.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
            else File.Move(temp, FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never crash over them. The next save tries again.
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
