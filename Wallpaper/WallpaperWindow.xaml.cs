using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MusicVisualizer.Audio;
using MusicVisualizer.NowPlaying;
using MusicVisualizer.Visualizer;

namespace MusicVisualizer.Wallpaper;

/// <summary>A borderless visualizer that lives behind the desktop icons.</summary>
public partial class WallpaperWindow : Window
{
    private bool _keepLayered;

    public WallpaperWindow(AudioEngine engine, NowPlayingService nowPlaying, VisualMode mode)
    {
        InitializeComponent();
        Viz.Attach(engine);
        Viz.Mode = mode;
        Card.ShowControls = false; // the wallpaper can't receive clicks
        Card.Scale = 1.25;
        Card.Bind(nowPlaying);
    }

    public VisualMode Mode { set => Viz.Mode = value; }
    public double? ThemeHue { set => Viz.ThemeHue = value; }

    /// <summary>
    /// Shows the window and moves it behind the desktop icons. This happens after
    /// <see cref="Window.Show"/> because WPF applies its own window styles while showing.
    /// The window starts off-screen (see XAML) so it never flashes as a normal window.
    /// </summary>
    public void ShowAsWallpaper(Int32Rect screen)
    {
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(KeepLayeredHook);
        DesktopWallpaper.Attach(hwnd, screen, beforeMakingLayered: () => _keepLayered = true);
    }

    /// <summary>Moves the running wallpaper to cover another screen.</summary>
    public void MoveTo(Int32Rect screen) =>
        DesktopWallpaper.MoveTo(new WindowInteropHelper(this).Handle, screen);

    /// <summary>
    /// WPF vetoes WS_EX_LAYERED on windows it didn't create as layered (it rewrites the new
    /// style in WM_STYLECHANGING). On Windows 11 24H2+ the desktop only draws layered child
    /// windows, so once we've decided to go layered, we keep that bit in every style change
    /// and don't let WPF's handler see the message.
    /// </summary>
    private IntPtr KeepLayeredHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_STYLECHANGING = 0x007C, GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x00080000;
        if (_keepLayered && msg == WM_STYLECHANGING && wParam.ToInt32() == GWL_EXSTYLE)
        {
            // lParam -> STYLESTRUCT { DWORD styleOld; DWORD styleNew; }
            int newStyle = Marshal.ReadInt32(lParam, 4);
            Marshal.WriteInt32(lParam, 4, newStyle | WS_EX_LAYERED);
            handled = true;
        }
        return IntPtr.Zero;
    }
}
