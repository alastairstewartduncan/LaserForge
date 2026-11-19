namespace AsdLasercraft.Core.Imaging;

/// <summary>Small image helpers shared by the smart image tools. Images are row-major float or byte grids.</summary>
public static class Filters
{
    /// <summary>Separable Gaussian blur with edge clamping.</summary>
    public static float[] GaussianBlur(float[] src, int w, int h, double sigma)
    {
        if (sigma < 0.3) return (float[])src.Clone();
        int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = new float[2 * r + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++) { k[i + r] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma)); sum += k[i + r]; }
        for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);

        var tmp = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int i = -r; i <= r; i++)
                {
                    int xx = Math.Clamp(x + i, 0, w - 1);
                    acc += k[i + r] * src[row + xx];
                }
                tmp[row + x] = acc;
            }
        });
        var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float acc = 0;
                for (int i = -r; i <= r; i++)
                {
                    int yy = Math.Clamp(y + i, 0, h - 1);
                    acc += k[i + r] * tmp[yy * w + x];
                }
                dst[y * w + x] = acc;
            }
        });
        return dst;
    }

    /// <summary>Bilinear resample of a float grid (used to scale model masks back up to the photo).</summary>
    public static float[] ResizeBilinear(float[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new float[dw * dh];
        double sx = (double)sw / dw, sy = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            double ty = fy - y0;
            for (int x = 0; x < dw; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                double tx = fx - x0;
                double top = src[y0 * sw + x0] * (1 - tx) + src[y0 * sw + x1] * tx;
                double bot = src[y1 * sw + x0] * (1 - tx) + src[y1 * sw + x1] * tx;
                dst[y * dw + x] = (float)(top * (1 - ty) + bot * ty);
            }
        }
        return dst;
    }

    public static float[] ToFloat(byte[] gray)
    {
        var f = new float[gray.Length];
        for (int i = 0; i < gray.Length; i++) f[i] = gray[i] / 255f;
        return f;
    }

    public static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length];
        for (int i = 0; i < f.Length; i++) b[i] = (byte)Math.Clamp(Math.Round(f[i] * 255), 0, 255);
        return b;
    }

    /// <summary>Value at the given fraction (0–1) of the sorted pixel values.</summary>
    public static byte Percentile(byte[] gray, double fraction)
    {
        if (gray.Length == 0) return 0;
        var hist = new long[256];
        foreach (var v in gray) hist[v]++;
        long target = (long)Math.Clamp(Math.Round(fraction * (gray.Length - 1)), 0, gray.Length - 1);
        long acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc > target) return (byte)i;
        }
        return 255;
    }
}
