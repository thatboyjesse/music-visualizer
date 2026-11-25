using System.Runtime.InteropServices;
using System.Windows;

namespace MusicVisualizer.Wallpaper;

/// <summary>A connected monitor. <see cref="Bounds"/> is in screen pixels (the primary monitor's top-left is 0,0).</summary>
public sealed record ScreenInfo(string DeviceName, bool IsPrimary, Int32Rect Bounds, int Number)
{
    public string Label => IsPrimary ? $"Screen {Number} (main)" : $"Screen {Number}";
}

/// <summary>Lists monitors through Win32 (WPF has no API for this).</summary>
public static class Screens
{
    /// <summary>All monitors, numbered 1..n left to right (then top to bottom).</summary>
    public static IReadOnlyList<ScreenInfo> All()
    {
        var found = new List<(string Device, bool Primary, Int32Rect Bounds)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                found.Add((info.szDevice, (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
            }
            return true;
        }, IntPtr.Zero);

        return found
            .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
            .Select((s, i) => new ScreenInfo(s.Device, s.Primary, s.Bounds, i + 1))
            .ToList();
    }

    /// <summary>The screen with this device name, or the primary screen if it's gone (or null).</summary>
    public static ScreenInfo Find(string? deviceName)
    {
        var all = All();
        return all.FirstOrDefault(s => s.DeviceName == deviceName)
            ?? all.FirstOrDefault(s => s.IsPrimary)
            ?? all.First();
    }

    // ------------------------------------------------------------------ Win32

    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
}
