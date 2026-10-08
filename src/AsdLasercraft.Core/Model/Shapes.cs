using System.Text.Json.Serialization;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Imaging;

namespace AsdLasercraft.Core.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(VectorShape), "vector")]
[JsonDerivedType(typeof(ImageShape), "image")]
public abstract class Shape
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LayerId { get; set; }
    public string Name { get; set; } = "";

    public abstract Bounds GetBounds();

    /// <summary>Bakes a transform into the shape's geometry.</summary>
    public abstract void ApplyTransform(Transform2D t);

    public abstract bool HitTest(Vec2 p, double tolerance);

    public abstract Shape Clone();

    public void MoveTo(double x, double y)
    {
        var b = GetBounds();
        ApplyTransform(Transform2D.Translate(x - b.MinX, y - b.MinY));
    }

    /// <summary>Resizes about the top-left corner of the current bounds.</summary>
    public void Resize(double width, double height)
    {
        var b = GetBounds();
        double sx = b.Width > 1e-9 ? width / b.Width : 1;
        double sy = b.Height > 1e-9 ? height / b.Height : 1;
        ApplyTransform(Transform2D.Scale(sx, sy, new Vec2(b.MinX, b.MinY)));
    }

    protected T CloneBase<T>(T copy) where T : Shape
    {
        copy.Id = Guid.NewGuid();
        copy.LayerId = LayerId;
        copy.Name = Name;
        return copy;
    }
}

/// <summary>Any vector geometry: rectangles, ellipses, lines, pen paths, SVG imports and text (as outlines).</summary>
public sealed class VectorShape : Shape
{
    public List<Polyline> Paths { get; set; } = new();

    public VectorShape() { }

    public VectorShape(IEnumerable<Polyline> paths, string name = "")
    {
        Paths = paths.ToList();
        Name = name;
    }

    public override Bounds GetBounds()
    {
        var b = Bounds.Empty;
        foreach (var p in Paths) b = b.Union(p.Bounds);
        return b;
    }

    public override void ApplyTransform(Transform2D t)
    {
        foreach (var path in Paths)
            for (int i = 0; i < path.Points.Count; i++)
                path.Points[i] = t.Apply(path.Points[i]);
    }

    public override bool HitTest(Vec2 p, double tolerance)
    {
        if (!GetBounds().Contains(p, tolerance)) return false;
        foreach (var path in Paths)
        {
            if (path.DistanceTo(p) <= tolerance) return true;
            if (path.Closed && path.ContainsPoint(p)) return true;
        }
        return false;
    }

    public override Shape Clone() => CloneBase(new VectorShape(Paths.Select(p => p.Clone())));
}

/// <summary>
/// A bitmap placed on the bed. Stored as 8-bit greyscale (0 = black/full burn, 255 = white/no burn).
/// Images stay axis-aligned; use <see cref="Rotate90"/> for quarter turns.
/// </summary>
public sealed class ImageShape : Shape
{
    public double X { get; set; }
    public double Y { get; set; }
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }

    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>Row-major greyscale pixels, length = PixelWidth * PixelHeight.</summary>
    public byte[] Gray { get; set; } = Array.Empty<byte>();

    public DitherMode Dither { get; set; } = DitherMode.Jarvis;
    public double Brightness { get; set; }   // -100..100
    public double Contrast { get; set; }     // -100..100
    public double Gamma { get; set; } = 1.0; // 0.2..5
    public bool Invert { get; set; }
    public byte Threshold { get; set; } = 128;

    public override Bounds GetBounds() => Bounds.FromRect(X, Y, WidthMm, HeightMm);

    public override void ApplyTransform(Transform2D t)
    {
        // Images can be moved and scaled; any rotation component is reduced to the transformed bounding box.
        var b = GetBounds();
        var nb = Bounds.Of(new[]
        {
            t.Apply(new Vec2(b.MinX, b.MinY)), t.Apply(new Vec2(b.MaxX, b.MinY)),
            t.Apply(new Vec2(b.MinX, b.MaxY)), t.Apply(new Vec2(b.MaxX, b.MaxY)),
        });
        X = nb.MinX; Y = nb.MinY; WidthMm = nb.Width; HeightMm = nb.Height;
    }

    public override bool HitTest(Vec2 p, double tolerance) => GetBounds().Contains(p, tolerance);

    /// <summary>Rotates the pixels a quarter turn about the image centre.</summary>
    public void Rotate90(bool clockwise)
    {
        int w = PixelWidth, h = PixelHeight;
        var dst = new byte[Gray.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int nx = clockwise ? h - 1 - y : y;
                int ny = clockwise ? x : w - 1 - x;
                dst[ny * h + nx] = Gray[y * w + x];
            }
        var c = GetBounds().Center;
        (PixelWidth, PixelHeight) = (h, w);
        (WidthMm, HeightMm) = (HeightMm, WidthMm);
        X = c.X - WidthMm / 2;
        Y = c.Y - HeightMm / 2;
        Gray = dst;
    }

    public override Shape Clone()
    {
        var c = (ImageShape)MemberwiseClone();
        c.Gray = (byte[])Gray.Clone();
        return CloneBase(c);
    }
}
