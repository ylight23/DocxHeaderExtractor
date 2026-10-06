using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class CanonicalSemanticVnextRuntimeTests
{
    [Fact]
    public void Repeat_and_continuation_survive_even_when_semantic_node_already_exists()
    {
        var graph = Graph(
            new CanonicalSemanticProposal("S0001", true, "STATEMENTS OF CASH FLOWS", SemanticRole: "SECTION", RelationHints: ["same-node:cash-flows"]),
            new CanonicalSemanticProposal("S0002", true, "STATEMENTS OF CASH FLOWS", SemanticRole: "SECTION", Scope: "continuation", RelationHints: ["same-node:cash-flows"]));

        Assert.Equal(2, graph.Occurrences.Count);
        Assert.Equal("CONTINUATION", graph.Occurrences[1].OccurrenceKind);
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
    public async Task Production_contract_validation_runs_before_binder()
    {
        var result = await CanonicalSemanticTextProductionEntryPoint.RunAsync(new(
            Catalog(("p1", "Heading")), null, "source-hash", null, null, null,
            [], [], []), new InvalidProposalTextModel());

        Assert.Equal(2, result.ContractInvalidProposalCount);
        Assert.Contains(result.ContractIssues, item => item.Code == "UNKNOWN_ALIAS");
        Assert.Contains(result.ContractIssues, item => item.Code == "NON_VERBATIM_TEXT");
        Assert.Empty(result.TextPipeline.BoundHeadings);
        Assert.Empty(result.TextPipeline.BindingObservations);
        Assert.Equal(1, result.TextModelCalls);
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
    public void Duplicate_text_with_unique_occurrence_ordinal_binds_that_occurrence()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(("p1", "A Results B Results C")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Results", Occurrence: 2)
        ], aliases, out var audit);

        var item = Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, audit[0].Status);
        Assert.Equal(12, item.Start);
        Assert.Equal(19, item.End);
    }

    [Fact]
    public void Duplicate_text_with_unique_exact_context_binds_that_occurrence()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(("p1", "prefix-A Results; prefix-B Results")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Results", LeftExactContext: "prefix-B ")
        ], aliases, out var audit);

        var item = Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, audit[0].Status);
        Assert.Equal(27, item.Start);
        Assert.Equal(34, item.End);
    }

    [Fact]
    public void Duplicate_text_with_non_unique_exact_context_is_ambiguous()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(("p1", "A Results shared; B Results shared")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Results", RightExactContext: " shared")
        ], aliases, out var audit);

        Assert.Empty(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.AmbiguousBinding, audit[0].Status);
        Assert.Equal("AMBIGUOUS_BINDING", audit[0].Reason);
    }

    [Fact]
    public void Duplicate_text_with_non_matching_exact_context_is_rejected()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(("p1", "A Results B Results C")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Results", RightExactContext: " missing")
        ], aliases, out var audit);

        Assert.Empty(bound);
        Assert.NotEqual(CanonicalSemanticBindingStatus.Bound, audit[0].Status);
    }

    [Fact]
    public void Single_exact_text_occurrence_binds_unchanged()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(("p1", "A Results B")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0001", true, "Results")
        ], aliases, out var audit);

        Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, audit[0].Status);
    }

    [Fact]
    public void Identical_text_in_two_aliases_binds_only_requested_alias()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(("p1", "Results"), ("p2", "Results")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal("S0002", true, "Results")
        ], aliases, out var audit);

        var item = Assert.Single(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.Bound, audit[0].Status);
        Assert.Equal("p2", item.SourceId);
    }

    [Fact]
    public void Ambiguous_part_prevents_composite_heading_binding()
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(Catalog(
            ("p1", "A Results B Results"),
            ("p2", "Tail")));

        var bound = CanonicalSemanticExactBinder.Bind([
            new CanonicalSemanticProposal(
                "S0001", true, null,
                VerbatimParts: ["Results", "Tail"],
                SourceAliases: ["S0001", "S0002"])
        ], aliases, out var audit);

        Assert.Empty(bound);
        Assert.Equal(CanonicalSemanticBindingStatus.AmbiguousBinding, audit[0].Status);
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
    public void Three_layer_context_is_explicit()
    {
        var packet = SemanticContextPacker.Pack(["target"], ["local"], ["global"]);

        Assert.Equal(["target"], packet.TargetEvidence);
        Assert.Equal(["local"], packet.LocalContext);
        Assert.Equal(["global"], packet.GlobalContext);
    }

    [Fact]
    public void Text_production_entry_point_binds_without_gold()
    {
        var catalog = Catalog(("p1", "😀 Heading"), ("p2", "Other"));
        var result = CanonicalSemanticTextProductionEntryPoint.Run(new(
            catalog,
            [new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION")],
            "source-hash", null, null, null,
            ["Heading"], ["local"], ["global"]));

        Assert.Single(result.TextPipeline.BoundHeadings);
        Assert.Single(result.TextPipeline.Graph.Occurrences);
        Assert.Equal(0, result.TextModelCalls);
        Assert.Empty(result.ContractIssues);
    }

    [Fact]
    public async Task Async_text_production_entry_point_owns_text_inference_before_binding()
    {
        var result = await CanonicalSemanticTextProductionEntryPoint.RunAsync(new(
            Catalog(("p1", "😀 Heading")),
            null,
            "source-hash", null, "DOC-TEST", null,
            ["Heading"], [], ["source-context"]),
            new FakeTextModel());

        Assert.Equal(1, result.TextModelCalls);
        Assert.Equal("S0001", result.ModelProposals[0].SourceAlias);
        Assert.Single(result.TextPipeline.BoundHeadings);
        Assert.Equal(3, result.TextPipeline.BoundHeadings[0].Start);
    }

    private sealed class FakeTextModel : ICanonicalSemanticTextModel
    {
        public Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticTextInferenceInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalSemanticTextInferenceResult(
                [new CanonicalSemanticProposal("S0001", true, "Heading", SemanticRole: "SECTION")],
                new CanonicalSemanticInferenceTelemetry("fake", "stop")));
    }

    private sealed class InvalidProposalTextModel : ICanonicalSemanticTextModel
    {
        public Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticTextInferenceInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CanonicalSemanticTextInferenceResult([
                new CanonicalSemanticProposal("S9999", true, "Heading"),
                new CanonicalSemanticProposal("S0001", true, "Not in source")
            ], new("fake", "stop")));
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
