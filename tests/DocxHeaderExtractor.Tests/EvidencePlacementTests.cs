using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Authority;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.DocumentProcessing.Hierarchy;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Intake;

namespace DocxHeaderExtractor.Tests;

public sealed class EvidencePlacementTests
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
    public async Task Source_grounded_parent_proposals_are_authorized_without_semantic_verifier_calls()
    {
        var (bound, evidence, parents) = Fixture();
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!);
        var result = await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        var hierarchy = HierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).ToDictionary(h => h.SourceId[3..]);
        Assert.Equal(1, hierarchy[C3].Level);
        Assert.Equal(2, hierarchy[D35].Level);
        Assert.Equal("id:" + C3, hierarchy[D35].ParentSourceId);
        Assert.Equal(3, hierarchy[Child].Level);
        Assert.Equal(HierarchyResolver.OutOfHierarchy, hierarchy["P028-L002"].Resolution);
        Assert.Equal(0, result.Observation.VerifiedCount);
        Assert.All(result.Observation.Decisions, d => Assert.Equal("NOT_RUN", d.VerificationStatus));
        Assert.All(result.Observation.Calls, c => Assert.Equal("HIERARCHY_PLACE", c.Phase));
        Assert.Equal(result.Observation.Calls.Count, result.Observation.LogicalCalls);
        Assert.Equal(EvidencePlacement.Version, result.Observation.Contract);
        Assert.DoesNotContain("ModelEvidence", JsonSerializer.Serialize(result.Observation));
    }

    [Fact]
    public async Task Structurally_valid_but_semantically_wrong_proposal_is_not_semantically_vetoed()
    {
        var (bound, evidence, parents) = Fixture();
        parents[D47] = D46; // A same-chapter but semantically false parent is still a legal earlier bound node.
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!);
        var result = await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        var hierarchy = HierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).Single(h => h.SourceId == "id:" + D47);
        Assert.Equal("id:" + D46, hierarchy.ParentSourceId);
        Assert.Equal(3, hierarchy.Level);
        Assert.Equal("NOT_RUN", result.Observation.Decisions.Single(d => d.Alias == D47).VerificationStatus);
    }

    [Fact]
    public async Task CSharp_rejects_a_forward_parent_and_keeps_the_heading_unresolved()
    {
        var (bound, evidence, parents) = Fixture();
        parents[C3] = D35;
        using var model = new Model(p => HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!);
        var result = await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        var chapter = result.Headings.Single(h => h.Alias == C3);
        Assert.DoesNotContain(chapter.RelationHints, h => h.StartsWith("parent-node:", StringComparison.Ordinal));
        Assert.Equal(HierarchyResolver.Unresolved,
            HierarchyResolver.DeriveHierarchyFromModelRelations([chapter]).Single().Resolution);
        Assert.Contains(result.Observation.Calls.SelectMany(c => c.ProtocolErrors), f => f == "PARENT_INVALID");
        Assert.Contains(result.Observation.Failures, f => f.Contains("NO_VALID_PARENT_PROPOSAL", StringComparison.Ordinal));
        Assert.All(result.Observation.Calls, c => Assert.Equal("HIERARCHY_PLACE", c.Phase));
    }

    // 023: a row that names a later heading as its parent was refused three times with the same answer, because the next round said only
    // PARENT_INVALID. The next packet now names the refused alias, its ordinal and what was wrong with its parent.
    [Fact]
    public async Task A_refused_parent_is_named_by_alias_in_the_next_round()
    {
        var (bound, evidence, parents) = Fixture();
        parents[C3] = D35;   // a heading later in the source
        var packets = new List<JsonNode>();
        using var model = new Model(p =>
        {
            packets.Add(p.DeepClone());
            return HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!;
        });

        var result = await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);

        // The two batches run side by side: take the packets that carry the refused row.
        var batch = packets.Where(p => p["toPlace"]!.AsArray().Any(a => a!.GetValue<string>() == C3)).ToArray();
        Assert.Null(batch[0]["protocolDetails"]);
        var details = batch[1]["protocolDetails"]!.AsArray().Select(d => d!.GetValue<string>()).ToArray();
        Assert.Single(details);
        Assert.StartsWith(C3 + " ", details[0], StringComparison.Ordinal);
        Assert.Contains(D35, details[0], StringComparison.Ordinal);
        Assert.Contains("earlier in the source", details[0], StringComparison.Ordinal);
        Assert.Contains(result.Observation.Calls, c => c.ProtocolErrors.Contains("PARENT_INVALID"));
    }

    // Batches run in order, and a later batch is told the parents and levels the earlier ones decided, as a fact.
    [Fact]
    public async Task Later_batches_carry_the_parents_and_levels_the_earlier_batches_decided()
    {
        var (bound, evidence, parents) = Fixture();
        var packets = new List<JsonNode>();
        using var model = new Model(p =>
        {
            packets.Add(p.DeepClone());
            return HierarchyTestProtocol.Reply(p.ToJsonString(), parents)!;
        });

        await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);

        Assert.Equal(2, packets.Count);   // 25 targets in batches of 16 and 9
        Assert.Empty(packets[0]["decidedHeadings"]!.AsArray());
        var decided = packets[1]["decidedHeadings"]!.AsArray().ToDictionary(d => d!["alias"]!.GetValue<string>(), d => d!);
        Assert.Equal(16, decided.Count);
        Assert.Equal(1, decided["P008-L013"]["level"]!.GetValue<int>());                 // Chương II under ROOT
        Assert.Equal(2, decided["P011-L001"]["level"]!.GetValue<int>());                 // Mục 3 under Chương II
        Assert.Equal(2, decided[D35]["level"]!.GetValue<int>());                         // Điều 35 under Chương III
        Assert.Equal(C3, decided[D35]["parent"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_parent_proposal_fails_closed_without_a_verifier_or_repair_call()
    {
        var (bound, evidence, _) = Fixture();
        using var model = new Model(_ => "not json");
        var result = await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);
        Assert.Equal(bound.Length, result.Headings.Count);
        Assert.All(result.Observation.Calls, c => Assert.Equal("HIERARCHY_PLACE", c.Phase));
        Assert.Equal(0, result.Observation.VerifiedCount);
        Assert.All(result.Observation.Decisions.Where(d => d.ProposedParentAlias is null),
            d => Assert.Equal(HierarchyResolver.Unresolved, d.Resolution));
    }

    // Two fields with the wrong names pass the field-count checks; they must be a protocol error, not an exception that ends the extraction.
    [Theory]
    [InlineData("row", "ROW_FIELDS_INVALID")]
    [InlineData("root", "ROOT_KEYS_INVALID")]
    public async Task A_misnamed_key_is_a_protocol_error_and_the_headings_stay_unresolved(string where, string code)
    {
        var (bound, evidence, _) = Fixture();
        using var model = new Model(p =>
        {
            var rows = p["toPlace"]!.AsArray().Select(a => where == "row"
                ? (object)new { alias = a!.GetValue<string>(), parentAlias = "ROOT" }
                : new { alias = a!.GetValue<string>(), parent = "ROOT" }).ToArray();
            return where == "row"
                ? JsonSerializer.Serialize(new { protocolVersion = EvidencePlacement.Version, placements = rows })
                : JsonSerializer.Serialize(new { protocolVersion = EvidencePlacement.Version, rows });
        });

        var result = await ParentResolver.PlaceWithObservationAsync(bound, model, CancellationToken.None, evidence);

        Assert.Equal(bound.Length, result.Headings.Count);
        Assert.Contains(result.Observation.Calls.SelectMany(c => c.ProtocolErrors), e => e == code);
        Assert.Contains(result.Observation.Failures, f => f.Contains("NO_VALID_PARENT_PROPOSAL", StringComparison.Ordinal));
        Assert.All(result.Headings, h => Assert.DoesNotContain(h.RelationHints, r => r.StartsWith("parent-node:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Empty_input_needs_no_model_call()
    {
        var (_, evidence, _) = Fixture();
        using var model = new Model(_ => throw new InvalidOperationException("must not call"));
        var result = await ParentResolver.PlaceWithObservationAsync([], model, CancellationToken.None, evidence);
        Assert.Equal(0, result.Observation.LogicalCalls);
        Assert.Equal(0, result.Observation.VerifiedCount);
    }

    sealed class Model(Func<JsonNode, string> script) : IStructuredInferenceTransport
    {
        public string ModelName => "offline scripted proposal";
        public int ContextSize => 100_000;
        public string RuntimeDescription => "No API calls";
        public int SharedPrefixTokens => 0;
        public Task<StructuredInferenceReply> CompleteStructuredAsync(string systemPrompt, string userMessage, int maxOutputTokens, CancellationToken ct = default) =>
            Task.FromResult(new StructuredInferenceReply(script(JsonNode.Parse(userMessage)!), "stop", maxOutputTokens));
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            Task.FromResult(script(JsonNode.Parse(userMessage)!));
        public void Dispose() { }
    }
}
