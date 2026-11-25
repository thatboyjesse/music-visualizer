using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace MusicVisualizer.Wallpaper;

/// <summary>
/// Puts a window behind the desktop icons so it acts as a live wallpaper.
///
/// Sending the undocumented message 0x052C to Progman (the desktop's root window)
/// makes Explorer create a "WorkerW" window between the icons and the static
/// wallpaper. Parenting our window to the right place makes it draw there.
///
/// Two layouts exist:
///  - Windows 10 / 11 before 24H2: a top-level WorkerW holds SHELLDLL_DefView (the icons);
///    the *next* top-level WorkerW is the empty one we parent into.
///  - Windows 11 24H2 and later: SHELLDLL_DefView and WorkerW are both children of Progman.
///    We parent to Progman and sit just below SHELLDLL_DefView.
/// </summary>
internal static class DesktopWallpaper
{
    /// <param name="screen">Area to cover, in screen pixels (see <see cref="Screens"/>).</param>
    /// <param name="beforeMakingLayered">Called (24H2+ only) just before the window is made layered,
    /// so the caller can stop its UI framework from undoing that.</param>
    public static void Attach(IntPtr hwnd, Int32Rect screen, Action? beforeMakingLayered = null)
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            throw new InvalidOperationException("Couldn't find the Windows desktop (is Explorer running?).");

        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        IntPtr parent;
        IntPtr insertAfter;

        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            // 24H2+ layout. Progman composes its children with DirectComposition and only
            // draws child windows that are layered, so make ours a fully opaque layered window.
            parent = progman;
            insertAfter = defView;
        }
        else
        {
            IntPtr workerW = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    workerW = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (workerW == IntPtr.Zero)
                throw new InvalidOperationException("Couldn't find the desktop wallpaper layer.");
            parent = workerW;
            insertAfter = HWND_TOP;
        }

        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        SetParent(hwnd, parent);

        if (parent == progman)
        {
            // 24H2+: Progman composes its children with DirectComposition and only draws
            // child windows that are layered, so make ours a fully opaque layered window.
            // (Layered child windows also require the Windows 8+ entries in app.manifest.)
            beforeMakingLayered?.Invoke();
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_LAYERED) == 0)
                throw new InvalidOperationException("Windows refused to make the wallpaper window layered.");
        }

        Place(hwnd, parent, insertAfter, screen, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Moves an attached wallpaper window to cover another screen (or the same screen after a resolution change).</summary>
    public static void MoveTo(IntPtr hwnd, Int32Rect screen)
    {
        // Re-assert the z-order too: on 24H2+ Explorer can raise its WorkerW above us
        // (e.g. after a display change), which would hide the visualizer.
        IntPtr parent = GetParent(hwnd);
        IntPtr defView = FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
        Place(hwnd, parent, defView != IntPtr.Zero ? defView : HWND_TOP, screen, SWP_NOACTIVATE);
    }

    private static void Place(IntPtr hwnd, IntPtr parent, IntPtr insertAfter, Int32Rect screen, uint flags)
    {
        // Child coordinates are relative to the parent, which spans the whole virtual
        // screen and may start left of or above the primary monitor's (0,0).
        GetWindowRect(parent, out var pr);
        SetWindowPos(hwnd, insertAfter, screen.X - pr.Left, screen.Y - pr.Top, screen.Width, screen.Height, flags);
    }

    /// <summary>Re-applies the user's normal wallpaper so no frozen frame is left behind.</summary>
    public static void RestoreStaticWallpaper()
    {
        var path = new StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0))
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path.ToString(), SPIF_SENDCHANGE);
    }

    // ------------------------------------------------------------------ Win32

    private const uint SMTO_NORMAL = 0x0000;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x2;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SPI_SETDESKWALLPAPER = 0x0014, SPI_GETDESKWALLPAPER = 0x0073, SPIF_SENDCHANGE = 0x02;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pvParam, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, string pvParam, uint winIni);
}
