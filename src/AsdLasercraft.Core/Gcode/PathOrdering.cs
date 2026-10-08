using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Gcode;

/// <summary>
/// Orders cut paths so that (1) inner shapes are cut before the shapes that contain them — otherwise the part
/// drops out of the sheet before its holes are cut — and (2) travel between paths is short (greedy nearest-neighbour).
/// </summary>
public static class PathOrdering
{
    public static List<Polyline> Order(IReadOnlyList<Polyline> paths, Vec2 start)
    {
        int n = paths.Count;
        var bounds = paths.Select(p => p.Bounds).ToArray();
        var depth = new int[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j && paths[j].Closed && bounds[j].Contains(bounds[i]) && bounds[j].Area > bounds[i].Area
                    && paths[j].ContainsPoint(paths[i].Points[0]))
                    depth[i]++;

        var result = new List<Polyline>(n);
        var pos = start;
        foreach (var group in Enumerable.Range(0, n).GroupBy(i => depth[i]).OrderByDescending(g => g.Key))
        {
            var remaining = group.ToList();
            while (remaining.Count > 0)
            {
                int bestIdx = 0, bestVertex = 0;
                bool bestReverse = false;
                double best = double.PositiveInfinity;
                for (int k = 0; k < remaining.Count; k++)
                {
                    var p = paths[remaining[k]];
                    if (p.Closed)
                    {
                        // Any vertex of a closed path can be the start point.
                        for (int v = 0; v < p.Points.Count; v++)
                        {
                            double d = Vec2.Distance(pos, p.Points[v]);
                            if (d < best) { best = d; bestIdx = k; bestVertex = v; bestReverse = false; }
                        }
                    }
                    else
                    {
                        double d0 = Vec2.Distance(pos, p.Points[0]);
                        double d1 = Vec2.Distance(pos, p.Points[^1]);
                        if (d0 < best) { best = d0; bestIdx = k; bestVertex = 0; bestReverse = false; }
                        if (d1 < best) { best = d1; bestIdx = k; bestVertex = 0; bestReverse = true; }
                    }
                }

                var chosen = paths[remaining[bestIdx]];
                remaining.RemoveAt(bestIdx);
                Polyline output;
                if (chosen.Closed && bestVertex != 0)
                    output = new Polyline(chosen.Points.Skip(bestVertex).Concat(chosen.Points.Take(bestVertex)), true);
                else if (bestReverse)
                    output = new Polyline(Enumerable.Reverse(chosen.Points), false);
                else
                    output = chosen;
                result.Add(output);
                pos = output.Closed ? output.Points[0] : output.Points[^1];
            }
        }
        return result;
    }
}
