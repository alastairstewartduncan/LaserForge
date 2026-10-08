using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Imaging;

public enum DitherMode
{
    /// <summary>Variable power per pixel. Good for wood/leather that shows tone; needs dynamic power.</summary>
    Grayscale,
    Threshold,
    FloydSteinberg,
    Jarvis,
    Stucki,
    Atkinson,
}

/// <summary>Image pipeline: adjust → resample to the layer's line interval → dither. 0 = burn, 255 = no burn.</summary>
public static class ImageProcessor
{
    /// <summary>
    /// Produces the pixel grid actually burned for an image shape: one pixel per line interval in both axes.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Prepare(ImageShape img, double lineIntervalMm)
    {
        lineIntervalMm = Math.Max(lineIntervalMm, 0.01);
        int w = Math.Max(1, (int)Math.Round(img.WidthMm / lineIntervalMm));
        int h = Math.Max(1, (int)Math.Round(img.HeightMm / lineIntervalMm));
        var adjusted = Adjust(img.Gray, img.Brightness, img.Contrast, img.Gamma, img.Invert);
        var resized = Resample(adjusted, img.PixelWidth, img.PixelHeight, w, h);
        return (Dither(resized, w, h, img.Dither, img.Threshold), w, h);
    }

    /// <summary>Brightness/contrast in -100..100, gamma &gt; 0 (values &gt; 1 lighten mid-tones).</summary>
    public static byte[] Adjust(byte[] gray, double brightness, double contrast, double gamma, bool invert)
    {
        var lut = new byte[256];
        double c = Math.Clamp(contrast, -100, 100) / 100.0;
        double contrastFactor = c >= 0 ? 1 + c * 3 : 1 + c; // up to 4x, down to 0x
        double b = Math.Clamp(brightness, -100, 100) / 100.0 * 255;
        double g = Math.Clamp(gamma <= 0 ? 1 : gamma, 0.1, 10);
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            v = Math.Pow(v, 1.0 / g);
            v = (v - 0.5) * contrastFactor + 0.5;
            v = v * 255 + b;
            if (invert) v = 255 - v;
            lut[i] = (byte)Math.Clamp(Math.Round(v), 0, 255);
        }
        var dst = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++) dst[i] = lut[gray[i]];
        return dst;
    }

    /// <summary>Area-average when shrinking, bilinear when enlarging.</summary>
    public static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (byte[])src.Clone();
        var dst = new byte[dw * dh];
        double sx = (double)sw / dw, sy = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                double v;
                if (sx > 1 || sy > 1)
                {
                    int x0 = (int)Math.Floor(x * sx), x1 = Math.Max(x0 + 1, (int)Math.Ceiling((x + 1) * sx));
                    int y0 = (int)Math.Floor(y * sy), y1 = Math.Max(y0 + 1, (int)Math.Ceiling((y + 1) * sy));
                    x1 = Math.Min(x1, sw); y1 = Math.Min(y1, sh);
                    long sum = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++) { sum += src[yy * sw + xx]; n++; }
                    v = n > 0 ? (double)sum / n : 255;
                }
                else
                {
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, sw - 1);
                    double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, sh - 1);
                    int ix = (int)fx, iy = (int)fy;
                    int ix1 = Math.Min(ix + 1, sw - 1), iy1 = Math.Min(iy + 1, sh - 1);
                    double tx = fx - ix, ty = fy - iy;
                    double top = src[iy * sw + ix] * (1 - tx) + src[iy * sw + ix1] * tx;
                    double bot = src[iy1 * sw + ix] * (1 - tx) + src[iy1 * sw + ix1] * tx;
                    v = top * (1 - ty) + bot * ty;
                }
                dst[y * dw + x] = (byte)Math.Clamp(Math.Round(v), 0, 255);
            }
        }
        return dst;
    }

    // Error-diffusion kernels: (dx, dy, weight); divisor is the sum of weights for that algorithm.
    private static readonly (int dx, int dy, int w)[] FloydKernel = { (1, 0, 7), (-1, 1, 3), (0, 1, 5), (1, 1, 1) };
    private static readonly (int dx, int dy, int w)[] JarvisKernel =
    {
        (1, 0, 7), (2, 0, 5),
        (-2, 1, 3), (-1, 1, 5), (0, 1, 7), (1, 1, 5), (2, 1, 3),
        (-2, 2, 1), (-1, 2, 3), (0, 2, 5), (1, 2, 3), (2, 2, 1),
    };
    private static readonly (int dx, int dy, int w)[] StuckiKernel =
    {
        (1, 0, 8), (2, 0, 4),
        (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2),
        (-2, 2, 1), (-1, 2, 2), (0, 2, 4), (1, 2, 2), (2, 2, 1),
    };
    // Atkinson deliberately diffuses only 6/8 of the error, giving crisper highlights.
    private static readonly (int dx, int dy, int w)[] AtkinsonKernel = { (1, 0, 1), (2, 0, 1), (-1, 1, 1), (0, 1, 1), (1, 1, 1), (0, 2, 1) };

    public static byte[] Dither(byte[] gray, int w, int h, DitherMode mode, byte threshold = 128)
    {
        switch (mode)
        {
            case DitherMode.Grayscale:
                return (byte[])gray.Clone();
            case DitherMode.Threshold:
                return gray.Select(v => v < threshold ? (byte)0 : (byte)255).ToArray();
            case DitherMode.FloydSteinberg:
                return Diffuse(gray, w, h, FloydKernel, 16, threshold);
            case DitherMode.Jarvis:
                return Diffuse(gray, w, h, JarvisKernel, 48, threshold);
            case DitherMode.Stucki:
                return Diffuse(gray, w, h, StuckiKernel, 42, threshold);
            case DitherMode.Atkinson:
                return Diffuse(gray, w, h, AtkinsonKernel, 8, threshold);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static byte[] Diffuse(byte[] gray, int w, int h, (int dx, int dy, int w)[] kernel, int divisor, byte threshold)
    {
        var buf = new float[gray.Length];
        for (int i = 0; i < gray.Length; i++) buf[i] = gray[i];
        var dst = new byte[gray.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float old = buf[i];
                byte nv = old < threshold ? (byte)0 : (byte)255;
                dst[i] = nv;
                float err = old - nv;
                foreach (var (dx, dy, wt) in kernel)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= w || ny >= h) continue;
                    buf[ny * w + nx] += err * wt / divisor;
                }
            }
        }
        return dst;
    }
}
