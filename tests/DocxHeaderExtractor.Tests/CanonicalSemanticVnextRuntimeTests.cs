using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalSemanticVnextRuntimeTests
{
    [Fact]
    public void Prompt_cannot_change_canonical_heading_membership_but_can_change_projection()
    {
        var graph = Graph(
            new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION"),
            new CanonicalSemanticProposal("S0002", true, "Heading", SemanticRole: "SECTION", Scope: "continuation"));

        var all = CanonicalSemanticProjection.Project(graph, new SemanticIntent("all-true-headings", false));
        var outline = CanonicalSemanticProjection.Project(graph, new SemanticIntent("main-document-outline", true));

        Assert.Equal(2, all.Count);
        Assert.Single(outline);
        Assert.Equal(2, graph.Occurrences.Count);
    }

    [Fact]
    public void Repeat_and_continuation_survive_even_when_semantic_node_already_exists()
    {
        var graph = Graph(
            new CanonicalSemanticProposal("S0001", true, "STATEMENTS OF CASH FLOWS", SemanticRole: "SECTION"),
            new CanonicalSemanticProposal("S0002", true, "STATEMENTS OF CASH FLOWS", SemanticRole: "SECTION", Scope: "continuation"));

        Assert.Equal(2, graph.Occurrences.Count);
        Assert.Equal("CONTINUATION", graph.Occurrences[1].OccurrenceKind);
        Assert.Single(CanonicalSemanticProjection.Project(graph, new SemanticIntent("outline", true)));
    }

    [Fact]
    public void Candidate_miss_does_not_cap_owned_semantic_discovery()
    {
        Assert.True(SemanticCandidatePolicy.CanAcceptOwnedOccurrence("S0001", [
            new SemanticCandidateAttentionHint("S0001", false, "heuristic-miss")
        ]));
    }

    [Fact]
    public void Unknown_and_out_of_owned_aliases_fail_closed()
    {
        var catalog = Catalog(("p1", "Heading"), ("p2", "Other"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var unknown = CanonicalSemanticExactBinder.Bind(
            [new CanonicalSemanticProposal("S9999", true, "Heading")], aliases, out var unknownAudit);
        var overlapOnly = CanonicalSemanticExactBinder.Bind(
            [new CanonicalSemanticProposal("S0002", true, "Other")], aliases, new HashSet<string>(["S0001"]), out var ownedAudit);

        Assert.Empty(unknown);
        Assert.Equal(CanonicalSemanticBindingStatus.UnknownAlias, unknownAudit[0].Status);
        Assert.Empty(overlapOnly);
        Assert.Equal(CanonicalSemanticBindingStatus.OutOfOwnedSegment, ownedAudit[0].Status);
    }

    [Fact]
    public void Non_verbatim_and_ambiguous_text_fail_closed()
    {
        var catalog = Catalog(("p1", "Heading Heading"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Heading"),
            new CanonicalSemanticProposal("S0001", true, "Not in source")
        ], aliases, out var audit);

        Assert.Empty(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.AmbiguousBinding, audit[0].Status);
        Assert.Equal(CanonicalSemanticBindingStatus.NonVerbatimText, audit[1].Status);
    }

    [Fact]
    public void Multipart_heading_binds_every_ordered_alias_and_part()
    {
        var catalog = Catalog(("p1", "SESSION V:"), ("p2", "Current Research (Cont'd)"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var proposal = new CanonicalSemanticProposal(
            "S0001", true, null, ["SESSION V:", "Current Research (Cont'd)"], "SECTION", SourceAliases: ["S0001", "S0002"]);

        var bound = CanonicalSemanticExactBinder.Bind([proposal], aliases, out var audit);

        var item = Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, audit[0].Status);
        Assert.Equal(2, item.Parts.Count);
        Assert.Equal(["S0001", "S0002"], item.Parts.Select(part => part.Alias));
    }

    [Fact]
    public void Contract_validator_rejects_numeric_fields_without_reading_gold()
    {
        using var json = JsonDocument.Parse("{\"headings\":[{\"sourceAlias\":\"S0001\",\"isHeading\":true,\"verbatimText\":\"H\",\"start\":0}]}");

        var issues = CanonicalSemanticContractValidator.ValidateJson(json.RootElement);

        Assert.Contains(issues, issue => issue.Code == "NUMERIC_COORDINATE_REJECTED");
        Assert.DoesNotContain("start", JsonSerializer.Serialize(CanonicalSemanticContract.Schema()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hard_binding_validator_checks_utf16_source_text_and_hash_lineage()
    {
        var catalog = Catalog(("p1", "😀 Heading"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Heading")
        ], aliases, out _);

        var result = CanonicalSemanticHardBindingValidator.Validate(bound, aliases, "abc", "abc");

        Assert.True(result.IsValid);
        Assert.Equal(3, bound[0].Start);
        Assert.Equal(10, bound[0].End);
    }

    [Fact]
    public void Hard_binding_validator_fails_closed_on_source_hash_mismatch()
    {
        var catalog = Catalog(("p1", "Heading"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Heading")
        ], aliases, out _);

        var result = CanonicalSemanticHardBindingValidator.Validate(bound, aliases, "actual", "expected");

        Assert.False(result.IsValid);
        Assert.Contains("SOURCE_HASH_MISMATCH", result.Errors);
    }

    [Fact]
    public void Global_resolution_accepts_explicit_parent_and_level_without_inferring_them_from_text()
    {
        var first = new CanonicalSemanticProposal("S0001", true, "Chapter", SemanticRole: "CHAPTER");
        var second = new CanonicalSemanticProposal("S0002", true, "Article", SemanticRole: "ARTICLE",
            RelationHints: ["parent-node:semantic-node:0001", "level:2"]);
        var graph = Graph(first, second);

        Assert.Equal(2, graph.Occurrences.Count);
        Assert.Equal(2, graph.Occurrences[1].Level);
        Assert.Equal(graph.Occurrences[0].OccurrenceId, graph.Occurrences[1].ParentOccurrenceId);
    }

    [Fact]
    public void V4_registry_artifacts_are_semantic_only_and_have_full_authority_totals()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "a99-closed-loop", "canonical-semantic-gold-vnext"));
        if (!File.Exists(Path.Combine(root, "freeze-registry.v4.checked.json"))) return;

        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "freeze-registry.v4.checked.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "inventory.v1.json")));
        Assert.Equal(4, registry.RootElement.GetProperty("registryRevision").GetInt32());
        Assert.Equal(1503, registry.RootElement.GetProperty("summary").GetProperty("semanticHeadingTotalAcrossTrackedSources").GetInt32());
        Assert.Equal(13, inventory.RootElement.GetProperty("frozenSemanticVnext").GetInt32());
        Assert.Equal(0, inventory.RootElement.GetProperty("exactOccurrenceFrozen").GetInt32());
        Assert.Equal(13, Directory.GetFiles(Path.Combine(root, "semantic"), "*.semantic-freeze.v1.json").Length);
        Assert.False(Directory.Exists(Path.Combine(root, "occurrence")));
        Assert.False(Directory.Exists(Path.Combine(root, "bindings")));
    }

    [Fact]
    public void Semantic_cache_key_is_prompt_independent_and_projection_does_not_mutate_graph()
    {
        var first = CanonicalSemanticGraphCacheKey.Create("ABC", "schema", "model", "extractor");
        var second = CanonicalSemanticGraphCacheKey.Create("abc", "schema", "model", "extractor");
        var graph = Graph(new CanonicalSemanticProposal("S0001", true, "Heading"));
        var before = graph.Occurrences.Count;

        _ = CanonicalSemanticProjection.Project(graph, new SemanticIntent("collapse", true));

        Assert.Equal(first, second);
        Assert.Equal(before, graph.Occurrences.Count);
    }

    [Fact]
    public void Three_layer_context_and_optional_visual_route_are_explicit()
    {
        var packet = SemanticContextPacker.Pack(["target"], ["local"], ["global"]);

        Assert.Equal(["target"], packet.TargetEvidence);
        Assert.Equal(["local"], packet.LocalContext);
        Assert.Equal(["global"], packet.GlobalContext);
        Assert.True(SemanticVisualEscalation.IsOptional);
    }

    [Fact]
    public void Production_entry_point_runs_the_vnext_stage_order_without_gold()
    {
        var catalog = Catalog(("p1", "😀 Heading"), ("p2", "Other"));
        var result = CanonicalSemanticProductionEntryPoint.Run(new(
            catalog,
            [new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION")],
            "source-hash",
            [new CanonicalSemanticPageEvidence("P0001", true, 0, "docx-text")],
            [new SemanticCandidateAttentionHint("S0001", false, "heuristic-miss")],
            ["Heading"], ["local"], ["global"]));

        Assert.Equal(CanonicalSemanticModality.Text, result.ModalityProfile.DocumentModality);
        Assert.Single(result.TextPipeline.BoundHeadings);
        Assert.Single(result.CanonicalOccurrences);
        Assert.Single(result.UnifiedOccurrences);
        Assert.Equal("SOURCE_IDENTITY", result.StageLedger[0].Stage);
        Assert.Equal("TASK_PROJECTION", result.StageLedger[^1].Stage);
        Assert.All(result.StageLedger, entry => Assert.NotEqual("LOSS_OBSERVED", entry.Status));
    }

    [Fact]
    public void Production_entry_point_recovers_and_binds_visual_evidence_without_text_offsets()
    {
        var result = CanonicalSemanticProductionEntryPoint.Run(new(
            Catalog(("p1", "text")),
            [],
            "source-hash",
            [new CanonicalSemanticPageEvidence("P0001", false, 1, "docx-image")],
            [], [], [], [],
            [new CanonicalSemanticVisualBlock(
                "P0001", 1, "image-hash",
                new CanonicalSemanticVisualBoundingBox(0, 0, 10, 10), "Visual heading")],
            [new CanonicalSemanticVisualProposal("V0001", true, "Visual heading", "SECTION")]));

        Assert.Equal(CanonicalSemanticModality.VisualOnly, result.ModalityProfile.DocumentModality);
        Assert.Single(result.VisualOccurrences);
        Assert.Single(result.VisualHeadings);
        Assert.Single(result.UnifiedOccurrences);
        Assert.Equal("VISUAL_REGION", result.VisualHeadings[0].Binding.CoordinateSystem);
    }

    [Fact]
    public async Task Async_production_entry_point_owns_text_inference_before_binding()
    {
        var result = await CanonicalSemanticProductionEntryPoint.RunAsync(new(
            Catalog(("p1", "😀 Heading")),
            null,
            "source-hash",
            [new CanonicalSemanticPageEvidence("P0001", true, 0, "docx-text")],
            [], ["Heading"], [], ["source-context"],
            DocumentId: "DOC-TEST"),
            new FakeTextModel());

        Assert.Equal(1, result.TextModelCalls);
        Assert.Equal("S0001", result.ModelProposals[0].SourceAlias);
        Assert.Single(result.TextPipeline.BoundHeadings);
        Assert.Equal(3, result.TextPipeline.BoundHeadings[0].Start);
    }

    [Fact]
    public async Task Async_production_entry_point_owns_visual_recovery_before_region_binding()
    {
        var result = await CanonicalSemanticProductionEntryPoint.RunAsync(new(
            Catalog(("p1", "text")),
            null,
            "source-hash",
            [new CanonicalSemanticPageEvidence("P0001", false, 1, "docx-image")],
            [], [], [], [],
            VisualPages: [new CanonicalSemanticVisualPageEvidence("P0001", "image-hash", [1, 2, 3], 10, 10)],
            DocumentId: "DOC-VISUAL-TEST"),
            new FakeTextModel(), new FakeVisualModel());

        Assert.Equal(1, result.TextModelCalls);
        Assert.Equal(1, result.VisualModelCalls);
        Assert.Single(result.VisualOccurrences);
        Assert.Single(result.VisualHeadings);
        Assert.True(CanonicalSemanticVisualBindingValidator.IsValid(result.VisualHeadings[0], result.VisualOccurrences));
    }

    private sealed class FakeTextModel : ICanonicalSemanticTextModel
    {
        public Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalSemanticTextInferenceResult(
                [new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION")],
                new CanonicalSemanticInferenceTelemetry("fake", "stop")));
    }

    private sealed class FakeVisualModel : ICanonicalSemanticVisualModel
    {
        public Task<CanonicalSemanticVisualInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            IReadOnlyList<CanonicalSemanticVisualOccurrence> recoveredOccurrences,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var block = new CanonicalSemanticVisualBlock("P0001", 0, "image-hash",
                new CanonicalSemanticVisualBoundingBox(0, 0, 10, 10), "Visual heading");
            return Task.FromResult(new CanonicalSemanticVisualInferenceResult(
                [block], [new CanonicalSemanticVisualProposal("V0001", true, "Visual heading", "SECTION")],
                new CanonicalSemanticInferenceTelemetry("fake", "stop")));
        }
    }

    private static CanonicalSemanticGraph Graph(params CanonicalSemanticProposal[] proposals)
    {
        var units = proposals.Select((proposal, index) => (
            "p" + (index + 1),
            proposal.VerbatimText ?? proposal.VerbatimParts?.FirstOrDefault() ?? "Heading"));
        var result = CanonicalSemanticPipeline.Run(Catalog(units.ToArray()), proposals, "hash");
        return result.Graph;
    }

    private static DocumentSourceCatalog Catalog(params (string Id, string Text)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id, index + 1, unit.Text,
            new SourceAnchor { SourceType = "test", ParagraphId = unit.Id },
            new StructuralSpan(0, unit.Text.Length))));
}
