using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AsdLasercraft.Core.Gcode;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App.Controls;

/// <summary>
/// A generated job prepared for the simulated preview. Moves are grouped into batches whose geometry is built
/// once, in millimetre space, so drawing a 300 000-move raster at any zoom level stays smooth: finished batches
/// are drawn as a handful of cached geometries and only the batch in progress is drawn move by move.
/// </summary>
public sealed class SimulationData
{
    public const int BatchSize = 1500;
    public const int PowerBuckets = 6;

    public sealed class Batch
    {
        public int Start;          // index of first move
        public int End;            // one past the last move
        public double EndSeconds;  // time at which the batch is complete
        public StreamGeometry?[]? Burns;  // one geometry per power bucket
        public StreamGeometry? Travel;
    }

    public SimulationData(GcodeJob job, double burnWidthMm)
    {
        Job = job;
        Moves = job.Moves;
        TotalSeconds = Moves.Count > 0 ? Moves[^1].EndSeconds : 0;
        BurnWidthMm = Math.Max(0.05, burnWidthMm);
        for (int i = 0; i < Moves.Count; i += BatchSize)
        {
            int end = Math.Min(Moves.Count, i + BatchSize);
            Batches.Add(new Batch { Start = i, End = end, EndSeconds = Moves[end - 1].EndSeconds });
        }
    }

    /// <summary>
    /// A raster image shown as its real dithered/greyscale burn bitmap rather than as thousands of overlapping
    /// scan lines (which would merge into a solid block when zoomed out). Revealed top-down as the head passes.
    /// </summary>
    public sealed class RasterOverlay
    {
        public required BitmapSource Bitmap;
        public Rect RectMm;
        public int FirstMove;
        public int LastMove;
    }

    /// <summary>Image overlays, and the layer indices whose burn moves they replace.</summary>
    public List<RasterOverlay> Rasters { get; } = new();
    public HashSet<int> RasterLayers { get; } = new();

    /// <summary>True when a move's burn is drawn by a raster overlay instead of as a line.</summary>
    public bool IsOverlaid(in JobMove m) => m.PowerFraction > 0 && RasterLayers.Contains(m.LayerIndex);

    /// <summary>
    /// Builds a burn bitmap (orange, alpha = burn strength, transparent where nothing burns) from processed pixels.
    /// </summary>
    public static BitmapSource BurnBitmap(byte[] px, int w, int h, bool grayscale)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i++)
        {
            byte v = px[i];
            if (v >= 255) continue;
            byte a = grayscale ? (byte)(40 + (255 - v) * 215 / 255) : (byte)235;
            bgra[i * 4] = 0x3D;     // B
            bgra[i * 4 + 1] = 0x9A; // G
            bgra[i * 4 + 2] = 0xFF; // R
            bgra[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public GcodeJob Job { get; }
    public IReadOnlyList<JobMove> Moves { get; }
    public double TotalSeconds { get; }
    public double BurnWidthMm { get; }
    public List<Batch> Batches { get; } = new();

    public static int Bucket(float power) => Math.Clamp((int)Math.Ceiling(power * PowerBuckets) - 1, 0, PowerBuckets - 1);

    /// <summary>Builds (once) the cached geometry for a finished batch.</summary>
    public void EnsureGeometry(Batch b)
    {
        if (b.Burns != null) return;
        var burns = new StreamGeometry?[PowerBuckets];
        var ctxs = new StreamGeometryContext?[PowerBuckets];
        var travel = new StreamGeometry();
        using (var tctx = travel.Open())
        {
            for (int i = b.Start; i < b.End; i++)
            {
                var m = Moves[i];
                if (IsOverlaid(m)) continue;
                if (m.PowerFraction > 0)
                {
                    int k = Bucket(m.PowerFraction);
                    if (ctxs[k] == null) { burns[k] = new StreamGeometry(); ctxs[k] = burns[k]!.Open(); }
                    ctxs[k]!.BeginFigure(P(m.From), false, false);
                    ctxs[k]!.LineTo(P(m.To), true, false);
                }
                else if (m.Rapid)
                {
                    tctx.BeginFigure(P(m.From), false, false);
                    tctx.LineTo(P(m.To), true, false);
                }
            }
        }
        for (int k = 0; k < PowerBuckets; k++)
        {
            ctxs[k]?.Close();
            burns[k]?.Freeze();
        }
        travel.Freeze();
        b.Burns = burns;
        b.Travel = travel;
    }

    /// <summary>Index of the move running at time <paramref name="seconds"/> (binary search).</summary>
    public int MoveAt(double seconds)
    {
        int lo = 0, hi = Moves.Count - 1;
        if (hi < 0) return -1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Moves[mid].EndSeconds < seconds) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Head position at a given time.</summary>
    public Vec2? HeadAt(double seconds)
    {
        int i = MoveAt(seconds);
        if (i < 0) return null;
        var m = Moves[i];
        double span = m.EndSeconds - m.StartSeconds;
        double t = span > 1e-12 ? Math.Clamp((seconds - m.StartSeconds) / span, 0, 1) : 1;
        return Vec2.Lerp(m.From, m.To, t);
    }

    public static Point P(Vec2 v) => new(v.X, v.Y);
}
