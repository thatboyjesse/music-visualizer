using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MusicVisualizer.NowPlaying;

public static class ImageUtil
{
    public static BitmapSource? Load(Stream stream)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? Load(byte[] bytes) => Load(new MemoryStream(bytes));

    /// <summary>
    /// The two most prominent colours of an album cover, adjusted to stay visible on a dark background.
    /// Works for every cover, including black-and-white ones (those give greys and white).
    ///
    /// The cover is scaled to 48×48 and every pixel votes into a 6×6×6 RGB grid, weighted towards
    /// colourful pixels and away from dark ones. If the cover has any real colour (a few percent of
    /// it), only colourful cells can win, so a red frame on a dark grey cover gives red, not grey.
    /// The winning cell's average colour is the first colour; the best cell that is clearly
    /// different from it is the second (or a lighter shade of the first if there is none).
    /// </summary>
    public static AlbumColors? Palette(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(48.0 / src.PixelWidth, 48.0 / src.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            const int L = 6;
            var score = new double[L * L * L];
            var sum = new double[L * L * L, 3];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max == 0 ? 0 : (max - min) / max;
                double weight = (0.25 + sat * 1.5) * (max < 0.1 ? 0.1 : max < 0.25 ? 0.4 : 1);
                int cell = (int)(r * (L - 0.001)) * L * L + (int)(g * (L - 0.001)) * L + (int)(b * (L - 0.001));
                score[cell] += weight;
                sum[cell, 0] += r * weight;
                sum[cell, 1] += g * weight;
                sum[cell, 2] += b * weight;
            }

            Color Avg(int c) => Color.FromRgb((byte)(sum[c, 0] / score[c] * 255), (byte)(sum[c, 1] / score[c] * 255), (byte)(sum[c, 2] / score[c] * 255));
            static double Dist(Color a, Color b) =>
                Math.Sqrt(Math.Pow(a.R - b.R, 2) * 0.3 + Math.Pow(a.G - b.G, 2) * 0.59 + Math.Pow(a.B - b.B, 2) * 0.11);

            bool Colourful(int c)
            {
                if (score[c] <= 0) return false;
                var a = Avg(c);
                double mx = Math.Max(a.R, Math.Max(a.G, a.B)) / 255.0, mn = Math.Min(a.R, Math.Min(a.G, a.B)) / 255.0;
                return mx > 0.2 && (mx - mn) / mx > 0.3;
            }

            // Prefer colour whenever the cover has a meaningful amount of it.
            double colourfulTotal = Enumerable.Range(0, score.Length).Where(Colourful).Sum(c => score[c]);
            bool useColour = colourfulTotal > score.Sum() * 0.04;
            var cells = Enumerable.Range(0, score.Length).Where(c => score[c] > 0 && (!useColour || Colourful(c))).ToList();
            if (cells.Count == 0) return null;

            int first = cells.OrderByDescending(c => score[c]).First();
            Color c1 = Avg(first);

            int second = -1;
            foreach (int c in cells)
                if (c != first && score[c] > score[first] * 0.05 && Dist(Avg(c), c1) > 55 && (second < 0 || score[c] > score[second]))
                    second = c;

            c1 = Visible(c1);
            Color c2 = second >= 0 ? Visible(Avg(second)) : Shade(c1);
            if (Dist(c1, c2) < 30) c2 = Shade(c1);
            return new AlbumColors(c1, c2);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Brightens dark colours and lifts dull ones so bars stand out on the dark background; greys stay grey.</summary>
    private static Color Visible(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double sat = max == 0 ? 0 : (max - min) / max;
        double hue = max == min ? 0
                   : max == r ? 60 * (((g - b) / (max - min)) % 6)
                   : max == g ? 60 * ((b - r) / (max - min) + 2)
                   : 60 * ((r - g) / (max - min) + 4);
        if (hue < 0) hue += 360;
        if (sat > 0.15) sat = Math.Max(sat, 0.55);
        return FromHsv(hue, sat, Math.Max(max, 0.85));
    }

    /// <summary>A lighter (or, for very light colours, deeper) version, for single-colour covers.</summary>
    private static Color Shade(Color c)
    {
        double k = (c.R + c.G + c.B) / 765.0 > 0.8 ? 0.6 : 1.0;
        byte S(byte v) => (byte)Math.Clamp(k < 1 ? v * k : v + (255 - v) * 0.55, 0, 255);
        return Color.FromRgb(S(c.R), S(c.G), S(c.B));
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

/// <summary>Two colours taken from an album cover (see <see cref="ImageUtil.Palette"/>).</summary>
public sealed record AlbumColors(Color First, Color Second);

