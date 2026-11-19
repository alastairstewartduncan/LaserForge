namespace AsdLasercraft.Core.Imaging;

/// <summary>
/// A salient-object model: takes a 1×3×S×S normalised image and returns an S×S map where high = subject.
/// The desktop app runs the U²-Net (u2netp) ONNX model on the PC; tests use a fake.
/// </summary>
public interface ISaliencyModel
{
    int InputSize { get; }
    float[] Run(float[] input);
}

/// <summary>
/// Removes the background behind the subject of a photo, leaving it white (= no burn). Two methods: the AI model
/// (works on busy backgrounds) and a plain-background flood fill (works offline with no download, for studio-style
/// shots on a plain wall or sheet).
/// </summary>
public static class BackgroundRemoval
{
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>Builds the model input: area-resized to S×S, scaled by the image max, ImageNet-normalised, grey copied to RGB.</summary>
    public static float[] PrepareInput(byte[] gray, int w, int h, int size)
    {
        var small = ImageProcessor.Resample(gray, w, h, size, size);
        float max = Math.Max((byte)1, small.Max()) / 255f;
        var input = new float[3 * size * size];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < size * size; i++)
                input[c * size * size + i] = (small[i] / 255f / max - Mean[c]) / Std[c];
        return input;
    }

    /// <summary>Normalises a raw model output to 0–1 and scales it up to the photo's size.</summary>
    public static float[] MaskFromOutput(float[] output, int size, int w, int h)
    {
        float lo = output.Min(), hi = output.Max(), range = Math.Max(hi - lo, 1e-6f);
        var norm = new float[output.Length];
        for (int i = 0; i < output.Length; i++) norm[i] = (output[i] - lo) / range;
        return Filters.ResizeBilinear(norm, size, size, w, h);
    }

    public static float[] PredictMask(ISaliencyModel model, byte[] gray, int w, int h)
    {
        int s = model.InputSize;
        var output = model.Run(PrepareInput(gray, w, h, s));
        if (output.Length != s * s) throw new InvalidOperationException($"Model returned {output.Length} values, expected {s * s}.");
        return MaskFromOutput(output, s, w, h);
    }

    /// <summary>
    /// Whitens everything outside the mask. <paramref name="softness"/> (0–1) widens the blend at the edge so hair and
    /// fur fade out rather than being cut off sharply.
    /// </summary>
    public static byte[] ApplyMask(byte[] gray, float[] mask, double softness = 0.5)
    {
        double half = 0.05 + 0.25 * Math.Clamp(softness, 0, 1);
        double lo = 0.5 - half, hi = 0.5 + half;
        var dst = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            double a = Math.Clamp((mask[i] - lo) / (hi - lo), 0, 1);
            dst[i] = (byte)Math.Clamp(Math.Round(255 - (255 - gray[i]) * a), 0, 255);
        }
        return dst;
    }

    /// <summary>
    /// Plain-background mask: everything connected to the image border whose tone is close to the border's typical
    /// tone is background (0); the rest is subject (1). <paramref name="tolerance"/> is in grey levels.
    /// </summary>
    public static float[] PlainBackgroundMask(byte[] gray, int w, int h, int tolerance = 28)
    {
        var border = new List<byte>(2 * (w + h));
        for (int x = 0; x < w; x++) { border.Add(gray[x]); border.Add(gray[(h - 1) * w + x]); }
        for (int y = 0; y < h; y++) { border.Add(gray[y * w]); border.Add(gray[y * w + w - 1]); }
        border.Sort();
        int bg = border[border.Count / 2];

        var isBg = new bool[gray.Length];
        var queue = new Queue<int>();
        void Seed(int i)
        {
            if (!isBg[i] && Math.Abs(gray[i] - bg) <= tolerance) { isBg[i] = true; queue.Enqueue(i); }
        }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % w, y = i / w;
            if (x > 0) Seed(i - 1);
            if (x < w - 1) Seed(i + 1);
            if (y > 0) Seed(i - w);
            if (y < h - 1) Seed(i + w);
        }
        var mask = new float[gray.Length];
        for (int i = 0; i < mask.Length; i++) mask[i] = isBg[i] ? 0f : 1f;
        // A light blur gives a soft edge instead of a jagged pixel staircase.
        return Filters.GaussianBlur(mask, w, h, Math.Max(0.8, Math.Max(w, h) / 800.0));
    }
}
