using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace MusicVisualizer.Core;

/// <summary>
/// The notification-area ("system tray") icon. WPF has no tray API, so this uses the
/// Windows Forms NotifyIcon. Left-click opens settings; right-click shows a small menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _wallpaperItem;

    public event EventHandler? SettingsRequested;
    public event EventHandler? VisualizerRequested;
    public event EventHandler? WallpaperToggled;
    public event EventHandler? StopRequested;
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false };
        menu.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Open visualizer", null, (_, _) => VisualizerRequested?.Invoke(this, EventArgs.Empty));
        _wallpaperItem = new Forms.ToolStripMenuItem("Wallpaper", null, (_, _) => WallpaperToggled?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_wallpaperItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        // "Stop" turns everything off but keeps the icon here; "Exit completely" closes the app.
        menu.Items.Add("Stop", null, (_, _) => StopRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Exit completely", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        foreach (Forms.ToolStripItem item in menu.Items) item.ForeColor = Color.FromArgb(0xE8, 0xE8, 0xF0);

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Music Visualizer",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SettingsRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void SetWallpaperChecked(bool on) => _wallpaperItem.Text = on ? "Wallpaper  ✓" : "Wallpaper";

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>Matches the app's dark theme.</summary>
    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        private sealed class DarkColors : Forms.ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(0x1A, 0x1A, 0x26);
            private static readonly Color Hover = Color.FromArgb(0x33, 0x33, 0x48);
            public override Color ToolStripDropDownBackground => Back;
            public override Color MenuBorder => Color.FromArgb(0x3A, 0x3A, 0x50);
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color SeparatorDark => Color.FromArgb(0x3A, 0x3A, 0x50);
            public override Color SeparatorLight => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
        }
    }
}
