using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MusicVisualizer.Audio;
using MusicVisualizer.NowPlaying;
using MusicVisualizer.Settings;
using MusicVisualizer.Visualizer;

namespace MusicVisualizer.Wallpaper;

/// <summary>A borderless visualizer that lives behind the desktop icons.</summary>
public partial class WallpaperWindow : Window
{
    private bool _keepLayered;

    public WallpaperWindow(AudioEngine engine, NowPlayingService nowPlaying, VisualMode mode, Appearance look)
    {
        InitializeComponent();
        Viz.Attach(engine);
        Viz.Bind(nowPlaying);
        Viz.Mode = mode;
        Viz.Look = look;
        Card.Place(look.Title, look.PeekCard);
        Card.ShowControls = false; // the wallpaper can't receive clicks
        Card.Scale = 1.25;
        Card.Bind(nowPlaying);
    }

    public VisualMode Mode { set => Viz.Mode = value; }
    public AlbumColors? AlbumColors { set => Viz.AlbumColors = value; }
    public Appearance Look
    {
        set
        {
            Viz.Look = value;
            Card.Place(value.Title, value.PeekCard);
        }
    }

    /// <summary>
    /// Shows the window and moves it behind the desktop icons. This happens after
    /// <see cref="Window.Show"/> because WPF applies its own window styles while showing.
    /// The window starts off-screen (see XAML) so it never flashes as a normal window.
    /// </summary>
    public void ShowAsWallpaper(Int32Rect screen)
    {
        _screen = screen;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(KeepLayeredHook);
        DesktopWallpaper.Attach(hwnd, screen, beforeMakingLayered: () => _keepLayered = true);
    }

    /// <summary>True while the normal desktop wallpaper is showing instead (nothing playing).</summary>
    public bool IsIdle { get; private set; }

    private bool _idleHidden, _covered;

    /// <summary>Set while a maximized or full-screen window hides this screen: drawing stops until it's visible again.</summary>
    public bool Covered
    {
        get => _covered;
        set
        {
            if (value == _covered) return;
            _covered = value;
            UpdatePause();
        }
    }

    /// <summary>True while nothing is being drawn (hidden because it's quiet, or covered by a window).</summary>
    public bool IsPaused => _idleHidden || _covered;

    private void UpdatePause() => Viz.IsPaused = IsPaused;

    /// <summary>
    /// Switches between the visualizer and the user's normal wallpaper. Going idle fades in a copy of
    /// the desktop background (same picture, fit and colour) and then hides the window, so the real
    /// wallpaper takes over with no visible jump. Waking shows the window with that copy on top and
    /// fades it out.
    /// </summary>
    public void SetIdle(bool idle)
    {
        if (idle == IsIdle) return;
        IsIdle = idle;
        var hwnd = new WindowInteropHelper(this).Handle;

        if (idle)
        {
            LoadDesktopBackground();
            var fade = new DoubleAnimation(1, TimeSpan.FromSeconds(1.2));
            fade.Completed += (_, _) =>
            {
                if (!IsIdle) return; // woke up during the fade
                _idleHidden = true;
                UpdatePause();
                ShowWindow(hwnd, SW_HIDE);
            };
            Desk.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            _idleHidden = false;
            UpdatePause();
            Desk.BeginAnimation(OpacityProperty, null);
            Desk.Opacity = 1;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            // Back under the icons, above Explorer's own wallpaper layer (see DesktopWallpaper.MoveTo).
            DesktopWallpaper.MoveTo(hwnd, _screen);
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.4));
            fade.Completed += (_, _) => { if (!IsIdle) DeskImage.Source = null; }; // free the decoded picture
            Desk.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>Reproduces the desktop background: solid colour plus the wallpaper picture in the user's fit mode.</summary>
    private void LoadDesktopBackground()
    {
        int c = GetSysColor(1 /* COLOR_DESKTOP */);
        Desk.Background = new SolidColorBrush(Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));
        DeskImage.Source = null;

        var path = new StringBuilder(520);
        if (!SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)path.Capacity, path, 0) || !File.Exists(path.ToString()))
            return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path.ToString());
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.DecodePixelWidth = Math.Max(640, _screen.Width); // no bigger than the screen needs
            img.EndInit();
            img.Freeze();
            DeskImage.Source = img;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return;
        }

        // HKCU\Control Panel\Desktop: WallpaperStyle 10 = Fill, 6 = Fit, 2 = Stretch, 22 = Span, 0 = Center/Tile.
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        string style = key?.GetValue("WallpaperStyle") as string ?? "10";
        bool tile = key?.GetValue("TileWallpaper") as string == "1";
        DeskImage.Stretch = style switch
        {
            "6" => Stretch.Uniform,
            "2" => Stretch.Fill,
            "0" when !tile => Stretch.None,
            _ => Stretch.UniformToFill,
        };
    }

    private Int32Rect _screen;

    /// <summary>Moves the running wallpaper to cover another screen.</summary>
    public void MoveTo(Int32Rect screen)
    {
        _screen = screen;
        DesktopWallpaper.MoveTo(new WindowInteropHelper(this).Handle, screen);
    }

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

    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern int GetSysColor(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder pvParam, uint winIni);
}
