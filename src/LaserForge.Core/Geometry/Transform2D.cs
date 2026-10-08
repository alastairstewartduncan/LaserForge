namespace LaserForge.Core.Geometry;

/// <summary>
/// 2D affine transform (SVG convention): x' = A*x + C*y + E, y' = B*x + D*y + F.
/// </summary>
public readonly record struct Transform2D(double A, double B, double C, double D, double E, double F)
{
    public static readonly Transform2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Transform2D Translate(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    public static Transform2D Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Transform2D Scale(double sx, double sy, Vec2 origin) =>
        Translate(-origin.X, -origin.Y).Then(Scale(sx, sy)).Then(Translate(origin.X, origin.Y));

    public static Transform2D Rotate(double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    public static Transform2D Rotate(double degrees, Vec2 origin) =>
        Translate(-origin.X, -origin.Y).Then(Rotate(degrees)).Then(Translate(origin.X, origin.Y));

    public static Transform2D SkewX(double degrees) => new(1, 0, Math.Tan(degrees * Math.PI / 180), 1, 0, 0);
    public static Transform2D SkewY(double degrees) => new(1, Math.Tan(degrees * Math.PI / 180), 0, 1, 0, 0);

    /// <summary>Returns the transform that applies <c>this</c> first, then <paramref name="next"/>.</summary>
    public Transform2D Then(Transform2D next) => new(
        next.A * A + next.C * B,
        next.B * A + next.D * B,
        next.A * C + next.C * D,
        next.B * C + next.D * D,
        next.A * E + next.C * F + next.E,
        next.B * E + next.D * F + next.F);

    public Vec2 Apply(Vec2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>Approximate uniform scale factor, used to pick curve flattening tolerance.</summary>
    public double ApproxScale => Math.Sqrt(Math.Abs(A * D - B * C));
}
