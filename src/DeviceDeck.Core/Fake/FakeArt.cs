using DeviceDeck.Core.Imaging;

namespace DeviceDeck.Core.Fake;

/// <summary>Placeholder pictures for the fake phones: soft two-colour landscapes, rounded app tiles, a lock-screen-like screenshot.</summary>
internal static class FakeArt
{
    public static byte[] Photo(string seed, int width, int height, bool dark)
    {
        var hash = StableHash(seed);
        var (r1, g1, b1) = Hsl(hash % 360, 0.55, dark ? 0.28 : 0.62);
        var (r2, g2, b2) = Hsl((hash / 360 + 140) % 360, 0.50, dark ? 0.12 : 0.38);
        var sunX = 0.2 + (hash % 61) / 100.0;
        var sunY = 0.18 + (hash % 23) / 100.0;
        var hill = 0.55 + (hash % 17) / 100.0;

        return PngWriter.Encode(width, height, (x, y) =>
        {
            double u = (double)x / width, v = (double)y / height;
            var t = Math.Clamp(v * 0.85 + u * 0.15, 0, 1);
            double r = Lerp(r1, r2, t), g = Lerp(g1, g2, t), b = Lerp(b1, b2, t);

            var dx = (u - sunX) * width / height;
            var dy = v - sunY;
            if (dx * dx + dy * dy < 0.012)
            {
                r = Lerp(r, 255, 0.75);
                g = Lerp(g, 244, 0.75);
                b = Lerp(b, 214, 0.75);
            }

            if (v > hill + 0.08 * Math.Sin(u * 6.3 + hash % 7))
            {
                r *= 0.55;
                g *= 0.6;
                b *= 0.65;
            }

            return Rgba(r, g, b, 255);
        });
    }

    public static byte[] AppIcon(string seed, int size, (int R, int G, int B) colour)
    {
        var hash = StableHash(seed);
        var radius = size * 0.225;
        return PngWriter.Encode(size, size, (x, y) =>
        {
            var alpha = RoundedRectCoverage(x + 0.5, y + 0.5, size, size, radius);
            if (alpha <= 0)
            {
                return 0;
            }

            double v = (double)y / size;
            var shade = 1.08 - v * 0.2;
            var r = colour.R * shade;
            var g = colour.G * shade;
            var b = colour.B * shade;

            // A simple glyph: a white ring whose size varies per app.
            double cx = x - size / 2.0, cy = y - size / 2.0;
            var d = Math.Sqrt(cx * cx + cy * cy) / size;
            var ring = 0.16 + (hash % 9) / 100.0;
            if (Math.Abs(d - ring) < 0.045)
            {
                r = g = b = 255;
            }

            return Rgba(r, g, b, alpha * 255);
        });
    }

    public static byte[] Screenshot(string seed, int width, int height)
    {
        var hash = StableHash(seed);
        var (r1, g1, b1) = Hsl(hash % 360, 0.6, 0.45);
        var (r2, g2, b2) = Hsl((hash + 60) % 360, 0.6, 0.18);
        return PngWriter.Encode(width, height, (x, y) =>
        {
            double v = (double)y / height;
            double r = Lerp(r1, r2, v), g = Lerp(g1, g2, v), b = Lerp(b1, b2, v);

            // Clock block and a dock, so it reads as a phone screen at a glance.
            var inClock = y > height * 0.12 && y < height * 0.2 && x > width * 0.28 && x < width * 0.72;
            var inDock = y > height * 0.86 && y < height * 0.95 && x > width * 0.06 && x < width * 0.94;
            if (inClock)
            {
                r = g = b = 245;
            }
            else if (inDock)
            {
                r = Lerp(r, 255, 0.35);
                g = Lerp(g, 255, 0.35);
                b = Lerp(b, 255, 0.35);
            }

            return Rgba(r, g, b, 255);
        });
    }

    public static int StableHash(string s)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in s)
            {
                h = (h ^ c) * 16777619u;
            }

            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static double RoundedRectCoverage(double x, double y, double w, double h, double r)
    {
        var qx = Math.Max(Math.Abs(x - w / 2) - (w / 2 - r), 0);
        var qy = Math.Max(Math.Abs(y - h / 2) - (h / 2 - r), 0);
        var dist = Math.Sqrt(qx * qx + qy * qy) - r;
        return Math.Clamp(0.5 - dist, 0, 1);
    }

    private static (double R, double G, double B) Hsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var hp = h / 60.0;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        var (r, g, b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = l - c / 2;
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static uint Rgba(double r, double g, double b, double a) =>
        ((uint)Math.Clamp(r, 0, 255) << 24) | ((uint)Math.Clamp(g, 0, 255) << 16) | ((uint)Math.Clamp(b, 0, 255) << 8) | (uint)Math.Clamp(a, 0, 255);
}
