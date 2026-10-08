namespace AsdLasercraft.Core.Geometry;

/// <summary>Curve flattening helpers. Tolerance is the maximum chord deviation in mm.</summary>
public static class Curves
{
    public const double DefaultTolerance = 0.02;

    public static IEnumerable<Vec2> Cubic(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance = DefaultTolerance)
    {
        // Pick segment count from control polygon length; cheap and good enough for laser work.
        double len = Vec2.Distance(p0, p1) + Vec2.Distance(p1, p2) + Vec2.Distance(p2, p3);
        int n = SegmentsFor(len, tolerance);
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            yield return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }
    }

    public static IEnumerable<Vec2> Quadratic(Vec2 p0, Vec2 p1, Vec2 p2, double tolerance = DefaultTolerance)
    {
        double len = Vec2.Distance(p0, p1) + Vec2.Distance(p1, p2);
        int n = SegmentsFor(len, tolerance);
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            yield return p0 * (u * u) + p1 * (2 * u * t) + p2 * (t * t);
        }
    }

    /// <summary>Full ellipse as a closed polyline (first point not repeated).</summary>
    public static List<Vec2> Ellipse(Vec2 center, double rx, double ry, double tolerance = DefaultTolerance)
    {
        double r = Math.Max(Math.Abs(rx), Math.Abs(ry));
        int n = Math.Max(16, (int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - tolerance / Math.Max(r, 1e-6)))));
        n = Math.Min(n, 2048);
        var pts = new List<Vec2>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts.Add(new Vec2(center.X + rx * Math.Cos(a), center.Y + ry * Math.Sin(a)));
        }
        return pts;
    }

    /// <summary>
    /// SVG elliptical arc (endpoint parameterisation, SVG spec F.6.5). Yields points after the start point.
    /// </summary>
    public static IEnumerable<Vec2> SvgArc(Vec2 p1, double rx, double ry, double xAxisRotationDeg,
                                           bool largeArc, bool sweep, Vec2 p2, double tolerance = DefaultTolerance)
    {
        if (p1 == p2) yield break;
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        if (rx < 1e-9 || ry < 1e-9) { yield return p2; yield break; }

        double phi = xAxisRotationDeg * Math.PI / 180;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
        double dx = (p1.X - p2.X) / 2, dy = (p1.Y - p2.Y) / 2;
        double x1p = cosPhi * dx + sinPhi * dy;
        double y1p = -sinPhi * dx + cosPhi * dy;

        double lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; }

        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double coef = Math.Sqrt(Math.Max(0, num / den)) * (largeArc == sweep ? -1 : 1);
        double cxp = coef * (rx * y1p / ry);
        double cyp = coef * (-ry * x1p / rx);
        double cx = cosPhi * cxp - sinPhi * cyp + (p1.X + p2.X) / 2;
        double cy = sinPhi * cxp + cosPhi * cyp + (p1.Y + p2.Y) / 2;

        double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double dTheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
        else if (sweep && dTheta < 0) dTheta += 2 * Math.PI;

        double arcLen = Math.Abs(dTheta) * Math.Max(rx, ry);
        int n = SegmentsFor(arcLen, tolerance);
        for (int i = 1; i <= n; i++)
        {
            double t = theta1 + dTheta * i / n;
            double x = rx * Math.Cos(t), y = ry * Math.Sin(t);
            yield return i == n ? p2 : new Vec2(cosPhi * x - sinPhi * y + cx, sinPhi * x + cosPhi * y + cy);
        }
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        return a;
    }

    private static int SegmentsFor(double length, double tolerance) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(length / Math.Max(tolerance, 1e-4)) * 1.5), 2, 512);
}
