using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

public sealed class EvidenceHeadingPlacementTests
{
    const string C3 = "P015-L010", D34 = "P015-L012", D35 = "P015-L019", C4 = "P019-L001", C5 = "P021-L010", D46 = "P021-L012", D47 = "P021-L014";
    const string Appendix = "P023-L001", Part1 = "P023-L002", Intro = "P023-L003", Child = "P023-L004";

    // Synthetic source fixture for protocol and graph regressions, not Gold or actual provider output.
    static (CanonicalSemanticBoundHeading[] Bound, HeadingPlacementEvidence Evidence, Dictionary<string, string> Parents) Fixture(string? missingStage = null)
    {
        (string Alias, string Text, string Parent)[] rows = [
            ("P008-L013", "Chương II", "ROOT"), ("P011-L001", "Mục 3 CHỨNG THỰC CHỮ KÝ", "P008-L013"),
            ("P011-L003", "Điều 23. Trách nhiệm", "P011-L001"), ("P012-L026", "Mục 4 NGƯỜI DỊCH", "P008-L013"),
            ("P012-L028", "Điều 27. Tiêu chuẩn", "P012-L026"), (C3, "Chương III", "ROOT"), (D34, "Điều 34. Phạm vi", C3),
            (D35, "Điều 35. Trách nhiệm", C3), (C4, "Chương IV", "ROOT"), ("P019-L003", "Điều 41. Trách nhiệm", C4),
            (C5, "Chương V", "ROOT"), (D46, "Điều 46. Hiệu lực", C5), (D47, "Điều 47. Chuyển tiếp", C5),
            ("P022-L001", "Điều 48. Thi hành", C5), ("P022-L006", "Điều 49. Trách nhiệm thi hành", C5),
            (Appendix, "Phụ lục", "ROOT"), (Part1, "I. LỜI CHỨNG", Appendix), (Intro, "Mẫu lời chứng bao gồm:", "NOT_HEADING"),
            (Child, "1. Lời chứng chứng thực bản sao từ bản chính", Part1), ("P024-L001", "2. Lời chứng chứng thực chữ ký", Part1),
            ("P027-L001", "II. MẪU SỔ CHỨNG THỰC", Appendix), ("P027-L002", "1. Bìa sổ", "P027-L001"),
            ("P028-L001", "2. Nội dung sổ", "P027-L001"), ("P028-L002", "a) Đề mục ngoài cây đã xác nhận", "NONE") ];
        var lines = rows.Select((r, i) => new SemanticSourceAtom(r.Alias, "id:" + r.Alias, i, int.Parse(r.Alias.AsSpan(1, 3)), i, 0, r.Text)).ToArray();
        var absent = missingStage is null ? new HashSet<string>() : new HashSet<string> { C3, D34, C5, D46 };
        var bound = lines.Where(l => l.Alias != Intro && !absent.Contains(l.Alias)).Select(l =>
            new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text, "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var states = lines.ToDictionary(l => l.Alias, l => l.Alias == Intro ? new HeadingAliasState("NOT_SELECT", "NOT_SELECT", null, "NOT_SELECTED")
            : absent.Contains(l.Alias) ? missingStage switch
            {
                "selection" => new HeadingAliasState("UNRESOLVED", "UNRESOLVED", null, "UNRESOLVED"),
                "binding" => new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"),
                _ => new HeadingAliasState("SELECT", "UNRESOLVED", "UNRESOLVED", "UNRESOLVED")
            } : new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"));
        return (bound, new("{}", new Dictionary<string, string>()) { Source = new(lines, states, new string('a', 64)) },
            rows.Where(r => r.Alias != Intro).ToDictionary(r => r.Alias, r => r.Parent));
    }

    [Fact]
    public async Task Chapters_and_appendix_depths_come_only_from_independently_verified_model_relations()
    {
        var (bound, evidence, parents) = Fixture();
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!);
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        var hierarchy = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).ToDictionary(h => h.SourceId[3..]);
        Assert.Equal(1, hierarchy[C3].Level);
        Assert.Equal(2, hierarchy[D35].Level);
        Assert.Equal("id:" + C3, hierarchy[D35].ParentSourceId);
        Assert.Equal("id:" + C5, hierarchy[D47].ParentSourceId);
        Assert.Equal("id:P008-L013", hierarchy["P011-L001"].ParentSourceId);
        Assert.Equal(3, hierarchy["P011-L003"].Level);
        Assert.Equal(3, hierarchy["P012-L028"].Level);
        Assert.Equal(1, hierarchy[Appendix].Level);
        Assert.Equal(2, hierarchy[Part1].Level);
        Assert.Equal(3, hierarchy[Child].Level);
        Assert.Equal(2, hierarchy["P027-L001"].Level);
        Assert.Equal(3, hierarchy["P028-L001"].Level);
        Assert.Equal(HeadingHierarchyResolver.OutOfHierarchy, hierarchy["P028-L002"].Resolution);
        Assert.Equal(bound.Length, result.Observation.VerifiedCount);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        Assert.Equal(4, result.Observation.LogicalCalls);
        Assert.Equal(EvidenceHeadingPlacement.Version, result.Observation.Contract);
        Assert.DoesNotContain(Intro, result.Observation.SourceTrace.Select(t => t.Alias));
        Assert.Contains(result.Observation.ModelEvidence, e => e.Contains("logicalRequestSha256", StringComparison.Ordinal));
        Assert.DoesNotContain("ModelEvidence", JsonSerializer.Serialize(result.Observation));
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("verification")]
    [InlineData("binding")]
    public async Task Missing_ancestors_are_traced_and_children_are_not_promoted_or_put_under_the_previous_chapter(string missingStage)
    {
        var (bound, evidence, correct) = Fixture(missingStage);
        var wrong = new Dictionary<string, string>(correct) { [D35] = "ROOT", [D47] = C4, ["P022-L001"] = C4, ["P022-L006"] = C4 };
        var blocked = new Dictionary<string, string> { [D35] = C3, [D47] = C5, ["P022-L001"] = C5, ["P022-L006"] = C5 };
        using var model = new Model(p =>
        {
            var withheld = p["upstreamWithheld"]!.AsArray();
            Assert.Contains(withheld, l => l!["alias"]!.GetValue<string>() == C3);
            Assert.DoesNotContain(p["headings"]!.AsArray(), h => h!["alias"]!.GetValue<string>() == C3);
            var root = JsonNode.Parse(HierarchyTestProtocol.Reply(p.ToJsonString(), wrong)!)!;
            if (IsVerification(p)) foreach (var row in root["verdicts"]!.AsArray())
            {
                var alias = row!["alias"]!.GetValue<string>();
                if (!blocked.TryGetValue(alias, out var ancestor)) continue;
                row["decision"] = "MISSING_ANCESTOR";
                row["evidenceRefs"] = JsonSerializer.SerializeToNode(new[] { alias, ancestor });
                row["blockingAliases"] = JsonSerializer.SerializeToNode(new[] { ancestor });
            }
            return root.ToJsonString();
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        var hierarchy = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).ToDictionary(h => h.SourceId[3..]);
        Assert.All(blocked.Keys, alias => Assert.Equal(HeadingHierarchyResolver.Unresolved, hierarchy[alias].Resolution));
        Assert.All(blocked.Keys, alias => Assert.Null(hierarchy[alias].ParentSourceId));
        Assert.Equal(HeadingHierarchyResolver.ResolvedRoot, hierarchy[C4].Resolution);
        Assert.All(new[] { C3, D34, C5, D46 }, alias =>
        {
            var state = result.Observation.SourceTrace.Single(t => t.Alias == alias);
            Assert.Equal("not-bound", state.PlacementResolution);
            Assert.Equal(missingStage == "binding" ? "BINDING_REJECTED" : "UNRESOLVED", state.BindingStatus);
        });
        Assert.All(result.Observation.Decisions.Where(d => blocked.ContainsKey(d.Alias)), d =>
        { Assert.Equal("MISSING_ANCESTOR", d.VerificationStatus); Assert.Equal(new[] { blocked[d.Alias] }, d.BlockingAliases); });
        Assert.Equal(bound.Length, result.Headings.Count);
        Assert.Equal(0, result.Observation.RepairCount);
    }

    [Fact]
    public async Task Rejected_parent_is_repaired_once_and_its_descendant_chain_is_reverified_without_reextracting()
    {
        var (bound, evidence, correct) = Fixture();
        var wrong = new Dictionary<string, string>(correct) { [Part1] = "ROOT" };
        using var model = new Model(p =>
        {
            var stage = p["stage"]!.GetValue<string>();
            if (stage == "HIERARCHY_REPAIR") Assert.Equal(new[] { Part1 }, Aliases(p));
            var response = JsonNode.Parse(HierarchyTestProtocol.Reply(p.ToJsonString(), stage == "HIERARCHY_REPAIR" ? correct : wrong)!)!;
            if (stage == "HIERARCHY_VERIFY")
                foreach (var row in response["verdicts"]!.AsArray().Where(v => v!["alias"]!.GetValue<string>() == Part1)) row!["decision"] = "REJECT";
            if (stage == "HIERARCHY_REVERIFY") Assert.Equal(new[] { Part1, Child, "P024-L001" }, Aliases(p));
            return response.ToJsonString();
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        var h = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).Single(h => h.SourceId == "id:" + Child);
        Assert.Equal(3, h.Level);
        Assert.Equal(1, result.Observation.RepairCount);
        Assert.Equal(6, result.Observation.LogicalCalls);
        Assert.Equal(0, result.Observation.UnresolvedCount);
    }

    [Fact]
    public async Task Invalid_verdict_repairs_only_its_alias_and_preserves_independent_acceptances()
    {
        var (bound, evidence, parents) = Fixture();
        var seen = 0;
        using var model = new Model(p =>
        {
            var response = JsonNode.Parse(HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!)!;
            if (IsVerification(p) && Aliases(p).Contains(Part1))
            {
                var entry = response["verdicts"]!.AsArray().Single(r => r!["alias"]!.GetValue<string>() == Part1)!;
                if (Interlocked.Increment(ref seen) == 1) entry["evidenceRefs"] = new JsonArray(Part1); // missing parent evidence
                else Assert.Equal(new[] { Part1 }, Aliases(p));
            }
            return response.ToJsonString();
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        Assert.Equal(5, result.Observation.LogicalCalls);
        Assert.Contains(result.Observation.ModelEvidence, e => e.Contains("ACCEPT_EVIDENCE_INCOMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_search_discovers_not_selected_lines_without_promoting_them()
    {
        var (bound, evidence, parents) = Fixture();
        using var model = new Model(p =>
        {
            if (IsVerification(p) && p["evidenceResults"]!.AsArray().Count == 0)
                return JsonSerializer.Serialize(new { protocolVersion = EvidenceHeadingPlacement.Version,
                    requests = new[] { new { name = "search_source_text", query = "Mẫu lời chứng", offset = 0, limit = 12 } } });
            if (IsVerification(p))
            {
                var results = p["evidenceResults"]!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                Assert.Contains("Mẫu lời chứng bao gồm", results);
                Assert.Contains("NOT_SELECT", results);
                Assert.Contains(Intro, results);
            }
            return HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!;
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        Assert.DoesNotContain(result.Headings, h => h.Alias == Intro);
        Assert.Equal(6, result.Observation.LogicalCalls);
        Assert.Contains(result.Observation.ModelEvidence, e => e.Contains("search_source_text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Independent_batches_overlap_with_a_fixed_jobs_cap_and_two_normal_rounds()
    {
        var lines = Enumerable.Range(0, 40).Select(i => new SemanticSourceAtom("L" + i, "S" + i, i, 1, i, 0, "Section " + i)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text, "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var evidence = new HeadingPlacementEvidence("{}", new Dictionary<string, string>()) { Source = new(lines, new Dictionary<string, HeadingAliasState>()) };
        var active = 0; var maximum = 0;
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new Model(async (p, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); } while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            try
            {
                if (current >= 2) overlap.TrySetResult();
                await overlap.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                return HierarchyTestProtocol.Reply(p.ToJsonString())!;
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.InRange(maximum, 2, EvidenceHeadingPlacement.Jobs);
        Assert.Equal(6, result.Observation.LogicalCalls);
        Assert.Equal(40, result.Observation.VerifiedCount);
        Assert.Equal(bound.Select(h => h.Alias), result.Headings.Select(h => h.Alias));
    }

    [Fact]
    public void An_unresolved_ancestor_chain_cannot_supply_depth_to_children()
    {
        var (bound, _, _) = Fixture();
        var chapter = bound.Single(h => h.Alias == C3);
        var article = bound.Single(h => h.Alias == D35) with { RelationHints = ["parent-node:" + C3] };
        var derived = HeadingHierarchyResolver.DeriveHierarchyFromModelRelations([chapter, article]);
        Assert.All(derived, h => Assert.Equal(HeadingHierarchyResolver.Unresolved, h.Resolution));
        Assert.Null(derived[1].ParentSourceId);
    }

    [Theory]
    [InlineData("unknown-reference")]
    [InlineData("missing-blocker")]
    [InlineData("bound-blocker")]
    [InlineData("accept-with-blocker")]
    public async Task Invalid_evidence_cannot_release_a_relation_or_invent_a_missing_ancestor(string mode)
    {
        var (bound, evidence, parents) = Fixture();
        using var model = new Model(p =>
        {
            var response = JsonNode.Parse(HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!)!;
            if (IsVerification(p)) foreach (var row in response["verdicts"]!.AsArray().Where(r => r!["alias"]!.GetValue<string>() == Part1))
            {
                if (mode == "unknown-reference") row!["evidenceRefs"] = new JsonArray("UNKNOWN");
                if (mode is "missing-blocker" or "bound-blocker") row!["decision"] = "MISSING_ANCESTOR";
                if (mode == "bound-blocker") row!["blockingAliases"] = new JsonArray(Appendix);
                if (mode == "accept-with-blocker") row!["blockingAliases"] = new JsonArray(Intro);
            }
            return response.ToJsonString();
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.Equal(bound.Length, result.Headings.Count);
        var part = result.Observation.Decisions.Single(d => d.Alias == Part1);
        Assert.Equal("NO_VALID_VERDICT", part.VerificationStatus);
        Assert.Equal(HeadingHierarchyResolver.Unresolved, part.Resolution);
        var child = result.Observation.Decisions.Single(d => d.Alias == Child);
        Assert.Equal("ACCEPT", child.VerificationStatus);
        Assert.Equal("ancestor-chain-unresolved", child.ResolutionBasis);
        Assert.Equal(0, result.Observation.RepairCount);
        Assert.Equal(6, result.Observation.LogicalCalls);
    }

    [Fact]
    public async Task Repeated_semantic_rejection_is_bounded_and_retains_the_heading_for_review()
    {
        var (bound, evidence, parents) = Fixture();
        using var model = new Model(p =>
        {
            var response = JsonNode.Parse(HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!)!;
            if (IsVerification(p)) foreach (var row in response["verdicts"]!.AsArray().Where(r => r!["alias"]!.GetValue<string>() == D47)) row!["decision"] = "REJECT";
            return response.ToJsonString();
        });
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.Equal(1, result.Observation.RepairCount);
        Assert.Equal(6, result.Observation.LogicalCalls);
        Assert.Equal("model-rejected", result.Observation.Decisions.Single(d => d.Alias == D47).ResolutionBasis);
        Assert.Equal(bound.Length, result.Headings.Count);
    }

    [Fact]
    public async Task Large_style_payloads_are_not_repeated_for_every_heading_and_every_upstream_gap()
    {
        var lines = Enumerable.Range(0, 1055).Select(i => new SemanticSourceAtom("L" + i, "S" + i, i, 1 + i / 32, i, 0,
            "Nguồn văn bản có dấu và ngữ cảnh của tài liệu " + i)).ToArray();
        var selected = lines.Where((_, i) => i % 9 == 0).ToArray();
        var selectedAliases = selected.Select(l => l.Alias).ToHashSet();
        var withheld = lines.Where(l => !selectedAliases.Contains(l.Alias)).Take(77).Select(l => l.Alias).ToHashSet();
        var bound = selected.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text, "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var states = lines.ToDictionary(l => l.Alias, l => selectedAliases.Contains(l.Alias) ? new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING")
            : withheld.Contains(l.Alias) ? new HeadingAliasState("SELECT", "UNRESOLVED", null, "UNRESOLVED") : new HeadingAliasState("NOT_SELECT", "NOT_SELECT", null, "NOT_SELECTED"));
        var styles = lines.ToDictionary(l => l.Alias, _ => JsonSerializer.Serialize(new { measuredDetails = new string('x', 1000) }));
        var evidence = new HeadingPlacementEvidence("{}", styles) { Source = new(lines, states) };
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString())!);
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.Equal(0, result.Observation.UnresolvedCount);
        Assert.Equal(2 * ((bound.Length + EvidenceHeadingPlacement.BatchSize - 1) / EvidenceHeadingPlacement.BatchSize), result.Observation.LogicalCalls);
        Assert.DoesNotContain(result.Observation.ModelEvidence, e => e.Contains("REQUEST_OVER_CAP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transport_cancellation_propagates_and_unbound_upstream_trace_needs_no_provider_call()
    {
        var (bound, evidence, _) = Fixture("verification");
        using var cts = new CancellationTokenSource();
        using var model = new Model((_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(""); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HeadingParentResolver.PlaceWithObservationAsync(bound, model, cts.Token, evidence));
        using var noCall = new Model(_ => throw new InvalidOperationException("must not call"));
        var empty = await HeadingParentResolver.PlaceWithObservationAsync([], noCall, CancellationToken.None, evidence);
        Assert.Equal(0, empty.Observation.LogicalCalls);
        Assert.Contains(empty.Observation.SourceTrace, s => s.Alias == C3 && s.PlacementResolution == "not-bound");
    }

    static bool IsVerification(JsonNode p) => p["stage"]!.GetValue<string>() is "HIERARCHY_VERIFY" or "HIERARCHY_REVERIFY";
    static string[] Aliases(JsonNode p) => p["toPlace"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();

    sealed class Model(Func<JsonNode, CancellationToken, Task<string>> script) : IInferenceTransport
    {
        public Model(Func<JsonNode, string> sync) : this((p, _) => Task.FromResult(sync(p))) { }
        public string ModelName => "offline scripted hierarchy";
        public int ContextSize => 100_000;
        public string RuntimeDescription => "No API calls";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        { Assert.Contains("JSON", systemPrompt); return script(JsonNode.Parse(userMessage)!, ct); }
        public void Dispose() { }
    }
}
