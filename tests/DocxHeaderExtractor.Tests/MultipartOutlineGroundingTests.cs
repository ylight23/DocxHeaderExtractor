extern alias WebApp;

using System.Text.Json;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Capabilities;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using WebApp::DocxHeaderExtractor.Web;

namespace DocxHeaderExtractor.Tests;

public sealed class MultipartOutlineGroundingTests
{
    private static string InputPath => TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf");
    [Fact]
    public async Task Vietnamese_multipart_survives_materializer_outline_harness_and_web_with_no_repair()
    {
        var execution = Fixture();
        var heading = Assert.Single(execution.Outline.Headings);
        // This is the old failure: the compatibility first span cannot contain the joined text.
        Assert.NotEqual(heading.Text, heading.OriginalText![heading.HeadingSpan!.Start..heading.HeadingSpan.End]);
        Assert.Equal(3, heading.SourceParts!.Count);
        Assert.Equal([0, 0, 1], heading.SourceParts.Select(p => p.SourceOrdinal));
        using var tool = new ExecutionTool(execution);
        var run = await Harness(tool).RunAsync(new DocumentAgentRequest(InputPath));
        Assert.Equal(1, tool.Calls);
        Assert.Equal(0, run.RepairAttempts);
        Assert.DoesNotContain(run.Trace, e => e.Stage == "repair");
        Assert.Same(execution, run.Execution);
        var web = WebPipelineProjection.Project(run, "pdf");
        var projected = Assert.Single(web.Headings);
        Assert.Equal(heading.Text, projected.Text);
        Assert.Equal(3, projected.Sources.Count);
        Assert.Equal(heading.SourceParts.Select(p => p.SourceId), projected.Sources.Select(p => p.SourceId));
        Assert.Equal(heading.SourceParts.Select(p => p.OriginalText[p.Span.Start..p.Span.End]),
            projected.Sources.Select(p => p.SelectedText));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("reordered")]
    [InlineData("wrong-id")]
    [InlineData("wrong-ordinal")]
    [InlineData("wrong-span")]
    [InlineData("wrong-source-text")]
    [InlineData("wrong-joined-text")]
    public async Task Corrupt_projection_fails_closed_without_semantic_retry_and_retains_evidence(string corruption)
    {
        var original = Fixture();
        var h = original.Outline.Headings.Single();
        var parts = h.SourceParts!.ToArray();
        switch (corruption)
        {
            case "missing": parts = parts[..2]; break;
            case "reordered": Array.Reverse(parts); break;
            case "wrong-id": parts[1] = parts[1] with { SourceId = "absent" }; break;
            case "wrong-ordinal": parts[1] = parts[1] with { SourceOrdinal = 2 }; break;
            case "wrong-span": parts[1] = parts[1] with { Span = new(0, 1) }; break;
            case "wrong-source-text": parts[1] = parts[1] with { OriginalText = "fabricated" }; break;
        }
        var bad = Copy(h, parts, corruption == "wrong-joined-text" ? "invented heading" : h.Text);
        var execution = WithHeading(original, bad);
        using var tool = new ExecutionTool(execution);
        var error = await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            Harness(tool).RunAsync(new DocumentAgentRequest(InputPath)));
        Assert.Contains(error.Issues, issue => issue.Code == "outline_projection_inconsistent");
        Assert.Equal(1, tool.Calls);
        Assert.DoesNotContain(error.Trace, e => e.Stage == "repair");
        Assert.Same(execution, error.Execution);
        Assert.Equal(original.Result.Structure, error.Execution!.Result.Structure);
        Assert.Equal(3, error.Execution.Result.Structure.Elements.Single().Sources.Count);
    }

    [Fact]
    public async Task Missing_all_runtime_parts_cannot_fall_back_to_single_source_validation()
    {
        var original = Fixture();
        using var tool = new ExecutionTool(WithHeading(original, Copy(original.Outline.Headings.Single(), null)));
        var error = await Assert.ThrowsAsync<AgentOutputValidationException>(() =>
            Harness(tool).RunAsync(new DocumentAgentRequest(InputPath)));
        Assert.Contains(error.Issues, issue => issue.Code == "outline_projection_inconsistent");
        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task Same_ordinal_text_and_span_with_distinct_source_ids_are_not_duplicate_headings()
    {
        var outline = new DocumentOutline { File = "synthetic.pdf", ParagraphCount = 1, Headings =
            new[] { "atom-a", "atom-b" }.Select(id => new HeadingRecord
            { Index = 0, SourceId = id, Text = "GIẤY MỜI", Level = 1, OriginalText = "GIẤY MỜI",
                HeadingSpan = new(0, 8) }).ToArray() };
        var validation = await new OutlineGroundingValidator().ValidateAsync(outline,
            new(new DocumentAgentRequest("synthetic.pdf"), Descriptor));
        Assert.True(validation.IsValid, string.Join(";", validation.Issues));
    }

    [Fact]
    public async Task Actual_missing_catalog_source_and_invalid_authority_span_are_not_accepted()
    {
        var original = Fixture();
        var missingCatalog = new DocumentSourceCatalog(original.Result.SourceCatalog.Units.Take(2));
        var missing = original with { Result = original.Result with { SourceCatalog = missingCatalog } };
        var result = await Validate(missing);
        Assert.Contains(result.Issues, i => i.Code == "heading_part_source_missing");

        var element = original.Result.Structure.Elements.Single();
        // Deliberately corrupt a previously valid span and the corresponding projection, not Gold.
        var sources = element.Sources.ToArray();
        sources[1] = sources[1] with { Span = new(0, 10000) };
        var parts = original.Outline.Headings.Single().SourceParts!.ToArray();
        parts[1] = parts[1] with { Span = new(0, 10000) };
        var invalid = WithHeading(original with { Result = original.Result with
            { Structure = ValidatedStructureFactory.Create([element with { Sources = sources }]) } },
            Copy(original.Outline.Headings.Single(), parts));
        result = await Validate(invalid);
        Assert.Contains(result.Issues, i => i.Code == "heading_part_not_grounded");
    }

    [Fact]
    public void Runtime_parts_do_not_change_outline_JSON_or_generic_authority_bytes()
    {
        var execution = Fixture();
        var heading = execution.Outline.Headings.Single();
        var before = JsonSerializer.Serialize(execution.Result.Structure);
        Assert.Equal(JsonSerializer.Serialize(Copy(heading, null, structuralId: null, partCount: 0)),
            JsonSerializer.Serialize(heading));
        Assert.DoesNotContain("SourceParts", JsonSerializer.Serialize(execution.Outline));
        Assert.Equal(before, JsonSerializer.Serialize(execution.Result.Structure));
    }

    private static ValueTask<AgentValidationResult> Validate(DocumentExtractionExecutionResult execution) =>
        new OutlineGroundingValidator().ValidateAsync(execution.Outline,
            new(new DocumentAgentRequest("synthetic.pdf"), Descriptor) { Execution = execution });

    private static DocumentExtractionExecutionResult Fixture()
    {
        var units = new[]
        {
            Unit("L0004:S0", 0, "Tham dự “Hội nghị triển khai Chương trình khoa học,"),
            Unit("L0004:S1", 0, "công nghệ và đổi"),
            Unit("L0005:S0", 1, "mới sáng tạo quốc gia đặc biệt về công nghệ chiến lược”")
        };
        var occurrences = units.ToDictionary(u => u.SourceId, u =>
            new StructureSourceOccurrence(u.SourceId, u.SourceOrdinal, u.Text, null, "pdf"));
        var primary = units[0].SourceId;
        var valid = new ValidatedHeading(primary, new(0, units[0].Text.Length), "SECTION", "document", "source-bound")
        { Parts = units.Select(u => new CanonicalSemanticBoundPart(u.SourceId, u.SourceId, u.SourceOrdinal,
            u.Text, 0, u.Text.Length)).ToArray() };
        var materialized = HeadingStructureMaterializer.Materialize([valid],
            new Dictionary<string, ResolvedHeadingHierarchy>
            { [primary] = new(primary, 1, null, HeadingHierarchyResolver.ResolvedRoot, "test") }, occurrences,
            "synthetic-pdf", "model");
        var catalog = new DocumentSourceCatalog(units);
        var outline = new DocumentOutline { File = "synthetic.pdf", ParagraphCount = units.Length, SourceCount = units.Length,
            Headings = HeadingOutlineProjection.Project(materialized.Structure, null, materialized.ProjectionContext, catalog) };
        return new(new(new("synthetic", "synthetic.pdf", "pdf", "synthetic.pdf"), catalog, materialized.Structure,
            [], [], new("test", "pdf-source", 0)), outline)
        { HeadingPipeline = new(materialized.Structure, null, "test")
            { SourceCatalog = catalog, ProjectionContext = materialized.ProjectionContext } };
    }

    private static DocumentSourceUnit Unit(string id, int ordinal, string text) =>
        new(id, ordinal, text, new SourceAnchor { SourceType = "pdf", Page = 1 }, new(0, text.Length));

    private static HeadingRecord Copy(HeadingRecord h, IReadOnlyList<HeadingSourcePart>? parts, string? text = null,
        string? structuralId = "keep", int? partCount = null) => new()
    {
        Index = h.Index, Level = h.Level, Text = text ?? h.Text, SourceId = h.SourceId, StableId = h.StableId,
        OriginalText = h.OriginalText, HeadingSpan = h.HeadingSpan, Source = h.Source, DecisionStatus = h.DecisionStatus,
        ConfidenceBasis = h.ConfidenceBasis, HierarchyResolution = h.HierarchyResolution, BoundarySource = h.BoundarySource,
        StructuralElementId = structuralId == "keep" ? h.StructuralElementId : structuralId,
        ValidatedSourcePartCount = partCount ?? h.ValidatedSourcePartCount, SourceParts = parts
    };

    private static DocumentExtractionExecutionResult WithHeading(DocumentExtractionExecutionResult execution, HeadingRecord heading) =>
        execution with { Outline = new DocumentOutline { File = execution.Outline.File, ParagraphCount = execution.Outline.ParagraphCount,
            SourceCount = execution.Outline.SourceCount, Headings = [heading] } };

    private static readonly CapabilityDescriptor Descriptor = new("fake_extract", "Synthetic PDF execution",
        CapabilityRisk.Low, false, false) { SupportsRepair = true };

    private static DocumentAgentHarness Harness(ExecutionTool tool) => new(tool,
        options: new AgentHarnessOptions { MaxRepairAttempts = 1 },
        skill: new("test", "test", "1", "digest", "memory", new(), []));

    private sealed class ExecutionTool(DocumentExtractionExecutionResult execution) : IDocumentExtractionTool, IDocumentExtractionExecutionSource
    {
        public int Calls { get; private set; }
        public CapabilityDescriptor Descriptor => MultipartOutlineGroundingTests.Descriptor;
        public DocumentExtractionExecutionResult? LastExecution { get; private set; }
        public Task<DocumentOutline> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct = default)
        { Calls++; LastExecution = execution; return Task.FromResult(execution.Outline); }
        public void Dispose() { }
    }
}
