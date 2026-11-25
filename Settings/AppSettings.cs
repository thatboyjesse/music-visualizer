using System.IO;
using System.Text.Json;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Settings;

public enum ListenMode { AllAudio, SelectedApps }

/// <summary>User preferences, stored as JSON in %AppData%\MusicVisualizer\settings.json.</summary>
public sealed class AppSettings
{
    public static string Folder => InstallInfo.DataDir;

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string Mode { get; set; } = "Bars";

    /// <summary>Colours, position, size, intensity, glow and background picture.</summary>
    public Appearance Look { get; set; } = new();

    /// <summary>Run the live wallpaper whenever the app is running.</summary>
    public bool WallpaperEnabled { get; set; }

    /// <summary>Device name of the monitor the wallpaper covers (e.g. \\.\DISPLAY2). Null means the main screen.</summary>
    public string? WallpaperScreen { get; set; }

    /// <summary>What to visualize: everything the PC plays, or only <see cref="Apps"/>.</summary>
    public ListenMode Listen { get; set; } = ListenMode.AllAudio;

    /// <summary>Process names (without ".exe") to listen to when <see cref="Listen"/> is SelectedApps.</summary>
    public List<string> Apps { get; set; } = new();

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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
