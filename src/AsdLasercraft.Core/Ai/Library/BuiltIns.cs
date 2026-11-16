namespace AsdLasercraft.Core.Ai.Library;

/// <summary>Starter skills and agents shipped with the app. Users can edit, duplicate or delete them.</summary>
public static class BuiltIns
{
    public const string DesignerName = "laser-designer";
    public const string CriticName = "burn-critic";
    public const string HelperName = "workshop-helper";

    /// <summary>Source files may be checked out with Windows line endings; text in the library always uses \n.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    public static List<Skill> Skills()
    {
        var list = RawSkills();
        foreach (var s in list) s.Body = Lf(s.Body);
        return list;
    }

    public static List<Agent> Agents()
    {
        var list = RawAgents();
        foreach (var a in list) a.Instructions = Lf(a.Instructions);
        return list;
    }

    private static List<Skill> RawSkills() => new()
    {
        new Skill
        {
            Name = "laser-ready-vectors", Title = "Laser-ready vectors", BuiltIn = true,
            Description = "General rules that make any vector design burn cleanly on a diode laser. Attach to every designer.",
            Body = """
                # Laser-ready vectors

                - Think in silhouettes: bold shapes with clear negative space read better than fine detail once burned.
                - Engraved areas are closed outlines. Keep every engraved feature at least 0.5 mm wide and every gap at least 0.4 mm.
                - Avoid slivers, hairlines and dots smaller than 0.5 mm: they char into blobs or vanish.
                - Leave a clear margin of 2 mm or more between the artwork and the edge of the piece.
                - Lettering under 3 mm tall is hard to read after burning. Prefer bold or semi-bold weights for small text.
                - One idea per piece: a main motif, the wording, and at most one decorative border.
                - Never rely on colour, shading or line thickness. Only the geometry is burned.
                """,
        },
        new Skill
        {
            Name = "plywood-cut-outs", Title = "Plywood cut-outs", BuiltIn = true,
            Description = "Tags, ornaments and keyrings cut from 3 mm plywood: outlines, hanging holes and parts that don't fall apart.",
            Body = """
                # Plywood cut-outs (3 mm)

                - Always include one closed cut outline around the whole piece, in the cut group, with gently rounded corners (radius 2 mm or more).
                - Keep engraving at least 2.5 mm inside the cut outline so the edge char doesn't touch it.
                - Hanging hole: a 3 to 4 mm circle in the cut group, its edge at least 3 mm from the outer outline.
                - Inner cut-outs must leave bridges at least 1.5 mm wide, and nothing may be left floating: the centre of an "O" drops out unless it is bridged, stencil-style.
                - Prefer simple outer shapes (circle, rounded rectangle, shield, hexagon). Spiky outlines snap and scorch.
                """,
        },
        new Skill
        {
            Name = "slate-coasters", Title = "Slate coasters", BuiltIn = true,
            Description = "Engraving on natural slate coasters: light marks on a dark surface, engrave only.",
            Body = """
                # Slate coasters

                - Slate engraves pale grey on a dark background, so the burned areas are the light parts of the picture. Design the light areas.
                - Slate is engraved, never cut, with a diode laser: do not add a cut outline.
                - Typical coaster: 100 mm round or square. Keep the artwork inside 88 mm so the rough, chipped edge stays clear.
                - Medium detail works well; large solid fills look patchy. Break big areas up with pattern or outline them instead.
                - Centre the design: coasters are rarely perfectly square, and an off-centre border shows it.
                """,
        },
        new Skill
        {
            Name = "round-badges", Title = "Round badges", BuiltIn = true,
            Description = "Circular badges and emblems with wording curved around the edge and a motif in the centre.",
            Body = """
                # Round badges

                - Build on concentric circles: an outer ring, a text band, and a centre disc for the motif.
                - Curve the top wording clockwise over the top and the bottom wording so it reads left to right along the bottom.
                - Use capitals with generous letter spacing for curved text, and keep it a constant distance from the ring.
                - Separate top and bottom wording with a small star, dot or dash at 3 and 9 o'clock.
                - Keep the centre motif simple and no wider than 55 % of the badge.
                """,
        },
    };

    private static List<Agent> RawAgents() => new()
    {
        new Agent
        {
            Name = DesignerName, Title = "Laser designer", Role = AgentRole.Designer, BuiltIn = true,
            Description = "Draws clean, bold vector artwork that burns well. The default designer.",
            Skills = { "laser-ready-vectors" },
            Instructions = """
                You are a graphic designer who specialises in artwork for laser engraving and cutting.
                Aim for a clear focal point, balanced composition and confident, simple shapes.
                When a brief is vague, choose the most classic, giftable interpretation rather than the most elaborate one.
                When you are given review feedback, fix every point raised and keep everything that wasn't criticised.
                """,
        },
        new Agent
        {
            Name = CriticName, Title = "Burn critic", Role = AgentRole.Critic, BuiltIn = true,
            Description = "Reviews a design against the brief and laser rules before it is shown to you. The default critic.",
            Skills = { "laser-ready-vectors" },
            Instructions = """
                You are a meticulous reviewer of laser artwork. You check a design against the brief and against what will actually burn well.
                Look for: wording that is wrong, missing or misspelt; elements the brief asked for that are absent; features too small or too thin to burn;
                shapes meant to be solid that are left open; crowded or unbalanced layout; anything running off the piece.
                Be strict about real problems and relaxed about taste. Accept a design that is good enough to burn; do not ask for changes for their own sake.
                """,
        },
        new Agent
        {
            Name = HelperName, Title = "Workshop helper", Role = AgentRole.General, BuiltIn = true,
            Description = "The standard assistant: advice on settings and materials, and edits to your design.",
            Tools = { Agent.AllTools },
            Instructions = """
                Help the user get a good result from their diode laser with the least fuss. Prefer their saved material presets,
                suggest a test grid when a material is new to them, and check the job before they burn.
                """,
        },
    };
}
