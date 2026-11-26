using System;
using System.Collections.Generic;
using System.IO;

namespace MusicVisualizer.Shared
{
    /// <summary>
    /// Names and paths shared by the app and the installer (Setup/). This file is compiled into
    /// both projects, and the installer targets .NET Framework 4.8, so keep it to plain C# 7.3.
    /// </summary>
    public static class InstallInfo
    {
        public const string AppName = "Music Visualizer";
        /// <summary>The engine (Rust): wallpaper, audio, tray. What shortcuts, the startup entry and the Apps entry run.</summary>
        public const string ExeName = "MusicVisualizer.exe";

        /// <summary>The settings window and visualizer window (this C# app), started by the engine only while they're open.</summary>
        public const string SettingsExeName = "MusicVisualizerSettings.exe";

        /// <summary>Add/remove programs entry (per user).</summary>
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicVisualizer";

        /// <summary>Entry written by the old Inno Setup installer (1.0.0). Setup removes it on upgrade.</summary>
        public const string LegacyInnoUninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6E2B-4C7D-9A1E-3B5F7D2C9E41}_is1";

        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "MusicVisualizer";

        // Single instance and signals between processes.
        public const string InstanceMutex = @"Local\MusicVisualizer.Instance";
        public const string ShowEvent = @"Local\MusicVisualizer.Show";
        public const string QuitEvent = @"Local\MusicVisualizer.Quit";
        public const string WallpaperEvent = @"Local\MusicVisualizer.Wallpaper";
        /// <summary>The settings window's Stop button: the engine turns everything off but stays in the tray.</summary>
        public const string StopEvent = @"Local\MusicVisualizer.Stop";

        // The settings app (MusicVisualizerSettings.exe): one copy, and signals from the engine.
        public const string HelperMutex = @"Local\MusicVisualizer.Helper";
        public const string HelperSettingsEvent = @"Local\MusicVisualizer.Helper.Settings";
        public const string HelperVisualizerEvent = @"Local\MusicVisualizer.Helper.Visualizer";
        public const string HelperQuitEvent = @"Local\MusicVisualizer.Helper.Quit";

        // Command-line switches.
        public const string BackgroundArg = "--background";
        public const string SettingsArg = "--settings";
        public const string VisualizerArg = "--visualizer";
        public const string UninstallArg = "--uninstall";
        public const string QuietArg = "--quiet";

        public static string DefaultInstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        /// <summary>Second Start-menu entry that opens settings ("Music Visualizer" itself starts the wallpaper).</summary>
        public static string SettingsShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + " Settings.lnk");

        public static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        /// <summary>
        /// List of every file Setup put in the install folder (relative paths, one per line). Updates and
        /// uninstalls delete exactly these, so nothing else that happens to be in the folder is touched.
        /// </summary>
        public const string ManifestName = "install-files.txt";

        /// <summary>Settings folder, %AppData%\MusicVisualizer.</summary>
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicVisualizer");

        /// <summary>The installed files listed in <see cref="ManifestName"/> (relative paths), or an empty list.</summary>
        public static List<string> ReadManifest(string installDir)
        {
            var result = new List<string>();
            string path = Path.Combine(installDir, ManifestName);
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadAllLines(path))
            {
                string f = line.Trim();
                // Only plain relative paths inside the folder.
                if (f.Length > 0 && !Path.IsPathRooted(f) && !f.Contains("..")) result.Add(f);
            }
            return result;
        }

        /// <summary>The command stored in the Run key: start hidden in the tray.</summary>
        public static string RunCommand(string exePath) => "\"" + exePath + "\" " + BackgroundArg;
    }
}
