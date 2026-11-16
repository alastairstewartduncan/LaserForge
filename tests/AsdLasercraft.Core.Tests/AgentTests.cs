using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Ai.Library;
using AsdLasercraft.Core.Geometry;
using AsdLasercraft.Core.Import;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.Core.Tests;

/// <summary>Answers HTTP calls from a script, recording what was sent.</summary>
internal sealed class ScriptedHttp : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode, string)> _answers = new();
    public List<(Uri Uri, string Body, Dictionary<string, string> Headers)> Calls { get; } = new();

    public ScriptedHttp Then(HttpStatusCode status, string body) { _answers.Enqueue((status, body)); return this; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.RequestUri!, body, request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value))));
        var (status, text) = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

internal static class ProviderTests
{
    private static AiRequest ToolConversation() => new()
    {
        System = "sys",
        MaxTokens = 500,
        Messages =
        {
            AiMessage.User(new AiImage("image/png", new byte[] { 1, 2, 3 }), new AiText("look")),
            new AiMessage(AiRole.Assistant, new AiContent[]
            {
                new AiText("Checking."),
                new AiToolUse("c1", "add_text", JsonNode.Parse("""{"text":"Hi"}""")!.AsObject(),
                    JsonNode.Parse("""{"thoughtSignature":"SIG","extra_content":{"google":{"thought_signature":"SIG"}}}""")!.AsObject()),
            }),
            new AiMessage(AiRole.User, new AiContent[] { new AiToolResult("c1", "Added."), }),
        },
        Tools =
        {
            new AiTool("add_text", "adds text", AiJson.Schema(("text", AiJson.StringProp("the text"), true), ("size_mm", AiJson.NumberProp("size"), false))),
            new AiTool("get_project", "reads", AiJson.Schema()),
        },
    };

    [Test] static void OpenAiBodyUsesChatCompletionsShapes()
    {
        var body = OpenAiCompatibleClient.BuildBody(ToolConversation(), "m1", useMaxCompletionTokens: false);
        Assert.Equal(500, body["max_tokens"]!.GetValue<int>());
        var m = body["messages"]!.AsArray();
        Assert.Equal("system", m[0]!["role"]!.GetValue<string>());
        Assert.Equal("user", m[1]!["role"]!.GetValue<string>());
        Assert.True(m[1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>().StartsWith("data:image/png;base64,AQID"), "image as data URL");
        Assert.Equal("assistant", m[2]!["role"]!.GetValue<string>());
        var call = m[2]!["tool_calls"]![0]!;
        Assert.Equal("c1", call["id"]!.GetValue<string>());
        Assert.Equal("""{"text":"Hi"}""", call["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("SIG", call["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>(), "provider data echoed");
        Assert.Equal("tool", m[3]!["role"]!.GetValue<string>());
        Assert.Equal("c1", m[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("text", body["tools"]![0]!["function"]!["parameters"]!["required"]![0]!.GetValue<string>());

        var newer = OpenAiCompatibleClient.BuildBody(ToolConversation(), "m1", useMaxCompletionTokens: true);
        Assert.True(newer["max_tokens"] == null && newer["max_completion_tokens"]!.GetValue<int>() >= 2500, "headroom for reasoning");
    }

    [Test] static void OpenAiParsesToolCallsAndText()
    {
        var r = OpenAiCompatibleClient.ParseResponse("""
            {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call_9","type":"function","function":{"name":"add_shape","arguments":"{\"shape\":\"circle\",\"width_mm\":20}"}},
                            {"type":"function","function":{"name":"get_project","arguments":"not json"}}]}}],
             "usage":{"prompt_tokens":11,"completion_tokens":7}}
            """);
        Assert.Equal("tool_use", r.StopReason);
        var calls = r.ToolUses.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_9", calls[0].Id);
        Assert.Equal(20.0, calls[0].Input.Num("width_mm")!.Value);
        Assert.True(calls[1].Id.Length > 0 && calls[1].Input.Count == 0, "missing id and bad arguments are tolerated");
        Assert.Equal(18, r.Usage.InputTokens + r.Usage.OutputTokens);

        var text = OpenAiCompatibleClient.ParseResponse("""{"choices":[{"finish_reason":"length","message":{"content":"Hello"}}]}""");
        Assert.Equal("Hello", text.Text);
        Assert.Equal("max_tokens", text.StopReason);
    }

    [Test] static void OpenAiClientSwitchesTokenParameterWhenRejected()
    {
        var http = new ScriptedHttp()
            .Then(HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."}}""")
            .Then(HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":"ready"}}]}""");
        using var client = new OpenAiCompatibleClient(() => "sk-test", "https://example.test/v1/", new HttpClient(http)) { Model = "m", ProviderName = "OpenAI" };
        var r = client.SendAsync(new AiRequest { Messages = { AiMessage.User("hi") } }).GetAwaiter().GetResult();
        Assert.Equal("ready", r.Text);
        Assert.Equal(2, http.Calls.Count);
        Assert.Equal("https://example.test/v1/chat/completions", http.Calls[0].Uri.ToString());
        Assert.True(http.Calls[0].Body.Contains("\"max_tokens\"") && http.Calls[1].Body.Contains("\"max_completion_tokens\""), "retried with the other name");
        Assert.Equal("Bearer sk-test", http.Calls[0].Headers["authorization"]);
        Assert.True(client.UseMaxCompletionTokens, "remembers what worked");
    }

    [Test] static void LocalServersNeedNoKeyAndErrorsNameTheProvider()
    {
        var http = new ScriptedHttp().Then(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");
        using var local = new OpenAiCompatibleClient(() => null, "http://localhost:11434/v1", new HttpClient(http)) { Model = "llava", KeyRequired = false, ProviderName = "Ollama" };
        Assert.True(local.IsConfigured);
        try { local.SendAsync(new AiRequest { Messages = { AiMessage.User("hi") } }).GetAwaiter().GetResult(); throw new Exception("expected"); }
        catch (AiException e) { Assert.Equal(AiErrorKind.BadKey, e.Kind); Assert.True(e.Message.Contains("Ollama"), e.Message); }
        Assert.True(!http.Calls[0].Headers.ContainsKey("authorization"), "no auth header without a key");

        using var cloud = new OpenAiCompatibleClient(() => "", "https://api.openai.com/v1") { Model = "m" };
        Assert.True(!cloud.IsConfigured);
        using var noModel = new OpenAiCompatibleClient(() => "k", "https://api.openai.com/v1");
        try { noModel.SendAsync(new AiRequest { Messages = { AiMessage.User("hi") } }).GetAwaiter().GetResult(); throw new Exception("expected"); }
        catch (AiException e) { Assert.True(e.Message.Contains("model"), e.Message); }
    }

    [Test] static void GeminiBodyUsesItsOwnShapes()
    {
        var body = GeminiClient.BuildBody(ToolConversation());
        Assert.Equal("sys", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        var c = body["contents"]!.AsArray();
        Assert.Equal("user", c[0]!["role"]!.GetValue<string>());
        Assert.Equal("AQID", c[0]!["parts"]![0]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal("model", c[1]!["role"]!.GetValue<string>());
        var callPart = c[1]!["parts"]![1]!;
        Assert.Equal("add_text", callPart["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("SIG", callPart["thoughtSignature"]!.GetValue<string>(), "thought signature returned with the call");
        var resp = c[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("add_text", resp["name"]!.GetValue<string>(), "response matched to the call by name");
        Assert.Equal("Added.", resp["response"]!["result"]!.GetValue<string>());

        var decls = body["tools"]![0]!["functionDeclarations"]!.AsArray();
        Assert.Equal("OBJECT", decls[0]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal("STRING", decls[0]!["parameters"]!["properties"]!["text"]!["type"]!.GetValue<string>());
        Assert.True(decls[1]!["parameters"] == null, "no parameters object for a tool that takes none");
        GeminiClient.BuildBody(ToolConversation()); // building twice must not re-parent nodes
    }

    [Test] static void GeminiParsesCallsSignaturesAndBlocks()
    {
        var r = GeminiClient.ParseResponse("""
            {"candidates":[{"finishReason":"STOP","content":{"role":"model","parts":[
               {"text":"thinking…","thought":true},
               {"text":"Sure."},
               {"functionCall":{"name":"add_shape","args":{"shape":"star","width_mm":30}},"thoughtSignature":"abc"}]}}],
             "usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":9}}
            """);
        Assert.Equal("Sure.", r.Text);
        Assert.Equal("tool_use", r.StopReason);
        var call = r.ToolUses.Single();
        Assert.Equal("abc", call.ProviderData!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal(30.0, call.Input.Num("width_mm")!.Value);
        Assert.Equal(29, r.Usage.InputTokens + r.Usage.OutputTokens);

        try { GeminiClient.ParseResponse("""{"promptFeedback":{"blockReason":"SAFETY"}}"""); throw new Exception("expected"); }
        catch (AiException e) { Assert.True(e.Message.Contains("SAFETY"), e.Message); }
    }

    [Test] static void GeminiClientSendsKeyHeaderAndModelInUrl()
    {
        var http = new ScriptedHttp().Then(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"ready"}]}}]}""");
        using var client = new GeminiClient(() => "g-key", new HttpClient(http)) { Model = "models/gemini-x" };
        Assert.Equal("ready", client.SendAsync(new AiRequest { Messages = { AiMessage.User("hi") } }).GetAwaiter().GetResult().Text);
        Assert.True(http.Calls[0].Uri.ToString().EndsWith("/v1beta/models/gemini-x:generateContent"), http.Calls[0].Uri.ToString());
        Assert.Equal("g-key", http.Calls[0].Headers["x-goog-api-key"]);
    }

    [Test] static void CatalogueMergesSavedProvidersAndFactoryPicksClients()
    {
        var saved = new List<AiProviderProfile>
        {
            new() { Id = AiProviderCatalog.OpenAiId, Name = "OpenAI", Model = "my-model", BaseUrl = "https://proxy.test/v1", SupportsVision = false },
            new() { Id = "work", Name = "Work gateway", BaseUrl = "https://gw.test/v1", Model = "x" },
        };
        var merged = AiProviderCatalog.Merge(saved);
        Assert.Equal(AiProviderCatalog.AnthropicId, merged[0].Id, "built-ins first");
        var openai = merged.Single(p => p.Id == AiProviderCatalog.OpenAiId);
        Assert.Equal("my-model", openai.Model, "saved choices kept");
        Assert.True(!openai.SupportsVision);
        Assert.Equal("work", merged[^1].Id, "extra providers kept at the end");
        Assert.True(merged.Count == AiProviderCatalog.Defaults().Count + 1);

        Assert.True(AiClientFactory.Create(merged[0], () => "k") is ClaudeClient);
        Assert.True(AiClientFactory.Create(openai, () => "k") is OpenAiCompatibleClient { Model: "my-model", BaseUrl: "https://proxy.test/v1" });
        var gem = merged.Single(p => p.Id == AiProviderCatalog.GeminiId);
        Assert.True(AiClientFactory.Create(gem, () => "k", null, "gemini-y") is GeminiClient { Model: "gemini-y" }, "model override");
        Assert.True(!AiClientFactory.IsReady(gem, "k") && AiClientFactory.IsReady(merged[0], "k"), "a model must be chosen except for Claude's default");
        var ollama = merged.Single(p => p.Id == AiProviderCatalog.OllamaId);
        ollama.Model = "llama";
        Assert.True(AiClientFactory.IsReady(ollama, null), "local needs no key");
        Assert.Equal("Ollama", AiClientFactory.ShortName(ollama));
    }

    [Test] static void ModelListsAreReadForEachProvider()
    {
        var openai = AiModelLister.Parse(AiProviderKind.OpenAiCompatible, """{"data":[{"id":"b-model"},{"id":"a-model"},{"id":"a-model"}]}""");
        Assert.Equal("a-model,b-model", string.Join(",", openai));
        var gemini = AiModelLister.Parse(AiProviderKind.Gemini, """
            {"models":[{"name":"models/gemini-flash","supportedGenerationMethods":["generateContent"]},
                       {"name":"models/embed-1","supportedGenerationMethods":["embedContent"]}]}
            """);
        Assert.Equal("gemini-flash", string.Join(",", gemini));
        var p = new AiProviderProfile { Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:1234/v1/" };
        Assert.Equal("http://localhost:1234/v1/models", AiModelLister.ListUri(p).ToString());

        var http = new ScriptedHttp().Then(HttpStatusCode.OK, """{"data":[{"id":"claude-x"}]}""");
        var list = AiModelLister.ListAsync(AiProviderCatalog.Defaults()[0], "k", new HttpClient(http)).GetAwaiter().GetResult();
        Assert.Equal("claude-x", list.Single());
        Assert.Equal("k", http.Calls[0].Headers["x-api-key"]);
    }
}

internal static class LibraryTests
{
    [Test] static void FrontmatterReadsTheCommonYamlShapes()
    {
        var fm = Frontmatter.Parse("""
            ---
            name: badge-maker
            description: "Makes badges: round ones"
            tools: Read, Grep , add_text
            skills:
              - round-badges
              - 'plywood-cut-outs'
            tags: [a, "b c", d]
            notes: >
              folded
              text
            metadata:
              version: 2
            model: sonnet # a comment
            ---

            # Body

            Text with --- inside.
            """);
        Assert.True(fm.HadHeader);
        Assert.Equal("badge-maker", fm.Get("name"));
        Assert.Equal("Makes badges: round ones", fm.Get("description"));
        Assert.Equal("Read|Grep|add_text", string.Join("|", fm.GetList("tools")));
        Assert.Equal("round-badges|plywood-cut-outs", string.Join("|", fm.GetList("skills")));
        Assert.Equal("a|b c|d", string.Join("|", fm.GetList("tags")));
        Assert.Equal("folded text", fm.Get("notes"));
        Assert.Equal("sonnet", fm.Get("model"));
        Assert.True(!fm.Has("version"), "nested maps are skipped, not misread");
        Assert.True(fm.Body.StartsWith("# Body") && fm.Body.Contains("--- inside"), fm.Body);

        var plain = Frontmatter.Parse("Just some text\n---\nnot a header");
        Assert.True(!plain.HadHeader && plain.Body.StartsWith("Just some text"));
    }

    [Test] static void SkillsAndAgentsRoundTripThroughMarkdown()
    {
        var agent = new Agent
        {
            Name = "scout-badges", Title = "Scout badges: 3 mm ply", Description = "Designs \"badges\"", Role = AgentRole.Designer,
            Instructions = "You are a badge designer.\n\n- Keep it bold", Provider = "gemini", Model = "gemini-x",
            Tools = { "add_text", "add_shape" }, Skills = { "round-badges" },
        };
        var back = Agent.FromFrontmatter(Frontmatter.Parse(agent.ToMarkdown()));
        Assert.Equal(agent.Name, back.Name);
        Assert.Equal(agent.Title, back.Title);
        Assert.Equal(agent.Description, back.Description);
        Assert.Equal(AgentRole.Designer, back.Role);
        Assert.Equal(agent.Instructions, back.Instructions);
        Assert.Equal("gemini/gemini-x", back.Provider + "/" + back.Model);
        Assert.Equal("add_text,add_shape", string.Join(",", back.Tools));
        Assert.Equal("round-badges", back.Skills.Single());

        var skill = Skill.FromFrontmatter(Frontmatter.Parse("---\nname: Slate Coasters!\ndescription: x\n---\nbody"));
        Assert.Equal("slate-coasters", skill.Name, "display names become ids");
        Assert.Equal("Slate Coasters!", skill.Title);
    }

    [Test] static void LibrarySavesOnlyUserChangesAndRemembersDeletedBuiltIns()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asd-lib-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = AiLibrary.Load(dir);
            Assert.True(lib.FindAgent(BuiltIns.DesignerName) != null && lib.FindAgent(BuiltIns.CriticName) != null, "built-ins present");
            Assert.True(lib.Skills.Count >= 3);

            lib.Add(new Skill { Name = "My Style", Title = "My style", Body = "Bold." });
            Assert.Equal("my-style-2", lib.Add(new Skill { Name = "my-style", Body = "Again." }), "names made unique");
            var critic = lib.FindAgent(BuiltIns.CriticName)!;
            critic.Instructions += "\nBe extra strict about spelling.";
            critic.Skills.Add("my-style");
            lib.Remove(lib.FindSkill("slate-coasters")!);
            lib.Save(dir);

            Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "skills")).Length, "untouched built-in skills aren't written");
            Assert.Equal(1, Directory.GetFiles(Path.Combine(dir, "agents")).Length, "only the edited agent is written");

            var again = AiLibrary.Load(dir);
            Assert.True(again.FindSkill("slate-coasters") == null, "deleted built-in stays deleted");
            Assert.True(again.FindAgent(BuiltIns.CriticName)!.Instructions.Contains("extra strict"), "edit kept");
            Assert.True(again.FindSkill("My style") != null, "found by display name");
            Assert.Equal(2, again.SkillsOf(again.FindAgent(BuiltIns.CriticName)).Count);

            again.Remove(again.FindSkill("my-style")!);
            Assert.True(!again.FindAgent(BuiltIns.CriticName)!.Skills.Contains("my-style"), "removed skills are detached from agents");
            again.Save(dir);
            Assert.Equal(1, Directory.GetFiles(Path.Combine(dir, "skills")).Length, "deleted files are removed");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test] static void ImportedAgentsArriveWithToolsOffAndNoModel()
    {
        var r = LibraryPorter.ImportText("code-reviewer.md", """
            ---
            name: code-reviewer
            description: Reviews code
            tools: Read, Grep, add_text, delete_shapes
            model: opus
            color: blue
            ---
            You are a careful reviewer.
            """);
        var a = r.Agents.Single();
        Assert.Equal(0, a.Tools.Count, "tools switched off");
        Assert.Equal("", a.Model + a.Provider);
        Assert.Equal("You are a careful reviewer.", a.Instructions);
        var notes = string.Join("\n", r.Notes);
        Assert.True(notes.Contains("add_text") && notes.Contains("Read, Grep") && notes.Contains("opus") && notes.Contains("color"), notes);
    }

    [Test] static void ImportGuessesSkillOrAgentFromTheFile()
    {
        Assert.Equal(1, LibraryPorter.ImportText("SKILL.md", "---\nname: coasters\ndescription: d\n---\n# Coasters\nRules").Skills.Count);
        Assert.Equal(1, LibraryPorter.ImportText("rules.mdc", "---\ndescription: d\nglobs: '*.ts'\nalwaysApply: true\n---\nUse tabs").Skills.Count);
        Assert.Equal(1, LibraryPorter.ImportText("helper.agent.md", "---\ndescription: d\n---\nHelp out").Agents.Count);

        var gpt = LibraryPorter.ImportText("My GPT.txt", "You are Etchy, a friendly engraving expert.\nAlways answer in metric.");
        Assert.Equal("My GPT", gpt.Agents.Single().Title);
        Assert.Equal("my-gpt", gpt.Agents.Single().Name);

        var notes = LibraryPorter.ImportText("house style.txt", "# House style\n\nRounded corners everywhere.");
        Assert.Equal("House style", notes.Skills.Single().Title);

        Assert.Equal(1, LibraryPorter.ImportText("x.txt", "You are a critic.", ImportAs.Skill).Skills.Count, "the user's choice wins");
        Assert.True(LibraryPorter.ImportText("empty.md", "   ").IsEmpty);
    }

    [Test] static void ImportsAssistantJson()
    {
        var r = LibraryPorter.ImportText("assistant.json", """
            {"name":"Engraving Buddy","description":"Helps","instructions":"You help with engraving.","model":"gpt-x",
             "tools":[{"type":"code_interpreter"},{"type":"function","function":{"name":"add_shape"}}]}
            """);
        var a = r.Agents.Single();
        Assert.Equal("Engraving Buddy", a.Title);
        Assert.Equal("engraving-buddy", a.Name);
        Assert.Equal(0, a.Tools.Count);
        Assert.True(string.Join(" ", r.Notes).Contains("code_interpreter") && string.Join(" ", r.Notes).Contains("add_shape"));

        var bundle = LibraryPorter.ImportText("b.json", """{"skills":[{"name":"s1","instructions":"Do x"}],"agents":[{"name":"a1","system_prompt":"You are a1"}]}""");
        Assert.Equal(1, bundle.Skills.Count);
        Assert.Equal(1, bundle.Agents.Count);
    }

    private static byte[] MakeZip(params (string Path, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    [Test] static void ImportsSkillZipsAndReportsWhatCannotComeAcross()
    {
        var zip = MakeZip(
            ("pdf-filler/SKILL.md", "---\nname: pdf-filler\ndescription: Fills PDFs\nallowed-tools: Bash\n---\n# PDF filler\nRun scripts/fill.py"),
            ("pdf-filler/scripts/fill.py", "print('hi')"),
            ("pdf-filler/reference.md", "More detail"),
            ("agents/helper.md", "---\nname: helper\ntools: Bash\n---\nYou help."));
        var r = LibraryPorter.ImportBytes("pack.zip", zip);
        Assert.Equal("pdf-filler", r.Skills.Single().Name);
        Assert.Equal("helper", r.Agents.Single().Name);
        var notes = string.Join("\n", r.Notes);
        Assert.True(notes.Contains("2 supporting file(s)"), notes);
        Assert.True(notes.Contains("tool permissions"), notes);
    }

    [Test] static void ExportsRoundTripAndUsePortableLayouts()
    {
        var lib = new AiLibrary();
        lib.AddBuiltIns();
        var skill = lib.FindSkill("round-badges")!;
        var agent = lib.FindAgent(BuiltIns.DesignerName)!;

        var md = LibraryPorter.Export(skill, ExportFormat.Markdown);
        Assert.Equal("round-badges.md", md.FileName);
        Assert.Equal(skill.Body, LibraryPorter.ImportBytes(md.FileName, md.Bytes).Skills.Single().Body);

        var zip = LibraryPorter.Export(skill, ExportFormat.SkillZip);
        using (var z = new ZipArchive(new MemoryStream(zip.Bytes)))
        {
            var entry = z.Entries.Single();
            Assert.Equal("round-badges/SKILL.md", entry.FullName);
            var fm = Frontmatter.Parse(new StreamReader(entry.Open()).ReadToEnd());
            Assert.Equal("name,description", string.Join(",", fm.Values.Select(v => v.Key)), "only the portable header fields");
        }
        Assert.Equal("round-badges", LibraryPorter.ImportBytes(zip.FileName, zip.Bytes).Skills.Single().Name);

        var text = Encoding.UTF8.GetString(LibraryPorter.Export(agent, ExportFormat.PlainText, lib).Bytes);
        Assert.True(text.StartsWith("Laser designer") && text.Contains("[Skill: Laser-ready vectors]") && !text.Contains("---"), "plain text carries the attached skills");

        var json = JsonNode.Parse(Encoding.UTF8.GetString(LibraryPorter.Export(agent, ExportFormat.Json, lib).Bytes))!;
        Assert.Equal("designer", json["role"]!.GetValue<string>());
        Assert.True(json["instructions"]!.GetValue<string>().Contains("0.5 mm"));

        var pack = LibraryPorter.Export(agent, ExportFormat.SkillZip, lib);
        var packBack = LibraryPorter.ImportBytes(pack.FileName, pack.Bytes);
        Assert.Equal(1, packBack.Agents.Count);
        Assert.Equal("laser-ready-vectors", packBack.Skills.Single().Name);

        var all = LibraryPorter.ExportAll(lib);
        var allBack = LibraryPorter.ImportBytes(all.FileName, all.Bytes);
        Assert.Equal(lib.Skills.Count, allBack.Skills.Count);
        Assert.Equal(lib.Agents.Count, allBack.Agents.Count);
    }

    [Test] static void RuntimeComposesAndLimitsTools()
    {
        var lib = new AiLibrary();
        lib.AddBuiltIns();
        var designer = lib.FindAgent(BuiltIns.DesignerName)!;
        var composed = AgentRuntime.Compose(designer, lib, new[] { lib.FindSkill("slate-coasters")!, lib.FindSkill("laser-ready-vectors")! });
        Assert.True(composed.Contains("graphic designer") && composed.Contains("[Skill: Slate coasters]"), composed);
        Assert.Equal(1, composed.Split("[Skill: Laser-ready vectors]").Length - 1, "a skill is included once");
        Assert.True(AgentRuntime.Section("X", "").Length == 0 && AgentRuntime.Section("X", "be bold").Contains("rules above win"));

        var all = AssistantTools.Definitions(true);
        var limited = new Agent { Tools = { "add_text", "ADD_SHAPE" } };
        var names = AgentRuntime.FilterTools(limited, all).Select(t => t.Name).ToList();
        Assert.Equal("get_project,check_job,add_text,add_shape", string.Join(",", names));
        Assert.Equal(all.Count, AgentRuntime.FilterTools(new Agent { Tools = { Agent.AllTools } }, all).Count);
        Assert.Equal(2, AgentRuntime.FilterTools(new Agent(), all).Count);

        var a = new Agent { Tools = { "add_text", "Bash", "delete_shapes", "*" } };
        var removed = AgentRuntime.ClampTools(a, ceiling: new[] { "add_text", "move_shapes" });
        Assert.Equal("add_text", string.Join(",", a.Tools));
        Assert.Equal("Bash,delete_shapes,*", string.Join(",", removed));
        Assert.True(AgentRuntime.KnownToolNames().Contains("refine_design") && !AgentRuntime.KnownToolNames().Any(n => n.Contains("laser") || n.Contains("burn") || n.Contains("gcode")),
            "no tool can drive the laser");
    }

    [Test] static void AssistProposalsAreParsedAndClamped()
    {
        var lib = new AiLibrary();
        lib.AddBuiltIns();
        var reply = """
            Here you go.
            ```markdown
            ---
            name: scout-badge-designer
            title: Scout badge designer
            description: Designs round Scout badges for 3 mm ply.
            role: general
            tools: [add_text, start_laser, generate_design]
            skills: [round-badges, made-up-skill]
            model: best-model
            ---
            You are a designer of Scout badges.

            ```
            small example fence
            ```
            ```
            Notes:
            - Assumed 60 mm badges.
            - Attached the round badges skill.
            """;
        var p = LibraryAssistant.Parse(reply, ImportAs.Agent, lib);
        var a = p.Agent!;
        Assert.Equal("scout-badge-designer", a.Name);
        Assert.Equal("add_text,generate_design", string.Join(",", a.Tools));
        Assert.Equal("round-badges", a.Skills.Single());
        Assert.Equal("", a.Model);
        Assert.True(p.Notes.Count == 4 && p.Notes[0].StartsWith("Assumed"), string.Join("|", p.Notes));
        Assert.True(string.Join(" ", p.Notes).Contains("start_laser") && string.Join(" ", p.Notes).Contains("made-up-skill"));

        var skill = LibraryAssistant.Parse("```markdown\n---\nname: x\ndescription: d\n---\n# X\nRule\n```\nNotes:\n- n", ImportAs.Skill, null, keepName: "kept");
        Assert.Equal("kept", skill.Skill!.Name);
        try { LibraryAssistant.Parse("I can't help with that.", ImportAs.Skill, null); throw new Exception("expected"); }
        catch (AiException e) { Assert.Equal(AiErrorKind.BadResponse, e.Kind); }
    }

    [Test] static void ImprovingAnAgentNeverWidensItsToolsOrChangesItsModel()
    {
        var lib = new AiLibrary();
        lib.AddBuiltIns();
        var agent = new Agent { Name = "tidy", Role = AgentRole.General, Instructions = "Tidy things.", Provider = "ollama", Model = "local", Tools = { "move_shapes" } };
        var fake = FakeAiClient.Sequence(AiResponse.FromText("""
            ```markdown
            ---
            name: renamed
            role: general
            tools: [move_shapes, delete_shapes, "*"]
            ---
            You tidy layouts: align, space evenly and keep everything on the bed.
            ```
            Notes:
            - Made it specific.
            """));
        var p = new LibraryAssistant(fake).ImproveAsync(agent, lib, "it deleted things").GetAwaiter().GetResult();
        Assert.Equal("tidy", p.Agent!.Name);
        Assert.Equal("move_shapes", string.Join(",", p.Agent.Tools));
        Assert.Equal("ollama/local", p.Agent.Provider + "/" + p.Agent.Model);
        Assert.True(fake.Requests[0].Messages[0].Text.Contains("it deleted things"));
        Assert.True(fake.Requests[0].System!.Contains("never start"), "assist knows the limits");
        Assert.Equal("Tidy things.", agent.Instructions, "the original is untouched until the user accepts");
    }
}

internal static class RefinementTests
{
    private static string Svg(string inner, int size = 100) =>
        $"Draft.\n```svg\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}mm\" height=\"{size}mm\" viewBox=\"0 0 {size} {size}\">{inner}</svg>\n```";

    private const string GoodBadge = """
        <g id="cut"><circle cx="50" cy="50" r="49"/></g>
        <path d="M30 30 L70 30 L70 70 L30 70 Z"/>
        <text x="50" y="85" font-family="Arial" font-size="8" text-anchor="middle">OAK</text>
        """;

    private static DesignResult Parse(string inner, DesignRequest req) => DesignGenerator.Parse(Svg(inner), req, PlaceholderTextOutliner.Instance);

    private static List<string> Codes(string inner, DesignRequest req, DesignStyle style, double bed = 0) =>
        DesignChecks.Run(Parse(inner, req), req, style, bed, bed).Select(f => f.Code).ToList();

    [Test] static void ChecksPassACleanDesign()
    {
        var req = new DesignRequest { Description = "A badge saying \"Oak\"", WidthMm = 100, Style = DesignStyle.EngraveAndCut };
        Assert.Equal("", string.Join(",", Codes(GoodBadge, req, DesignStyle.EngraveAndCut, 400)));
    }

    [Test] static void ChecksCatchWhatWontBurnProperly()
    {
        var req = new DesignRequest { Description = "Sign with \"The Duncans\" and “Est. 2010”", WidthMm = 100, Style = DesignStyle.EngraveAndCut };
        var codes = Codes("""
            <path d="M10 10 L90 10 L90 90 L10 90 L10 10.2"/>
            <path d="M40 40 L60 40 L60 40.1 L40 40.1 Z"/>
            <path d="M5 5 L5.1 5 L5.1 5.1 Z"/>
            <path d="M20 20 L30 20 L30 30 Z"/><path d="M20 20 L30 20 L30 30 Z"/>
            <text x="50" y="50" font-size="8">THE DUNCANS</text>
            """, req, DesignStyle.EngraveAndCut, 80);
        foreach (var expected in new[] { "too-big-for-bed", "open-shapes", "slivers", "tiny-shapes", "duplicate-paths", "no-cut-outline", "missing-wording" })
            Assert.True(codes.Contains(expected), $"expected {expected} in {string.Join(",", codes)}");
        Assert.Equal(1, codes.Count(c => c == "missing-wording"), "only Est. 2010 is missing; The Duncans matches whatever the case");

        var outside = Codes("""<g id="cut"><rect x="30" y="30" width="40" height="40"/></g><path d="M0 0 L100 0 L100 100 L0 100 Z"/>""",
            new DesignRequest { WidthMm = 100 }, DesignStyle.EngraveAndCut);
        Assert.True(outside.Contains("art-outside-cut"), string.Join(",", outside));

        var lines = Codes("""<path d="M0 0 L50 50"/>""", new DesignRequest { WidthMm = 50 }, DesignStyle.Engrave);
        Assert.True(lines.Contains("nothing-to-fill"));
        Assert.Equal("", string.Join(",", Codes("""<path d="M0 0 L50 50"/>""", new DesignRequest { WidthMm = 50 }, DesignStyle.LineArt)));
    }

    [Test] static void CurvedWordingIsRecognisedCharacterByCharacter()
    {
        var svg = """<svg><text transform="rotate(-20 50 50)">H</text><text>A</text><text>N<tspan>D</tspan></text><text>M&amp;E</text></svg>""";
        Assert.True(DesignChecks.ContainsWording(svg, "hand m&e"));
        Assert.True(!DesignChecks.ContainsWording(svg, "handmade"));
        Assert.Equal("Oak|Est. 2010", string.Join("|", DesignChecks.QuotedPhrases("Say \"Oak\" and “Est. 2010” and \"...\"")));
    }

    [Test] static void LoopRevisesUntilTheCriticAccepts()
    {
        var req = new DesignRequest { Description = "A badge saying \"OAK\"", WidthMm = 100, Style = DesignStyle.EngraveAndCut };
        var designer = FakeAiClient.Sequence(
            AiResponse.FromText(Svg("""<path d="M30 30 L70 30 L70 70 L30 70 Z"/>""")),   // no cut outline, no wording
            AiResponse.FromText(Svg("""<g id="cut"><circle cx="50" cy="50" r="49"/></g><path d="M30 30 L70 30 L70 70 L30 70 Z"/><text x="50" y="85" font-size="8">OAK</text>""")),
            AiResponse.FromText(Svg(GoodBadge)));
        var critic = FakeAiClient.Sequence(
            AiResponse.FromText("""{"verdict":"revise","score":3,"issues":["No wording"],"changes":"Add OAK under the square."}"""),
            AiResponse.FromText("""{"verdict":"revise","score":6,"issues":["Text touches the edge"],"changes":"Move the text up 3 mm."}"""),
            AiResponse.FromText("```json\n{\"verdict\":\"accept\",\"score\":9,\"praise\":\"Clean and bold.\"}\n```"));
        var progress = new List<string>();
        var gen = new DesignGenerator(designer) { ExtraInstructions = "House style: chunky." };
        var outcome = new DesignRefiner(gen, new RefineOptions
        {
            Style = DesignStyle.EngraveAndCut, MaxRevisions = 3, CriticClient = critic, CriticInstructions = "Be strict about spelling.", Progress = progress.Add,
        }).RunAsync(req).GetAwaiter().GetResult();

        Assert.Equal(3, outcome.Rounds.Count);
        Assert.Equal(3, outcome.Best.Number);
        Assert.True(outcome.Best.Passed && outcome.Improved);
        Assert.True(outcome.Summary.Contains("2 revision"), outcome.Summary);
        Assert.Equal(2, outcome.First.ProblemCount, "first draft: no cut outline and missing wording");

        // The designer was told both the measured problems and the critic's request.
        var revise = designer.Requests[1].Messages[^1].Text;
        Assert.True(revise.Contains("no cut outline") && revise.Contains("\"OAK\"") && revise.Contains("Add OAK under the square"), revise);
        Assert.True(designer.Requests[0].System!.Contains("House style: chunky") && designer.Requests[0].System!.Contains("Allowed elements ONLY"), "agent text is added to, not instead of, the drawing rules");
        Assert.True(critic.Requests[0].System!.Contains("Be strict about spelling") && critic.Requests[0].Messages[0].Text.Contains("[Problem]"), "critic sees the measurements");
        Assert.True(progress.Count >= 5, string.Join("|", progress));
    }

    [Test] static void LoopStopsAtTheLimitAndOffersTheBestDraft()
    {
        var req = new DesignRequest { Description = "Tag", WidthMm = 100, Style = DesignStyle.EngraveAndCut };
        var designer = FakeAiClient.Sequence(
            AiResponse.FromText(Svg(GoodBadge)),
            AiResponse.FromText(Svg("""<path d="M30 30 L70 30 L70 70 L30 70 Z"/>""")),   // worse: lost the cut outline
            AiResponse.FromText(Svg("""<path d="M30 30 L70 30 L70 70 L30 70 Z"/>""")));
        var critic = FakeAiClient.Sequence(AiResponse.FromText("""{"verdict":"revise","score":7,"issues":["Bit plain"],"changes":"Add a border."}"""));
        var outcome = new DesignRefiner(new DesignGenerator(designer), new RefineOptions { Style = DesignStyle.EngraveAndCut, MaxRevisions = 2, CriticClient = critic })
            .RunAsync(req).GetAwaiter().GetResult();
        Assert.Equal(3, outcome.Rounds.Count);
        Assert.Equal(1, outcome.Best.Number, "a later draft with new problems doesn't replace a better earlier one");
        Assert.True(!outcome.Improved && outcome.Summary.Contains("Stopped after 2"), outcome.Summary);
        Assert.Equal("no-cut-outline", outcome.Recurring.Single().Code);
    }

    [Test] static void ChecksAloneCanDriveTheLoopAndACriticFailureIsSurvived()
    {
        var req = new DesignRequest { Description = "Tag", WidthMm = 100, Style = DesignStyle.EngraveAndCut };
        var designer = FakeAiClient.Sequence(AiResponse.FromText(Svg("""<path d="M30 30 L70 30 L70 70 L30 70 Z"/>""")), AiResponse.FromText(Svg(GoodBadge)));
        var noCritic = new DesignRefiner(new DesignGenerator(designer), new RefineOptions { Style = DesignStyle.EngraveAndCut }).RunAsync(req).GetAwaiter().GetResult();
        Assert.True(noCritic.Rounds.Count == 2 && noCritic.Best.Passed && noCritic.Best.Critic == null);

        var failing = new FakeAiClient(_ => throw new AiException(AiErrorKind.Overloaded, "busy"));
        var d2 = FakeAiClient.Sequence(AiResponse.FromText(Svg(GoodBadge)));
        var outcome = new DesignRefiner(new DesignGenerator(d2), new RefineOptions { Style = DesignStyle.EngraveAndCut, CriticClient = failing }).RunAsync(req).GetAwaiter().GetResult();
        Assert.True(outcome.Best.Passed && outcome.Summary.Contains("critic couldn't be reached"), outcome.Summary);

        Assert.True(DesignRefiner.ParseVerdict("no json here").Accept, "an unreadable critic can't stall the loop");
        Assert.True(!DesignRefiner.ParseVerdict("""{"issues":["x"]}""").Accept);
        Assert.True(DesignRefiner.ParseVerdict("""{"verdict":"Accepted","score":"8"}""").Score == 8);
    }

    [Test] static void SvgExportSimplifiesAndRoundTrips()
    {
        var circle = ShapeFactory.Ellipse(10, 20, 40, 40);
        var line = new Polyline(Enumerable.Range(0, 200).Select(i => new Vec2(10 + i * 0.25, 70)), false);
        var export = SvgExporter.Export(circle.Paths.Append(line), new[] { ShapeFactory.Rectangle(5, 15, 60, 60).Paths[0] });
        Assert.True(export.Svg.Contains("viewBox=\"0 0 60 60\"") && export.Svg.Contains("<g id=\"cut\">"), export.Svg[..120]);
        Assert.True(export.PointCount < 150, $"straight runs collapse: {export.PointCount} points");
        Assert.Near(5, export.Bounds.MinX, 0.001);

        var back = SvgImporter.Import(export.Svg);
        Assert.Equal(3, back.Count);
        Assert.True(back[0].Closed && !back[1].Closed && back[2].Closed);
        Assert.Near(40, back[0].Bounds.Width, 0.1, "circle width survives");
        Assert.Near(2, back[1].Points.Count, 0, "a straight line is two points");

        var dense = new Polyline(Enumerable.Range(0, 20000).Select(i => new Vec2(i * 0.01, Math.Sin(i * 0.05) * 20)), false);
        Assert.True(SvgExporter.Export(new[] { dense }).PointCount <= SvgExporter.MaxPoints, "tolerance grows to fit the budget");
    }

    private sealed class Host : IAssistantHost
    {
        public LaserProject Project { get; } = LaserProject.CreateDefault();
        public HashSet<Guid> Sel = new();
        public IReadOnlyCollection<Guid> Selection => Sel;
        public MaterialLibrary Materials { get; } = MaterialLibrary.CreateDefault();
        public ITextOutliner Outliner => PlaceholderTextOutliner.Instance;
        public IReadOnlyList<string> InstalledFonts { get; } = new[] { "Arial" };
        public IReadOnlyList<string> RecentConsole { get; } = Array.Empty<string>();
        public int Edits;
        public string? SeenSvg, SeenChange;
        public void BeginEdit(string description) => Edits++;
        public void EndEdit(IEnumerable<Guid>? select) { if (select != null) Sel = select.ToHashSet(); }
        public Task<DesignResult>? GenerateDesign(DesignRequest request, CancellationToken ct) => null;
        public Task<DesignResult>? ReviseDesign(string svg, string change, DesignRequest request, CancellationToken ct)
        {
            SeenSvg = svg; SeenChange = change;
            return new DesignGenerator(FakeAiClient.Sequence(AiResponse.FromText(Svg("""<path d="M0 0 L40 0 L40 20 L0 20 Z"/><path d="M5 5 L35 5 L35 15 L5 15 Z"/>""", 40))))
                .ReviseAsync(svg, change, request, ct);
        }
    }

    private static AiResponse Tool(string name, string json) => new()
    {
        StopReason = "tool_use",
        Content = { new AiToolUse("t1", name, JsonNode.Parse(json)!.AsObject()) },
    };

    [Test] static void AssistantCanRedrawExistingShapesInPlace()
    {
        var host = new Host();
        var rect = ShapeFactory.Rectangle(100, 50, 80, 40);
        rect.LayerId = host.Project.GetOrCreateLayer(LayerMode.Fill).Id;
        host.Project.Shapes.Add(rect);
        host.Sel.Add(rect.Id);

        var fake = FakeAiClient.Sequence(Tool("refine_design", """{"target":"selection","change":"add an inner border"}"""), AiResponse.FromText("Done."));
        var reply = new AssistantSession(fake, host).SendAsync("add an inner border to this").GetAwaiter().GetResult();

        Assert.True(host.SeenSvg!.Contains("viewBox=\"0 0 80 40\"") && host.SeenChange == "add an inner border");
        Assert.True(host.Project.Shapes.All(s => s.Id != rect.Id), "old shape replaced");
        var b = ArrayTool.BoundsOf(host.Project.Shapes);
        Assert.Near(100, b.MinX, 0.01, "same place");
        Assert.Near(50, b.MinY, 0.01);
        Assert.Near(80, b.Width, 0.01, "same width");
        Assert.Equal(2, ((VectorShape)host.Project.Shapes.Single()).Paths.Count);
        Assert.Equal(1, host.Edits, "one undo step");
        Assert.True(reply.Actions.Single().Summary.Contains("add an inner border"));
    }

    [Test] static void AgentsLimitWhatTheAssistantMayDo()
    {
        var host = new Host();
        var lib = new AiLibrary();
        lib.AddBuiltIns();
        var agent = new Agent { Name = "labeller", Title = "Labeller", Instructions = "You only add labels.", Tools = { "add_text" }, Skills = { "round-badges" } };
        var fake = FakeAiClient.Sequence(Tool("delete_shapes", """{"target":"all"}"""), Tool("add_text", """{"text":"A","size_mm":5}"""), AiResponse.FromText("Labelled."));
        host.Project.Shapes.Add(ShapeFactory.Rectangle(0, 0, 10, 10));
        var session = new AssistantSession(fake, host) { Agent = agent, Library = lib, Model = "special-model" };
        var reply = session.SendAsync("clear it and label it").GetAwaiter().GetResult();

        Assert.Equal("get_project,check_job,add_text", string.Join(",", fake.Requests[0].Tools.Select(t => t.Name)));
        Assert.Equal("special-model", fake.Requests[0].Model);
        var sys = fake.Requests[0].System!;
        Assert.True(sys.Contains("Your role: Labeller") && sys.Contains("You only add labels.") && sys.Contains("[Skill: Round badges]") && sys.Contains("only have these editing tools: add_text"), "role, skills and limits in the prompt");
        Assert.True(sys.IndexOf("cannot frame, start", StringComparison.Ordinal) < sys.IndexOf("Your role:", StringComparison.Ordinal), "the app's rules come first");
        Assert.Equal(2, host.Project.Shapes.Count, "delete was refused even though the model asked; add_text ran");
        Assert.True(fake.Requests[1].Messages[^1].Content.Single() is AiToolResult { IsError: true } r && r.Content.Contains("isn't allowed"));
        Assert.Equal(1, reply.Actions.Count);

        var noTools = new AssistantSession(FakeAiClient.Sequence(AiResponse.FromText("Advice.")), host) { ToolsSupported = false };
        noTools.SendAsync("help").GetAwaiter().GetResult();
        Assert.Equal(0, noTools.CurrentTools().Count);
        Assert.True(noTools.BuildSystemPrompt().Contains("can't use tools"));
    }
}
