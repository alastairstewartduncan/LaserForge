namespace AsdLasercraft.Core.Imaging;

/// <summary>How a material responds to the laser, for tuning photos.</summary>
public sealed record MaterialResponse(string Name, double Gamma, bool Invert, DitherMode Dither, string Why);

public sealed record AutoTuneResult(double Brightness, double Contrast, double Gamma, bool Invert, DitherMode Dither, MaterialResponse Material, string Explanation);

/// <summary>
/// One-click photo settings: stretches the photo's tonal range to use the full burn range, then applies the
/// material's response (wood darkens mid-tones so needs lightening; slate and black anodised aluminium mark
/// white so need inverting). The result only sets the existing image sliders, so it's fully adjustable after.
/// </summary>
public static class AutoTune
{
    public static readonly MaterialResponse Wood = new("Wood", 1.45, false, DitherMode.Jarvis,
        "Wood darkens quickly, so mid-tones are lightened to keep detail out of the shadows.");
    public static readonly MaterialResponse Mdf = new("MDF", 1.25, false, DitherMode.Jarvis,
        "MDF burns evenly dark; a little lightening keeps mid-tones separate.");
    public static readonly MaterialResponse Leather = new("Leather", 1.3, false, DitherMode.Stucki,
        "Leather darkens and spreads, so tones are lightened and a smoother dither is used.");
    public static readonly MaterialResponse Card = new("Paper / card", 1.6, false, DitherMode.Atkinson,
        "Paper scorches easily; strong lightening and Atkinson dithering keep highlights clean.");
    public static readonly MaterialResponse MarksLight = new("Marks light", 0.9, true, DitherMode.Jarvis,
        "This material turns light where the laser hits, so the image is inverted.");
    public static readonly MaterialResponse General = new("General", 1.2, false, DitherMode.Jarvis,
        "General settings: full tonal range with slightly lifted mid-tones.");

    /// <summary>Guesses the response from a material or layer name like "Plywood (birch) 3 mm – Photo".</summary>
    public static MaterialResponse ResponseFor(string? material)
    {
        var m = (material ?? "").ToLowerInvariant();
        bool Has(params string[] words) => words.Any(m.Contains);
        if (Has("slate", "anodis", "anodiz", "black acrylic", "acrylic (black", "black tile", "painted", "granite", "marble black", "powder coat"))
            return MarksLight;
        if (Has("mdf", "hardboard")) return Mdf;
        if (Has("leather", "cork")) return Leather;
        if (Has("paper", "card")) return Card;
        if (Has("wood", "ply", "birch", "basswood", "oak", "walnut", "maple", "bamboo", "pine", "cherry", "beech")) return Wood;
        return General;
    }

    public static AutoTuneResult Tune(byte[] gray, string? material)
    {
        var response = ResponseFor(material);
        double g = response.Gamma;
        double lo = Math.Pow(Filters.Percentile(gray, 0.01) / 255.0, 1 / g);
        double hi = Math.Pow(Filters.Percentile(gray, 0.99) / 255.0, 1 / g);

        // ImageProcessor.Adjust: v' = (v^(1/g) − 0.5)·cf + 0.5 + b, with cf = 1 + 3c (c ≥ 0) or 1 + c (c < 0).
        double cf = hi - lo > 0.02 ? 1 / (hi - lo) : 1;
        double contrast = cf >= 1 ? (cf - 1) / 3 * 100 : (cf - 1) * 100;
        contrast = Math.Round(Math.Clamp(contrast, -100, 100));
        double c = contrast / 100;
        cf = c >= 0 ? 1 + c * 3 : 1 + c;
        // Darkest tones → black. If contrast hit its limit the range can't fill 0–1, so centre it instead.
        double low = (lo - 0.5) * cf + 0.5;
        double span = (hi - lo) * cf;
        double shift = span >= 0.999 ? -low : 0.5 - ((lo + hi) / 2 - 0.5) * cf - 0.5;
        double brightness = Math.Round(Math.Clamp(shift * 100, -100, 100));

        string expl = response.Why + (contrast > 5 ? " Contrast was raised to use the full burn range." : "");
        return new AutoTuneResult(brightness, contrast, Math.Round(g, 2), response.Invert, response.Dither, response, expl);
    }

    public static void ApplyTo(AutoTuneResult r, Model.ImageShape img)
    {
        img.Brightness = r.Brightness;
        img.Contrast = r.Contrast;
        img.Gamma = r.Gamma;
        img.Invert = r.Invert;
        img.Dither = r.Dither;
    }
}
