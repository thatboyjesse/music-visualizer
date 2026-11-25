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

    /// <summary>Finds the most prominent vivid hue (0–360) in an image, or null if it's mostly grey.</summary>
    public static double? DominantHue(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(32.0 / src.PixelWidth, 32.0 / src.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            var hist = new double[36];
            double total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double delta = max - min;
                double sat = max == 0 ? 0 : delta / max;
                if (sat < 0.25 || max < 0.2) continue;

                double hue = max == r ? 60 * (((g - b) / delta) % 6)
                           : max == g ? 60 * ((b - r) / delta + 2)
                           : 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;

                double weight = sat * max;
                hist[(int)(hue / 10) % 36] += weight;
                total += weight;
            }

            if (total < 3) return null;

            // Smooth neighbouring bins so a hue split across a boundary still wins.
            int best = 0;
            double bestScore = -1;
            for (int i = 0; i < 36; i++)
            {
                double score = hist[(i + 35) % 36] + hist[i] * 2 + hist[(i + 1) % 36];
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best * 10 + 5;
        }
        catch
        {
            return null;
        }
    }
}
