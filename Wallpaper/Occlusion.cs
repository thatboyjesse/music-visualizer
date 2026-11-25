using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace MusicVisualizer.Wallpaper;

/// <summary>
/// Tells whether the wallpaper's screen is hidden behind a maximized or full-screen window, so the
/// visualizer can stop drawing what nobody can see.
/// </summary>
public static class Occlusion
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SHELLDLL_DefView",
        "Windows.UI.Core.CoreWindow", // Start menu, search, notification centre
    };

    /// <summary>
    /// True if a visible, normal app window covers at least 90% of the screen (a maximized window
    /// covers everything except the taskbar). Ignores our own windows, the shell, minimized and
    /// cloaked windows (other virtual desktops, suspended UWP apps), and tool/overlay windows that
    /// can't be the real foreground app (click-through or no-activate overlays from GPU tools etc.).
    /// </summary>
    public static bool IsCovered(Int32Rect screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return false;
        uint self = (uint)Environment.ProcessId;
        double screenArea = (double)screen.Width * screen.Height;
        bool covered = false;
        var cls = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0) return true;

            cls.Clear();
            GetClassName(hwnd, cls, cls.Capacity);
            if (ShellClasses.Contains(cls.ToString())) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 &&
                !GetWindowRect(hwnd, out r)) return true;

            double w = Math.Min(r.Right, screen.X + screen.Width) - Math.Max(r.Left, screen.X);
            double h = Math.Min(r.Bottom, screen.Y + screen.Height) - Math.Max(r.Top, screen.Y);
            if (w > 0 && h > 0 && w * h >= screenArea * 0.9)
            {
                covered = true;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);

        return covered;
    }

    // ------------------------------------------------------------------ Win32

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TRANSPARENT = 0x00000020, WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
