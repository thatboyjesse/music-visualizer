using Microsoft.Win32;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Core;

/// <summary>The per-user "Run" registry entry that starts the app in the tray when you sign in.</summary>
public static class StartWithWindows
{
    /// <summary>What starts with Windows: the engine (helper mode), else this exe.</summary>
    private static string ExePath => AppController.EngineExe ?? Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey);
            return key?.GetValue(InstallInfo.RunValue) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InstallInfo.RunKey);
        if (enabled)
            key.SetValue(InstallInfo.RunValue, InstallInfo.RunCommand(ExePath));
        else
            key.DeleteValue(InstallInfo.RunValue, throwOnMissingValue: false);
    }

    /// <summary>If the entry exists but points at an older copy of the exe (moved or reinstalled), fix it.</summary>
    public static void Repair()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey, writable: true);
        if (key?.GetValue(InstallInfo.RunValue) is string value
            && value != InstallInfo.RunCommand(ExePath))
            key.SetValue(InstallInfo.RunValue, InstallInfo.RunCommand(ExePath));
    }
}
