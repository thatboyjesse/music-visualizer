using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Core;

/// <summary>
/// Runs for "MusicVisualizer.exe --uninstall", which is what Settings → Apps → Uninstall calls.
/// The app removes itself, so the installer doesn't need to leave a separate uninstaller behind.
/// </summary>
public static class Uninstaller
{
    public static void Run(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove Music Visualizer and its settings from this PC?", InstallInfo.AppName,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        StopRunningInstance();

        string? installDir;
        using (var key = Registry.CurrentUser.OpenSubKey(InstallInfo.UninstallKey))
            installDir = key?.GetValue("InstallLocation") as string;

        using (var run = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey, writable: true))
            run?.DeleteValue(InstallInfo.RunValue, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(InstallInfo.UninstallKey, throwOnMissingSubKey: false);

        TryDelete(() => File.Delete(InstallInfo.StartMenuShortcut));
        TryDelete(() => File.Delete(InstallInfo.SettingsShortcut));
        TryDelete(() => File.Delete(InstallInfo.DesktopShortcut));
        // Retried: antivirus or the search indexer can hold a file open for a moment.
        for (int i = 0; i < 10 && Directory.Exists(InstallInfo.DataDir); i++)
        {
            TryDelete(() => Directory.Delete(InstallInfo.DataDir, recursive: true));
            if (Directory.Exists(InstallInfo.DataDir)) Thread.Sleep(300);
        }

        // A running exe can't delete itself, so a hidden cmd waits for us to exit and removes the folder.
        // Only ever the registered install folder, never wherever a dev build happens to run from.
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        if (installDir is not null && SamePath(installDir, exeDir) && File.Exists(Path.Combine(installDir, InstallInfo.ExeName)))
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{installDir.TrimEnd('\\')}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }

        if (!quiet)
            MessageBox.Show("Music Visualizer was removed.", InstallInfo.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Asks a running copy to quit (it restores the desktop wallpaper on the way out).</summary>
    public static void StopRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(InstallInfo.QuitEvent, out var quit))
            {
                using (quit) quit.Set();
            }
            // Wait until the running copy releases its single-instance mutex.
            for (int i = 0; i < 50; i++)
            {
                if (!Mutex.TryOpenExisting(InstallInfo.InstanceMutex, out var m)) return;
                m.Dispose();
                Thread.Sleep(100);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
