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

    [Fact] public void Gold_drafts_v2_apply_exactly_the_recorded_user_decisions()
    {
        var v2 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v2");
        JsonElement[] Labels(string dir, string id) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".gold-draft.json"))).RootElement.GetProperty("labels").EnumerateArray().ToArray();
        var d087 = Labels(v2, "087").ToDictionary(l => l.GetProperty("sourceAlias").GetString()!);
        foreach (var a in new[] { "L1118:S0", "L1119:S0", "L1207:S0", "L1208:S0", "L1209:S0", "L1210:S0", "L1211:S0" })
        {
            Assert.Equal("OTHER", d087[a].GetProperty("draftLabel").GetString());
            Assert.Equal("USER_DECIDED_2026_10_10", d087[a].GetProperty("approval").GetString());
        }
        var d044 = Labels(v2, "044").ToDictionary(l => l.GetProperty("sourceAlias").GetString()!);
        Assert.Equal("OTHER", d044["L0000:S0"].GetProperty("draftLabel").GetString());
        foreach (var a in new[] { "L0044:S0", "L0045:S0", "L0074:S0", "L0091:S0" }) Assert.Equal("ESTABLISHES_STRUCTURE", d044[a].GetProperty("draftLabel").GetString());
        Assert.Equal("OTHER", d044["L0046:S0"].GetProperty("draftLabel").GetString());
        Assert.Equal("USER_DECIDED", d044["L0046:S0"].GetProperty("reviewFlag").GetString());
        // Nothing else changed label between V1 and V2.
        var changed = 0;
        foreach (var file in Directory.GetFiles(v2, "*.gold-draft.json"))
        {
            var id = Path.GetFileName(file).Split('.')[0];
            var before = Labels(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v1"), id).ToDictionary(l => l.GetProperty("sourceAlias").GetString()!, l => l.GetProperty("draftLabel").GetString());
            changed += Labels(v2, id).Count(l => before[l.GetProperty("sourceAlias").GetString()!] != l.GetProperty("draftLabel").GetString());
        }
        Assert.Equal(7, changed);
        Assert.True(File.Exists(Path.Combine(Dir, "p7.f1q.heldout.review-bundles.v1", "087.review.json")));
        Assert.Equal(86, Directory.GetFiles(Path.Combine(Dir, "p7.f1q.heldout.visual-review.v1"), "*.jpg", SearchOption.AllDirectories).Length);
    }

    [Fact] public void Gold_drafts_v3_apply_the_user_visual_audit_with_an_append_only_ledger()
    {
        var v3 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v3");
        Dictionary<string, JsonElement> Rows(string dir, string id) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".gold-draft.json")))
            .RootElement.GetProperty("labels").EnumerateArray().ToDictionary(l => l.GetProperty("sourceAlias").GetString()!);
        string? Label(string id, string alias) => Rows(v3, id)[alias].GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? null : Rows(v3, id)[alias].GetProperty("draftLabel").GetString();
        Assert.Equal("OTHER", Label("049", "L0005:S0"));
        Assert.Equal("ESTABLISHES_STRUCTURE", Label("017", "L1688:S0")); Assert.Equal("ESTABLISHES_STRUCTURE", Label("017", "L1691:S0"));
        Assert.Equal("ESTABLISHES_STRUCTURE", Label("087", "L1222:S0")); Assert.Equal("ESTABLISHES_STRUCTURE", Label("087", "L1224:S0"));
        Assert.Null(Label("083", "L1075:S0")); Assert.Null(Label("087", "L1216:S0"));
        Assert.Equal("EXCLUDED_SOURCE_CORRUPTED_MIXED_FUNCTION", Rows(v3, "083")["L1075:S0"].GetProperty("goldStatus").GetString());
        var history = Rows(v3, "049")["L0005:S0"].GetProperty("decisions")[0];
        Assert.Equal("ESTABLISHES_STRUCTURE", history.GetProperty("previousLabel").GetString());
        Assert.Equal("OTHER", history.GetProperty("newLabel").GetString());
        Assert.False(string.IsNullOrWhiteSpace(history.GetProperty("reason").GetString()));
        using var ledger = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "ledger-2026-10-10-user-visual-audit.json")));
        Assert.Equal(7, ledger.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(0, ledger.RootElement.GetProperty("goldApprovedDocuments").GetInt32());
        var changed = 0;
        foreach (var file in Directory.GetFiles(v3, "*.gold-draft.json"))
        {
            var id = Path.GetFileName(file).Split('.')[0]; var before = Rows(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v2"), id); var after = Rows(v3, id);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            changed += after.Count(p => p.Value.GetProperty("draftLabel").ToString() != before[p.Key].GetProperty("draftLabel").ToString());
            Assert.Equal("DRAFT_USER_REVIEWED_NOT_APPROVED", JsonDocument.Parse(File.ReadAllBytes(file)).RootElement.GetProperty("status").GetString());
        }
        Assert.Equal(7, changed);
        using var audit = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.source-quality-audit.v1.json")));
        Assert.Equal(2, audit.RootElement.GetProperty("missingSourceHeadings").GetArrayLength());
        Assert.True(audit.RootElement.GetProperty("declaredBeforeProviderRun").GetBoolean());
    }

    [Fact] public void Gold_drafts_v4_sync_rationale_with_the_decided_labels_and_keep_open_questions_unlabelled()
    {
        var v3 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v3"); var v4 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v4");
        Dictionary<string, JsonElement> Rows(string dir, string id) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".gold-draft.json")))
            .RootElement.GetProperty("labels").EnumerateArray().ToDictionary(l => l.GetProperty("sourceAlias").GetString()!);
        var title = Rows(v4, "034")["L0210:S0"];
        Assert.Equal("ESTABLISHES_STRUCTURE", title.GetProperty("draftLabel").GetString());
        Assert.Contains("restated-title rule", title.GetProperty("rationale").GetString());
        foreach (var (id, alias) in new[] { ("049", "L0005:S0"), ("017", "L1688:S0"), ("017", "L1691:S0"), ("087", "L1222:S0"), ("087", "L1224:S0"), ("083", "L1075:S0"), ("087", "L1216:S0") })
        {
            var row = Rows(v4, id)[alias]; var last = row.GetProperty("decisions").EnumerateArray().Last();
            Assert.Equal($"USER DECISION (USER_CHECKPOINT_2026_10_10B_ISSUE_6): {last.GetProperty("reason").GetString()}", row.GetProperty("rationale").GetString());
            Assert.Equal(Rows(v3, id)[alias].GetProperty("rationale").GetString(), last.GetProperty("previousRationale").GetString());
        }
        foreach (var (id, alias) in new[] { ("087", "L1225:S0"), ("087", "L1226:S0"), ("051", "L0004:S0") })
        {
            var row = Rows(v4, id)[alias];
            Assert.Equal("DECISION_NEEDED", row.GetProperty("reviewFlag").GetString());
            Assert.Equal(Rows(v3, id)[alias].GetProperty("draftLabel").GetString(), row.GetProperty("draftLabel").GetString());
            Assert.Contains("confirm E or O", row.GetProperty("openQuestion").GetString());
        }
        var totals = new Dictionary<string, int>();
        foreach (var file in Directory.GetFiles(v4, "*.gold-draft.json"))
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(file));
            Assert.Equal("DRAFT_USER_REVIEWED_NOT_APPROVED", d.RootElement.GetProperty("status").GetString());
            var id = d.RootElement.GetProperty("id").GetString()!; var before = Rows(v3, id);
            foreach (var l in d.RootElement.GetProperty("labels").EnumerateArray())
            {
                Assert.Equal(before[l.GetProperty("sourceAlias").GetString()!].GetProperty("draftLabel").ToString(), l.GetProperty("draftLabel").ToString());
                var key = l.GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? l.GetProperty("goldStatus").GetString()! : l.GetProperty("draftLabel").GetString()!;
                totals[key] = totals.GetValueOrDefault(key) + 1;
            }
        }
        Assert.Equal(250, totals["ESTABLISHES_STRUCTURE"]); Assert.Equal(191, totals["REPRESENTS_STRUCTURE"]);
        Assert.Equal(2708, totals["OTHER"]); Assert.Equal(2, totals["EXCLUDED_SOURCE_CORRUPTED_MIXED_FUNCTION"]);
        using var ledger = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "ledger-2026-10-10b-user-checkpoint.json")));
        Assert.Equal(0, ledger.RootElement.GetProperty("goldApprovedDocuments").GetInt32());
        Assert.Equal(0, ledger.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(250, ledger.RootElement.GetProperty("totalsAfter").GetProperty("ESTABLISHES_STRUCTURE").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "decisions-2026-10-10b-user-checkpoint.txt")))),
            ledger.RootElement.GetProperty("decisionsFileSha256").GetString());
    }

    [Fact] public void Gold_workspace_manifest_binds_every_document_to_the_v4_draft_and_keeps_page_renders_out_of_git()
    {
        var ws = Path.Combine(Dir, "p7.f1q.heldout.gold-workspace.v1");
        using var m = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ws, "workspace-manifest.json")));
        var r = m.RootElement;
        Assert.False(r.GetProperty("goldApproved").GetBoolean()); Assert.Equal(0, r.GetProperty("providerCalls").GetInt32());
        Assert.Equal(3151, r.GetProperty("totalOccurrences").GetInt32()); Assert.Equal(86, r.GetProperty("totalPages").GetInt32());
        Assert.Equal(["044", "049", "087", "032"], r.GetProperty("reviewOrder").EnumerateArray().Take(4).Select(e => e.GetString()!).ToArray());
        string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var docs = r.GetProperty("documents").EnumerateArray().ToArray();
        Assert.Equal(24, docs.Length);
        foreach (var d in docs)
        {
            var id = d.GetProperty("id").GetString()!;
            Assert.Equal(Sha(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v4", id + ".gold-draft.json")), d.GetProperty("draftSha256").GetString());
            foreach (var kind in new[] { "html", "pdf" })
            {
                var local = Path.Combine(ws, d.GetProperty(kind).GetString()!);
                if (File.Exists(local)) Assert.Equal(d.GetProperty(kind + "Sha256").GetString(), Sha(local));
            }
        }
        var ignore = File.ReadAllText(TestRepository.Path(".gitignore"));
        Assert.Contains("p7.f1q.heldout.gold-workspace.*/*.review.html", ignore);
        Assert.Contains("p7.f1q.heldout.gold-workspace.*/*.review.pdf", ignore);
        Assert.Contains("E 250 · R 191 · O 2708 · excluded 2", File.ReadAllText(Path.Combine(ws, "README.md")));
    }

    [Fact] public void Gold_drafts_v5_resolve_exactly_the_three_open_questions_and_nothing_else()
    {
        var v4 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v4"); var v5 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v5");
        Dictionary<string, JsonElement> Rows(string dir, string id) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".gold-draft.json")))
            .RootElement.GetProperty("labels").EnumerateArray().ToDictionary(l => l.GetProperty("sourceAlias").GetString()!);
        var expected = new Dictionary<(string, string), string> { [("087", "L1225:S0")] = "OTHER", [("087", "L1226:S0")] = "OTHER", [("051", "L0004:S0")] = "OTHER" };
        var changed = new List<(string, string)>(); var totals = new Dictionary<string, int>();
        foreach (var file in Directory.GetFiles(v5, "*.gold-draft.json"))
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(file));
            Assert.Equal("DRAFT_USER_REVIEWED_NOT_APPROVED", d.RootElement.GetProperty("status").GetString());
            Assert.False(d.RootElement.TryGetProperty("documentApproval", out _));
            var id = d.RootElement.GetProperty("id").GetString()!; var before = Rows(v4, id);
            foreach (var l in d.RootElement.GetProperty("labels").EnumerateArray())
            {
                var alias = l.GetProperty("sourceAlias").GetString()!;
                Assert.NotEqual("DECISION_NEEDED", l.GetProperty("reviewFlag").GetString());
                if (l.GetRawText() != before[alias].GetRawText()) changed.Add((id, alias));
                var key = l.GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? l.GetProperty("goldStatus").GetString()! : l.GetProperty("draftLabel").GetString()!;
                totals[key] = totals.GetValueOrDefault(key) + 1;
            }
        }
        Assert.Equal(expected.Keys.Order(), changed.Order());
        foreach (var ((id, alias), label) in expected)
        {
            var row = Rows(v5, id)[alias]; var last = row.GetProperty("decisions").EnumerateArray().Last();
            Assert.Equal(label, row.GetProperty("draftLabel").GetString());
            Assert.Equal("USER_DECIDED", row.GetProperty("reviewFlag").GetString());
            Assert.False(row.TryGetProperty("openQuestion", out _));
            Assert.Equal("DECISION_NEEDED", last.GetProperty("previousFlag").GetString());
            Assert.Equal(Rows(v4, id)[alias].GetProperty("openQuestion").GetString(), last.GetProperty("resolvedQuestion").GetString());
            Assert.Equal("USER_DECISION_2026_10_10C_ISSUE_6", last.GetProperty("source").GetString());
        }
        Assert.Equal("ESTABLISHES_STRUCTURE", Rows(v5, "087")["L1224:S0"].GetProperty("draftLabel").GetString());
        Assert.Equal(249, totals["ESTABLISHES_STRUCTURE"]); Assert.Equal(191, totals["REPRESENTS_STRUCTURE"]);
        Assert.Equal(2709, totals["OTHER"]); Assert.Equal(2, totals["EXCLUDED_SOURCE_CORRUPTED_MIXED_FUNCTION"]);
        using var ledger = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "ledger-2026-10-10c-user-v4-open-questions.json")));
        Assert.Equal(3, ledger.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(0, ledger.RootElement.GetProperty("goldApprovedDocuments").GetInt32());
        using var ws = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-workspace.v2", "workspace-manifest.json")));
        string Sha(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        foreach (var d in ws.RootElement.GetProperty("documents").EnumerateArray())
            Assert.Equal(Sha(Path.Combine(v5, d.GetProperty("id").GetString() + ".gold-draft.json")), d.GetProperty("draftSha256").GetString());
    }

    [Fact] public void Visual_review_v2_shows_every_v5_occurrence_of_the_first_four_documents_on_already_public_pages()
    {
        var vr = Path.Combine(Dir, "p7.f1q.heldout.visual-review.v2");
        using var m = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(vr, "render-manifest.json")));
        Assert.Equal("P7_F1Q_HELDOUT_GOLD_DRAFT_V5", m.RootElement.GetProperty("drafts").GetString());
        Assert.Equal(["044", "049", "087", "032"], m.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(110, m.RootElement.GetProperty("dpi").GetInt32());
        Assert.Equal(0, m.RootElement.GetProperty("providerCalls").GetInt32());
        var images = m.RootElement.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(16, images.Length);
        foreach (var image in images)
        {
            var rel = image.GetProperty("file").GetString()!;
            Assert.Equal(image.GetProperty("sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(vr, rel)))));
            Assert.True(File.Exists(Path.Combine(Dir, "p7.f1q.heldout.visual-review.v1", rel)), "page must already be public in v1: " + rel);
        }
        foreach (var id in new[] { "044", "049", "087", "032" })
        {
            var md = File.ReadAllText(Path.Combine(vr, id + ".md"));
            using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v5", id + ".gold-draft.json")));
            foreach (var l in d.RootElement.GetProperty("labels").EnumerateArray())
                Assert.Contains($"| {l.GetProperty("sourceAlias").GetString()} |", md);
            Assert.Contains("OTHER rows to check for a missed heading", md);
        }
    }

    [Fact] public void Visual_review_v3_shows_every_v6_occurrence_of_the_remaining_twenty_documents_on_already_public_pages()
    {
        var vr = Path.Combine(Dir, "p7.f1q.heldout.visual-review.v3");
        using var m = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(vr, "render-manifest.json")));
        Assert.Equal("P7_F1Q_HELDOUT_GOLD_DRAFT_V6", m.RootElement.GetProperty("drafts").GetString());
        var ids = m.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()!).ToArray();
        using var sel = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.page-selection.v1.json")));
        var all = sel.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToHashSet();
        Assert.Equal(all.Except(["044", "049", "087", "032"]).Order(), ids.Order());
        Assert.Equal(110, m.RootElement.GetProperty("dpi").GetInt32());
        var images = m.RootElement.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(70, images.Length);
        foreach (var image in images)
        {
            var rel = image.GetProperty("file").GetString()!;
            Assert.Equal(image.GetProperty("sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(vr, rel)))));
            Assert.True(File.Exists(Path.Combine(Dir, "p7.f1q.heldout.visual-review.v1", rel)), "page must already be public in v1: " + rel);
        }
        foreach (var id in ids)
        {
            var md = File.ReadAllText(Path.Combine(vr, id + ".md"));
            using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v6", id + ".gold-draft.json")));
            foreach (var l in d.RootElement.GetProperty("labels").EnumerateArray())
                Assert.Contains($"| {l.GetProperty("sourceAlias").GetString()} |", md);
        }
        Assert.Contains("most rows on this page are bold, so bold is ignored", File.ReadAllText(Path.Combine(vr, "024.md")));
    }

    [Fact] public void Gold_drafts_v7_record_four_ai_reviewer_approvals_under_user_delegation_bound_to_v6_labels()
    {
        var v6 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v6"); var v7 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v7");
        string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
        string LabelsSha(JsonElement d) => Sha(Encoding.UTF8.GetBytes(string.Join("\n", d.GetProperty("labels").EnumerateArray().Select(l =>
            $"{l.GetProperty("sourceAlias").GetString()}|{(l.GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? "NULL" : l.GetProperty("draftLabel").GetString())}|{l.GetProperty("goldStatus").GetString()}"))));
        string[] batch = ["032", "044", "049", "087"];
        var approved = new List<string>();
        foreach (var file in Directory.GetFiles(v7, "*.gold-draft.json"))
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(file)); var r = d.RootElement; var id = r.GetProperty("id").GetString()!;
            var before = File.ReadAllBytes(Path.Combine(v6, id + ".gold-draft.json"));
            using var b = JsonDocument.Parse(before);
            Assert.Equal(b.RootElement.GetProperty("labels").GetRawText(), r.GetProperty("labels").GetRawText());
            if (!r.TryGetProperty("documentApproval", out var a)) { Assert.Equal("DRAFT_USER_REVIEWED_NOT_APPROVED", r.GetProperty("status").GetString()); continue; }
            approved.Add(id);
            Assert.Equal("APPROVED_AI_REVIEWER_USER_DELEGATED", r.GetProperty("status").GetString());
            Assert.True(a.GetProperty("approved").GetBoolean());
            Assert.Equal("GPT-6", a.GetProperty("by").GetString());
            Assert.Equal("AI_REVIEWER", a.GetProperty("reviewerType").GetString());
            Assert.Equal("USER_DELEGATED", a.GetProperty("authority").GetString());
            Assert.Equal(Sha(before), a.GetProperty("inputDraftSha256").GetString());
            Assert.Equal(LabelsSha(r), a.GetProperty("labelsSha256").GetString());
            var evidence = a.GetProperty("evidence");
            Assert.Equal(Sha(File.ReadAllBytes(TestRepository.Path(evidence.GetProperty("path").GetString()!))), evidence.GetProperty("sha256").GetString());
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("note").GetString()));
        }
        Assert.Equal(batch, approved.Order().ToArray());
        using var ledger = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "ledger-2026-10-10e-gpt6-approvals-batch1.json")));
        Assert.Equal(4, ledger.RootElement.GetProperty("goldApprovedDocuments").GetInt32());
        Assert.Equal(0, ledger.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(0, ledger.RootElement.GetProperty("revokedApprovals").GetArrayLength());
        Assert.Equal(0, ledger.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(246, ledger.RootElement.GetProperty("totalsAfter").GetProperty("ESTABLISHES_STRUCTURE").GetInt32());
    }

    [Fact] public void Gold_drafts_v6_change_exactly_the_three_032_cover_rows_and_record_no_approval()
    {
        var v5 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v5"); var v6 = Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v6");
        Dictionary<string, JsonElement> Rows(string dir, string id) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".gold-draft.json")))
            .RootElement.GetProperty("labels").EnumerateArray().ToDictionary(l => l.GetProperty("sourceAlias").GetString()!);
        var changed = new List<(string, string)>(); var totals = new Dictionary<string, int>();
        foreach (var file in Directory.GetFiles(v6, "*.gold-draft.json"))
        {
            using var d = JsonDocument.Parse(File.ReadAllBytes(file));
            Assert.Equal("DRAFT_USER_REVIEWED_NOT_APPROVED", d.RootElement.GetProperty("status").GetString());
            Assert.False(d.RootElement.TryGetProperty("documentApproval", out _));
            var id = d.RootElement.GetProperty("id").GetString()!; var before = Rows(v5, id);
            Assert.Equal(before.Keys.Order(), d.RootElement.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("sourceAlias").GetString()!).Order());
            foreach (var l in d.RootElement.GetProperty("labels").EnumerateArray())
            {
                var alias = l.GetProperty("sourceAlias").GetString()!;
                if (l.GetRawText() != before[alias].GetRawText()) changed.Add((id, alias));
                var key = l.GetProperty("draftLabel").ValueKind == JsonValueKind.Null ? l.GetProperty("goldStatus").GetString()! : l.GetProperty("draftLabel").GetString()!;
                totals[key] = totals.GetValueOrDefault(key) + 1;
            }
        }
        Assert.Equal([("032", "L0006:S0"), ("032", "L0007:S0"), ("032", "L0008:S0")], changed.Order().ToArray());
        foreach (var alias in new[] { "L0006:S0", "L0007:S0", "L0008:S0" })
        {
            var row = Rows(v6, "032")[alias]; var last = row.GetProperty("decisions").EnumerateArray().Last();
            Assert.Equal("OTHER", row.GetProperty("draftLabel").GetString());
            Assert.Equal("ESTABLISHES_STRUCTURE", last.GetProperty("previousLabel").GetString());
            Assert.Equal("GPT6_RECOMMENDATION_USER_AUTHORIZED_EDITS_2026_10_10D_ISSUE_6", last.GetProperty("source").GetString());
        }
        Assert.Equal(246, totals["ESTABLISHES_STRUCTURE"]); Assert.Equal(191, totals["REPRESENTS_STRUCTURE"]);
        Assert.Equal(2712, totals["OTHER"]); Assert.Equal(2, totals["EXCLUDED_SOURCE_CORRUPTED_MIXED_FUNCTION"]);
        using var ledger = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "ledger-2026-10-10d-gpt6-visual-review.json")));
        Assert.Equal(3, ledger.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(0, ledger.RootElement.GetProperty("goldApprovedDocuments").GetInt32());
        Assert.DoesNotContain("APPROVE", File.ReadAllLines(Path.Combine(Dir, "p7.f1q.heldout.gold-decisions", "decisions-2026-10-10d-gpt6-visual-review.txt")).Where(l => !l.StartsWith('#')).SelectMany(l => l.Split(' ')));
    }

    [Fact] public void Source_quality_audit_v2_keeps_v1_and_records_headings_lost_inside_corrupted_atoms()
    {
        using var v1 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.source-quality-audit.v1.json")));
        using var v2 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.source-quality-audit.v2.json")));
        Assert.Equal(v1.RootElement.GetProperty("corruptedSourceOccurrences").GetRawText(), v2.RootElement.GetProperty("corruptedSourceOccurrences").GetRawText());
        Assert.Equal(v1.RootElement.GetProperty("missingSourceHeadings").GetRawText(), v2.RootElement.GetProperty("missingSourceHeadings").GetRawText());
        var loss = v2.RootElement.GetProperty("sourceCoverageLoss").EnumerateArray().ToArray();
        Assert.Equal(["083:L1075:S0", "087:L1216:S0"], loss.Select(l => l.GetProperty("document").GetString() + ":" + l.GetProperty("sourceAlias").GetString()).Order().ToArray());
        Assert.All(loss, l => Assert.Equal("SOURCE_COVERAGE_LOSS", l.GetProperty("status").GetString()));
        foreach (var l in loss)
        {   // Coverage loss changes no label: the atom stays excluded in V6.
            using var d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, "p7.f1q.heldout.gold-drafts.v6", l.GetProperty("document").GetString() + ".gold-draft.json")));
            var row = d.RootElement.GetProperty("labels").EnumerateArray().Single(r => r.GetProperty("sourceAlias").GetString() == l.GetProperty("sourceAlias").GetString());
            Assert.Equal("EXCLUDED_SOURCE_CORRUPTED_MIXED_FUNCTION", row.GetProperty("goldStatus").GetString());
        }
        Assert.False(v2.RootElement.GetProperty("goldApproved").GetBoolean());
        Assert.True(v2.RootElement.GetProperty("declaredBeforeProviderRun").GetBoolean());
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
