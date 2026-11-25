namespace MusicVisualizer.Settings;

public enum ColorMode
{
    /// <summary>Follows the dominant colour of the album cover; falls back to the rainbow.</summary>
    AlbumArt,
    /// <summary>Cycles through the rainbow, faster with the bass.</summary>
    Rainbow,
    /// <summary>A gradient between two colours the user picks.</summary>
    Custom,
}

/// <summary>Where the now-playing card (song title, artist, album art) sits on the wallpaper.</summary>
public enum TitlePosition { BottomRight, BottomCenter, BottomLeft, TopRight, TopCenter, TopLeft, Hidden }

/// <summary>
/// How the visualizer looks. Shared by the wallpaper, the visualizer window and the settings preview.
/// Positions and sizes are fractions of the screen, so the preview matches the wallpaper.
/// </summary>
public sealed class Appearance
{
    // Look
    public bool PeakCaps { get; set; } = true;
    public bool Reflection { get; set; } = true;
    /// <summary>Bar thickness: 0.2 (thin lines) .. 1 (no gaps).</summary>
    public double BarWidth { get; set; } = 0.72;
    /// <summary>Number of bars (frequency bands), 16 .. 128. Fewer looks bolder and simpler.</summary>
    public int BarCount { get; set; } = 96;

    // Position and size
    /// <summary>Centre of the visualizer, 0 (left) .. 1 (right).</summary>
    public double PositionX { get; set; } = 0.5;
    /// <summary>Centre of the visualizer, 0 (top) .. 1 (bottom).</summary>
    public double PositionY { get; set; } = 0.5;
    /// <summary>Scale (height, and the size of round styles), 0.3 .. 1.5.</summary>
    public double Size { get; set; } = 1.0;
    /// <summary>How much of the screen's width the visualizer spans, 0.15 .. 1.</summary>
    public double Width { get; set; } = 1.0;

    // Song title
    public TitlePosition Title { get; set; } = TitlePosition.BottomRight;

    // Intensity
    /// <summary>How strongly it reacts, 0.25 .. 4 (1 = default). Applied as gain before the dB mapping.</summary>
    public double Intensity { get; set; } = 1.0;

    /// <summary>Extra sensitivity per frequency range, in dB (−12 .. +12). See SpectrumAnalyzer.SetEq.</summary>
    public double EqBass { get; set; }
    public double EqLowMids { get; set; }
    public double EqHighMids { get; set; }
    public double EqTreble { get; set; }

    // Colours
    public ColorMode Colors { get; set; } = ColorMode.AlbumArt;
    public string Color1 { get; set; } = "#7A5CFF";
    public string Color2 { get; set; } = "#3FD0C9";

    // Background
    public bool Glow { get; set; } = true;
    /// <summary>Strength of the bass-pulsing glow, 0 .. 2 (1 = default).</summary>
    public double GlowStrength { get; set; } = 1.0;
    /// <summary>Picture behind the visualizer (a copy in %AppData%\MusicVisualizer), or null for plain dark.</summary>
    public string? BackgroundImage { get; set; }
    /// <summary>Darkens the picture so the visualizer stands out, 0 .. 0.9.</summary>
    public double BackgroundDim { get; set; } = 0.35;

    public Appearance Clone() => (Appearance)MemberwiseClone();
}
