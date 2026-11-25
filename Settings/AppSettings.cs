using System.IO;
using System.Text.Json;

namespace MusicVisualizer.Settings;

/// <summary>User preferences, stored as JSON in %AppData%\MusicVisualizer\settings.json.</summary>
public sealed class AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicVisualizer");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string Mode { get; set; } = "Bars";

    /// <summary>Device name of the monitor the wallpaper covers (e.g. \\.\DISPLAY2). Null means the main screen.</summary>
    public string? WallpaperScreen { get; set; }

    /// <summary>Start capturing system audio automatically when a track starts playing.</summary>
    public bool AutoSystemAudio { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
