namespace AsdLasercraft.Core.Imaging;

public sealed class LineArtOptions
{
    /// <summary>0–1: higher keeps finer detail (thinner, more lines).</summary>
    public double Detail { get; set; } = 0.5;
    /// <summary>0–1: higher gives darker, bolder lines and picks up fainter edges.</summary>
    public double Strength { get; set; } = 0.5;
    /// <summary>0–1: 0 = lines only; higher fills the darkest areas solid (hair, shadows).</summary>
    public double Shading { get; set; }
}

/// <summary>
/// Photo → line art: a difference-of-Gaussians edge filter (the classic "sketch" effect) that turns a busy photo
/// into clean dark lines on white. Line drawings engrave far crisper than dithered photos on wood.
/// Runs entirely on the PC.
/// </summary>
public static class LineArt
{
    public static byte[] Apply(byte[] gray, int w, int h, LineArtOptions? options = null)
    {
        var o = options ?? new LineArtOptions();
        double detail = Math.Clamp(o.Detail, 0, 1), strength = Math.Clamp(o.Strength, 0, 1), shading = Math.Clamp(o.Shading, 0, 1);
        var f = Filters.ToFloat(gray);

        // Scale the filter with the image so results look similar at any resolution.
        double sigma = Math.Max(0.7, Math.Max(w, h) / 900.0) * (1.8 - 1.2 * detail);
        var g1 = Filters.GaussianBlur(f, w, h, sigma);
        var g2 = Filters.GaussianBlur(f, w, h, sigma * 1.6);
        double tau = 0.985 - 0.02 * strength;  // lower → more edges count as lines
        double phi = 60 + 140 * strength;      // steepness of the line edge

        float[]? tone = null;
        double shadeThreshold = 0.08 + 0.22 * shading;
        if (shading > 0) tone = Filters.GaussianBlur(f, w, h, sigma * 2);

        var dst = new byte[gray.Length];
        for (int i = 0; i < dst.Length; i++)
        {
            double d = g1[i] - tau * g2[i];
            double v = d > 0 ? 1.0 : 1.0 + Math.Tanh(phi * d);
            if (tone != null && tone[i] < shadeThreshold) v = 0;
            dst[i] = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }
        return dst;
    }
}
