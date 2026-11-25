using System;
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
        public const string ExeName = "MusicVisualizer.exe";

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

        // Command-line switches.
        public const string BackgroundArg = "--background";
        public const string SettingsArg = "--settings";
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

        /// <summary>Settings folder, %AppData%\MusicVisualizer.</summary>
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicVisualizer");

        /// <summary>The command stored in the Run key: start hidden in the tray.</summary>
        public static string RunCommand(string exePath) => "\"" + exePath + "\" " + BackgroundArg;
    }
}
