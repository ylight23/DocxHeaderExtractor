using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Deterministic, non-provider tests for the Issue #6 held-out gate preparation.</summary>
public sealed class P7F1QHeldoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "p7f1q-heldout-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static string Dir => TestRepository.Path("artifacts/web-pdf-semantic-diagnostic");

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", true)]
    [InlineData("  ```\n{\"a\":1}\n```  \n", true)]
    [InlineData("```JSON\r\n{\"a\":[1,2]}\r\n```", true)]
    [InlineData("{\"a\":1}", false)]
    [InlineData("Here is the JSON:\n```json\n{\"a\":1}\n```", false)]
    [InlineData("```json\n{\"a\":1}\n```\nThanks", false)]
    [InlineData("```json\n[1,2]\n```", false)]
    [InlineData("```json\n{\"a\":1} {\"b\":2}\n```", false)]
    [InlineData("```json\n{\"a\":1\n```", false)]
    [InlineData("```json\n```json\n{\"a\":1}\n```\n```", false)]
    public void Fence_normalization_removes_exactly_one_outer_fence_around_one_json_object(string raw, bool applied)
    {
        var r = P7F1QFenceNormalization.Normalize(raw);
        Assert.Equal(applied, r.Applied);
        if (applied) Assert.True(JsonDocument.Parse(r.Normalized).RootElement.ValueKind == JsonValueKind.Object);
        else Assert.Equal(raw, r.Normalized);
    }

    private static JsonElement Box(double l, double r, double b, double t) => JsonSerializer.SerializeToElement(new { left = l, right = r, bottom = b, top = t });
    private static readonly F1QSourceLine[] Lines = [new("L0000:S0", 0, 1, "TITLE", Box(1, 2, 3, 4), null), new("L0001:S0", 1, 1, "Body", Box(1, 2, 1, 2), null)];
    private static P7F1QEvidenceTools Tools() => new("s", "u", "st", Lines, new Dictionary<string, string> { ["O1"] = "L0000:S0", ["O2"] = "L0001:S0" }, 2);
    private static readonly F1QIssuedOccurrence[] Issued = [new("O1", "L0000:S0", 1, "TITLE"), new("O2", "L0001:S0", 1, "Body")];
    private static string Valid => """{"protocolVersion":"P7_F1Q_V2","decisions":[{"occurrence":"O1","sourceAlias":"L0000:S0","assessment":"SUPPORTED","function":"ESTABLISHES_STRUCTURE","observedRole":"r","evidenceRefs":["L0000:S0"],"interpretation":"i","missingEvidence":[]},{"occurrence":"O2","sourceAlias":"L0001:S0","assessment":"SUPPORTED","function":"OTHER","observedRole":"r","evidenceRefs":["L0001:S0"],"interpretation":"i","missingEvidence":[]}]}""";

    private sealed class Scripted(Func<byte[], F1QHttpObservation> f) : IF1QTransport
    {
        public int Calls { get; private set; }
        public Task<F1QHttpObservation> SendAsync(byte[] body, CancellationToken ct) { Calls++; return Task.FromResult(f(body)); }
    }
    private static F1QHttpObservation Final(string content) => new(200, Encoding.UTF8.GetBytes(
        $"data: {{\"provider\":\"Alibaba\",\"choices\":[{{\"delta\":{JsonSerializer.Serialize(new { content })}}}]}}\n\ndata: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":1,\"completion_tokens\":1,\"cost\":0.0001}}}}\n\ndata: [DONE]\n\n"), null, 1, "text/event-stream");
    private static F1QRequestSpec Spec(bool normalize) => new("h|F1QEvidenceV2", "h", F1QArm.F1QEvidenceV2, "sys", "user", Issued,
        new HashSet<string> { "L0000:S0", "L0001:S0" }, Tools(), null, null, FenceNormalization: normalize);
    private static readonly F1QBodyPolicy Policy = new("qwen/qwen3.7-flash", "alibaba", 32768, true, 2);

    [Fact] public async Task Fenced_response_is_accepted_on_the_normalized_view_and_rejected_on_the_strict_raw_view()
    {
        var fenced = "```json\n" + Valid + "\n```";
        var outcome = await new P7F1QConversationRunner(new Scripted(_ => Final(fenced)), Policy, new(MaxTransportAttemptsPerTurn: 1)).RunAsync(Spec(true), Path.Combine(root, "n1"), () => 0, default);
        Assert.Equal("ACCEPTED", outcome.Status);
        Assert.Equal(fenced, File.ReadAllText(Path.Combine(root, "n1", "response.txt"))); // raw untouched
        var rawView = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "n1", "validation.raw.json")))!;
        Assert.False(rawView["strictAccepted"]!.GetValue<bool>());
        Assert.Equal("RESPONSE_NOT_JSON", rawView["failureCode"]!.GetValue<string>());
        var norm = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "n1", "normalization.json")))!;
        Assert.True(norm["applied"]!.GetValue<bool>());
        Assert.NotEqual(norm["rawResponseSha256"]!.GetValue<string>(), norm["normalizedSha256"]!.GetValue<string>());
    }

    [Fact] public async Task Normalization_never_repairs_contract_errors_inside_the_fence()
    {
        var broken = "```json\n" + Valid.Replace("\"sourceAlias\":\"L0001:S0\"", "\"sourceAlias\":\"L0000:S0\"") + "\n```";
        var outcome = await new P7F1QConversationRunner(new Scripted(_ => Final(broken)), Policy, new(MaxTransportAttemptsPerTurn: 1)).RunAsync(Spec(true), Path.Combine(root, "n2"), () => 0, default);
        Assert.Equal(("CONTRACT_FAILED", "SOURCE_ALIAS_MISMATCH"), (outcome.Status, outcome.FailureCode));
    }

    [Fact] public async Task Without_the_flag_a_fenced_response_stays_a_contract_failure()
    {
        var outcome = await new P7F1QConversationRunner(new Scripted(_ => Final("```json\n" + Valid + "\n```")), Policy, new(MaxTransportAttemptsPerTurn: 1)).RunAsync(Spec(false), Path.Combine(root, "n3"), () => 0, default);
        Assert.Equal("RESPONSE_NOT_JSON", outcome.FailureCode);
        Assert.False(File.Exists(Path.Combine(root, "n3", "normalization.json")));
    }

    [Fact] public async Task Hard_cap_stops_before_sending_when_the_worst_case_call_would_breach_it()
    {
        var transport = new Scripted(_ => Final(Valid));
        var caps = new F1QRunCaps(MaxTransportAttemptsPerTurn: 1, HardCapUsd: 3.00m);
        var worst = caps.WorstCaseUsd(10_000, 32768);
        Assert.True(worst > 0.026m && worst < 0.03m);
        var outcome = await new P7F1QConversationRunner(transport, Policy, caps).RunAsync(Spec(true), Path.Combine(root, "c1"), () => 2.99m, default);
        Assert.Equal(("BUDGET_STOP", "PRE_CALL_WORST_CASE_EXCEEDS_CAP"), (outcome.Status, outcome.FailureCode));
        Assert.Equal(0, transport.Calls);
        var ok = await new P7F1QConversationRunner(transport, Policy, caps).RunAsync(Spec(true), Path.Combine(root, "c2"), () => 0.5m, default);
        Assert.Equal("ACCEPTED", ok.Status);
    }

    [Fact] public void Contamination_audit_rejects_exposed_candidates_and_replaces_them_by_the_reserve_order()
    {
        using var a = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.contamination-audit.v2.json")));
        var r = a.RootElement;
        var rejected = r.GetProperty("candidates").EnumerateArray().Where(c => c.GetProperty("verdict").GetString() == "REJECTED")
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("rejectReasons").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(["002", "030", "035", "043", "058", "074"], rejected.Keys.Order().ToArray());
        Assert.Contains("NO_PARSER_TEXT_LAYER", rejected["002"]);
        Assert.Contains("NEAR_DUPLICATE_OF_DEVELOPMENT_029", rejected["035"]);
        foreach (var id in new[] { "030", "043", "058", "074" }) Assert.Contains("MODEL_PREDICTION_EXPOSURE", rejected[id]);
        var final = r.GetProperty("finalCohort").EnumerateArray().Select(f => f.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(24, final.Length);
        Assert.Empty(final.Intersect(["012", "029", "054", "080", "092", "089", "095"]));
        Assert.DoesNotContain("072", final); Assert.DoesNotContain("076", final); // F1-exposed reserves skipped
        Assert.Equal("NOT_MET_PENDING_INDEPENDENT_ADMINISTRATIVE_PDF", r.GetProperty("external25").GetProperty("status").GetString());
    }

    [Fact] public void Page_selection_is_source_only_and_bounded()
    {
        using var s = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.page-selection.v1.json")));
        Assert.False(s.RootElement.GetProperty("goldRead").GetBoolean());
        foreach (var d in s.RootElement.GetProperty("documents").EnumerateArray())
        {
            var pages = d.GetProperty("selectedPages").EnumerateArray().ToArray();
            if (d.GetProperty("pageCount").GetInt32() < 4) Assert.All(pages, p => Assert.Equal("ALL_PAGES", p.GetProperty("stratum").GetString()));
            else
            {
                Assert.InRange(pages.Length, 3, 4);
                Assert.Equal(1, pages.Count(p => p.GetProperty("stratum").GetString() == "FIRST"));
                Assert.Equal(2, pages.Count(p => p.GetProperty("stratum").GetString() == "SEEDED_INTERIOR"));
            }
        }
        Assert.Equal(3151, s.RootElement.GetProperty("totalGoldOccurrences").GetInt32());
    }

    [Fact] public void Gold_drafts_label_every_selected_occurrence_and_stay_unapproved()
    {
        var total = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v1"), "*.gold-draft.json"))
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(file));
            Assert.Equal("DRAFT_REVIEWER_A_NOT_APPROVED", d.RootElement.GetProperty("status").GetString());
            var id = d.RootElement.GetProperty("id").GetString()!;
            using var review = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.review-bundles.v1", id + ".review.json")));
            var expected = review.RootElement.GetProperty("occurrences").EnumerateArray().Select(o => o.GetProperty("sourceAlias").GetString()).ToArray();
            var labels = d.RootElement.GetProperty("labels").EnumerateArray().ToArray();
            Assert.Equal(expected, labels.Select(l => l.GetProperty("sourceAlias").GetString()).ToArray());
            Assert.All(labels, l => Assert.Equal("PENDING_USER", l.GetProperty("approval").GetString()));
            total += labels.Length;
        }
        Assert.Equal(3151, total);
    }

    [Fact] public void Heldout_plan_reuses_the_frozen_v3_prompt_tools_and_route_and_gates_on_gold()
    {
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.execution-plan.v1.json")));
        using var v3 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.execution-plan.v3.json")));
        var r = plan.RootElement;
        Assert.Equal(v3.RootElement.GetProperty("toolDefinitionsSha256").GetString(), r.GetProperty("toolDefinitionsSha256").GetString());
        Assert.Equal(v3.RootElement.GetProperty("requests")[0].GetProperty("systemPromptSha256").GetString(), r.GetProperty("systemPromptSha256").GetString());
        Assert.Equal(3.00m, r.GetProperty("budget").GetProperty("hardCapUsd").GetDecimal());
        Assert.StartsWith("EXECUTION_REFUSES_TO_RUN_WITHOUT", r.GetProperty("goldGate").GetString());
        var requests = r.GetProperty("requests").EnumerateArray().ToArray();
        Assert.Equal(186, requests.Length);
        var redactedCount = 0;
        foreach (var q in requests)
        {
            var path = Path.Combine(Dir, "p7.f1q.heldout-raw.v1", "initial-bodies", q.GetProperty("bodyFile").GetString()!);
            if (!File.Exists(path))
            {
                // PII redaction (Issue #6): the exact body is private; the public receipt binds its sha to the plan.
                redactedCount++;
                using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(path, ".redaction.json")));
                Assert.Equal(q.GetProperty("bodySha256").GetString(), receipt.RootElement.GetProperty("originalSha256").GetString());
                var red = File.ReadAllBytes(Path.ChangeExtension(path, ".redacted.json"));
                Assert.Equal(receipt.RootElement.GetProperty("redactedSha256").GetString(), SpatialCanonical.Hash(red));
                Assert.DoesNotMatch(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", Encoding.UTF8.GetString(red));
                continue;
            }
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(q.GetProperty("bodySha256").GetString(), SpatialCanonical.Hash(bytes));
            var body = JsonNode.Parse(bytes)!;
            Assert.Equal(P7F1QProtocolV2.SystemPrompt(), body["messages"]![0]!["content"]!.GetValue<string>());
            Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
            Assert.DoesNotContain("draftLabel", body["messages"]![1]!["content"]!.GetValue<string>());
        }
        Assert.Equal(4, redactedCount);
        Assert.False(Directory.Exists(Path.Combine(Dir, "p7.f1q.heldout-raw.v1", "requests"))); // provider locked: no calls yet
    }
}
