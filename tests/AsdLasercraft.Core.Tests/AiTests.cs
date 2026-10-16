using System.Net;
using System.Text.Json.Nodes;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Generators;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Grbl;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Import;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.Core.Tests;

internal static class ClaudeClientTests
{
    [Test] static void BuildsMessagesBodyWithImagesAndTools()
    {
        var req = new AiRequest
        {
            System = "sys",
            MaxTokens = 123,
            Messages = { AiMessage.User(new AiImage("image/png", new byte[] { 1, 2, 3 }), new AiText("hello")) },
            Tools = { new AiTool("t", "desc", AiJson.Schema(("a", AiJson.NumberProp("x"), true))) },
        };
        var body = ClaudeClient.BuildBody(req, "claude-test");
        Assert.Equal("claude-test", body["model"]!.GetValue<string>());
        Assert.Equal(123, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("sys", body["system"]!.GetValue<string>());
        var content = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("AQID", content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("hello", content[1]!["text"]!.GetValue<string>());
        Assert.Equal("t", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("a", body["tools"]![0]!["input_schema"]!["required"]![0]!.GetValue<string>());
        // Building twice must not fail (schemas are cloned, not re-parented).
        ClaudeClient.BuildBody(req, "claude-test");
    }

    [Test] static void ParsesTextAndToolUse()
    {
        var r = ClaudeClient.ParseResponse("""
            {"content":[{"type":"text","text":"Sure."},{"type":"tool_use","id":"tu_1","name":"add_text","input":{"text":"Hi","size_mm":12}}],
             "stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":5}}
            """);
        Assert.Equal("Sure.", r.Text);
        Assert.Equal("tool_use", r.StopReason);
        var tu = r.ToolUses.Single();
        Assert.Equal("add_text", tu.Name);
        Assert.Equal(12.0, tu.Input.Num("size_mm")!.Value);
        Assert.Equal(15, r.Usage.InputTokens + r.Usage.OutputTokens);
    }

    [Test] static void MapsHttpErrorsToFriendlyMessages()
    {
        Assert.Equal(AiErrorKind.BadKey, ClaudeClient.ErrorFor(HttpStatusCode.Unauthorized, "{}").Kind);
        Assert.Equal(AiErrorKind.RateLimited, ClaudeClient.ErrorFor((HttpStatusCode)429, "").Kind);
        Assert.Equal(AiErrorKind.Overloaded, ClaudeClient.ErrorFor((HttpStatusCode)529, "").Kind);
        var bad = ClaudeClient.ErrorFor(HttpStatusCode.BadRequest, """{"error":{"message":"max_tokens too big"}}""");
        Assert.True(bad.Message.Contains("max_tokens too big"), bad.Message);
    }

    [Test] static void NoKeyMeansNotConfigured()
    {
        var c = new ClaudeClient(() => "  ");
        Assert.True(!c.IsConfigured);
        try { c.SendAsync(new AiRequest()).GetAwaiter().GetResult(); throw new Exception("expected AiException"); }
        catch (AiException e) { Assert.Equal(AiErrorKind.NoKey, e.Kind); }
    }

    [Test] static void ExtractsJsonFromReplies()
    {
        Assert.Equal(3, AiJson.ExtractObject("Here you go:\n```json\n{\"row\": 3}\n```")!.Int("row"));
        Assert.Equal(2, AiJson.ExtractObject("blah {\"row\": \"2\", \"x\": {\"y\": 1}} trailing")!.Int("row"));
        Assert.True(AiJson.ExtractObject("no json here") == null);
    }
}

internal static class DesignGeneratorTests
{
    private const string Reply = """
        A round badge with a star and the word OAK.
        ```svg
        <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
          <g id="cut"><circle cx="50" cy="50" r="48"/></g>
          <path d="M50 20 L60 45 L85 45 L65 60 L72 85 L50 70 L28 85 L35 60 L15 45 L40 45 Z"/>
          <path d="M20 90 L80 90"/>
          <text x="50" y="40" font-family="Georgia" font-size="10" text-anchor="middle" font-weight="bold">OAK</text>
        </svg>
        ```
        """;

    [Test] static void ParsesSvgIntoClassifiedScaledShapes()
    {
        var r = DesignGenerator.Parse(Reply, new DesignRequest { WidthMm = 50, Style = DesignStyle.EngraveAndCut }, PlaceholderTextOutliner.Instance);
        Assert.Equal(1, r.Cut.Count, "cut outline");
        Assert.Equal(1, r.Closed.Count, "star");
        Assert.Equal(1, r.Open.Count, "line");
        Assert.Equal(1, r.Text.Count, "text placeholder");
        Assert.True(Math.Abs(r.Bounds.Width - 50) < 0.01, $"width {r.Bounds.Width}");
        Assert.True(r.All.All(p => p.Bounds.MinX >= -0.01 && p.Bounds.MinY >= -0.01), "top-left at origin");
        Assert.True(r.Notes.StartsWith("A round badge"), r.Notes);
        // Text is centred on x = 50 of 100 → 25 mm after scaling to half size.
        Assert.True(Math.Abs(r.Text[0].Bounds.Center.X - 25) < 0.5, $"text centre {r.Text[0].Bounds.Center.X}");
    }

    [Test] static void MissingSvgIsAFriendlyError()
    {
        try { DesignGenerator.Parse("Sorry, I can't draw that.", new DesignRequest(), PlaceholderTextOutliner.Instance); throw new Exception("expected"); }
        catch (AiException e) { Assert.Equal(AiErrorKind.BadResponse, e.Kind); }
    }

    [Test] static void BrokenSvgIsAFriendlyError()
    {
        try { DesignGenerator.Parse("```svg\n<svg><path d='M0 0 L1 1'></svg>\n```", new DesignRequest(), PlaceholderTextOutliner.Instance); throw new Exception("expected"); }
        catch (AiException e) { Assert.Equal(AiErrorKind.BadResponse, e.Kind); }
    }

    [Test] static void RefineSendsHistoryAndKeepsSize()
    {
        var fake = new FakeAiClient(_ => AiResponse.FromText(Reply));
        var gen = new DesignGenerator(fake);
        gen.GenerateAsync(new DesignRequest { Description = "oak badge", WidthMm = 60, AvailableFonts = new[] { "Georgia", "Arial" } }).GetAwaiter().GetResult();
        Assert.True(gen.HasDesign);
        var r2 = gen.RefineAsync("make the star bigger").GetAwaiter().GetResult();
        Assert.Equal(2, fake.Requests.Count);
        var msgs = fake.Requests[1].Messages;
        Assert.Equal(3, msgs.Count, "brief, previous svg, change");
        Assert.True(msgs[0].Text.Contains("Georgia") && msgs[0].Text.Contains("oak badge"), msgs[0].Text);
        Assert.True(msgs[2].Text.Contains("make the star bigger") && msgs[2].Text.Contains("60 mm"), msgs[2].Text);
        Assert.True(Math.Abs(r2.Bounds.Width - 60) < 0.01);
    }

    [Test] static void FailedParseDoesNotPoisonHistory()
    {
        var fake = FakeAiClient.Sequence(AiResponse.FromText("no svg"), AiResponse.FromText(Reply));
        var gen = new DesignGenerator(fake);
        try { gen.GenerateAsync(new DesignRequest { Description = "x" }).GetAwaiter().GetResult(); } catch (AiException) { }
        Assert.True(!gen.HasDesign);
    }

    [Test] static void ImporterReadsTextWithTransforms()
    {
        var (paths, texts) = SvgImporter.ImportWithText("""
            <svg width="100mm" height="100mm" viewBox="0 0 100 100">
              <g font-family="Arial" font-size="8">
                <text x="10" y="20">Hello <tspan>world</tspan></text>
                <text transform="rotate(90 50 50)" x="50" y="10" text-anchor="end" style="font-weight:700;font-style:italic">R</text>
              </g>
              <rect x="0" y="0" width="5" height="5"/>
            </svg>
            """);
        Assert.Equal(1, paths.Count);
        Assert.Equal(2, texts.Count);
        Assert.Equal("Hello world", texts[0].Text);
        Assert.Equal("Arial", texts[0].FontFamily);
        Assert.True(Math.Abs(texts[0].FontSize - 8) < 1e-9, $"size {texts[0].FontSize}");
        var origin = texts[0].Transform.Apply(new Vec2(0, 0));
        Assert.True(Math.Abs(origin.X - 10) < 1e-6 && Math.Abs(origin.Y - 20) < 1e-6, $"origin {origin}");
        Assert.True(texts[1].Bold && texts[1].Italic && texts[1].Anchor == TextAnchor.End);
        // (50,10) rotated 90° about (50,50) → (90,50).
        var o2 = texts[1].Transform.Apply(new Vec2(0, 0));
        Assert.True(Math.Abs(o2.X - 90) < 1e-6 && Math.Abs(o2.Y - 50) < 1e-6, $"rotated origin {o2}");
    }

    [Test] static void DesignShapesGoOnSensibleLayersAndCentre()
    {
        var p = LaserProject.CreateDefault();
        var r = DesignGenerator.Parse(Reply, new DesignRequest { WidthMm = 50, Style = DesignStyle.EngraveAndCut }, PlaceholderTextOutliner.Instance);
        var shapes = DesignShapes.Build(r, p, DesignStyle.EngraveAndCut);
        var fill = p.Layers.First(l => l.Mode == LayerMode.Fill);
        Assert.True(shapes.Any(s => s.LayerId == fill.Id && s.Name == "AI design"));
        Assert.True(shapes.Any(s => s.Name == "AI cut outline" && p.FindLayer(s.LayerId)!.Mode == LayerMode.Line));
        var b = ArrayTool.BoundsOf(shapes);
        Assert.True(Math.Abs(b.Center.X - p.Machine.BedWidthMm / 2) < 0.01 && Math.Abs(b.Center.Y - p.Machine.BedHeightMm / 2) < 0.01, "centred");
    }
}

internal static class TestGridReaderTests
{
    private static MaterialTestOptions Grid() => new() { MinSpeed = 1000, MaxSpeed = 5000, SpeedSteps = 5, MinPower = 10, MaxPower = 90, PowerSteps = 5 };

    [Test] static void MapsCellToSettings()
    {
        var r = TestGridReader.Parse("""{"row": 2, "column": 3, "confidence": 0.8, "reasoning": "Even dark brown.", "alternative": {"row": 1, "column": 3}, "advice": "Use air."}""", Grid());
        Assert.Equal(3000.0, r.SpeedMmMin);
        Assert.Equal(70.0, r.PowerPercent);
        Assert.Equal((1, 3), r.Alternative!.Value);
        Assert.Equal("Use air.", r.Advice);
    }

    [Test] static void RejectsCellsOutsideTheGrid()
    {
        try { TestGridReader.Parse("""{"row": 7, "column": 0}""", Grid()); throw new Exception("expected"); }
        catch (AiException e) { Assert.True(e.Message.Contains("5 × 5"), e.Message); }
    }

    [Test] static void BriefDescribesRowsAndColumns()
    {
        var brief = TestGridReader.BuildBrief(Grid(), GridGoal.Cut, "Birch ply 3mm");
        Assert.True(brief.Contains("1000, 2000, 3000, 4000, 5000"), brief);
        Assert.True(brief.Contains("10, 30, 50, 70, 90"), brief);
        Assert.True(brief.Contains("cut") && brief.Contains("Birch ply 3mm"));
        Assert.True(brief.Contains("{\"row\": 0"), "literal JSON example survives");
    }

    [Test] static void SendsPhotoAndMakesPreset()
    {
        var fake = new FakeAiClient(_ => AiResponse.FromText("""{"row":0,"column":4,"confidence":0.9,"reasoning":"Cut through."}"""));
        var reading = new TestGridReader(fake).ReadAsync(new byte[] { 9 }, "image/jpeg", Grid(), GridGoal.Cut, "Ply").GetAwaiter().GetResult();
        Assert.True(fake.Requests[0].Messages[0].Content[0] is AiImage);
        var preset = TestGridReader.ToPreset(reading, Grid(), GridGoal.Cut, "Ply", 3);
        Assert.Equal(LayerMode.Line, preset.Mode);
        Assert.Equal("Cut", preset.Operation);
        Assert.Equal(1000.0, preset.SpeedMmMin);
        Assert.Equal(90.0, preset.PowerPercent);
    }
}

internal static class GrblCodeTests
{
    [Test] static void ExplainsAlarmsAndErrors()
    {
        var a = GrblCodes.Explain("ALARM:2")!;
        Assert.True(a.Meaning.Contains("Soft limit") && a.Fix.Contains("$X"), a.Meaning);
        var e = GrblCodes.Explain("!! error:9 – something (line: G1)")!;
        Assert.Equal("error:9", e.Code);
        Assert.True(GrblCodes.Explain("ok") == null);
        Assert.True(GrblCodes.Explain("error:99")!.Meaning.StartsWith("Unknown"));
        Assert.Equal("error:22 – Feed rate hasn't been set", GrblErrors.Describe("error:22"));
    }
}

internal static class SmartImageTests
{
    private static byte[] Square(int w, int h, int x0, int y0, int x1, int y1, byte inside, byte outside)
    {
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                g[y * w + x] = x >= x0 && x < x1 && y >= y0 && y < y1 ? inside : outside;
        return g;
    }

    [Test] static void LineArtDrawsEdgesOnWhite()
    {
        int w = 120, h = 120;
        var g = Square(w, h, 30, 30, 90, 90, 40, 230);
        var lines = LineArt.Apply(g, w, h);
        Assert.True(lines[5 * w + 5] > 240, "far outside stays white");
        Assert.True(lines[60 * w + 60] > 240, "flat interior stays white");
        int dark = Enumerable.Range(25, 10).Count(x => lines[60 * w + x] < 128);
        Assert.True(dark > 0, "an edge line near x = 30");
        Assert.True(LineArt.Apply(new byte[w * h].Select(_ => (byte)200).ToArray(), w, h).All(v => v > 240), "flat image has no lines");
    }

    [Test] static void ShadingFillsDarkAreas()
    {
        int w = 80, h = 80;
        var g = Square(w, h, 20, 20, 60, 60, 5, 230);
        var shaded = LineArt.Apply(g, w, h, new LineArtOptions { Shading = 0.8 });
        Assert.True(shaded[40 * w + 40] < 30, "dark interior filled");
    }

    private sealed class CentreBlobModel : ISaliencyModel
    {
        public int InputSize => 32;
        public float[] Inputs = Array.Empty<float>();
        public float[] Run(float[] input)
        {
            Inputs = input;
            var o = new float[32 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    o[y * 32 + x] = x is >= 8 and < 24 && y is >= 8 and < 24 ? 5f : -5f;
            return o;
        }
    }

    [Test] static void ModelMaskWhitensBackground()
    {
        int w = 64, h = 48;
        var g = Enumerable.Repeat((byte)90, w * h).ToArray();
        var model = new CentreBlobModel();
        var mask = BackgroundRemoval.PredictMask(model, g, w, h);
        Assert.Equal(3 * 32 * 32, model.Inputs.Length);
        var result = BackgroundRemoval.ApplyMask(g, mask);
        Assert.Equal((byte)255, result[0], "corner whitened");
        Assert.Equal((byte)90, result[24 * w + 32], "subject kept");
    }

    [Test] static void PlainBackgroundIsRemovedOffline()
    {
        int w = 60, h = 60;
        var g = Square(w, h, 20, 20, 40, 40, 30, 240);
        g[0] = 235; // slight noise in the background
        var mask = BackgroundRemoval.PlainBackgroundMask(g, w, h);
        Assert.True(mask[2 * w + 2] < 0.1, "background");
        Assert.True(mask[30 * w + 30] > 0.9, "subject");
    }

    [Test] static void AutoTuneStretchesAndKnowsMaterials()
    {
        // A flat, low-contrast photo: values 80..170 only.
        var rnd = new Random(1);
        var g = Enumerable.Range(0, 10000).Select(_ => (byte)rnd.Next(80, 171)).ToArray();
        var t = AutoTune.Tune(g, "Plywood (birch) 3 mm – Photo");
        Assert.Equal("Wood", t.Material.Name);
        var adjusted = ImageProcessor.Adjust(g, t.Brightness, t.Contrast, t.Gamma, t.Invert);
        Assert.True(Filters.Percentile(adjusted, 0.02) < 40, $"darks {Filters.Percentile(adjusted, 0.02)}");
        Assert.True(Filters.Percentile(adjusted, 0.98) > 215, $"lights {Filters.Percentile(adjusted, 0.98)}");
        Assert.True(AutoTune.Tune(g, "Slate").Invert, "slate inverts");
        Assert.True(AutoTune.Tune(g, "Anodised aluminium").Invert, "anodised inverts");
        Assert.Equal("General", AutoTune.Tune(g, null).Material.Name);
    }
}

internal static class AssistantTests
{
    private sealed class Host : IAssistantHost
    {
        public LaserProject Project { get; } = LaserProject.CreateDefault();
        public HashSet<Guid> Sel = new();
        public IReadOnlyCollection<Guid> Selection => Sel;
        public MaterialLibrary Materials { get; } = MaterialLibrary.CreateDefault();
        public ITextOutliner Outliner => PlaceholderTextOutliner.Instance;
        public IReadOnlyList<string> InstalledFonts { get; } = new[] { "Arial", "Georgia" };
        public IReadOnlyList<string> RecentConsole { get; } = new[] { "ALARM:2" };
        public List<string> Edits = new();
        public void BeginEdit(string description) => Edits.Add(description);
        public void EndEdit(IEnumerable<Guid>? select) { if (select != null) Sel = select.ToHashSet(); }
        public Task<DesignResult>? GenerateDesign(DesignRequest request, CancellationToken ct) => null;
    }

    private static AiResponse Tool(string name, string json, string id = "t1") => new()
    {
        StopReason = "tool_use",
        Content = { new AiText("On it."), new AiToolUse(id, name, JsonNode.Parse(json)!.AsObject()) },
    };

    [Test] static void RunsToolsThenAnswers()
    {
        var host = new Host();
        var fake = FakeAiClient.Sequence(
            Tool("add_text", """{"text":"DUNCAN","size_mm":20,"font":"georgia","centre_on_bed":true}"""),
            AiResponse.FromText("Added DUNCAN in the middle of the bed."));
        var session = new AssistantSession(fake, host);
        var reply = session.SendAsync("put my surname in the middle").GetAwaiter().GetResult();

        Assert.Equal("Added DUNCAN in the middle of the bed.", reply.Text);
        var shape = host.Project.Shapes.Single();
        Assert.Equal("Text \"DUNCAN\"", shape.Name);
        Assert.True(Math.Abs(shape.GetBounds().Center.X - host.Project.Machine.BedWidthMm / 2) < 0.01, "centred");
        Assert.True(host.Sel.Contains(shape.Id), "new text selected");
        Assert.Equal(1, host.Edits.Count);
        Assert.True(reply.Actions.Single().Summary.Contains("Georgia"), reply.Actions[0].Summary);
        // Second request carried the tool result back.
        var last = fake.Requests[1].Messages[^1];
        Assert.True(last.Content.Single() is AiToolResult { IsError: false });
        Assert.True(fake.Requests[0].System!.Contains("ALARM:2") && fake.Requests[0].System!.Contains("cannot frame, start"), "context + limits in prompt");
    }

    [Test] static void ReadOnlyModeRefusesEdits()
    {
        var host = new Host();
        var fake = FakeAiClient.Sequence(Tool("add_shape", """{"shape":"circle","x_mm":0,"y_mm":0,"width_mm":10}"""), AiResponse.FromText("ok"));
        var session = new AssistantSession(fake, host) { CanEdit = false };
        session.SendAsync("add a circle").GetAwaiter().GetResult();
        Assert.Equal(0, host.Project.Shapes.Count);
        Assert.True(fake.Requests[0].Tools.All(t => AssistantTools.ReadOnlyTools.Contains(t.Name)), "only read-only tools offered");
        Assert.True(fake.Requests[1].Messages[^1].Content.Single() is AiToolResult { IsError: true });
    }

    [Test] static void FuzzyPresetsAndLayers()
    {
        var host = new Host();
        var tools = new AssistantTools(host);
        var (res, action) = tools.ExecuteAsync(new AiToolUse("1", "apply_material_preset", JsonNode.Parse("""{"layer":"cut","preset":"birch ply cut"}""")!.AsObject()), true, default).GetAwaiter().GetResult();
        Assert.True(!res.IsError, res.Content);
        var cut = host.Project.Layers.First(l => l.Mode == LayerMode.Line);
        Assert.True(cut.MaterialName!.Contains("Plywood (birch)") && cut.MaterialName.Contains("Cut"), cut.MaterialName);

        var (res2, _) = tools.ExecuteAsync(new AiToolUse("2", "set_layer_settings", JsonNode.Parse("""{"layer":"2","power_percent":"150","passes":3}""")!.AsObject()), true, default).GetAwaiter().GetResult();
        Assert.True(!res2.IsError, res2.Content);
        Assert.Equal(100.0, host.Project.Layers[1].PowerPercent, "clamped");
        Assert.Equal(3, host.Project.Layers[1].Passes);

        var (res3, a3) = tools.ExecuteAsync(new AiToolUse("3", "set_layer_settings", JsonNode.Parse("""{"layer":"nope","speed_mm_min":5}""")!.AsObject()), true, default).GetAwaiter().GetResult();
        Assert.True(res3.IsError && res3.Content.Contains("Engrave") && a3!.Failed, res3.Content);
    }

    [Test] static void ArraysTheLastAddedShape()
    {
        var host = new Host();
        var tools = new AssistantTools(host);
        tools.ExecuteAsync(new AiToolUse("1", "add_shape", JsonNode.Parse("""{"shape":"rounded_rectangle","x_mm":10,"y_mm":10,"width_mm":40,"height_mm":20}""")!.AsObject()), true, default).GetAwaiter().GetResult();
        var (res, _) = tools.ExecuteAsync(new AiToolUse("2", "array_shapes", JsonNode.Parse("""{"target":"last_added","kind":"grid","columns":3,"rows":2,"gap_x_mm":5,"gap_y_mm":5}""")!.AsObject()), true, default).GetAwaiter().GetResult();
        Assert.True(!res.IsError, res.Content);
        Assert.Equal(6, host.Project.Shapes.Count);
        Assert.True(res.Content.Contains("130 × 45"), res.Content);
    }

    [Test] static void FailedCallRollsBackHistory()
    {
        var host = new Host();
        var fake = new FakeAiClient(_ => throw new AiException(AiErrorKind.Network, "offline"));
        var session = new AssistantSession(fake, host);
        try { session.SendAsync("hi").GetAwaiter().GetResult(); } catch (AiException) { }
        Assert.Equal(0, session.History.Count);
    }

    [Test] static void ProjectDescriptionListsLayersShapesAndPresets()
    {
        var host = new Host();
        host.Project.Shapes.Add(new VectorShape(ShapeFactory.Rectangle(5, 6, 10, 10).Paths, "Box") { LayerId = host.Project.Layers[2].Id });
        var text = new AssistantTools(host).DescribeProject();
        Assert.True(text.Contains("\"Box\"") && text.Contains("on \"Cut\"") && text.Contains("x 5, y 6"), text);
        Assert.True(text.Contains("Plywood (birch) 3 mm – Cut"), "presets listed");
    }
}
