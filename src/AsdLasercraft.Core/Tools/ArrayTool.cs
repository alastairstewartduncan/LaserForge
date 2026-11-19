using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Tools;

/// <summary>Grid and circular arrays — batches of coasters, keyrings, tags and so on.</summary>
public static class ArrayTool
{
    /// <summary>
    /// Copies the selection into a grid. Spacing is the selection's size plus the gap, so copies never overlap.
    /// Returns the new copies only (the originals stay at row 0, column 0).
    /// </summary>
    public static List<Shape> Grid(IReadOnlyList<Shape> source, int columns, int rows, double gapX, double gapY)
    {
        var result = new List<Shape>();
        if (source.Count == 0) return result;
        var b = BoundsOf(source);
        double stepX = b.Width + gapX, stepY = b.Height + gapY;
        for (int r = 0; r < Math.Max(1, rows); r++)
            for (int c = 0; c < Math.Max(1, columns); c++)
            {
                if (r == 0 && c == 0) continue;
                var t = Transform2D.Translate(c * stepX, r * stepY);
                foreach (var s in source)
                {
                    var copy = s.Clone();
                    copy.ApplyTransform(t);
                    result.Add(copy);
                }
            }
        return result;
    }

    /// <summary>
    /// Copies the selection round a circle centred on <paramref name="centre"/>, <paramref name="count"/> items in total
    /// (including the original). With <paramref name="rotateCopies"/> each copy turns to face outward like spokes;
    /// images can't rotate freely, so they are always moved without turning.
    /// </summary>
    public static List<Shape> Circular(IReadOnlyList<Shape> source, int count, Vec2 centre, bool rotateCopies)
    {
        var result = new List<Shape>();
        if (source.Count == 0 || count < 2) return result;
        var groupCentre = BoundsOf(source).Center;
        for (int k = 1; k < count; k++)
        {
            double deg = 360.0 * k / count;
            var rot = Transform2D.Rotate(deg, centre);
            var movedCentre = rot.Apply(groupCentre);
            var shift = Transform2D.Translate(movedCentre.X - groupCentre.X, movedCentre.Y - groupCentre.Y);
            foreach (var s in source)
            {
                var copy = s.Clone();
                copy.ApplyTransform(rotateCopies && copy is VectorShape ? rot : shift);
                result.Add(copy);
            }
        }
        return result;
    }

    public static Bounds BoundsOf(IEnumerable<Shape> shapes) =>
        shapes.Aggregate(Bounds.Empty, (b, s) => b.Union(s.GetBounds()));
}
