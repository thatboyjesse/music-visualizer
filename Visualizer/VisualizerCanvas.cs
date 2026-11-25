using System.Windows;
using System.Windows.Media;
using MusicVisualizer.Audio;

namespace MusicVisualizer.Visualizer;

public enum VisualMode { Bars, Radial, Wave }

/// <summary>Draws the spectrum every frame using WPF's retained renderer.</summary>
public sealed class VisualizerCanvas : FrameworkElement
{
    private const int BandCount = 96;

    private readonly SpectrumAnalyzer _analyzer = new(BandCount);
    private readonly SolidColorBrush[] _hue = new SolidColorBrush[360];
    private readonly Brush _background;
    private readonly RadialGradientBrush[] _glow = new RadialGradientBrush[36];
    private Pen[]? _radialPens;
    private double _radialPenWidth;

    private AudioEngine? _engine;
    private TimeSpan _lastFrame;
    private double _hueShift;
    private double _time;

    public VisualMode Mode { get; set; } = VisualMode.Bars;

    /// <summary>When set (e.g. from album art), colours stay near this hue instead of cycling the rainbow.</summary>
    public double? ThemeHue { get; set; }

    /// <summary>Stops analysing and redrawing (window minimised, or the wallpaper is showing instead).</summary>
    public bool IsPaused { get; set; }

    public VisualizerCanvas()
    {
        for (int h = 0; h < 360; h++)
        {
            _hue[h] = new SolidColorBrush(Hsv(h, 0.75, 1.0));
            _hue[h].Freeze();
        }

        for (int i = 0; i < 36; i++)
        {
            var c = Hsv(i * 10, 0.7, 1.0);
            _glow[i] = new RadialGradientBrush(Color.FromArgb(80, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            _glow[i].Freeze();
        }

        _background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x12));
        _background.Freeze();

        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public void Attach(AudioEngine engine) => _engine = engine;

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastFrame) return; // Rendering can fire more than once per frame
        double dt = _lastFrame == default ? 1 / 60.0 : (t - _lastFrame).TotalSeconds;
        _lastFrame = t;
        if (IsPaused) return;
        dt = Math.Clamp(dt, 0, 0.1);

        _analyzer.Update(_engine, dt);
        _time += dt;
        _hueShift = (_hueShift + dt * (6 + _analyzer.Bass * 50)) % 360;
        InvalidateVisual();
    }

    /// <summary>Starting hue for this frame: album-art theme (gently swaying) or the rainbow cycle.</summary>
    private double BaseHue => ThemeHue is { } h ? h - 25 + Math.Sin(_time * 0.6) * 12 + _analyzer.Bass * 20 : _hueShift;

    /// <summary>How far the hue travels across the spectrum.</summary>
    private double HueSpread(double rainbow) => ThemeHue.HasValue ? 55 : rainbow;

    private Brush HueBrush(double hue) => _hue[((int)hue % 360 + 360) % 360];

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(_background, null, new Rect(0, 0, w, h));

        // Soft glow that pulses with the bass.
        double pulse = _analyzer.Bass * _analyzer.Bass;
        dc.PushOpacity(Math.Clamp(0.25 + pulse * 1.5, 0, 1));
        double gr = Math.Max(w, h) * (0.45 + pulse * 0.25);
        int glowIndex = (((int)BaseHue % 360 + 360) % 360) / 10;
        dc.DrawEllipse(_glow[glowIndex], null, new Point(w / 2, h / 2), gr, gr);
        dc.Pop();

        switch (Mode)
        {
            case VisualMode.Bars: DrawBars(dc, w, h); break;
            case VisualMode.Radial: DrawRadial(dc, w, h); break;
            case VisualMode.Wave: DrawWave(dc, w, h); break;
        }
    }

    private void DrawBars(DrawingContext dc, double w, double h)
    {
        var bands = _analyzer.Bands;
        var peaks = _analyzer.Peaks;
        int n = bands.Length;
        double margin = 24;
        double slot = (w - margin * 2) / n;
        double barW = Math.Max(1, slot * 0.72);
        double baseline = h * 0.78;
        double maxH = h * 0.62;

        for (int i = 0; i < n; i++)
        {
            double x = margin + i * slot + (slot - barW) / 2;
            double bh = Math.Max(2, bands[i] * maxH);
            var brush = HueBrush(BaseHue + i * HueSpread(200) / n);
            dc.DrawRoundedRectangle(brush, null, new Rect(x, baseline - bh, barW, bh), 2, 2);

            double py = baseline - peaks[i] * maxH - 6;
            dc.DrawRectangle(brush, null, new Rect(x, py, barW, 2.5));
        }

        // Reflection
        dc.PushOpacity(0.16);
        for (int i = 0; i < n; i++)
        {
            double x = margin + i * slot + (slot - barW) / 2;
            double rh = bands[i] * maxH * 0.3;
            if (rh < 1) continue;
            dc.DrawRectangle(HueBrush(BaseHue + i * HueSpread(200) / n), null, new Rect(x, baseline + 4, barW, rh));
        }
        dc.Pop();
    }

    private void DrawRadial(DrawingContext dc, double w, double h)
    {
        var bands = _analyzer.Bands;
        int n = bands.Length;
        var c = new Point(w / 2, h / 2);
        double size = Math.Min(w, h);
        double r0 = size * 0.17;
        double r = r0 * (1 + _analyzer.Bass * _analyzer.Bass * 0.35);
        double maxLen = size * 0.28;

        // Two mirrored halves => 2n spokes around the circle.
        double penW = Math.Max(1.5, 2 * Math.PI * r0 / (2 * n) * 0.6);
        if (_radialPens is null || Math.Abs(penW - _radialPenWidth) > 0.25)
        {
            _radialPenWidth = penW;
            _radialPens = new Pen[360];
            for (int k = 0; k < 360; k++)
            {
                var p = new Pen(_hue[k], penW) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                p.Freeze();
                _radialPens[k] = p;
            }
        }

        for (int i = 0; i < n; i++)
        {
            double len = 3 + bands[i] * maxLen;
            var pen = _radialPens[((int)(BaseHue + i * HueSpread(240) / n) % 360 + 360) % 360];
            double t = (i + 0.5) / n * Math.PI;
            foreach (double a in new[] { -Math.PI / 2 + t, -Math.PI / 2 - t })
            {
                double cos = Math.Cos(a), sin = Math.Sin(a);
                dc.DrawLine(pen,
                    new Point(c.X + cos * r, c.Y + sin * r),
                    new Point(c.X + cos * (r + len), c.Y + sin * (r + len)));
            }
        }

        var ring = new Pen(HueBrush(BaseHue), 2);
        ring.Freeze();
        dc.DrawEllipse(null, ring, c, r - 8, r - 8);
    }

    private void DrawWave(DrawingContext dc, double w, double h)
    {
        var s = _analyzer.Samples;
        int n = s.Length;

        // Start at a rising zero crossing so the wave doesn't jitter sideways.
        int start = 0;
        for (int i = 1; i < n / 2; i++)
        {
            if (s[i - 1] < 0 && s[i] >= 0) { start = i; break; }
        }

        const int points = 512;
        int span = n / 2;
        double mid = h / 2;
        double amp = h * 0.38 * 1.6;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int p = 0; p < points; p++)
            {
                int idx = start + p * span / points;
                double x = p * w / (points - 1);
                double y = mid - Math.Clamp(s[idx] * amp, -h * 0.45, h * 0.45);
                if (p == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        geo.Freeze();

        var brush = HueBrush(BaseHue);
        var glow = new Pen(brush, 10) { LineJoin = PenLineJoin.Round };
        var line = new Pen(brush, 2.5) { LineJoin = PenLineJoin.Round };
        glow.Freeze();
        line.Freeze();

        dc.PushOpacity(0.18);
        dc.DrawGeometry(null, glow, geo);
        dc.Pop();
        dc.DrawGeometry(null, line, geo);
    }

    private static Color Hsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
