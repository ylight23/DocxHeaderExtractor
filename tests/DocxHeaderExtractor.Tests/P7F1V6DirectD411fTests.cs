using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.V5Qualification.P7;
using A = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D4.11f (offline): prompt V3 (heading extraction as the user defines it), the per-unit tool-result cache, the frozen
/// call/provider/document of the plan, and the raw publication step. Scripted models and temp directories only. No network.</summary>
public sealed class P7F1V6DirectD411fTests : IDisposable
{
    readonly string scratch = Path.Combine(Path.GetTempPath(), "dhx-d411f-" + Guid.NewGuid().ToString("N"));

    public P7F1V6DirectD411fTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly Lazy<P7F1V6Universe> U = new(() =>
        P7F1V6Universe.Rebuild(TestRepository.Root(), P7F1V6DirectD45.Document, P7F1V6DirectD45.ReadGoldPin(TestRepository.Root()).SourceSha256, out var f)
        ?? throw new InvalidOperationException(f));

    static P7F1V6AgentTools NewTools() => new(U.Value.Tools);
    static string L(int i) => U.Value.Lines[i].Alias;

    static JsonElement Json(P7F1V6AgentTools.Result r)
    {
        using var d = JsonDocument.Parse(r.Content);
        return d.RootElement.Clone();
    }

    // ================= prompt V3 =================

    [Fact]
    public void Prompt_StatesTheTaskAsHeadingExtractionByTheUsersDefinition_AndDefinesNothing()
    {
        foreach (var arm in new[] { A.Arm.Auto, A.Arm.EvidenceFirst })
        {
            var p = A.Prompt(arm);
            Assert.Contains("Your task is to extract headings from one document, as the user defines them", p);
            Assert.Contains("it alone defines what counts as a heading for this request and which lines to select", p);
            Assert.Contains("Apply it as written", p);
            Assert.Contains("do not assume a fixed meaning for heading or any other label", p);
            Assert.Contains("do not add or drop a requirement the instruction does not state", p);
            Assert.Contains("whether each target is a heading, or a line of a heading, that the instruction asks for", p);
            Assert.Contains("grouping is a later step", p);
            Assert.Contains("A repeat of an earlier call returns the same result again and says so", p);
            // Tools stay the way to gather evidence; the prompt defines no heading and names no kind of line.
            Assert.Contains("gather evidence first: reply with tool calls instead of the JSON answer", p);
            Assert.DoesNotMatch(new Regex(@"\b(toc|contents|table of contents|headers?|footers?|page numbers?|dot leaders?|index|indexes|entry|entries|navigation|furniture|metadata|letterhead|titles?|subtitle|caption|chapter|section)\b|mục lục",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), p);
            Assert.DoesNotMatch(new Regex(@"\bheadings? (is|are|means|must|should|usually|typically)\b", RegexOptions.IgnoreCase), p);   // no definition of a heading
        }
        Assert.Equal("P7_F1_V6_SELECT_AGENT_PROMPT_V3", A.PromptVersion);
        Assert.Equal(A.AutoPromptSha256, A.Sha(A.AutoPrompt));
        Assert.Equal(A.EvidenceFirstPromptSha256, A.Sha(A.EvidenceFirstPrompt));
    }

    // ================= tool-result cache =================

    [Fact]
    public void ARepeatedCall_ReturnsTheStoredResult_ByteForByteButForTheIdAndTheNote_AndIsRecorded()
    {
        var tools = NewTools();
        var args = JsonSerializer.Serialize(new { alias = L(60), before = 2, after = 2 });
        var first = tools.Execute("E1.01", P7F1V6AgentTools.SourceContext, args);
        // Same call, keys in another order.
        var again = tools.Execute("E2.03", P7F1V6AgentTools.SourceContext, "{\"after\":2,\"before\":2,\"alias\":\"" + L(60) + "\"}");

        Assert.Equal("OK", again.Status);
        Assert.Equal(first.ReturnedAliases, again.ReturnedAliases);
        var a = JsonNode.Parse(first.Content)!.AsObject();
        var b = JsonNode.Parse(again.Content)!.AsObject();
        Assert.Equal("E2.03", b["evidenceId"]!.GetValue<string>());
        Assert.Equal("E1.01", b["cache"]!["sameResultAs"]!.GetValue<string>());
        Assert.True(b["cache"]!["hit"]!.GetValue<bool>());
        Assert.Null(a["cache"]);                                   // a first call carries no note
        a["evidenceId"] = "X"; b["evidenceId"] = "X"; b.Remove("cache");
        Assert.Equal(a.ToJsonString(), b.ToJsonString());          // nothing else differs
        Assert.Equal(1, tools.CacheHits);
        Assert.Null(tools.Ledger[0].CacheOf);
        Assert.Equal("E1.01", tools.Ledger[1].CacheOf);
        Assert.True(again.Content.Length > first.Content.Length);  // the repeat still counts in full toward the byte cap
    }

    [Fact]
    public void OnlySuccessfulCallsAreCached_AndADifferentCallIsAMiss()
    {
        var tools = NewTools();
        var bad = "{\"alias\":\"nope\",\"before\":1,\"after\":1}";
        Assert.Equal("INVALID_ARGUMENT", tools.Execute("E1.01", P7F1V6AgentTools.SourceContext, bad).Status);
        Assert.Equal("INVALID_ARGUMENT", tools.Execute("E1.02", P7F1V6AgentTools.SourceContext, bad).Status);
        Assert.Equal(0, tools.CacheHits);
        tools.Reject("E1.03", P7F1V6AgentTools.SourceContext, bad, "PER_TURN_CALL_CAP_EXCEEDED");
        var ok1 = tools.Execute("E2.01", P7F1V6AgentTools.SourceContext, JsonSerializer.Serialize(new { alias = L(60), before = 1, after = 1 }));
        var ok2 = tools.Execute("E2.02", P7F1V6AgentTools.SourceContext, JsonSerializer.Serialize(new { alias = L(60), before = 1, after = 2 }));
        var other = tools.Execute("E2.03", P7F1V6AgentTools.Repeated, JsonSerializer.Serialize(new { alias = L(60), maxResults = 3 }));
        Assert.All(new[] { ok1, ok2, other }, r => Assert.Equal("OK", r.Status));
        Assert.Equal(0, tools.CacheHits);
        Assert.All(tools.Ledger, c => Assert.Null(c.CacheOf));
    }

    [Fact]
    public void ACachedCutResult_StaysUnderTheCap_WithItsNote()
    {
        var box = JsonDocument.Parse("{\"left\":72.0,\"right\":540.0,\"bottom\":700.0,\"top\":712.0}").RootElement;
        var font = JsonDocument.Parse("{\"fontSize\":10.0,\"fontName\":\"F1\",\"boldRatio\":0.0,\"italicRatio\":0.0}").RootElement;
        var synthetic = Enumerable.Range(0, 60).Select(i => new F1QSourceLine($"L{i:0000}:S0", i, 1 + i / 30, $"line {i:00} " + new string('x', 280), box, font)).ToArray();
        var tools = new P7F1V6AgentTools(new P7F1QEvidenceTools("s", "u", "e", synthetic, new Dictionary<string, string> { ["O1"] = "L0000:S0" }, 2));
        var args = JsonSerializer.Serialize(new { query = "line", mode = "literal", offset = 0, limit = 50 });
        var first = tools.Execute("E1.01", P7F1V6AgentTools.SearchText, args);
        var again = tools.Execute("E3.08", P7F1V6AgentTools.SearchText, args);
        Assert.True(first.Truncated);
        Assert.True(again.Truncated);
        Assert.True(first.Content.Length <= P7F1V6AgentTools.MaxResultBytes);
        Assert.True(again.Content.Length <= P7F1V6AgentTools.MaxResultBytes, again.Content.Length.ToString());
        Assert.Equal(1, tools.CacheHits);
    }

    [Fact]
    public void TheCacheLivesInOneUnit_AndTheEvidenceReportCountsItsHits()
    {
        var outcomes = new List<A.AgentUnitOutcome>();
        foreach (var segment in new[] { "SEG-001", "SEG-001" })
        {
            var (decide, ctx) = P7F1V6DirectCanary410.Scope(U.Value, segment);
            var same = JsonSerializer.Serialize(new { aliases = new[] { L(48) } });
            outcomes.Add(A.RunUnit(A.Arm.Auto, P7F1V6DirectD45.Instruction, U.Value, segment, decide, ctx, (unit, turn, req) =>
            {
                if (turn <= 2) return new A.ModelTurn(null, new[] { new A.ToolCall($"c{turn}", P7F1V6AgentTools.SourceSpan, same) }, "tool_calls");
                using var d = JsonDocument.Parse(req.Body);
                using var p = JsonDocument.Parse(d.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                var targets = p.RootElement.GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!);
                return new A.ModelTurn(new JsonObject
                {
                    ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
                    ["decisions"] = new JsonArray(targets.Select(a => (JsonNode)new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" }).ToArray()),
                }.ToJsonString(), Array.Empty<A.ToolCall>(), "stop");
            }, new P7F1V6DirectV2.Budget(100, 100, 2_000_000)));
        }
        foreach (var o in outcomes)
        {
            Assert.Equal("COMPLETE", o.Unit.Status);
            Assert.Equal(2, o.Evidence.ToolSucceeded);
            Assert.Equal(1, o.Evidence.ToolCacheHits);               // the second unit does not inherit the first unit's cache
            Assert.Equal("E1.01", o.ToolCalls[1].CacheOf);
        }
    }

    // ================= frozen call, provider, document =================

    static readonly Lazy<byte[]> PlanBytes = new(() => P7F1V6DirectAgentCanary.Serialize(P7F1V6DirectAgentCanary.PinnedValues(TestRepository.Root(), "TEST_COMMIT")));

    [Fact]
    public void Plan_FreezesTheCallTheProviderAndTheDocument_FromTheRealRequests()
    {
        using var doc = JsonDocument.Parse(PlanBytes.Value);
        var f = doc.RootElement.GetProperty("frozen");
        Assert.Equal(P7F1V6DirectD45.Document, f.GetProperty("document").GetString());
        Assert.Equal(U.Value.SourceSha256, f.GetProperty("sourceSha256").GetString());
        Assert.Equal(U.Value.UniverseSha256, f.GetProperty("universeSha256").GetString());
        var provider = f.GetProperty("provider");
        Assert.Equal(new[] { "alibaba" }, provider.GetProperty("only").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "alibaba/us-east-1" }, provider.GetProperty("ignore").EnumerateArray().Select(x => x.GetString()));
        Assert.False(provider.GetProperty("allowFallbacks").GetBoolean());
        // The provider block hash and the template hash come from the bytes that are sent.
        foreach (var arm in P7F1V6DirectAgentCanary.Arms)
            foreach (var seg in P7F1V6DirectAgentCanary.Segments)
            {
                using var body = JsonDocument.Parse(P7F1V6DirectAgentCanary.FirstRequest(arm.Mode, U.Value, seg).Body);
                Assert.Equal(provider.GetProperty("providerBlockSha256").GetString(), P7F1V6Protocol.Sha(body.RootElement.GetProperty("provider").GetRawText()));
            }
        Assert.Equal(A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new JsonArray(), "auto").Sha256, f.GetProperty("requestTemplateSha256").GetString());
        Assert.Equal(A.Build("<<SYSTEM_PROMPT>>", "<<USER_PAYLOAD>>", new JsonArray(), "none").Sha256, f.GetProperty("finalTurnTemplateSha256").GetString());
        Assert.NotEqual(f.GetProperty("requestTemplateSha256").GetString(), f.GetProperty("finalTurnTemplateSha256").GetString());
        Assert.Equal(A.ToolDefinitionsSha256, f.GetProperty("toolDefinitionsSha256").GetString());
        Assert.Equal(A.AutoPromptSha256, f.GetProperty("promptSha256").GetProperty("auto").GetString());
        Assert.Equal(64, f.GetProperty("firstRequestsSha256").GetString()!.Length);
        Assert.Contains("rejected", doc.RootElement.GetProperty("toolResultCache").GetString());
        Assert.Contains("pushed", doc.RootElement.GetProperty("rawPublication").GetString());
        Assert.Equal(P7F1V6DirectAgentCanary.PlanArtifact, doc.RootElement.GetProperty("artifact").GetString());
        Assert.Equal("842d7fb042982965d3afcb3ac8b277655f0eb2029c15049a1d2bddd355b11531",
            doc.RootElement.GetProperty("supersedes").EnumerateArray().Last().GetProperty("planSha256").GetString());
    }

    [Theory]
    [InlineData("document")]
    [InlineData("sourceSha256")]
    [InlineData("providerOnly")]
    [InlineData("providerBlockSha256")]
    [InlineData("requestTemplateSha256")]
    public void Plan_RefusesTheRun_WhenAFrozenValueIsEdited(string field)
    {
        var plan = Path.Combine(scratch, "plan-" + field + ".json");
        var node = JsonNode.Parse(PlanBytes.Value)!.AsObject();
        var frozen = node["frozen"]!.AsObject();
        switch (field)
        {
            case "document": frozen["document"] = "todo10_8/heading_corpus_100/07_system_generated/other.pdf"; break;
            case "sourceSha256": frozen["sourceSha256"] = new string('0', 64); break;
            case "providerOnly": frozen["provider"]!["only"] = new JsonArray("other-provider"); break;
            case "providerBlockSha256": frozen["provider"]!["providerBlockSha256"] = new string('0', 64); break;
            default: frozen["requestTemplateSha256"] = new string('0', 64); break;
        }
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        File.WriteAllBytes(plan, bytes);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Contains("PINNED_VALUES_DIFFER", P7F1V6DirectAgentCanary.Verify(TestRepository.Root(), plan, sha, "TEST_COMMIT"));
    }

    // ================= publication =================

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>A fake safe root: a consumed marker, and two arm runs each with a stored SSE response, a request and a summary.</summary>
    (string Plan, string PlanSha, string Root, string[] Runs) MakeRuns(string? summaryPlanSha = null, Func<string, string>? sseText = null)
    {
        var plan = Path.Combine(scratch, "plan.json");
        var planBytes = Encoding.UTF8.GetBytes("{\"plan\":\"x\"}\n");
        File.WriteAllBytes(plan, planBytes);
        var planSha = Sha(planBytes);
        var root = Path.Combine(scratch, "safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "consumed-" + planSha + ".json"), "{}");
        var runs = new List<string>();
        foreach (var arm in new[] { "AUTO", "EVIDENCE_FIRST" })
        {
            var run = Path.Combine(root, "run-" + arm);
            Directory.CreateDirectory(Path.Combine(run, "requests"));
            var sse = Encoding.UTF8.GetBytes((sseText ?? (a => ": OPENROUTER PROCESSING\n\ndata: {\"choices\":[]}\n\ndata: [DONE]\n\n " + a))(arm));
            File.WriteAllBytes(Path.Combine(run, Sha(sse) + ".bin"), sse);
            File.WriteAllText(Path.Combine(run, "requests", "SEG-001-turn-1.abc.json"), "{\"model\":\"m\"}");
            File.WriteAllText(Path.Combine(run, "run-summary.json"), JsonSerializer.Serialize(new { Arm = arm, planSha256 = summaryPlanSha ?? planSha }));
            runs.Add(run);
        }
        return (plan, planSha, root, runs.ToArray());
    }

    [Fact]
    public void Publish_CopiesTheRunsUnchanged_AndWritesAManifestWithHashes()
    {
        var (plan, planSha, _, runs) = MakeRuns(sseText: arm => "data: {\"choices\":[{\"delta\":{\"content\":\"Tiêu đề — “đóng”\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n" + arm);
        var outDir = Path.Combine(scratch, "out");
        var r = P7F1V6Publication.Publish(plan, outDir, runs, "sk-or-REAL-KEY-NOT-IN-ANY-FILE-000000");
        Assert.Null(r.Refusal);
        Assert.Equal(6, r.Files.Count);                                              // per arm: summary, one raw SSE, one request
        Assert.Equal(2, r.Files.Count(f => f.Kind == "raw-sse"));
        foreach (var arm in new[] { "AUTO", "EVIDENCE_FIRST" })
        {
            var stored = Directory.GetFiles(Path.Combine(runs[arm == "AUTO" ? 0 : 1]), "*.bin").Single();
            var copied = Directory.GetFiles(Path.Combine(outDir, "arm-" + arm, "raw"), "*.sse").Single();
            Assert.Equal(File.ReadAllBytes(stored), File.ReadAllBytes(copied));         // byte for byte, CRLF and non-ASCII included
            Assert.Equal(Path.GetFileNameWithoutExtension(stored), Path.GetFileNameWithoutExtension(copied));
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outDir, "publication-manifest.json")));
        Assert.Equal(planSha, manifest.RootElement.GetProperty("planSha256").GetString());
        Assert.All(manifest.RootElement.GetProperty("files").EnumerateArray(), f =>
            Assert.Equal(f.GetProperty("sha256").GetString(), Sha(File.ReadAllBytes(Path.Combine(outDir, f.GetProperty("file").GetString()!)))));
    }

    [Fact]
    public void Publish_RefusesWithoutAConsumedMarker_ForAnotherPlan_AndForRunsInTwoRoots()
    {
        var (plan, _, root, runs) = MakeRuns();
        File.Delete(Directory.GetFiles(root, "consumed-*.json").Single());
        Assert.Equal("NO_CONSUMED_MARKER_FOR_PLAN", P7F1V6Publication.Publish(plan, Path.Combine(scratch, "o1"), runs, null).Refusal);

        var (plan2, _, _, runs2) = MakeRuns(summaryPlanSha: new string('a', 64));
        Assert.Equal("RUN_BELONGS_TO_ANOTHER_PLAN", P7F1V6Publication.Publish(plan2, Path.Combine(scratch, "o2"), runs2, null).Refusal);

        var (plan3, _, _, runs3) = MakeRuns();
        var elsewhere = Path.Combine(scratch, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var moved = Path.Combine(elsewhere, "run-EVIDENCE_FIRST");
        Directory.Move(runs3[1], moved);
        Assert.Equal("RUNS_NOT_IN_ONE_SAFE_ROOT", P7F1V6Publication.Publish(plan3, Path.Combine(scratch, "o3"), new[] { runs3[0], moved }, null).Refusal);
        Assert.False(Directory.Exists(Path.Combine(scratch, "o3")));
    }

    [Fact]
    public void Publish_RefusesAStoredResponseWhoseHashIsNotItsName()
    {
        var (plan, _, _, runs) = MakeRuns();
        var bin = Directory.GetFiles(runs[0], "*.bin").Single();
        File.AppendAllText(bin, "tampered");
        Assert.Equal("STORED_RESPONSE_HASH_MISMATCH", P7F1V6Publication.Publish(plan, Path.Combine(scratch, "o"), runs, null).Refusal);
    }

    [Theory]
    [InlineData("the key sk-or-v1-abcdef0123456789abcdef is here", null, "KEY_SHAPED_STRING_IN_FILE")]
    [InlineData("a custom secret OPENROUTER-SECRET-VALUE-42 leaked", "OPENROUTER-SECRET-VALUE-42", "API_KEY_IN_FILE")]
    public void Publish_RefusesTheWholePublication_IfAnyFileHoldsTheKey_AndCopiesNothing(string leak, string? apiKey, string code)
    {
        var (plan, _, _, runs) = MakeRuns(sseText: arm => arm == "EVIDENCE_FIRST" ? "data: {\"choices\":[]}\n\n" + leak + "\n\ndata: [DONE]\n\n" : "data: [DONE]\n\n");
        var outDir = Path.Combine(scratch, "out-key");
        Assert.Equal(code, P7F1V6Publication.Publish(plan, outDir, runs, apiKey).Refusal);
        Assert.False(Directory.Exists(outDir));                                        // the clean arm was not copied either
    }

    [Fact]
    public void Publish_WithGzip_WritesLosslessFiles_AndTheManifestKeepsTheHashOfTheReceivedBytes()
    {
        var (plan, _, _, runs) = MakeRuns(sseText: arm => "data: {\"choices\":[{\"delta\":{\"content\":\"Tiêu đề — “đóng”\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n" + arm + new string('x', 5000));
        var outDir = Path.Combine(scratch, "out-gz");
        var r = P7F1V6Publication.Publish(plan, outDir, runs, "sk-or-REAL-KEY-NOT-IN-ANY-FILE-000000", compressRawSse: true);
        Assert.Null(r.Refusal);
        Assert.Equal(2, r.Files.Count(f => f.Kind == "raw-sse-gz"));
        Assert.Empty(r.Files.Where(f => f.Kind == "raw-sse"));
        for (int i = 0; i < 2; i++)
        {
            var arm = i == 0 ? "AUTO" : "EVIDENCE_FIRST";
            var stored = Directory.GetFiles(runs[i], "*.bin").Single();
            var gz = Directory.GetFiles(Path.Combine(outDir, "arm-" + arm, "raw"), "*.sse.gz").Single();
            using var back = new System.IO.Compression.GZipStream(File.OpenRead(gz), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            back.CopyTo(ms);
            Assert.Equal(File.ReadAllBytes(stored), ms.ToArray());                               // lossless, CRLF and non-ASCII included
            Assert.True(new FileInfo(gz).Length < new FileInfo(stored).Length);
            var entry = r.Files.Single(f => f.File.EndsWith(Path.GetFileName(gz), StringComparison.Ordinal));
            Assert.Equal(Path.GetFileNameWithoutExtension(stored), entry.UncompressedSha256);     // the received bytes' hash is the safe-store name
            Assert.Equal(new FileInfo(stored).Length, entry.UncompressedBytes);
            Assert.Equal(Sha(File.ReadAllBytes(gz)), entry.Sha256);
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outDir, "publication-manifest.json")));
        Assert.Contains("gzip", manifest.RootElement.GetProperty("note").GetString());
        Assert.Contains("public heading corpus", manifest.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Publish_WithGzip_StillRefusesAKeyAnywhere_AndCopiesNothing()
    {
        var (plan, _, _, runs) = MakeRuns(sseText: arm => arm == "EVIDENCE_FIRST"
            ? "data: {\"choices\":[]}\n\nthe key sk-or-v1-abcdef0123456789abcdef\n\ndata: [DONE]\n\n" : "data: [DONE]\n\n");
        var outDir = Path.Combine(scratch, "out-gz-key");
        Assert.Equal("KEY_SHAPED_STRING_IN_FILE", P7F1V6Publication.Publish(plan, outDir, runs, null, compressRawSse: true).Refusal);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Publish_NeverOverwrites()
    {
        var (plan, _, _, runs) = MakeRuns();
        var outDir = Path.Combine(scratch, "out-twice");
        Assert.Null(P7F1V6Publication.Publish(plan, outDir, runs, null).Refusal);
        Assert.Equal("OUTPUT_EXISTS", P7F1V6Publication.Publish(plan, outDir, runs, null).Refusal);
    }
}
