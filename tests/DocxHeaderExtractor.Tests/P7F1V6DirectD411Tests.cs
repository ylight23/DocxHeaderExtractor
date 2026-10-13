using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;
using F = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectTwoStage.SelectFormat;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.11 (offline): arm B only, no version field in the SELECT request, strict reply version; the V6 evidence-tool
/// registry and the native tool-calling agent loop, with scripted models and fake HTTP only. No network.</summary>
public sealed class P7F1V6DirectD411Tests : IDisposable
{
    const string Key = "sk-or-TEST-not-a-real-key-d411-0000";
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d411-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD411Tests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Lazy<P7F1V6Universe> U = new(() =>
        P7F1V6Universe.Rebuild(TestRepository.Root(), P7F1V6DirectD45.Document, P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).SourceSha256, out var f)
        ?? throw new InvalidOperationException(f));

    static readonly string[] Decide = { "a1", "a2" };
    static readonly IReadOnlyDictionary<string, int> Shown = new Dictionary<string, int> { ["a0"] = 0, ["a1"] = 1, ["a2"] = 2 };

    static string Reply(Action<JsonObject>? edit = null)
    {
        var o = new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
            ["decisions"] = new JsonArray(
                new JsonObject { ["alias"] = "a1", ["decision"] = "SELECT", ["evidenceRefs"] = new JsonArray("a0") },
                new JsonObject { ["alias"] = "a2", ["decision"] = "NOT_SELECT" }),
        };
        edit?.Invoke(o);
        return o.ToJsonString();
    }

    // ---------------- request carries no version field ----------------

    [Fact]
    public void SelectRequest_HasNoVersionField_ForEitherFormat_AndTheRequestVersionIsV3()
    {
        Assert.Equal("P7_F1_V6_SELECT_REQUEST_V3", P7F1V6DirectTwoStage.SelectRequestVersion);
        foreach (var f in new[] { F.Objects, F.Compact })
        {
            var req = P7F1V6DirectCanary410.FirstRequest(f, U.Value, "SEG-008");
            using var body = JsonDocument.Parse(req.Body);
            var payload = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.DoesNotContain("protocolVersion", payload);
            Assert.DoesNotContain("responseVersion", payload);
            Assert.DoesNotContain("P7_F1_V6_SELECT", payload);
            // The reply version is still stated, by the pinned system prompt only.
            Assert.Contains(P7F1V6DirectTwoStage.SelectVersion(f), body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        }
    }

    [Fact]
    public void Prompts_AreUnchanged_ByTheRequestChange()
    {
        Assert.Equal(P7F1V6DirectTwoStage.SelectObjectsPromptSha256, P7F1V6DirectTwoStage.Sha(P7F1V6DirectTwoStage.SelectObjectsPrompt));
        Assert.Equal(P7F1V6DirectTwoStage.SelectCompactPromptSha256, P7F1V6DirectTwoStage.Sha(P7F1V6DirectTwoStage.SelectCompactPrompt));
    }

    // ---------------- the parser stays strict; the two failures are told apart ----------------

    [Fact]
    public void Parser_AcceptsTheExactReply()
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Objects, Reply(), Shown, Decide);
        Assert.True(r.Ok, r.Failure);
    }

    [Fact]
    public void ExtraRootKey_WithTheCorrectVersion_IsRootKeysInvalid()
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Objects, Reply(o => o["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion), Shown, Decide);
        Assert.False(r.Ok);
        Assert.Equal("ROOT_KEYS_INVALID", r.Failure);
    }

    [Theory]
    [InlineData("P7_F1_V6_SELECT_REQUEST_V2")]
    [InlineData("P7_F1_V6_SELECT_REQUEST_V3")]
    [InlineData("P7_F1_V6_SELECT_COMPACT_V1")]
    [InlineData("")]
    public void WrongVersion_WithNoExtraKey_IsProtocolVersionInvalid(string version)
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Objects, Reply(o => o["protocolVersion"] = version), Shown, Decide);
        Assert.False(r.Ok);
        Assert.Equal("PROTOCOL_VERSION_INVALID", r.Failure);
    }

    [Fact]
    public void MissingVersion_IsProtocolVersionInvalid()
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Objects, Reply(o => o.Remove("protocolVersion")), Shown, Decide);
        Assert.Equal("PROTOCOL_VERSION_INVALID", r.Failure);
    }

    [Fact]
    public void ExtraKey_AndWrongVersion_ReportsRootKeysFirst()
    {
        var r = P7F1V6DirectTwoStage.ParseSelect(F.Objects, Reply(o =>
        {
            o["protocolVersion"] = "P7_F1_V6_SELECT_REQUEST_V2";
            o["responseVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion;
        }), Shown, Decide);
        Assert.Equal("ROOT_KEYS_INVALID", r.Failure);
    }

    // ---------------- counterfactual on the recorded D4.10 arm B replies (NOT an official Qwen result) ----------------

    static string RecordedContent(string rawKey)
    {
        var path = Path.Combine(TestRepository.Root(), "artifacts", "v5-p7-f1-v6-d4-contractfix", "d410-canary", "arm-B", "raw", rawKey + ".json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
    }

    /// <summary>COUNTERFACTUAL. The recorded replies are edited in memory to show which check each copied field trips. An edited reply is
    /// not what Qwen sent; nothing here is a Qwen result, and no recorded file is changed.</summary>
    [Theory]
    [InlineData("SEG-008", "26922f43d610ebdc80206d5e0ec4e52d1ac524598aa9df8594ca66ab1689ce43")]
    [InlineData("SEG-009", "c00b214b80d97da8a7f4c9ddc881cda57ecd7925421c9950c613f166f2bfb7cb")]
    public void Counterfactual_RecordedArmBReplies_FailOnTheCopiedVersionFieldsOnly(string segment, string rawKey)
    {
        var (decide, context) = P7F1V6DirectCanary410.Scope(U.Value, segment);
        var shown = context.ToDictionary(i => U.Value.Lines[i].Alias, i => i, StringComparer.Ordinal);
        var recorded = RecordedContent(rawKey);
        var root = JsonNode.Parse(recorded)!.AsObject();
        Assert.Equal("P7_F1_V6_SELECT_REQUEST_V2", root["protocolVersion"]!.GetValue<string>());
        Assert.Equal(P7F1V6DirectTwoStage.SelectObjectsVersion, root["responseVersion"]!.GetValue<string>());

        // As recorded: the extra key is caught first.
        Assert.Equal("ROOT_KEYS_INVALID", P7F1V6DirectTwoStage.ParseSelect(F.Objects, recorded, shown, decide).Failure);

        // Counterfactual 1: without responseVersion, the copied request version is caught.
        var noExtra = root.DeepClone().AsObject();
        noExtra.Remove("responseVersion");
        Assert.Equal("PROTOCOL_VERSION_INVALID", P7F1V6DirectTwoStage.ParseSelect(F.Objects, noExtra.ToJsonString(), shown, decide).Failure);

        // Counterfactual 2: with the reply version the prompt states, the rest of the reply passes the contract.
        noExtra["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion;
        var fixedReply = P7F1V6DirectTwoStage.ParseSelect(F.Objects, noExtra.ToJsonString(), shown, decide);
        Assert.True(fixedReply.Ok, fixedReply.Failure);
        Assert.Equal(decide.Length, fixedReply.Values.Count);
    }

    // ================= evidence-tool registry =================

    static P7F1V6AgentTools NewTools() => new(U.Value.Tools);
    static string L(int i) => U.Value.Lines[i].Alias;

    /// <summary>A SEG-001 target (index 48..95) whose normalized text also occurs outside the segment's shown window, and that occurrence.
    /// Found from parser text only; nothing here says which of the two is a heading.</summary>
    static (int Target, int Other) RepeatPairOutsideWindow()
    {
        var (_, ctx) = P7F1V6DirectCanary410.Scope(U.Value, "SEG-001");
        var shown = ctx.ToHashSet();
        var lines = U.Value.Lines;
        for (int i = 48; i < 96; i++)
        {
            var key = P7F1QEvidenceTools.RepeatKey(lines[i].Text);
            if (key.Length < 8) continue;
            for (int j = 0; j < lines.Count; j++)
                if (j != i && !shown.Contains(j) && P7F1QEvidenceTools.RepeatKey(lines[j].Text) == key) return (i, j);
        }
        throw new InvalidOperationException("NO_PAIR");
    }

    static JsonElement Json(P7F1V6AgentTools.Result r)
    {
        using var d = JsonDocument.Parse(r.Content);
        return d.RootElement.Clone();
    }

    [Fact]
    public void Registry_DeclaresSevenTools_PinnedSha_NoPageImage_AndALineageForEach()
    {
        var defs = P7F1V6AgentTools.Definitions();
        var names = defs.Select(d => d!["function"]!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(P7F1V6AgentTools.Names, names);
        Assert.Equal(7, names.Length);
        Assert.DoesNotContain("get_page_image", names);
        Assert.Equal(A.ToolDefinitionsSha256, P7F1V6AgentTools.DefinitionsSha256());
        Assert.All(names, n => Assert.True(P7F1V6AgentTools.Lineage.ContainsKey(n)));
        Assert.All(defs, d => Assert.False(d!["function"]!["parameters"]!["additionalProperties"]!.GetValue<bool>()));
        Assert.StartsWith("NOT_REGISTERED", P7F1V6AgentTools.PageImageStatus, StringComparison.Ordinal);
    }

    static readonly Regex KindWords = new(@"\b(toc|contents|table of contents|heading|headings|title|titles|header|footer|caption|section|chapter)\b|mục lục",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void ToolDescriptions_SayWhatIsReturned_NeverWhichKindOfLineToLookFor()
    {
        foreach (var d in P7F1V6AgentTools.Definitions())
        {
            var text = d!["function"]!.ToJsonString();
            Assert.False(KindWords.IsMatch(text), d["function"]!["name"] + ": " + KindWords.Match(text).Value);
        }
        // The F1Q descriptions that did name kinds of line are not reused.
        Assert.Matches(KindWords, P7F1QEvidenceTools.DefinitionsV2().ToJsonString());
    }

    [Fact]
    public void AgentPrompts_HoldNoRuleAboutContentsLines_AndArePinned()
    {
        var toc = new Regex(@"\b(toc|contents|table of contents|header|footer|page number|dot leader)\b|mục lục", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var arm in new[] { A.Arm.Auto, A.Arm.EvidenceFirst })
        {
            Assert.False(toc.IsMatch(A.Prompt(arm)), toc.Match(A.Prompt(arm)).Value);
            Assert.DoesNotContain("NEEDS_EVIDENCE", A.Prompt(arm));
            Assert.Contains(P7F1V6DirectTwoStage.SelectObjectsVersion, A.Prompt(arm));
        }
        Assert.Equal(A.AutoPromptSha256, A.Sha(A.AutoPrompt));
        Assert.Equal(A.EvidenceFirstPromptSha256, A.Sha(A.EvidenceFirstPrompt));
        // The two arms differ by one line only.
        var auto = A.AutoPrompt.Split('\n'); var first = A.EvidenceFirstPrompt.Split('\n');
        Assert.Equal(auto.Length + 1, first.Length);
        Assert.Single(first.Except(auto));
        Assert.Contains($"at most {A.MaxToolTurns} rounds of tool calls", A.AutoPrompt);
    }

    static readonly Regex KindOfLine = new(@"\bb(toc|contents|table of contents|headers?|footers?|page numbers?|dot leaders?|index|indexes|entry|entries|navigation|furniture|metadata|letterhead|title|titles|subtitle|caption|chapter|section)\b|mục lục",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void AgentPrompt_TellsTheModelWhenAndHowToGatherEvidence_WithoutNamingAnyKindOfLine()
    {
        foreach (var arm in new[] { A.Arm.Auto, A.Arm.EvidenceFirst })
        {
            var prompt = A.Prompt(arm);
            // What the tools read, that the view is partial, the procedure, the exit, and the limits.
            Assert.Contains("read the parser's record of the whole document", prompt);
            Assert.Contains("You see only the lines in this request", prompt);
            Assert.Contains("ask for each target whether the lines shown are enough", prompt);
            Assert.Contains("gather evidence first: reply with tool calls instead of the JSON answer", prompt);
            Assert.Contains($"up to {A.MaxCallsPerTurn} calls in one reply", prompt);
            Assert.Contains($"at most {A.MaxToolTurns} rounds of tool calls", prompt);
            Assert.Contains("answer directly; do not call a tool for its own sake", prompt);
            // References are aliases; an evidence id would be refused as not shown.
            Assert.DoesNotContain("evidenceId", prompt);
            Assert.DoesNotContain("evidence id", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.False(KindOfLine.IsMatch(prompt), KindOfLine.Match(prompt).Value);
        }
        Assert.Contains("your first reply must be a batch of tool calls", A.EvidenceFirstPrompt);
        Assert.DoesNotContain("must be a batch of tool calls", A.AutoPrompt);
        Assert.Equal("P7_F1_V6_SELECT_AGENT_PROMPT_V3", A.PromptVersion);
    }

    [Fact]
    public void TheToolParagraph_MatchesTheRegistry_EveryCapabilityItNamesIsATool()
    {
        // Each capability the prompt names has a registered tool behind it.
        var described = new Dictionary<string, string>
        {
            ["the lines before and after a line"] = P7F1V6AgentTools.SourceContext,
            ["the lines of a page"] = P7F1V6AgentTools.PageLayout,
            ["the full record of a line"] = P7F1V6AgentTools.SourceSpan,
            ["a text search"] = P7F1V6AgentTools.SearchText,
            ["the other lines with the same text"] = P7F1V6AgentTools.Repeated,
            ["two lines side by side"] = P7F1V6AgentTools.Compare,
        };
        foreach (var (phrase, tool) in described)
        {
            Assert.Contains(phrase, A.AutoPrompt);
            Assert.Contains(tool, P7F1V6AgentTools.Names);
        }
    }

    [Fact]
    public void RepeatedOccurrences_ShowsTheOtherOccurrence_OfASegment001Line()
    {
        var (t, o) = RepeatPairOutsideWindow();
        var tools = NewTools();
        var r = tools.Execute("E1.01", P7F1V6AgentTools.Repeated, JsonSerializer.Serialize(new { alias = L(t), maxResults = 5 }));
        Assert.Equal("OK", r.Status);
        Assert.Contains(L(o), r.ReturnedAliases);
        Assert.Contains(L(o), tools.ObservedAliases);
        var j = Json(r);
        Assert.Equal("NONE", j.GetProperty("provenance").GetProperty("semanticLabels").GetString());
        Assert.True(j.GetProperty("result").GetProperty("totalMatches").GetInt32() >= 1);
    }

    [Fact]
    public void CompareSourceLocations_PutsBothLinesSideBySide_WithArithmeticDeltasOnly()
    {
        var (t, o) = RepeatPairOutsideWindow();
        var r = NewTools().Execute("E1.01", P7F1V6AgentTools.Compare, JsonSerializer.Serialize(new { first = L(t), second = L(o), window = 2 }));
        Assert.Equal("OK", r.Status);
        var d = Json(r).GetProperty("result").GetProperty("delta");
        Assert.True(d.GetProperty("sameNormalizedText").GetBoolean());
        Assert.Equal(o - t, d.GetProperty("readingOrderDistance").GetInt32());
        Assert.True(d.GetProperty("pageDistance").GetInt32() > 0);
        Assert.Contains(L(t), r.ReturnedAliases);
        Assert.Contains(L(o), r.ReturnedAliases);
        // Field names are observations; none names a kind of line.
        Assert.DoesNotContain(PropertyNames(Json(r)), n => KindWords.IsMatch(n));
    }

    static IEnumerable<string> PropertyNames(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(PropertyNames),
        _ => Enumerable.Empty<string>(),
    };

    [Fact]
    public void EveryResult_FitsTheCap_AndACutResultSaysSo()
    {
        var tools = NewTools();
        var u = U.Value;
        var results = new List<P7F1V6AgentTools.Result> { tools.Execute("E1.01", P7F1V6AgentTools.DocumentOverview, "{}") };
        foreach (var page in u.Lines.Select(l => l.Page).Distinct())
            results.Add(tools.Execute("E1.02", P7F1V6AgentTools.PageLayout, JsonSerializer.Serialize(new { page, offset = 0, limit = 40 })));
        var longest = Enumerable.Range(0, u.Lines.Count).OrderByDescending(i => u.Lines[i].Text.Length).Take(8).Select(L).ToArray();
        results.Add(tools.Execute("E1.03", P7F1V6AgentTools.SourceSpan, JsonSerializer.Serialize(new { aliases = longest })));
        results.Add(tools.Execute("E1.04", P7F1V6AgentTools.SourceContext, JsonSerializer.Serialize(new { alias = L(500), before = 6, after = 6 })));
        results.Add(tools.Execute("E1.05", P7F1V6AgentTools.Compare, JsonSerializer.Serialize(new { first = L(500), second = L(900), window = 3 })));
        var search = tools.Execute("E1.06", P7F1V6AgentTools.SearchText, JsonSerializer.Serialize(new { query = "the", mode = "normalized", offset = 0, limit = 50 }));
        results.Add(search);
        Assert.All(results, r => { Assert.Equal("OK", r.Status); Assert.True(r.Content.Length <= P7F1V6AgentTools.MaxResultBytes, r.Tool + " " + r.Content.Length); });
        Assert.Equal(results.Count, tools.Ledger.Count);
    }

    [Fact]
    public void AResultOverTheCap_IsCutByWholeItems_AndSaysWhereToContinue()
    {
        // Synthetic lines (SRC-095 lines are short enough that its results fit): 60 lines of 290 characters.
        var box = JsonDocument.Parse("{\"left\":72.0,\"right\":540.0,\"bottom\":700.0,\"top\":712.0}").RootElement;
        var font = JsonDocument.Parse("{\"fontSize\":10.0,\"fontName\":\"F1\",\"boldRatio\":0.0,\"italicRatio\":0.0}").RootElement;
        var synthetic = Enumerable.Range(0, 60).Select(i => new F1QSourceLine($"L{i:0000}:S0", i, 1 + i / 30, $"line {i:00} " + new string('x', 280), box, font)).ToArray();
        var tools = new P7F1V6AgentTools(new P7F1QEvidenceTools("s", "u", "e", synthetic, new Dictionary<string, string> { ["O1"] = "L0000:S0" }, 2));
        var search = tools.Execute("E1.01", P7F1V6AgentTools.SearchText, JsonSerializer.Serialize(new { query = "line", mode = "literal", offset = 0, limit = 50 }));
        Assert.Equal("OK", search.Status);
        Assert.True(search.Content.Length <= P7F1V6AgentTools.MaxResultBytes);
        Assert.True(search.Truncated);
        Assert.True(tools.Ledger.Single().Truncated);
        var res = Json(search).GetProperty("result");
        Assert.True(res.GetProperty("truncation").GetProperty("truncated").GetBoolean());
        var returned = res.GetProperty("matches").GetArrayLength();
        Assert.Equal(returned, res.GetProperty("nextOffset").GetInt32());
        Assert.InRange(returned, 1, 49);
        Assert.Equal(returned, search.ReturnedAliases.Count);
        Assert.Equal(60, res.GetProperty("total").GetInt32());
        // Continuing from nextOffset returns the next lines, not the same ones.
        var next = tools.Execute("E1.02", P7F1V6AgentTools.SearchText, JsonSerializer.Serialize(new { query = "line", mode = "literal", offset = returned, limit = 50 }));
        Assert.Empty(next.ReturnedAliases.Intersect(search.ReturnedAliases));
    }

    [Theory]
    [InlineData("get_page_image", "{}", "UNKNOWN_TOOL", "UNKNOWN_TOOL")]
    [InlineData("get_source_context", "{\"alias\":\"L0042:S0\",\"before\":1}", "INVALID_ARGUMENT", "ARGUMENT_REQUIRED_MISSING")]
    [InlineData("get_source_context", "{\"alias\":\"L0042:S0\",\"before\":1,\"after\":1,\"kind\":\"toc\"}", "INVALID_ARGUMENT", "ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE")]
    [InlineData("get_source_context", "{\"alias\":\"L0042:S0\",\"before\":7,\"after\":1}", "INVALID_ARGUMENT", "ARGUMENT_OUT_OF_RANGE")]
    [InlineData("get_source_context", "{\"alias\":\"L0042:S0\",\"before\":\"1\",\"after\":1}", "INVALID_ARGUMENT", "ARGUMENT_TYPE_INVALID")]
    [InlineData("get_source_context", "{\"alias\":\"O1\",\"before\":1,\"after\":1}", "INVALID_ARGUMENT", "ALIAS_NOT_IN_DOCUMENT")]
    [InlineData("get_source_span", "{\"aliases\":[]}", "INVALID_ARGUMENT", "ALIASES_INVALID")]
    [InlineData("get_page_layout", "{\"page\":999,\"offset\":0,\"limit\":5}", "INVALID_ARGUMENT", "PAGE_NOT_IN_DOCUMENT")]
    [InlineData("search_document_text", "{\"query\":\"a\",\"mode\":\"literal\",\"offset\":0,\"limit\":5}", "INVALID_ARGUMENT", "QUERY_TOO_SHORT")]
    [InlineData("search_document_text", "{\"query\":\"HTTP\",\"mode\":\"regex\",\"offset\":0,\"limit\":5}", "INVALID_ARGUMENT", "MODE_INVALID")]
    [InlineData("compare_source_locations", "{\"first\":\"L0042:S0\",\"second\":\"L0042:S0\",\"window\":1}", "INVALID_ARGUMENT", "COMPARE_SAME_LINE")]
    [InlineData("get_repeated_occurrences", "not json", "INVALID_ARGUMENT", "ARGUMENTS_NOT_JSON")]
    [InlineData("get_repeated_occurrences", "[1]", "INVALID_ARGUMENT", "ARGUMENTS_NOT_OBJECT")]
    [InlineData("get_document_overview", "{\"x\":1}", "INVALID_ARGUMENT", "ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE")]
    public void BadArguments_AreAnsweredWithACode_RecordedAndObserveNothing(string name, string args, string status, string code)
    {
        var tools = NewTools();
        var r = tools.Execute("E1.01", name, args);
        Assert.Equal(status, r.Status);
        Assert.Equal(code, r.ErrorCode);
        Assert.Empty(r.ReturnedAliases);
        Assert.Empty(tools.ObservedAliases);
        Assert.Equal(code, Assert.Single(tools.Ledger).ErrorCode);
        Assert.True(r.Content.Length <= P7F1V6AgentTools.MaxResultBytes);
    }

    [Fact]
    public void OverlongArguments_AreRefused_AndAnEmptyArgumentStringMeansNoArguments()
    {
        var tools = NewTools();
        Assert.Equal("ARGUMENTS_TOO_LARGE", tools.Execute("E1.01", P7F1V6AgentTools.SearchText, "{\"query\":\"" + new string('x', 3000) + "\"}").ErrorCode);
        Assert.Equal("OK", tools.Execute("E1.02", P7F1V6AgentTools.DocumentOverview, "").Status);
        Assert.Equal("OK", tools.Execute("E1.03", P7F1V6AgentTools.DocumentOverview, null).Status);
    }

    // ================= agent loop =================

    static string Final(IEnumerable<string> targets, Func<string, (string Decision, string[]? Refs)>? decide = null, string status = "COMPLETE") => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = status,
        ["decisions"] = new JsonArray(targets.Select(a =>
        {
            var (d, refs) = decide?.Invoke(a) ?? ("NOT_SELECT", null);
            var o = new JsonObject { ["alias"] = a, ["decision"] = d };
            if (refs is not null) o["evidenceRefs"] = new JsonArray(refs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            return (JsonNode)o;
        }).ToArray()),
    }.ToJsonString();

    static JsonElement Body(P7F1V6Protocol.PreparedRequest r)
    {
        using var d = JsonDocument.Parse(r.Body);
        return d.RootElement.Clone();
    }

    static string[] Targets(P7F1V6Protocol.PreparedRequest r)
    {
        using var p = JsonDocument.Parse(Body(r).GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    }

    static A.ModelTurn Call(params (string? Id, string Name, object Args)[] calls) =>
        new(null, calls.Select(c => new A.ToolCall(c.Id, c.Name, JsonSerializer.Serialize(c.Args))).ToArray(), "tool_calls");

    static A.ModelTurn Answer(string content) => new(content, Array.Empty<A.ToolCall>(), "stop");

    static A.AgentUnitOutcome Run(A.Arm arm, A.AgentModel model, P7F1V6DirectV2.Budget? budget = null, string segment = "SEG-001")
    {
        var (decide, ctx) = P7F1V6DirectCanary410.Scope(U.Value, segment);
        return A.RunUnit(arm, P7F1V6DirectD45.Instruction, U.Value, segment, decide, ctx, model, budget ?? new P7F1V6DirectV2.Budget(100, 100, 2_000_000));
    }

    [Fact]
    public void Chain_ToolCall_ToolMessageWithItsId_ThenAnAnswerCitingALineSeenOnlyThroughTheTool()
    {
        var (t, o) = RepeatPairOutsideWindow();
        var seen = new List<P7F1V6Protocol.PreparedRequest>();
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            seen.Add(req);
            return turn == 1
                ? Call(("call_a", P7F1V6AgentTools.Compare, new { first = L(t), second = L(o), window = 1 }))
                : Answer(Final(Targets(req), a => a == L(t) ? ("NOT_SELECT", new[] { L(o) }) : ("NOT_SELECT", null)));
        });

        Assert.True(outcome.Unit.Status == "COMPLETE", outcome.Unit.Failure);
        Assert.Equal(2, seen.Count);
        var turn2 = Body(seen[1]);
        var msgs = turn2.GetProperty("messages");
        Assert.Equal(4, msgs.GetArrayLength());
        Assert.Equal("assistant", msgs[2].GetProperty("role").GetString());
        var call = msgs[2].GetProperty("tool_calls")[0];
        Assert.Equal("call_a", call.GetProperty("id").GetString());
        Assert.Equal(P7F1V6AgentTools.Compare, call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", msgs[3].GetProperty("role").GetString());
        Assert.Equal("call_a", msgs[3].GetProperty("tool_call_id").GetString());
        var toolContent = msgs[3].GetProperty("content").GetString()!;
        Assert.Contains("\"evidenceId\":\"E1.01\"", toolContent);
        Assert.Contains(L(o), toolContent);
        Assert.Equal("auto", turn2.GetProperty("tool_choice").GetString());
        // The first two messages and the declared tools are the same in both turns.
        Assert.Equal(Body(seen[0]).GetProperty("messages")[1].GetRawText(), msgs[1].GetRawText());
        Assert.Equal(Body(seen[0]).GetProperty("tools").GetRawText(), turn2.GetProperty("tools").GetRawText());

        Assert.Equal(1, outcome.SuccessfulToolCalls);
        Assert.Equal(1, outcome.Scope.RefsToToolOnlyLines);
        Assert.Equal(new[] { L(t) }, outcome.Scope.DecisionsCitingToolOnlyLines);
        Assert.True(outcome.Scope.ObservedThroughToolsOnly >= 1);
        Assert.Equal(48, outcome.Scope.EvaluatedTargets);
        Assert.False(outcome.Scope.WholeDocumentObserved);
        Assert.Equal(U.Value.Lines.Count, outcome.Scope.WholeDocumentLines);
        Assert.Equal(new[] { "auto", "auto" }, outcome.Turns.Select(x => x.ToolChoice));
    }

    [Fact]
    public void AnAnswerCitingALineItNeverSaw_IsRefused()
    {
        var (t, o) = RepeatPairOutsideWindow();
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
            Answer(Final(Targets(req), a => a == L(t) ? ("NOT_SELECT", new[] { L(o) }) : ("NOT_SELECT", null))));
        Assert.Equal("FAILED", outcome.Unit.Status);
        Assert.Equal("PROTOCOL:EVIDENCE_NOT_SHOWN", outcome.Unit.Failure);
        Assert.All(outcome.Unit.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    [Fact]
    public void Auto_MayAnswerWithoutTools()
    {
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => Answer(Final(Targets(req))));
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Equal(1, outcome.Unit.Calls);
        Assert.Empty(outcome.ToolCalls);
        Assert.Equal(0, outcome.Scope.ObservedThroughToolsOnly);
    }

    [Fact]
    public void EvidenceFirst_RefusesAnAnswerBeforeAnySuccessfulToolResult()
    {
        var direct = Run(A.Arm.EvidenceFirst, (unit, turn, req) => Answer(Final(Targets(req))));
        Assert.Equal("PROTOCOL:TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED", direct.Unit.Failure);

        // A call that failed its argument check is not evidence.
        var invalidOnly = Run(A.Arm.EvidenceFirst, (unit, turn, req) => turn == 1
            ? Call(("c1", P7F1V6AgentTools.SourceContext, new { alias = "nope", before = 1, after = 1 }))
            : Answer(Final(Targets(req))));
        Assert.Equal("PROTOCOL:TOOL_EVIDENCE_REQUIRED_NOT_REQUESTED", invalidOnly.Unit.Failure);
        Assert.Equal("ALIAS_NOT_IN_DOCUMENT", Assert.Single(invalidOnly.ToolCalls).ErrorCode);

        var ok = Run(A.Arm.EvidenceFirst, (unit, turn, req) => turn == 1
            ? Call(("c1", P7F1V6AgentTools.DocumentOverview, new { }))
            : Answer(Final(Targets(req))));
        Assert.Equal("COMPLETE", ok.Unit.Status);
    }

    [Fact]
    public void AfterThreeToolTurns_TheFourthTurnIsToolChoiceNone_AndAToolCallThereFails()
    {
        var seen = new List<string>();
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            seen.Add(Body(req).GetProperty("tool_choice").GetString()!);
            return Call(("c" + turn, P7F1V6AgentTools.DocumentOverview, new { }));
        });
        Assert.Equal(new[] { "auto", "auto", "auto", "none" }, seen);
        Assert.Equal("PROTOCOL:TOOL_CALL_AFTER_TOOL_CAP", outcome.Unit.Failure);
        Assert.Equal(A.MaxPostsPerUnit, outcome.Unit.Calls);
    }

    [Fact]
    public void FourthTurn_AnswerIsAccepted()
    {
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => turn < 4 ? Call(("c" + turn, P7F1V6AgentTools.DocumentOverview, new { })) : Answer(Final(Targets(req))));
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Equal(4, outcome.Unit.Calls);
        Assert.Equal(3, outcome.SuccessfulToolCalls);
    }

    [Fact]
    public void OverThePerTurnCap_EveryCallIsAnsweredUnderQwensOwnId_AndTheNinthIsRejected()
    {
        P7F1V6Protocol.PreparedRequest? second = null;
        // Small results, so only the per-turn cap is at work, not the byte cap.
        var nine = Enumerable.Range(1, 9).Select(k => ((string?)("qwen_" + k), P7F1V6AgentTools.SourceSpan, (object)new { aliases = new[] { L(47 + k) } })).ToArray();
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            if (turn == 2) second = req;
            return turn == 1 ? Call(nine) : Answer(Final(Targets(req)));
        });
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        var msgs = Body(second!).GetProperty("messages");
        var expected = Enumerable.Range(1, 9).Select(k => "qwen_" + k).ToArray();
        Assert.Equal(expected, msgs[2].GetProperty("tool_calls").EnumerateArray().Select(c => c.GetProperty("id").GetString()!));
        Assert.Equal(expected, Enumerable.Range(3, 9).Select(i => msgs[i].GetProperty("tool_call_id").GetString()!));
        Assert.Equal(8, outcome.ToolCalls.Count(c => c.Status == "OK"));
        Assert.Equal("PER_TURN_CALL_CAP_EXCEEDED", outcome.ToolCalls.Last().ErrorCode);
        Assert.Contains("\"status\":\"REJECTED\"", msgs[11].GetProperty("content").GetString());
    }

    public static TheoryData<string?, string?, string> BadIdTurns => new()
    {
        { null, "b", "PROTOCOL:TOOL_CALL_ID_MISSING" },
        { "", "b", "PROTOCOL:TOOL_CALL_ID_MISSING" },
        { "a", null, "PROTOCOL:TOOL_CALL_ID_MISSING" },
        { "a", "a", "PROTOCOL:TOOL_CALL_ID_DUPLICATE" },
    };

    [Theory]
    [MemberData(nameof(BadIdTurns))]
    public void AMissingOrRepeatedId_RefusesTheWholeTurn_BeforeAnyToolRuns(string? first, string? second, string failure)
    {
        var posts = 0;
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            posts++;
            return Call((first, P7F1V6AgentTools.DocumentOverview, new { }), (second, P7F1V6AgentTools.SourceContext, new { alias = L(48), before = 1, after = 1 }));
        });
        Assert.Equal("FAILED", outcome.Unit.Status);
        Assert.Equal(failure, outcome.Unit.Failure);
        Assert.Empty(outcome.ToolCalls);              // nothing was dispatched
        Assert.Equal(0, outcome.Evidence.ToolSucceeded);
        // Qwen did emit two calls; a refused turn still counts them (D4.11b).
        Assert.Equal(2, outcome.Evidence.ModelToolCallsEmitted);
        Assert.Equal(0, outcome.Evidence.ToolsDispatched);
        Assert.Equal(1, posts);
    }

    [Fact]
    public void AnIdReusedInALaterTurn_IsADuplicate_AndOnlyTheFirstTurnRan()
    {
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => Call(("same", P7F1V6AgentTools.DocumentOverview, new { })));
        Assert.Equal("PROTOCOL:TOOL_CALL_ID_DUPLICATE", outcome.Unit.Failure);
        Assert.Equal(2, outcome.Unit.Calls);
        Assert.Single(outcome.ToolCalls);
    }

    [Fact]
    public void ToolCallsWithFinishStop_AreNotReadAsAToolTurn()
    {
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
            new A.ModelTurn(null, new[] { new A.ToolCall("x1", P7F1V6AgentTools.DocumentOverview, "{}") }, "stop"));
        Assert.Equal("PROTOCOL:TOOL_CALLS_WITH_FINISH_STOP", outcome.Unit.Failure);
        Assert.Empty(outcome.ToolCalls);
        var nullFinish = Run(A.Arm.Auto, (unit, turn, req) =>
            new A.ModelTurn(null, new[] { new A.ToolCall("x1", P7F1V6AgentTools.DocumentOverview, "{}") }, null));
        Assert.Equal("PROTOCOL:TOOL_CALLS_WITH_FINISH_NULL", nullFinish.Unit.Failure);
    }

    [Theory]
    [InlineData("length", "PROTOCOL:ANSWER_WITH_FINISH_LENGTH")]
    [InlineData("tool_calls", "PROTOCOL:ANSWER_WITH_FINISH_TOOL_CALLS")]
    [InlineData(null, "PROTOCOL:ANSWER_WITH_FINISH_NULL")]
    public void AnAnswerNeedsFinishStop(string? finish, string failure)
    {
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => new A.ModelTurn(Final(Targets(req)), Array.Empty<A.ToolCall>(), finish));
        Assert.Equal(failure, outcome.Unit.Failure);
        Assert.All(outcome.Unit.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    [Fact]
    public void EvidenceReport_KeepsInvokedSucceededShownAndUsedApart()
    {
        // An overview satisfies EVIDENCE_FIRST, but shows no target and is cited by no decision.
        var overview = Run(A.Arm.EvidenceFirst, (unit, turn, req) => turn == 1
            ? Call(("o1", P7F1V6AgentTools.DocumentOverview, new { }), ("o2", P7F1V6AgentTools.SourceContext, new { alias = "nope", before = 1, after = 1 }))
            : Answer(Final(Targets(req))));
        Assert.Equal("COMPLETE", overview.Unit.Status);
        Assert.Equal(2, overview.Evidence.ModelToolCallsEmitted);
        Assert.Equal(2, overview.Evidence.ToolsDispatched);
        Assert.Equal(0, overview.Evidence.ToolsRejectedBeforeDispatch);
        Assert.Equal(1, overview.Evidence.ToolSucceeded);
        Assert.Equal(0, overview.Evidence.SuccessfulResultsShowingATarget);
        Assert.Empty(overview.Evidence.TargetsShownByTools);
        Assert.Empty(overview.Evidence.EvidenceUsed);
        Assert.Equal(A.RelevanceAuditedOffline, overview.Evidence.RelevantEvidenceObserved);

        var (t, o) = RepeatPairOutsideWindow();
        var used = Run(A.Arm.EvidenceFirst, (unit, turn, req) => turn == 1
            ? Call(("c1", P7F1V6AgentTools.Compare, new { first = L(t), second = L(o), window = 0 }))
            : Answer(Final(Targets(req), a => a == L(t) ? ("NOT_SELECT", new[] { L(o) }) : ("NOT_SELECT", null))));
        Assert.Equal(1, used.Evidence.ToolSucceeded);
        Assert.Equal(1, used.Evidence.SuccessfulResultsShowingATarget);
        Assert.Contains(L(t), used.Evidence.TargetsShownByTools);
        Assert.Equal(new[] { L(t) }, used.Evidence.EvidenceUsed);
        Assert.Equal(A.RelevanceAuditedOffline, used.Evidence.RelevantEvidenceObserved);
    }

    [Fact]
    public void LargeResults_StopAtTheUnitByteBudget_SoTheFinalTurnStaysUnderTheRequestCap()
    {
        var pages = U.Value.Lines.Select(l => l.Page).Distinct().ToArray();
        P7F1V6Protocol.PreparedRequest? last = null;
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            last = req;
            return turn < 4
                ? Call(Enumerable.Range(0, 8).Select(k => ((string?)$"p{turn}{k}", P7F1V6AgentTools.PageLayout, (object)new { page = pages[(turn * 8 + k) % pages.Length], offset = 0, limit = 40 })).ToArray())
                : Answer(Final(Targets(req)));
        });
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Contains(outcome.ToolCalls, c => c.ErrorCode == "UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED");
        // The cap counts every tool message, rejected ones included (D4.11b: the D4.11a dry-run reached 68,938 B).
        Assert.True(outcome.ToolCalls.Sum(c => c.ContentBytes) <= A.MaxToolMessageBytesPerUnit, outcome.ToolCalls.Sum(c => c.ContentBytes).ToString());
        Assert.Equal(outcome.ToolCalls.Sum(c => c.ContentBytes), outcome.Evidence.ToolMessageBytes);
        Assert.True(last!.Bytes <= P7F1V6DirectAgentCanary.MaxRequestBytes, last.Bytes.ToString());
    }

    /// <summary>The bytes of the tool messages actually sent back in the last request, read from that request.</summary>
    static int ToolMessageBytesIn(P7F1V6Protocol.PreparedRequest req) =>
        Body(req).GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool")
            .Sum(m => Encoding.UTF8.GetByteCount(m.GetProperty("content").GetString()!));

    public static TheoryData<int, int> Floods => new() { { 8, 3 }, { 9, 3 }, { 20, 3 }, { 60, 1 }, { 60, 3 } };

    [Theory]
    [MemberData(nameof(Floods))]
    public void Adversarial_ManyCallsPerTurn_NeverPushTheToolMessagesOverTheCap(int callsPerTurn, int toolTurns)
    {
        var pages = U.Value.Lines.Select(l => l.Page).Distinct().ToArray();
        P7F1V6Protocol.PreparedRequest? last = null;
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            last = req;
            return turn <= toolTurns
                ? Call(Enumerable.Range(0, callsPerTurn).Select(k => ((string?)$"f{turn}_{k}", P7F1V6AgentTools.PageLayout,
                    (object)new { page = pages[(turn * 31 + k) % pages.Length], offset = 0, limit = 40 })).ToArray())
                : Answer(Final(Targets(req)));
        });
        var sent = ToolMessageBytesIn(last!);
        Assert.True(sent <= A.MaxToolMessageBytesPerUnit, sent.ToString());
        Assert.True(outcome.Evidence.ToolMessageBytes <= A.MaxToolMessageBytesPerUnit);
        Assert.Equal(outcome.Evidence.ModelToolCallsEmitted, outcome.Evidence.ToolsDispatched + outcome.Evidence.ToolsRejectedBeforeDispatch
            + (outcome.Unit.Failure == "BUDGET:UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED" ? callsPerTurn : 0));
        Assert.True(last!.Bytes <= P7F1V6DirectAgentCanary.MaxRequestBytes);
        // Within the per-turn cap, every turn is answered and the unit completes; only a flood above it may be refused.
        if (callsPerTurn <= A.MaxCallsPerTurn) Assert.Equal("COMPLETE", outcome.Unit.Status);
        else Assert.True(outcome.Unit.Status is "COMPLETE" or "INCOMPLETE", outcome.Unit.Failure);
    }

    [Fact]
    public void ATurnWhoseRejectionsAloneDoNotFit_IsRefusedBeforeAnyDispatch()
    {
        // 200 calls: their rejections alone exceed 64 KB. Nothing runs, nothing is sent back, the unit stops INCOMPLETE.
        var posts = 0;
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            posts++;
            return Call(Enumerable.Range(0, 200).Select(k => ((string?)$"x{k}", P7F1V6AgentTools.DocumentOverview, (object)new { })).ToArray());
        });
        Assert.Equal("INCOMPLETE", outcome.Unit.Status);
        Assert.Equal("BUDGET:UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED", outcome.Unit.Failure);
        Assert.Empty(outcome.ToolCalls);
        Assert.Equal(1, posts);
        Assert.Equal(200, outcome.Evidence.ModelToolCallsEmitted);
        Assert.Equal(0, outcome.Evidence.ToolsDispatched);
        Assert.Equal(0, outcome.Evidence.ToolMessageBytes);
    }

    [Fact]
    public void AnUnregisteredToolName_IsNeverEchoed_SoItCannotInflateAMessage()
    {
        var huge = new string('n', 50_000);
        var tools = NewTools();
        var r = tools.Execute("E1.01", huge, "{}");
        Assert.Equal("UNKNOWN_TOOL", r.Status);
        Assert.True(r.Content.Length < 1024, r.Content.Length.ToString());
        Assert.Equal("UNREGISTERED", tools.Ledger.Single().Tool);
        Assert.Equal(tools.RejectionBytes("E1.01", P7F1V6AgentTools.Names[0]) - P7F1V6AgentTools.Names[0].Length + "null".Length - 2,
            tools.RejectionBytes("E1.01", huge));
        var rejected = tools.Reject("E1.02", huge, "{}", "PER_TURN_CALL_CAP_EXCEEDED");
        Assert.DoesNotContain(huge, Encoding.UTF8.GetString(rejected.Content));
        Assert.Throws<ArgumentException>(() => tools.Reject("E1.03", "x", "{}", "MADE_UP"));
    }

    [Fact]
    public void NeedsEvidence_IsRefusedInAgentMode()
    {
        var needs = JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectTwoStage.SelectObjectsVersion, status = "NEEDS_EVIDENCE",
            retrieve = new[] { new { kind = "SEARCH", text = "HTTP" } } });
        Assert.Equal("PROTOCOL:NEEDS_EVIDENCE_NOT_IN_AGENT_MODE", Run(A.Arm.Auto, (u, t, r) => Answer(needs)).Unit.Failure);
    }

    [Fact]
    public void TheBudgetAdmitsEveryPost_AndARefusalLeavesTheUnitIncomplete()
    {
        var outcome = Run(A.Arm.Auto, (u, t, r) => Call(("c" + t, P7F1V6AgentTools.DocumentOverview, new { })), new P7F1V6DirectV2.Budget(1, 100, 2_000_000));
        Assert.Equal("INCOMPLETE", outcome.Unit.Status);
        Assert.Equal("BUDGET:CALL_LIMIT", outcome.Unit.Failure);
        Assert.Equal(1, outcome.Unit.Calls);
    }

    [Fact]
    public void FirstRequest_CarriesNoVersionField_TheTools_AndTheSameLockedEnvelopeAsArmB()
    {
        var agent = P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Auto, U.Value, "SEG-001");
        var b = P7F1V6DirectCanary410.FirstRequest(P7F1V6DirectTwoStage.SelectFormat.Objects, U.Value, "SEG-001");
        var ab = Body(agent); var bb = Body(b);
        var payload = ab.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("protocolVersion", payload);
        Assert.DoesNotContain("responseVersion", payload);
        Assert.DoesNotContain("retrievalAllowed", payload);
        Assert.Equal(A.AutoPrompt, ab.GetProperty("messages")[0].GetProperty("content").GetString());
        foreach (var field in new[] { "model", "temperature", "max_tokens", "provider", "usage" })
            Assert.Equal(bb.GetProperty(field).GetRawText(), ab.GetProperty(field).GetRawText());
        // D4.11g: arm B forces JSON with response_format; the agent's tool turns do not.
        Assert.True(bb.TryGetProperty("response_format", out _));
        Assert.False(ab.TryGetProperty("response_format", out _));
        Assert.Equal(P7F1V6AgentTools.Definitions().ToJsonString(), JsonNode.Parse(ab.GetProperty("tools").GetRawText())!.ToJsonString());
        // D4.11c: the F1Q request shape under which Qwen made tool calls on this route.
        Assert.Equal("{\"enabled\":true}", ab.GetProperty("reasoning").GetRawText());
        Assert.True(ab.GetProperty("stream").GetBoolean());
        Assert.False(bb.TryGetProperty("stream", out _));
        // Same targets and context as arm B.
        using var bp = JsonDocument.Parse(bb.GetProperty("messages")[1].GetProperty("content").GetString()!);
        using var ap = JsonDocument.Parse(payload);
        Assert.Equal(bp.RootElement.GetProperty("targets").GetRawText(), ap.RootElement.GetProperty("targets").GetRawText());
        Assert.Equal(bp.RootElement.GetProperty("context").GetRawText(), ap.RootElement.GetProperty("context").GetRawText());
    }

    // ================= response reading and audit =================

    /// <summary>An OpenRouter-style SSE body: role and reasoning, content in two chunks, each tool call announced with id and name and its
    /// arguments split over two chunks, a finish chunk, a usage chunk, then [DONE]. done=false or error simulate a broken stream.</summary>
    static string Sse(string requestBody, string? content, (string Id, string Name, string Args)[] calls, string finish, bool done = true, bool error = false)
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        var head = new { id = "gen-mock", provider = "Alibaba", model = "qwen/qwen3.7-flash" };
        var sb = new StringBuilder();
        void Data(object o) => sb.Append("data: ").Append(JsonSerializer.Serialize(o)).Append("\n\n");
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { role = "assistant", reasoning = "thinking about it" } } } });
        if (content is not null)
            foreach (var part in new[] { content[..(content.Length / 2)], content[(content.Length / 2)..] })
                Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { content = part } } } });
        for (int i = 0; i < calls.Length; i++)
        {
            var (id, name, args) = calls[i];
            Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = i, id, type = "function", function = new { name, arguments = "" } } } } } } });
            foreach (var part in new[] { args[..(args.Length / 2)], args[(args.Length / 2)..] })
                Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { tool_calls = new[] { new { index = i, function = new { arguments = part } } } } } } });
        }
        if (error) Data(new { head.id, error = new { code = 502, message = "upstream" } });
        Data(new { head.id, head.provider, head.model, choices = new[] { new { index = 0, delta = (object)new { }, finish_reason = finish, native_finish_reason = finish } } });
        Data(new { head.id, head.provider, head.model, choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300),
                completion_tokens_details = new { reasoning_tokens = 200 }, prompt_tokens_details = new { cached_tokens = 0, cache_write_tokens = 0 } } });
        if (done) sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    static string ChatToolCalls(string requestBody, params (string Id, string Name, string Args)[] calls)
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        return JsonSerializer.Serialize(new
        {
            id = "gen-mock", model = "qwen/qwen3.7-flash", provider = "Alibaba",
            choices = new[] { new { index = 0, finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                tool_calls = calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Args } }).ToArray() } } },
            usage = new { prompt_tokens = prompt, completion_tokens = 200, cost = P7F1V6Usage.Cost(prompt, 200), completion_tokens_details = new { reasoning_tokens = 150 } },
        });
    }

    static string ChatStop(string requestBody, string content)
    {
        var prompt = Encoding.UTF8.GetByteCount(requestBody) / 4;
        return JsonSerializer.Serialize(new
        {
            id = "gen-mock", model = "qwen/qwen3.7-flash", provider = "Alibaba",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = prompt, completion_tokens = 300, cost = P7F1V6Usage.Cost(prompt, 300), completion_tokens_details = new { reasoning_tokens = 200 } },
        });
    }

    [Fact]
    public void ReadResponse_ReadsToolCallsAsGiven_AndRefusesAToolCallsFinishWithoutCalls()
    {
        var (turn, failure) = A.ReadResponse(ChatToolCalls("{}", ("x1", "get_document_overview", "{}")));
        Assert.Null(failure);
        Assert.Null(turn!.Content);
        Assert.Equal("x1", Assert.Single(turn.ToolCalls).Id);
        Assert.Equal("tool_calls", turn.FinishReason);
        Assert.Equal("FINISH_TOOL_CALLS_WITHOUT_CALLS", A.ReadResponse(ChatToolCalls("{}")).Failure);
        var objArgs = "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"a\",\"function\":{\"name\":\"n\",\"arguments\":{\"k\":1}}}]}}]}";
        Assert.Equal("{\"k\":1}", A.ReadResponse(objArgs).Turn!.ToolCalls[0].Arguments);
    }

    [Fact]
    public void UsageAudit_AcceptsToolCallsFinish_OnlyWhenAllowed()
    {
        var text = ChatToolCalls("{\"x\":\"" + new string('a', 4000) + "\"}", ("x1", "get_document_overview", "{}"));
        Assert.Contains("FINISH_REASON_UNEXPECTED", P7F1V6Usage.Check(text, 4100, P7F1V6Protocol.MaxTokens).Codes);
        Assert.Equal(new[] { "OK" }, P7F1V6Usage.Check(text, 4100, P7F1V6Protocol.MaxTokens, toolCallsAllowed: true).Codes);
    }

    // ================= read-only prompt-cache telemetry (D4.11b) =================

    static P7F1V6Usage.CacheTelemetry Cache(string json)
    {
        using var d = JsonDocument.Parse(json);
        return P7F1V6Usage.ReadCache(d.RootElement);
    }

    [Theory]
    [InlineData("4fea52e68926352bac1b77fc272b2ed54dbc6d1642c1c376ff2168c9272e0444")]
    [InlineData("26922f43d610ebdc80206d5e0ec4e52d1ac524598aa9df8594ca66ab1689ce43")]
    [InlineData("c00b214b80d97da8a7f4c9ddc881cda57ecd7925421c9950c613f166f2bfb7cb")]
    public void RecordedAlibabaResponses_ReportZeroCachedAndZeroWrittenTokens_AndNoDiscount(string rawKey)
    {
        var path = Path.Combine(TestRepository.Root(), "artifacts", "v5-p7-f1-v6-d4-contractfix", "d410-canary", "arm-B", "raw", rawKey + ".json");
        var c = Cache(File.ReadAllText(path));
        Assert.Equal(new P7F1V6Usage.CacheField("ZERO", 0, "usage.prompt_tokens_details.cached_tokens"), c.CachedTokens);
        Assert.Equal(new P7F1V6Usage.CacheField("ZERO", 0, "usage.prompt_tokens_details.cache_write_tokens"), c.CacheWriteTokens);
        Assert.Equal("ABSENT", c.CacheDiscount.Status);
    }

    [Fact]
    public void CacheTelemetry_TellsAbsentZeroPositiveAndNotNumberApart()
    {
        var absent = Cache("{\"usage\":{\"prompt_tokens\":10}}");
        Assert.All(new[] { absent.CachedTokens, absent.CacheWriteTokens, absent.CacheDiscount }, f => Assert.Equal(new P7F1V6Usage.CacheField("ABSENT", null, null), f));
        var positive = Cache("{\"usage\":{\"prompt_tokens_details\":{\"cached_tokens\":8000,\"cache_write_tokens\":0},\"cache_discount\":0.0021}}");
        Assert.Equal(new P7F1V6Usage.CacheField("POSITIVE", 8000, "usage.prompt_tokens_details.cached_tokens"), positive.CachedTokens);
        Assert.Equal("ZERO", positive.CacheWriteTokens.Status);
        Assert.Equal(new P7F1V6Usage.CacheField("POSITIVE", 0.0021, "usage.cache_discount"), positive.CacheDiscount);
        var odd = Cache("{\"usage\":{\"prompt_tokens_details\":{\"cached_tokens\":\"8000\",\"cache_write_tokens\":null}},\"cache_discount\":-0.5}");
        Assert.Equal("NOT_NUMBER", odd.CachedTokens.Status);
        Assert.Equal("ABSENT", odd.CacheWriteTokens.Status);           // null is treated as not reported
        Assert.Equal(new P7F1V6Usage.CacheField("NEGATIVE", -0.5, "cache_discount"), odd.CacheDiscount);
    }

    [Fact]
    public void CacheTelemetry_ChangesNoAuditVerdict_AndNoRequestCarriesACacheDirective()
    {
        var text = ChatStop("{\"x\":\"" + new string('a', 4000) + "\"}", "{}");
        var withCache = text.Replace("\"completion_tokens_details\"", "\"prompt_tokens_details\":{\"cached_tokens\":900},\"completion_tokens_details\"");
        Assert.Equal(P7F1V6Usage.Check(text, 4100, P7F1V6Protocol.MaxTokens).Codes, P7F1V6Usage.Check(withCache, 4100, P7F1V6Protocol.MaxTokens).Codes);
        Assert.Equal("POSITIVE", P7F1V6Usage.Check(withCache, 4100, P7F1V6Protocol.MaxTokens).Cache!.CachedTokens.Status);
        var body = P7F1V6DirectAgentCanary.FirstRequest(A.Arm.Auto, U.Value, "SEG-001").Body;
        Assert.DoesNotContain("cache_control", body);
        Assert.DoesNotContain("session_id", body);
    }

    sealed class Handler(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
        }
    }

    string SafeRoot(string name)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) P7F1V6SafeRawStore.RestrictRootToCurrentUser(root);
        return root;
    }

    static int TurnOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("messages").GetArrayLength() == 2 ? 1 : 2;

    static string[] TargetsOfBody(string body)
    {
        using var d = JsonDocument.Parse(body);
        using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    }

    // ================= streaming (D4.11c) =================

    P7F1V6AgentAuditedModel StreamModel(Func<string, string> respond, out Handler http, out P7F1V6DirectV2.Budget budget, out P7F1V6SafeRawStore store)
    {
        http = new Handler(respond);
        budget = new P7F1V6DirectV2.Budget(100, 100, 2_000_000);
        store = P7F1V6SafeRawStore.CreateRun(SafeRoot("stream-" + Guid.NewGuid().ToString("N")));
        return new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(new P7F1V6OpenRouterTransport(http, TimeSpan.FromSeconds(30)), Key, store, budget));
    }

    [Fact]
    public void Streaming_ToolCallsAreAssembledFromChunks_AnsweredByTheDispatcher_AndTheRawSseIsWhatIsStored()
    {
        var (t, o) = RepeatPairOutsideWindow();
        var args = JsonSerializer.Serialize(new { first = L(t), second = L(o), window = 1 });
        string? secondBody = null;
        var model = StreamModel(body =>
        {
            if (TurnOf(body) == 1) return Sse(body, null, new[] { ("call_s1", P7F1V6AgentTools.Compare, args) }, "tool_calls");
            secondBody = body;
            return Sse(body, Final(TargetsOfBody(body), a => a == L(t) ? ("NOT_SELECT", new[] { L(o) }) : ("NOT_SELECT", null)), Array.Empty<(string, string, string)>(), "stop");
        }, out var http, out _, out var store);
        var outcome = Run(A.Arm.Auto, model.Turn);

        Assert.True(outcome.Unit.Status == "COMPLETE", outcome.Unit.Failure);
        Assert.Equal(2, http.Bodies.Count);
        Assert.All(http.Bodies, b => Assert.Contains("\"stream\":true", b));
        // The arguments split over two chunks reached the handler whole, and the result went back under Qwen's id.
        var call = Assert.Single(outcome.ToolCalls);
        Assert.Equal("OK", call.Status);
        using var sent = JsonDocument.Parse(secondBody!);
        var msgs = sent.RootElement.GetProperty("messages");
        Assert.Equal("call_s1", msgs[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal(args, msgs[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("call_s1", msgs[3].GetProperty("tool_call_id").GetString());
        Assert.Equal(call.ContentSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(msgs[3].GetProperty("content").GetString()!))));
        Assert.Equal(new[] { L(t) }, outcome.Evidence.EvidenceUsed);
        // The store keeps the raw SSE bytes, not the assembled view.
        Assert.All(model.Ledger, l =>
        {
            var raw = File.ReadAllText(Path.Combine(store.RunDirectory, l.RawStoreKey + ".bin"));
            Assert.StartsWith("data: ", raw, StringComparison.Ordinal);
            Assert.EndsWith("data: [DONE]\n\n", raw, StringComparison.Ordinal);
        });
        Assert.Equal(new[] { "tool_calls", "stop" }, model.Ledger.Select(l => l.FinishReason));
    }

    [Theory]
    [InlineData(false, false, "CALL:TRANSPORT:STREAM_INCOMPLETE")]
    [InlineData(true, true, "CALL:TRANSPORT:STREAM_ERROR")]
    public void Streaming_ABrokenStream_FailsTheCall_ButItsRawIsKept(bool done, bool error, string failure)
    {
        var model = StreamModel(body => Sse(body, Final(TargetsOfBody(body)), Array.Empty<(string, string, string)>(), "stop", done, error),
            out _, out var budget, out var store);
        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.Equal(failure, outcome.Unit.Failure);
        var entry = Assert.Single(model.Ledger);
        Assert.True(File.Exists(Path.Combine(store.RunDirectory, entry.RawStoreKey + ".bin")));
        Assert.Equal(0, budget.ContractFailures);   // a broken stream is a transport outcome, not a contract failure
    }

    [Fact]
    public void AssembledView_StatesWhatTheStreamCarried_AndNothingElse()
    {
        var sse = Sse("{}", null, new[] { ("a1", "get_document_overview", "{}"), ("a2", "get_source_span", "{\"aliases\":[\"L0042:S0\"]}") }, "tool_calls");
        using var view = JsonDocument.Parse(P7F1V6DirectAuditedModel.AssembledView(P7F1QSse.Parse(sse)));
        var m = view.RootElement.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal(JsonValueKind.Null, m.GetProperty("content").ValueKind);
        Assert.Equal(new[] { "a1", "a2" }, m.GetProperty("tool_calls").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Equal("{\"aliases\":[\"L0042:S0\"]}", m.GetProperty("tool_calls")[1].GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", view.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal("Alibaba", view.RootElement.GetProperty("provider").GetString());
        Assert.Equal(0, view.RootElement.GetProperty("usage").GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());
    }

    // ================= SSE guard and byte preservation (D4.11d) =================

    static string GoodStream(string body) => Sse(body, Final(TargetsOfBody(body)), Array.Empty<(string, string, string)>(), "stop");

    sealed class ByteHandler(Func<string, (byte[] Bytes, string ContentType)> respond) : HttpMessageHandler
    {
        public List<byte[]> Served { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var (bytes, contentType) = respond(body);
            Served.Add(bytes);
            var content = new ByteArrayContent(bytes);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    P7F1V6AgentAuditedModel BytesModel(Func<string, (byte[], string)> respond, out ByteHandler http, out P7F1V6SafeRawStore store)
    {
        http = new ByteHandler(respond);
        store = P7F1V6SafeRawStore.CreateRun(SafeRoot("bytes-" + Guid.NewGuid().ToString("N")));
        return new P7F1V6AgentAuditedModel(new P7F1V6DirectAuditedModel(new P7F1V6OpenRouterTransport(http, TimeSpan.FromSeconds(30)), Key,
            store, new P7F1V6DirectV2.Budget(100, 100, 2_000_000)));
    }

    [Theory]
    [InlineData("event: error\ndata: {\"message\":\"upstream failure\"}\n\n", "STREAM_ERROR_EVENT")]
    [InlineData("event:error\ndata: {\"message\":\"upstream failure\"}\n\n", "STREAM_ERROR_EVENT")]
    [InlineData("event: error  \r\ndata: {\"choices\":[]}\r\n\r\n", "STREAM_ERROR_EVENT")]
    [InlineData("event: ERROR\ndata: {\"choices\":[]}\n\n", "STREAM_ERROR_EVENT")]
    [InlineData("event: ping\ndata: {\"choices\":[]}\n\n", "STREAM_NAMED_EVENT")]
    [InlineData("event\ndata: {\"choices\":[]}\n\n", "STREAM_NAMED_EVENT")]
    [InlineData("id: 7\ndata: {\"choices\":[]}\n\n", "STREAM_FIELD_UNKNOWN")]
    [InlineData("retry: 10\ndata: {\"choices\":[]}\n\n", "STREAM_FIELD_UNKNOWN")]
    [InlineData("Data: {\"choices\":[]}\n\n", "STREAM_FIELD_UNKNOWN")]
    [InlineData("data: {\"message\":\"upstream failure\"}\n\n", "STREAM_EVENT_SHAPE_UNKNOWN")]
    [InlineData("data: [1,2]\n\n", "STREAM_EVENT_SHAPE_UNKNOWN")]
    [InlineData("data: \"text\"\n\n", "STREAM_EVENT_SHAPE_UNKNOWN")]
    [InlineData("data: {\"choices\":[]}\n\ndata: [DONE]\n\ndata: {\"choices\":[]}\n\n", "STREAM_DATA_AFTER_DONE")]
    public void SseGuard_RefusesWhatTheFqParserWouldLetPass(string raw, string code) => Assert.Equal(code, P7F1V6SseGuard.Check(raw));

    [Fact]
    public void SseGuard_AllowsTheShapesRealStreamsHave()
    {
        // Keep-alive comments, blank lines, CRLF, usage-only events with empty choices, error payloads (reported by the assembly), malformed JSON
        // (reported by the assembly), and a body without [DONE] (reported by the assembly as STREAM_INCOMPLETE).
        var ok = ": OPENROUTER PROCESSING\n\ndata: {\"choices\":[{\"delta\":{}}]}\n\r\ndata: {\"choices\":[],\"usage\":{}}\n\ndata: [DONE]\n\n";
        Assert.Null(P7F1V6SseGuard.Check(ok));
        Assert.Null(P7F1V6SseGuard.Check(ok.Replace("\n", "\r\n")));
        Assert.Null(P7F1V6SseGuard.Check("data: {\"error\":{\"code\":502}}\n\n"));
        Assert.Null(P7F1V6SseGuard.Check("data: {not json\n\n"));
        Assert.Null(P7F1V6SseGuard.Check("data: {\"choices\":[]}\n\n"));
        Assert.Null(P7F1V6SseGuard.Check(Sse("{}", "{\"status\":\"COMPLETE\"}", Array.Empty<(string, string, string)>(), "stop")));
    }

    [Fact]
    public void ErrorEvent_WithoutAnErrorKey_IsRefused_EvenThoughTheRestOfTheStreamIsAValidAnswer()
    {
        string? sent = null;
        var model = StreamModel(body =>
        {
            sent = body;
            // Everything an answer needs, plus an error event whose payload has no "error" key.
            return "event: error\ndata: {\"message\":\"upstream failure\"}\n\n" + GoodStream(body);
        }, out _, out var budget, out var store);

        // The historical F1Q parser alone accepts this stream: no error, [DONE] present, the answer assembled.
        var raw = "event: error\ndata: {\"message\":\"upstream failure\"}\n\n" + Sse("{}", "{\"status\":\"COMPLETE\"}", Array.Empty<(string, string, string)>(), "stop");
        var asm = P7F1QSse.Parse(raw);
        Assert.Null(asm.Error);
        Assert.True(asm.Done);

        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.NotNull(sent);
        Assert.Equal("FAILED", outcome.Unit.Status);
        Assert.Equal("CALL:TRANSPORT:STREAM_ERROR_EVENT", outcome.Unit.Failure);
        Assert.All(outcome.Unit.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
        var entry = Assert.Single(model.Ledger);
        Assert.Equal("STREAM_ERROR_EVENT", entry.Verdict);
        Assert.True(File.Exists(Path.Combine(store.RunDirectory, entry.RawStoreKey + ".bin")));   // the evidence is kept
        Assert.Equal(0, budget.ContractFailures);
    }

    [Fact]
    public void StoredResponse_IsTheBytesReceived_NotADecodedStringEncodedAgain()
    {
        // Non-ASCII text, CRLF line endings and a charset header that names another encoding: the old read-as-string path decoded by the header.
        var vi = "Tiêu đề: Kết nối HTTP/3 — “đóng”";
        string? bodyText = null;
        var model = BytesModel(body =>
        {
            bodyText = body;
            var sse = GoodStream(body).Replace("thinking about it", vi).Replace("\n", "\r\n");
            return (Encoding.UTF8.GetBytes(sse), "text/event-stream; charset=iso-8859-1");
        }, out var http, out var store);
        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.True(outcome.Unit.Status == "COMPLETE", outcome.Unit.Failure);
        var entry = Assert.Single(model.Ledger);
        var stored = File.ReadAllBytes(Path.Combine(store.RunDirectory, entry.RawStoreKey + ".bin"));
        Assert.Equal(http.Served[0], stored);                                              // byte for byte
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(http.Served[0])), entry.ResponseSha256);
        Assert.Contains(vi, Encoding.UTF8.GetString(stored));
        Assert.NotNull(bodyText);
    }

    [Fact]
    public void ABomOnTheStream_IsKeptAsReceived_AndTheStreamIsRefused()
    {
        var model = BytesModel(body => (Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(GoodStream(body))).ToArray(), "text/event-stream"),
            out var http, out var store);
        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.Equal("CALL:TRANSPORT:STREAM_FIELD_UNKNOWN", outcome.Unit.Failure);          // the first line no longer starts with "data"
        var entry = Assert.Single(model.Ledger);
        Assert.Equal(http.Served[0], File.ReadAllBytes(Path.Combine(store.RunDirectory, entry.RawStoreKey + ".bin")));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, http.Served[0][..3]);
    }

    [Fact]
    public void InvalidUtf8_IsNeverRepaired_ItFailsTheCallAndTheBytesAreKept()
    {
        var model = BytesModel(body =>
        {
            var good = Encoding.UTF8.GetBytes(GoodStream(body));
            var bad = good.Take(good.Length / 2).Concat(new byte[] { 0xFF, 0xFE, 0xC3 }).Concat(good.Skip(good.Length / 2)).ToArray();
            return (bad, "text/event-stream");
        }, out var http, out var store);
        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.Equal("FAILED", outcome.Unit.Status);
        Assert.Equal("CALL:TRANSPORT:RESPONSE_NOT_UTF8", outcome.Unit.Failure);
        var entry = Assert.Single(model.Ledger);
        Assert.Equal("TRANSPORT_FAILED", entry.Verdict);
        Assert.Equal(http.Served[0], File.ReadAllBytes(Path.Combine(store.RunDirectory, entry.RawStoreKey + ".bin")));   // 0xFF 0xFE 0xC3 still there
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(http.Served[0])), entry.ResponseSha256);
    }

    [Fact]
    public void NonStreamingBodies_AreStoredAsReceivedToo()
    {
        var model = BytesModel(body => (Encoding.UTF8.GetBytes(ChatStop(body, Final(TargetsOfBody(body))) + "\r\n"), "application/json"),
            out var http, out var store);
        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.True(outcome.Unit.Status == "COMPLETE" || outcome.Unit.Failure is not null);
        var entry = Assert.Single(model.Ledger);
        Assert.Equal(http.Served[0], File.ReadAllBytes(Path.Combine(store.RunDirectory, entry.RawStoreKey + ".bin")));
    }

    // ================= response_format only on the forced-answer turn (D4.11g) =================

    [Fact]
    public void ResponseFormat_IsSentOnlyOnTheForcedAnswerTurn_AndNothingElseDiffersBetweenTheTurns()
    {
        var history = new JsonArray();
        var auto = JsonNode.Parse(A.Build("S", "{\"p\":1}", history, "auto").Body)!.AsObject();
        var none = JsonNode.Parse(A.Build("S", "{\"p\":1}", history, "none").Body)!.AsObject();
        Assert.Null(auto["response_format"]);
        Assert.Equal("{\"type\":\"json_object\"}", none["response_format"]!.ToJsonString());
        Assert.Equal("auto", auto["tool_choice"]!.GetValue<string>());
        Assert.Equal("none", none["tool_choice"]!.GetValue<string>());
        Assert.NotNull(auto["tools"]);                        // the tools stay declared on both
        Assert.NotNull(none["tools"]);
        foreach (var n in new[] { auto, none }) { n.Remove("response_format"); n.Remove("tool_choice"); }
        Assert.Equal(auto.ToJsonString(), none.ToJsonString());                        // otherwise the same request
        // Position of the key is stable: after messages, before temperature.
        var keys = JsonNode.Parse(A.Build("S", "{}", history, "none").Body)!.AsObject().Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { "model", "messages", "response_format", "temperature" }, keys.Take(4));
    }

    [Fact]
    public void EveryRequestOfAUnit_HasResponseFormatOnlyOnTheFourthTurn()
    {
        var seen = new List<(int Turn, bool HasFormat, string Choice)>();
        var outcome = Run(A.Arm.Auto, (unit, turn, req) =>
        {
            var b = Body(req);
            seen.Add((turn, b.TryGetProperty("response_format", out _), b.GetProperty("tool_choice").GetString()!));
            return turn < 4 ? Call(("c" + turn, P7F1V6AgentTools.DocumentOverview, new { })) : Answer(Final(Targets(req)));
        });
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Equal(new[] { (1, false, "auto"), (2, false, "auto"), (3, false, "auto"), (4, true, "none") }, seen);
    }

    public static TheoryData<string> NotExactlyOneJsonObject => new()
    {
        { "```json\n{\"protocolVersion\":\"P7_F1_V6_SELECT_OBJECTS_V1\",\"status\":\"COMPLETE\",\"decisions\":[]}\n```" },
        { "Here is my answer:\n{\"protocolVersion\":\"P7_F1_V6_SELECT_OBJECTS_V1\",\"status\":\"COMPLETE\",\"decisions\":[]}" },
        { "{\"protocolVersion\":\"P7_F1_V6_SELECT_OBJECTS_V1\",\"status\":\"COMPLETE\",\"decisions\":[]}\nDone." },
        { "{\"protocolVersion\":\"P7_F1_V6_SELECT_OBJECTS_V1\",\"status\":\"COMPLETE\",\"decisions\":[]} {\"x\":1}" },
        { "" },
        { "   " },
        { "no tool needed, all evidence is in the prompt" },
    };

    [Theory]
    [MemberData(nameof(NotExactlyOneJsonObject))]
    public void WithoutResponseFormat_AFinalAnswerThatIsNotExactlyOneJsonObject_IsRefused_NeverRepaired(string content)
    {
        // The answer comes on turn 1, where the provider no longer forces JSON.
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => Answer(content));
        Assert.Equal("FAILED", outcome.Unit.Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", outcome.Unit.Failure);
        Assert.All(outcome.Unit.Decisions.Values, v => Assert.Equal("UNRESOLVED", v));
    }

    [Fact]
    public void AFencedAnswerAfterToolTurns_IsRefusedToo_AndCountsAsAContractFailure()
    {
        var budget = new P7F1V6DirectV2.Budget(100, 100, 2_000_000);
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => turn == 1
            ? Call(("c1", P7F1V6AgentTools.DocumentOverview, new { }))
            : Answer("```json\n" + Final(Targets(req)) + "\n```"), budget);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", outcome.Unit.Failure);
        Assert.Equal(1, outcome.Evidence.ToolSucceeded);        // the tool turn was fine; only the answer was refused
        Assert.Equal(1, budget.ContractFailures);
    }

    [Fact]
    public void AToolTurnMayCarryTextNextToItsCalls_ItIsNotAnAnswerAndIsNotParsed()
    {
        // Without response_format the model may write words beside its tool calls; the loop must not read them as the answer.
        var outcome = Run(A.Arm.Auto, (unit, turn, req) => turn == 1
            ? new A.ModelTurn("Let me check the neighbouring lines first.", new[] { new A.ToolCall("c1", P7F1V6AgentTools.DocumentOverview, "{}") }, "tool_calls")
            : Answer(Final(Targets(req))));
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.Equal(1, outcome.Evidence.ToolSucceeded);
    }

    [Fact]
    public void Streaming_AnUnforcedAnswerThatIsExactlyJson_IsAccepted_AndThroughTheAuditedSenderToo()
    {
        var model = StreamModel(body => Sse(body, Final(TargetsOfBody(body)), Array.Empty<(string, string, string)>(), "stop"), out var http, out _, out _);
        var outcome = Run(A.Arm.Auto, model.Turn);
        Assert.Equal("COMPLETE", outcome.Unit.Status);
        Assert.DoesNotContain("response_format", http.Bodies[0]);
        var fenced = StreamModel(body => Sse(body, "```json\n" + Final(TargetsOfBody(body)) + "\n```", Array.Empty<(string, string, string)>(), "stop"), out _, out var budget, out _);
        var refused = Run(A.Arm.Auto, fenced.Turn, budget);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON", refused.Unit.Failure);
        Assert.Equal(1, budget.ContractFailures);
    }

    // ================= plan and execute =================

    static readonly Lazy<byte[]> PlanBytes = new(() => P7F1V6DirectAgentCanary.Serialize(P7F1V6DirectAgentCanary.PinnedValues(TestRepository.Root(), "TEST_COMMIT")));

    [Fact]
    public void Plan_PinsTwoArmsTwoSegments_SixteenPosts_FiftyCents_AndTheToolRegistry()
    {
        using var doc = JsonDocument.Parse(PlanBytes.Value);
        var p = doc.RootElement;
        Assert.Equal(P7F1V6DirectAgentCanary.PlanArtifact, p.GetProperty("artifact").GetString());
        Assert.False(p.GetProperty("approvalToSend").GetBoolean());
        Assert.Equal(P7F1V6DirectAgentCanary.PreviousCampaignPlanSha256, p.GetProperty("previousCampaign").GetProperty("planSha256").GetString());
        Assert.Equal(P7F1V6DirectAgentCanary.ComparedCampaignPlanSha256, p.GetProperty("comparedCampaign").GetProperty("planSha256").GetString());
        Assert.Contains("absent on turns 1 to 3", p.GetProperty("responseFormat").GetString());
        Assert.Contains("tool_choice auto", p.GetProperty("singleFactorTest").GetProperty("change").GetString());
        Assert.Contains("refused (REPLY_NOT_JSON)", p.GetProperty("singleFactorTest").GetProperty("parser").GetString());
        Assert.StartsWith("enabled true", p.GetProperty("reasoning").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("true", p.GetProperty("stream").GetString(), StringComparison.Ordinal);
        Assert.Equal(new[] { "e3f8e57eb43275db5166a27eb930afe85fa002f03b9e6076213063e00367ebcf", "51daf9412732f9685b0d3ea36a7637a0e4d2b561ee7044d857c662c7fc2e6cfa",
            "9a6bcbd1aecd961a25e4a0c0c52359135750ab2f0ab4bc4642a9854634729697",
            "281ad5d0b99ce308e389121cf0c92b53521351b8858c1d0b34354f585610fadd", "842d7fb042982965d3afcb3ac8b277655f0eb2029c15049a1d2bddd355b11531" },
            p.GetProperty("supersedes").EnumerateArray().Select(x => x.GetProperty("planSha256").GetString()));
        Assert.All(p.GetProperty("supersedes").EnumerateArray(), x => Assert.False(x.GetProperty("run").GetBoolean()));
        Assert.Contains("rejected and invalid ones included", p.GetProperty("toolMessageBytes").GetString());
        Assert.Equal(A.MaxToolMessageBytesPerUnit, p.GetProperty("limits").GetProperty("MaxToolMessageBytesPerUnit").GetInt32());
        Assert.Contains("never makes up an id", p.GetProperty("toolCallIds").GetString());
        Assert.Equal(new[] { "SEG-001", "SEG-008" }, p.GetProperty("segments").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(16, p.GetProperty("budget").GetProperty("maxCalls").GetInt32());
        Assert.Equal(0.50, p.GetProperty("budget").GetProperty("ceilingUsd").GetDouble(), 9);
        Assert.Equal(0, p.GetProperty("retries").GetInt32());
        Assert.False(p.GetProperty("versionFieldsInPayload").GetBoolean());
        Assert.Equal(A.ToolDefinitionsSha256, p.GetProperty("tools").GetProperty("definitionsSha256").GetString());
        var arms = p.GetProperty("arms").EnumerateArray().ToList();
        Assert.Equal(new[] { "AUTO", "EVIDENCE_FIRST" }, arms.Select(a => a.GetProperty("arm").GetString()));
        Assert.All(arms, a =>
        {
            var b = a.GetProperty("budget");
            Assert.Equal(8, b.GetProperty("maxCalls").GetInt32());
            Assert.True(b.GetProperty("worstCaseReserveUsd").GetDouble() <= b.GetProperty("ceilingUsd").GetDouble());
            Assert.Equal(2, a.GetProperty("firstRequests").GetArrayLength());
        });
        Assert.Equal(A.AutoPromptSha256, arms[0].GetProperty("promptSha256").GetString());
        Assert.Equal(A.EvidenceFirstPromptSha256, arms[1].GetProperty("promptSha256").GetString());
    }

    [Fact]
    public void Plan_Verify_RefusesAnotherSha_AnotherCommit_AndADriftedValue()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, PlanBytes.Value);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PlanBytes.Value));
        Assert.Empty(P7F1V6DirectAgentCanary.Verify(TestRepository.Root(), plan, sha, "TEST_COMMIT"));
        Assert.Contains("PLAN_SHA_NOT_CONFIRMED", P7F1V6DirectAgentCanary.Verify(TestRepository.Root(), plan, new string('0', 64), "TEST_COMMIT"));
        Assert.Contains("CODE_COMMIT_DIFFERS", P7F1V6DirectAgentCanary.Verify(TestRepository.Root(), plan, sha, "OTHER"));
        var edited = JsonNode.Parse(PlanBytes.Value)!.AsObject();
        edited["toolChoice"] = "required";
        var drift = Path.Combine(scratch, "drift.json");
        var bytes = Encoding.UTF8.GetBytes(edited.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        File.WriteAllBytes(drift, bytes);
        Assert.Contains("PINNED_VALUES_DIFFER", P7F1V6DirectAgentCanary.Verify(TestRepository.Root(), drift,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), "TEST_COMMIT"));
    }

    [Fact]
    public void Execute_SendsNothingWithoutAuthorization_ThenRunsTheToolChainThroughTheAuditedModel_Once()
    {
        var plan = Path.Combine(scratch, "plan.json");
        File.WriteAllBytes(plan, PlanBytes.Value);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PlanBytes.Value));
        var root = SafeRoot("safe");
        // Streaming (D4.11c). Turn 1: one tool call; turn 2: an answer.
        var http = new Handler(body => TurnOf(body) == 1
            ? Sse(body, null, new[] { ("t1", P7F1V6AgentTools.DocumentOverview, "{}") }, "tool_calls")
            : Sse(body, Final(TargetsOfBody(body)), Array.Empty<(string, string, string)>(), "stop"));

        Assert.StartsWith("AUTHORIZATION_REFUSED:", Assert.Throws<InvalidOperationException>(() =>
            P7F1V6DirectAgentCanary.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", Path.Combine(scratch, "none.json"), Key, http, root)).Message,
            StringComparison.Ordinal);
        Assert.Empty(http.Bodies);

        var auth = Path.Combine(scratch, "auth.json");
        File.WriteAllBytes(auth, P7F1V6DirectAuthorization.Approve(P7F1V6DirectAuthorization.Draft(plan, root), "TEST_USER", "TEST approval",
            "TEST: allow", P7F1V6RoutePolicy.RouteAllowed(P7F1V6Protocol.ProviderTag).Blocking, DateTime.UtcNow));
        var arms = P7F1V6DirectAgentCanary.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root);

        Assert.Equal(8, http.Bodies.Count);   // 2 arms x 2 segments x 2 turns
        Assert.All(arms, a =>
        {
            Assert.All(a.Units, x => Assert.Equal("COMPLETE", x.Unit.Status));
            Assert.All(a.Units, x => Assert.Equal(1, x.SuccessfulToolCalls));
            Assert.Equal(new[] { "TOOL_CALLS_TO_DISPATCHER", "PASSED_AUDIT_TO_PARSER", "TOOL_CALLS_TO_DISPATCHER", "PASSED_AUDIT_TO_PARSER" }, a.Ledger.Select(l => l.Verdict));
            Assert.Equal(new[] { "tool_calls", "stop", "tool_calls", "stop" }, a.Ledger.Select(l => l.FinishReason));
            Assert.All(a.Ledger, l => Assert.Equal("ZERO", l.Cache!.CachedTokens.Status));     // read from the SSE usage chunk
        });
        Assert.NotEqual(arms[0].SafeRunDirectory, arms[1].SafeRunDirectory);
        Assert.DoesNotContain(http.Bodies, b => b.Contains(Key, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => P7F1V6DirectAgentCanary.Execute(TestRepository.Root(), plan, sha, "TEST_COMMIT", auth, Key, http, root));
        Assert.Equal(8, http.Bodies.Count);
    }
}
