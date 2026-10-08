using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Model;

/// <summary>Builders for the basic drawing primitives. All sizes in mm.</summary>
public static class ShapeFactory
{
    public static VectorShape Rectangle(double x, double y, double w, double h, double cornerRadius = 0)
    {
        Normalise(ref x, ref w);
        Normalise(ref y, ref h);
        if (cornerRadius <= 0)
        {
            return new VectorShape(new[]
            {
                new Polyline(new[] { new Vec2(x, y), new Vec2(x + w, y), new Vec2(x + w, y + h), new Vec2(x, y + h) }, true),
            }, "Rectangle");
        }

        double r = Math.Min(cornerRadius, Math.Min(w, h) / 2);
        var pts = new List<Vec2>();
        void Corner(double cx, double cy, double startDeg)
        {
            const int steps = 12;
            for (int i = 0; i <= steps; i++)
            {
                double a = (startDeg + 90.0 * i / steps) * Math.PI / 180;
                pts.Add(new Vec2(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
        }
        Corner(x + w - r, y + r, 270);
        Corner(x + w - r, y + h - r, 0);
        Corner(x + r, y + h - r, 90);
        Corner(x + r, y + r, 180);
        return new VectorShape(new[] { new Polyline(pts, true) }, "Rounded rectangle");
    }

    public static VectorShape Ellipse(double x, double y, double w, double h)
    {
        Normalise(ref x, ref w);
        Normalise(ref y, ref h);
        var pts = Curves.Ellipse(new Vec2(x + w / 2, y + h / 2), w / 2, h / 2);
        return new VectorShape(new[] { new Polyline(pts, true) }, "Ellipse");
    }

    public static VectorShape Line(Vec2 a, Vec2 b) =>
        new(new[] { new Polyline(new[] { a, b }, false) }, "Line");

    public static VectorShape Path(IEnumerable<Vec2> points, bool closed) =>
        new(new[] { new Polyline(points, closed) }, closed ? "Polygon" : "Path");

    /// <summary>Regular polygon (sides ≥ 3) or star (when innerRatio &lt; 1) inscribed in the given box.</summary>
    public static VectorShape Polygon(double x, double y, double w, double h, int sides, double innerRatio = 1.0)
    {
        Normalise(ref x, ref w);
        Normalise(ref y, ref h);
        sides = Math.Max(3, sides);
        bool star = innerRatio < 0.999;
        int n = star ? sides * 2 : sides;
        var pts = new List<Vec2>(n);
        for (int i = 0; i < n; i++)
        {
            double a = -Math.PI / 2 + 2 * Math.PI * i / n;
            double r = star && i % 2 == 1 ? innerRatio : 1.0;
            pts.Add(new Vec2(x + w / 2 + r * w / 2 * Math.Cos(a), y + h / 2 + r * h / 2 * Math.Sin(a)));
        }
        return new VectorShape(new[] { new Polyline(pts, true) }, star ? "Star" : "Polygon");
    }

    private static void Normalise(ref double pos, ref double size)
    {
        if (size < 0) { pos += size; size = -size; }
    }
}
