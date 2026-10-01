using System.Globalization;

namespace Coo.Mobile.Presentation.Theme;

/// <summary>WCAG 2.x contrast and CIEDE2000 colour difference, for the colour tests (Spec 005 §3).</summary>
public static class ColourMath
{
    public static (double R, double G, double B) Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var h = hex.TrimStart('#');
        if (h.Length != 6)
            throw new FormatException($"'{hex}' is not #RRGGBB.");
        return (Channel(h, 0), Channel(h, 2), Channel(h, 4));

        static double Channel(string h, int at) => int.Parse(h.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
    }

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = Parse(hex);
        return (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));
    }

    /// <summary>The WCAG contrast ratio, from 1 to 21.</summary>
    public static double ContrastRatio(string a, string b)
    {
        var (la, lb) = (RelativeLuminance(a), RelativeLuminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>CIEDE2000 ΔE between two sRGB colours. About 2 is barely visible; 10 is clearly different.</summary>
    public static double DeltaE2000(string a, string b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);

        var c1 = Math.Sqrt((a1 * a1) + (b1 * b1));
        var c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        var cBar = (c1 + c2) / 2;
        var g = 0.5 * (1 - Math.Sqrt(Math.Pow(cBar, 7) / (Math.Pow(cBar, 7) + Math.Pow(25, 7))));
        var a1p = (1 + g) * a1;
        var a2p = (1 + g) * a2;
        var c1p = Math.Sqrt((a1p * a1p) + (b1 * b1));
        var c2p = Math.Sqrt((a2p * a2p) + (b2 * b2));
        var h1p = Hue(b1, a1p);
        var h2p = Hue(b2, a2p);

        var dLp = l2 - l1;
        var dCp = c2p - c1p;
        var dhp = c1p * c2p == 0 ? 0 : Math.Abs(h2p - h1p) <= 180 ? h2p - h1p : h2p - h1p > 180 ? h2p - h1p - 360 : h2p - h1p + 360;
        var dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(Rad(dhp / 2));

        var lBarp = (l1 + l2) / 2;
        var cBarp = (c1p + c2p) / 2;
        var hBarp = c1p * c2p == 0 ? h1p + h2p
            : Math.Abs(h1p - h2p) <= 180 ? (h1p + h2p) / 2
            : h1p + h2p < 360 ? (h1p + h2p + 360) / 2 : (h1p + h2p - 360) / 2;

        var t = 1 - (0.17 * Math.Cos(Rad(hBarp - 30))) + (0.24 * Math.Cos(Rad(2 * hBarp)))
                + (0.32 * Math.Cos(Rad((3 * hBarp) + 6))) - (0.20 * Math.Cos(Rad((4 * hBarp) - 63)));
        var dTheta = 30 * Math.Exp(-Math.Pow((hBarp - 275) / 25, 2));
        var rc = 2 * Math.Sqrt(Math.Pow(cBarp, 7) / (Math.Pow(cBarp, 7) + Math.Pow(25, 7)));
        var sl = 1 + (0.015 * Math.Pow(lBarp - 50, 2) / Math.Sqrt(20 + Math.Pow(lBarp - 50, 2)));
        var sc = 1 + (0.045 * cBarp);
        var sh = 1 + (0.015 * cBarp * t);
        var rt = -Math.Sin(Rad(2 * dTheta)) * rc;

        return Math.Sqrt(Math.Pow(dLp / sl, 2) + Math.Pow(dCp / sc, 2) + Math.Pow(dHp / sh, 2) + (rt * (dCp / sc) * (dHp / sh)));
    }

    private static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static (double L, double A, double B) Lab(string hex)
    {
        var (r, g, b) = Parse(hex);
        var (lr, lg, lb) = (Linear(r), Linear(g), Linear(b));
        var x = ((0.4124 * lr) + (0.3576 * lg) + (0.1805 * lb)) / 0.95047;
        var y = (0.2126 * lr) + (0.7152 * lg) + (0.0722 * lb);
        var z = ((0.0193 * lr) + (0.1192 * lg) + (0.9505 * lb)) / 1.08883;
        var (fx, fy, fz) = (F(x), F(y), F(z));
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : ((24389.0 / 27 * t) + 16) / 116;
    }

    private static double Hue(double b, double a)
    {
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;
}
