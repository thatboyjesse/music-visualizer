using Microsoft.Win32;
using MusicVisualizer.Shared;

namespace MusicVisualizer.Core;

/// <summary>The per-user "Run" registry entry that starts the app in the tray when you sign in.</summary>
public static class StartWithWindows
{
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
            key.SetValue(InstallInfo.RunValue, InstallInfo.RunCommand(Environment.ProcessPath!));
        else
            key.DeleteValue(InstallInfo.RunValue, throwOnMissingValue: false);
    }

    /// <summary>If the entry exists but points at an older copy of the exe (moved or reinstalled), fix it.</summary>
    public static void Repair()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallInfo.RunKey, writable: true);
        if (key?.GetValue(InstallInfo.RunValue) is string value
            && value != InstallInfo.RunCommand(Environment.ProcessPath!))
            key.SetValue(InstallInfo.RunValue, InstallInfo.RunCommand(Environment.ProcessPath!));
    }
}
