using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MusicVisualizer.Core;

/// <summary>Asks Windows 10 (20H1+) / 11 to draw a window's title bar dark, to match the app.</summary>
public static class DarkTitleBar
{
    public static void Apply(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            int on = 1;
            var hwnd = new WindowInteropHelper(window).Handle;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
