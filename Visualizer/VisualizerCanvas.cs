using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicVisualizer.Audio;
using MusicVisualizer.NowPlaying;
using MusicVisualizer.Settings;

namespace MusicVisualizer.Visualizer;

public enum VisualMode { Bars, Radial, Wave, Mirror, Blocks, Spectrum, Ring }

/// <summary>Draws the spectrum every frame using WPF's retained renderer.</summary>
public sealed class VisualizerCanvas : FrameworkElement
{
    private SpectrumAnalyzer _analyzer = new(96);
    private readonly Brush _background;
    private readonly Brush _dim = Frozen(new SolidColorBrush(Colors.Black));

    // Colour palette: 360 brushes. For rainbow/album-art colours, index = hue in degrees;
    // for custom colours, index = position along the user's two-colour gradient.
    private readonly SolidColorBrush[] _palette = new SolidColorBrush[360];
    private readonly RadialGradientBrush[] _glow = new RadialGradientBrush[36];
    private string? _paletteKey;
    private Pen[]? _pens;
    private double _penWidth;

    private string? _imagePath;
    private BitmapSource? _image;

    private AudioEngine? _engine;
    private TimeSpan _lastFrame;
    private double _hueShift;
    private double _time;

    public VisualMode Mode { get; set; } = VisualMode.Bars;

    /// <summary>Colours of the current album cover. Used when <see cref="Look"/> is set to song-art colours.</summary>
    public AlbumColors? AlbumColors { get; set; }

    // The album colours actually drawn; they glide towards AlbumColors so song changes blend over about a second.
    private Color? _albumA, _albumB;

    /// <summary>Colours, position, size, intensity, glow and background picture.</summary>
    public Appearance Look { get; set; } = new();

    /// <summary>Stops analysing and redrawing (window minimised, or the wallpaper is showing instead).</summary>
    public bool IsPaused { get; set; }

    // The track shown by the "album art in the middle" option.
    private ImageSource? _art;
    private string? _title, _artist;
    private FormattedText? _titleText, _artistText;
    private string? _captionKey;

    /// <summary>Follows a now-playing service so the centred cover and caption show the current song.</summary>
    public void Bind(NowPlayingService service)
    {
        void Update(object? sender, EventArgs e)
        {
            var t = service.Current;
            _art = t?.Art;
            _title = t?.Title;
            _artist = t?.Artist;
        }
        service.Changed += Update;
        Unloaded += (_, _) => service.Changed -= Update;
        Update(null, EventArgs.Empty);
    }

    public VisualizerCanvas()
    {
        _background = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x12)));
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

        int bars = Math.Clamp(Look.BarCount, 8, 128);
        if (bars != _analyzer.Bands.Length) _analyzer = new SpectrumAnalyzer(bars);
        _analyzer.Gain = Look.Intensity;
        _analyzer.SetEq(Look.EqBass, Look.EqLowMids, Look.EqHighMids, Look.EqTreble);
        _analyzer.Update(_engine, dt);
        _time += dt;
        _hueShift = (_hueShift + dt * (6 + _analyzer.Bass * 50)) % 360;
        GlideAlbumColors(dt);
        InvalidateVisual();
    }

    // ------------------------------------------------------------ colours

    /// <summary>Rainbow mode only; song-art and custom colours are a fixed two-colour gradient.</summary>
    private bool Rainbow => Look.Colors == ColorMode.Rainbow;

    /// <summary>Starting hue for this frame of the rainbow cycle.</summary>
    private double BaseHue => _hueShift;

    /// <summary>
    /// Palette index for a point <paramref name="t"/> (0..1) along the spectrum.
    /// <paramref name="rainbowSpread"/> is how many degrees of hue the rainbow covers.
    /// </summary>
    private int ColorIndex(double t, double rainbowSpread)
    {
        if (!Rainbow) return (int)Math.Clamp(t * 359, 0, 359);
        return (((int)(BaseHue + t * rainbowSpread)) % 360 + 360) % 360;
    }

    /// <summary>
    /// The two gradient colours: the user's own, or the album's. Song-art mode never falls back
    /// to the rainbow: before the first cover is known it uses the user's two colours.
    /// </summary>
    private (Color, Color) GradientColors()
    {
        Color c1 = Parse(Look.Color1, Color.FromRgb(0x7A, 0x5C, 0xFF));
        Color c2 = Parse(Look.Color2, Color.FromRgb(0x3F, 0xD0, 0xC9));
        if (Look.Colors == ColorMode.AlbumArt && _albumA is { } a && _albumB is { } b) return (a, b);
        return (c1, c2);
    }

    private void GlideAlbumColors(double dt)
    {
        if (AlbumColors is not { } target) return;
        if (_albumA is null || _albumB is null)
        {
            _albumA = target.First;
            _albumB = target.Second;
            return;
        }
        double k = 1 - Math.Exp(-dt * 3);
        _albumA = Lerp(_albumA.Value, target.First, k, snap: true);
        _albumB = Lerp(_albumB.Value, target.Second, k, snap: true);
    }

    private void EnsurePalette()
    {
        var (c1, c2) = GradientColors();
        string key = Rainbow ? "hue" : $"{c1}|{c2}";
        if (key == _paletteKey) return;
        _paletteKey = key;

        for (int k = 0; k < 360; k++)
        {
            var c = Rainbow ? Hsv(k, 0.75, 1.0) : Lerp(c1, c2, k / 359.0);
            _palette[k] = Frozen(new SolidColorBrush(c));
        }
        for (int i = 0; i < 36; i++)
        {
            var c = _palette[i * 10].Color;
            _glow[i] = Frozen(new RadialGradientBrush(Color.FromArgb(80, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)));
        }
        _pens = null;
    }

    private Pen PenFor(int index, double width)
    {
        if (_pens is null || Math.Abs(width - _penWidth) > 0.25)
        {
            _penWidth = width;
            _pens = new Pen[360];
        }
        return _pens[index] ??= Frozen(new Pen(_palette[index], width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    }

    // ------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        EnsurePalette();

        DrawBackground(dc, w, h);

        var center = new Point(Look.PositionX * w, Look.PositionY * h);
        if (Look.Glow && Look.GlowStrength > 0)
        {
            // Soft glow that pulses with the bass.
            double pulse = _analyzer.Bass * _analyzer.Bass;
            dc.PushOpacity(Math.Clamp((0.25 + pulse * 1.5) * Look.GlowStrength, 0, 1));
            double gr = Math.Max(w, h) * (0.45 + pulse * 0.25) * Math.Max(0.5, Look.Size);
            dc.DrawEllipse(_glow[ColorIndex(0.3, 0) / 10], null, center, gr, gr);
            dc.Pop();
        }

        // Album art: always at the centre of the screen; only the visualizer moves with the position
        // sliders. Round styles size it to their circle; the others draw it behind the visualizer,
        // bouncing with the same bass pulse as the glow.
        bool round = Mode is VisualMode.Radial or VisualMode.Ring;
        bool art = Look.AlbumArt && _art is not null;
        var mid = new Point(w / 2, h / 2);
        double coverSize = 0, captionDistance = 0;
        if (art && !round)
        {
            double pulse = _analyzer.Bass * _analyzer.Bass;
            double side = Math.Clamp(Look.AlbumSize, 0.08, 0.8) * h * (1 + pulse * 0.12);
            var rect = new Rect(mid.X - side / 2, mid.Y - side / 2, side, side);
            dc.PushClip(new RectangleGeometry(rect, side * 0.06, side * 0.06));
            dc.DrawImage(_art, rect);
            dc.Pop();
            coverSize = side;
            captionDistance = side / 2 + side * 0.06;
        }

        switch (Mode)
        {
            case VisualMode.Bars: DrawBars(dc, w, h, center); break;
            case VisualMode.Radial: DrawRadial(dc, w, h, center); break;
            case VisualMode.Wave: DrawWave(dc, w, h, center); break;
            case VisualMode.Mirror: DrawMirror(dc, w, h, center); break;
            case VisualMode.Blocks: DrawBlocks(dc, w, h, center); break;
            case VisualMode.Spectrum: DrawSpectrum(dc, w, h, center); break;
            case VisualMode.Ring: DrawRing(dc, w, h, center); break;
        }

        if (art && round)
        {
            double size = Math.Min(w, h) * Look.Size;
            double radius = Mode == VisualMode.Radial
                ? size * 0.17 * (1 + _analyzer.Bass * _analyzer.Bass * 0.35) - 12 // fits inside the ring line
                : size * 0.22 * (1 + _analyzer.Bass * _analyzer.Bass * 0.25) * 0.72;  // fits inside the waveform
            radius = Math.Max(4, radius);
            dc.PushClip(new EllipseGeometry(mid, radius, radius));
            dc.DrawImage(_art, new Rect(mid.X - radius, mid.Y - radius, radius * 2, radius * 2));
            dc.Pop();
            coverSize = radius * 2;
            // Clear the spokes / waveform when the circle is around the cover; otherwise just the cover's edge.
            bool circleAroundCover = (center - mid).Length < radius;
            captionDistance = circleAroundCover ? Math.Max(radius + 10, size * 0.35) : radius + 10;
        }

        if (art && Look.AlbumCaption) DrawCaption(dc, mid, captionDistance, coverSize);
    }

    /// <summary>Song title and artist, centred under the cover, with a soft shadow so they read on any background.</summary>
    /// <param name="cover">Centre of the cover.</param>
    /// <param name="distance">How far from the centre the text starts, in the chosen direction.</param>
    private void DrawCaption(DrawingContext dc, Point cover, double distance, double coverSize)
    {
        if (string.IsNullOrEmpty(_title)) return;
        double fontSize = Math.Clamp(coverSize * 0.085, 12, 30);
        string key = $"{_title}|{_artist}|{fontSize:0}";
        if (key != _captionKey)
        {
            _captionKey = key;
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            _titleText = new FormattedText(_title, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                face, fontSize, Brushes.White, dip) { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(200, coverSize * 2) };
            _artistText = string.IsNullOrEmpty(_artist) ? null : new FormattedText(_artist, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize * 0.72, Brushes.White, dip)
                { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(200, coverSize * 2) };
        }

        if (_titleText is null) return;
        double gap = 2;
        double blockH = _titleText.Height + (_artistText is null ? 0 : gap + _artistText.Height);

        // x is the anchor; align says which edge of each line sits on it (0 = left, 0.5 = centre, 1 = right).
        (double x, double top, double align) = Look.CaptionPlace switch
        {
            CaptionPlace.Above => (cover.X, cover.Y - distance - blockH, 0.5),
            CaptionPlace.Left => (cover.X - distance - 8, cover.Y - blockH / 2, 1.0),
            CaptionPlace.Right => (cover.X + distance + 8, cover.Y - blockH / 2, 0.0),
            _ => (cover.X, cover.Y + distance, 0.5),
        };
        DrawShadowed(dc, _titleText, x, top, align, 1.0);
        if (_artistText is not null) DrawShadowed(dc, _artistText, x, top + _titleText.Height + gap, align, 0.75);
    }

    /// <summary>Draws text with a dark drop shadow; <paramref name="align"/> 0 = starts at x, 0.5 = centred, 1 = ends at x.</summary>
    private static void DrawShadowed(DrawingContext dc, FormattedText text, double x, double top, double align, double opacity)
    {
        var at = new Point(x - text.Width * align, top);
        text.SetForegroundBrush(Brushes.Black);
        dc.PushOpacity(0.55 * opacity);
        dc.DrawText(text, new Point(at.X + 1.5, at.Y + 1.5));
        dc.Pop();
        text.SetForegroundBrush(Brushes.White);
        dc.PushOpacity(opacity);
        dc.DrawText(text, at);
        dc.Pop();
    }


    private void DrawBackground(DrawingContext dc, double w, double h)
    {
        dc.DrawRectangle(_background, null, new Rect(0, 0, w, h));

        if (Look.BackgroundImage != _imagePath)
        {
            _imagePath = Look.BackgroundImage;
            _image = LoadImage(_imagePath);
        }
        if (_image is null) return;

        // Fill the area and crop the overflow, like Windows' "Fill" wallpaper option.
        double scale = Math.Max(w / _image.PixelWidth, h / _image.PixelHeight);
        double iw = _image.PixelWidth * scale, ih = _image.PixelHeight * scale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawImage(_image, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
        dc.Pop();

        if (Look.BackgroundDim > 0)
        {
            dc.PushOpacity(Math.Clamp(Look.BackgroundDim, 0, 0.95));
            dc.DrawRectangle(_dim, null, new Rect(0, 0, w, h));
            dc.Pop();
        }
    }

    private void DrawBars(DrawingContext dc, double w, double h, Point center)
    {
        var bands = _analyzer.Bands;
        var peaks = _analyzer.Peaks;
        int n = bands.Length;

        double width = BarsWidth(w);
        double maxH = h * 0.62 * Look.Size;
        double groupH = maxH * (Look.Reflection ? 1.3 : 1);
        double baseline = center.Y - groupH / 2 + maxH;
        double left = center.X - width / 2;
        double slot = width / n;
        double barW = Math.Max(1, slot * Math.Clamp(Look.BarWidth, 0.1, 1));

        for (int i = 0; i < n; i++)
        {
            double x = left + i * slot + (slot - barW) / 2;
            double bh = Math.Max(2, bands[i] * maxH);
            var brush = _palette[ColorIndex((double)i / n, 200)];
            dc.DrawRoundedRectangle(brush, null, new Rect(x, baseline - bh, barW, bh), 2, 2);

            if (Look.PeakCaps)
            {
                double py = baseline - peaks[i] * maxH - 6;
                dc.DrawRectangle(brush, null, new Rect(x, py, barW, 2.5));
            }
        }

        if (!Look.Reflection) return;
        dc.PushOpacity(0.16);
        for (int i = 0; i < n; i++)
        {
            double x = left + i * slot + (slot - barW) / 2;
            double rh = bands[i] * maxH * 0.3;
            if (rh < 1) continue;
            dc.DrawRectangle(_palette[ColorIndex((double)i / n, 200)], null, new Rect(x, baseline + 4, barW, rh));
        }
        dc.Pop();
    }

    private void DrawRadial(DrawingContext dc, double w, double h, Point c)
    {
        var bands = _analyzer.Bands;
        int n = bands.Length;
        double size = Math.Min(w, h) * Look.Size;
        double r0 = size * 0.17;
        double r = r0 * (1 + _analyzer.Bass * _analyzer.Bass * 0.35);
        double maxLen = size * 0.28;

        // Two mirrored halves => 2n spokes around the circle. Bar width sets spoke thickness.
        double penW = Math.Max(1.5, 2 * Math.PI * r0 / (2 * n) * Math.Clamp(Look.BarWidth, 0.1, 1) / 0.72 * 0.6);

        for (int i = 0; i < n; i++)
        {
            double len = 3 + bands[i] * maxLen;
            var pen = PenFor(ColorIndex((double)i / n, 240), penW);
            double t = (i + 0.5) / n * Math.PI;
            foreach (double a in new[] { -Math.PI / 2 + t, -Math.PI / 2 - t })
            {
                double cos = Math.Cos(a), sin = Math.Sin(a);
                dc.DrawLine(pen,
                    new Point(c.X + cos * r, c.Y + sin * r),
                    new Point(c.X + cos * (r + len), c.Y + sin * (r + len)));
            }
        }

        var ring = Frozen(new Pen(_palette[ColorIndex(0, 0)], 2));
        dc.DrawEllipse(null, ring, c, Math.Max(1, r - 8), Math.Max(1, r - 8));
    }

    private void DrawWave(DrawingContext dc, double w, double h, Point center)
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
        double width = w * Math.Clamp(Look.Width, 0.1, 1);
        double left = center.X - width / 2;
        double amp = h * 0.38 * 1.6 * Look.Size * Look.Intensity;
        double limit = h * 0.45 * Look.Size;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int p = 0; p < points; p++)
            {
                int idx = start + p * span / points;
                double x = left + p * width / (points - 1);
                double y = center.Y - Math.Clamp(s[idx] * amp, -limit, limit);
                if (p == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, true);
            }
        }
        geo.Freeze();

        // Custom colours: a horizontal gradient along the line. Otherwise one moving hue.
        Brush brush = !Rainbow
            ? Frozen(new LinearGradientBrush(_palette[0].Color, _palette[359].Color, new Point(0, 0), new Point(1, 0)))
            : _palette[ColorIndex(0, 0)];
        var glow = Frozen(new Pen(brush, 10) { LineJoin = PenLineJoin.Round });
        var line = Frozen(new Pen(brush, 2.5) { LineJoin = PenLineJoin.Round });

        dc.PushOpacity(0.18);
        dc.DrawGeometry(null, glow, geo);
        dc.Pop();
        dc.DrawGeometry(null, line, geo);
    }

    /// <summary>Horizontal span of the bar-style modes (the "Width" setting), leaving a small margin.</summary>
    private double BarsWidth(double w) => Math.Max(40, (w - 48) * Math.Clamp(Look.Width, 0.1, 1));

    /// <summary>Bars growing up and down from a centre line.</summary>
    private void DrawMirror(DrawingContext dc, double w, double h, Point center)
    {
        var bands = _analyzer.Bands;
        var peaks = _analyzer.Peaks;
        int n = bands.Length;
        double width = BarsWidth(w);
        double half = h * 0.36 * Look.Size;
        double left = center.X - width / 2;
        double slot = width / n;
        double barW = Math.Max(1, slot * Math.Clamp(Look.BarWidth, 0.1, 1));

        for (int i = 0; i < n; i++)
        {
            double x = left + i * slot + (slot - barW) / 2;
            double bh = Math.Max(1.5, bands[i] * half);
            var brush = _palette[ColorIndex((double)i / n, 200)];
            dc.DrawRoundedRectangle(brush, null, new Rect(x, center.Y - bh, barW, bh * 2), 2, 2);
            if (Look.PeakCaps)
            {
                double p = peaks[i] * half + 5;
                dc.DrawRectangle(brush, null, new Rect(x, center.Y - p - 2.5, barW, 2.5));
                dc.DrawRectangle(brush, null, new Rect(x, center.Y + p, barW, 2.5));
            }
        }
    }

    /// <summary>LED-meter style: each bar is a stack of separate blocks.</summary>
    private void DrawBlocks(DrawingContext dc, double w, double h, Point center)
    {
        var bands = _analyzer.Bands;
        var peaks = _analyzer.Peaks;
        int n = bands.Length;
        const int rows = 20;
        double width = BarsWidth(w);
        double maxH = h * 0.62 * Look.Size;
        double groupH = maxH * (Look.Reflection ? 1.3 : 1);
        double baseline = center.Y - groupH / 2 + maxH;
        double left = center.X - width / 2;
        double slot = width / n;
        double barW = Math.Max(1, slot * Math.Clamp(Look.BarWidth, 0.1, 1));
        double cell = maxH / rows;
        double blockH = Math.Max(1, cell * 0.72);

        for (int i = 0; i < n; i++)
        {
            double x = left + i * slot + (slot - barW) / 2;
            var brush = _palette[ColorIndex((double)i / n, 200)];
            int lit = (int)Math.Round(bands[i] * rows);
            for (int r = 0; r < lit; r++)
                dc.DrawRectangle(brush, null, new Rect(x, baseline - (r + 1) * cell, barW, blockH));
            if (Look.PeakCaps)
            {
                int pr = Math.Min(rows - 1, (int)Math.Round(peaks[i] * rows));
                if (pr >= lit)
                {
                    dc.PushOpacity(0.6);
                    dc.DrawRectangle(brush, null, new Rect(x, baseline - (pr + 1) * cell, barW, blockH));
                    dc.Pop();
                }
            }
            if (Look.Reflection && lit > 0)
            {
                dc.PushOpacity(0.14);
                for (int r = 0; r < Math.Min(lit, 6); r++)
                    dc.DrawRectangle(brush, null, new Rect(x, baseline + 4 + r * cell, barW, blockH));
                dc.Pop();
            }
        }
    }

    /// <summary>A smooth filled curve through the bar tops, like a studio spectrum analyser.</summary>
    private void DrawSpectrum(DrawingContext dc, double w, double h, Point center)
    {
        var bands = _analyzer.Bands;
        int n = bands.Length;
        double width = BarsWidth(w);
        double maxH = h * 0.62 * Look.Size;
        double groupH = maxH * (Look.Reflection ? 1.3 : 1);
        double baseline = center.Y - groupH / 2 + maxH;
        double left = center.X - width / 2;

        Point P(int i) => new(left + (i + 0.5) * width / n, baseline - Math.Max(1, bands[i] * maxH));

        // Quadratic curves through the midpoints between neighbours give a smooth line.
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        foreach (var (geo, closed) in new[] { (line, false), (fill, true) })
        {
            using var ctx = geo.Open();
            var first = P(0);
            if (closed)
            {
                ctx.BeginFigure(new Point(left, baseline), true, true);
                ctx.LineTo(new Point(left, first.Y), false, false);
                ctx.LineTo(first, false, false);
            }
            else ctx.BeginFigure(first, false, false);
            for (int i = 1; i < n; i++)
            {
                var a = P(i - 1);
                var b = P(i);
                ctx.QuadraticBezierTo(a, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), true, true);
            }
            var last = P(n - 1);
            ctx.LineTo(last, true, true);
            if (closed)
            {
                ctx.LineTo(new Point(left + width, last.Y), false, false);
                ctx.LineTo(new Point(left + width, baseline), false, false);
            }
        }
        line.Freeze();
        fill.Freeze();

        var gradient = SpectrumBrush();
        dc.PushOpacity(0.28);
        dc.DrawGeometry(gradient, null, fill);
        dc.Pop();
        dc.DrawGeometry(null, Frozen(new Pen(gradient, 3) { LineJoin = PenLineJoin.Round }), line);

        if (Look.Reflection)
        {
            dc.PushTransform(new MatrixTransform(1, 0, 0, -0.3, 0, baseline * 1.3 + 4));
            dc.PushOpacity(0.12);
            dc.DrawGeometry(gradient, null, fill);
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>The waveform bent into a circle, with the bass pushing the ring outwards.</summary>
    private void DrawRing(DrawingContext dc, double w, double h, Point c)
    {
        var s = _analyzer.Samples;
        int n = s.Length;
        const int points = 360;
        double size = Math.Min(w, h) * Look.Size;
        double r0 = size * 0.22 * (1 + _analyzer.Bass * _analyzer.Bass * 0.25);
        double amp = size * 0.18 * Look.Intensity;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int p = 0; p < points; p++)
            {
                // Blend the end into the start so the ring closes without a jump.
                double t = (double)p / points;
                double v = s[p * (n / 2) / points] * (1 - t) + s[(p * (n / 2) / points + n / 2) % n] * t;
                double r = r0 + Math.Clamp(v * amp, -r0 * 0.6, size * 0.3);
                double a = -Math.PI / 2 + t * 2 * Math.PI;
                var pt = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
                if (p == 0) ctx.BeginFigure(pt, true, true);
                else ctx.LineTo(pt, true, true);
            }
        }
        geo.Freeze();

        var brush = SpectrumBrush();
        dc.PushOpacity(0.12);
        dc.DrawGeometry(brush, null, geo);
        dc.Pop();
        dc.PushOpacity(0.2);
        dc.DrawGeometry(null, Frozen(new Pen(brush, 10) { LineJoin = PenLineJoin.Round }), geo);
        dc.Pop();
        dc.DrawGeometry(null, Frozen(new Pen(brush, 2.5) { LineJoin = PenLineJoin.Round }), geo);
    }

    /// <summary>A left-to-right gradient through the current colours, for line and area styles.</summary>
    private Brush SpectrumBrush()
    {
        var stops = new GradientStopCollection();
        for (int k = 0; k <= 4; k++)
            stops.Add(new GradientStop(_palette[ColorIndex(k / 4.0, 200)].Color, k / 4.0));
        return Frozen(new LinearGradientBrush(stops, 0));
    }

    // ------------------------------------------------------------ helpers

    private static BitmapSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            // Decode no larger than needed for a 4K screen; big photos would waste memory.
            var probe = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (probe.PixelWidth > 3840) img.DecodePixelWidth = 3840;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    public static Color Parse(string? hex, Color fallback)
    {
        try { return hex is null ? fallback : (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return fallback; }
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Like <see cref="Lerp(Color, Color, double)"/>, but always moves at least one step so a glide finishes.</summary>
    private static Color Lerp(Color a, Color b, double t, bool snap)
    {
        static byte Step(byte from, byte to, double t)
        {
            if (from == to) return to;
            double v = from + (to - from) * t;
            int moved = (int)Math.Round(v);
            if (moved == from) moved += Math.Sign(to - from);
            return (byte)moved;
        }
        return Color.FromRgb(Step(a.R, b.R, t), Step(a.G, b.G, t), Step(a.B, b.B, t));
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
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
