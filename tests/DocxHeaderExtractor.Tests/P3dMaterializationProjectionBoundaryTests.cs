using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class P3dMaterializationProjectionBoundaryTests
{
    [Fact]
    public void Materializer_applies_only_the_upstream_primary_occurrence_set()
    {
        var validated = new[]
        {
            Heading("S0001", 0, 5),
            Heading("S0002", 0, 5),
        };
        var structures = new Dictionary<string, PdfValidatedStructure>(StringComparer.Ordinal)
        {
            ["S0001"] = new("S0001", 1, null, "model-root", "requires_review"),
            ["S0002"] = new("S0002", 7, "S0001", "model-parent-relation", "requires_review"),
        };
        var occurrences = new Dictionary<string, CanonicalSourceOccurrence>(StringComparer.Ordinal)
        {
            ["S0001"] = new("S0001", 0, "Alpha", null),
            ["S0002"] = new("S0002", 1, "Bravo", null),
        };

        var result = CanonicalStructureMaterializer.Materialize(
            validated, structures, occurrences, "test",
            new HashSet<string>(["S0001"], StringComparer.Ordinal));

        var element = Assert.Single(result.Elements);
        Assert.Equal("structural:test:S0001", element.Id);
        Assert.Equal(1, element.Level);
        Assert.Null(element.ParentId);
        Assert.Equal("Alpha", element.Text);
    }

    [Fact]
    public void Same_resolved_input_produces_the_same_validated_structure()
    {
        var validated = new[] { Heading("S0001", 0, 5) };
        var structures = new Dictionary<string, PdfValidatedStructure>(StringComparer.Ordinal)
        {
            ["S0001"] = new("S0001", 2, null, "model-root", "requires_review"),
        };
        var occurrences = new Dictionary<string, CanonicalSourceOccurrence>(StringComparer.Ordinal)
        {
            ["S0001"] = new("S0001", 0, "Alpha", null),
        };
        var primary = new HashSet<string>(["S0001"], StringComparer.Ordinal);

        var first = CanonicalStructureMaterializer.Materialize(validated, structures, occurrences, "test", primary);
        var second = CanonicalStructureMaterializer.Materialize(validated, structures, occurrences, "test", primary);

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public void Materializer_does_not_recompute_hierarchy_or_semantic_identity()
    {
        var validated = new[] { Heading("S0002", 0, 5) };
        var structures = new Dictionary<string, PdfValidatedStructure>(StringComparer.Ordinal)
        {
            ["S0002"] = new("S0002", 9, "S0001", "unresolved", "requires_review"),
        };
        var occurrences = new Dictionary<string, CanonicalSourceOccurrence>(StringComparer.Ordinal)
        {
            ["S0002"] = new("S0002", 1, "Bravo", null),
        };

        var result = CanonicalStructureMaterializer.Materialize(
            validated, structures, occurrences, "test",
            new HashSet<string>(["S0002"], StringComparer.Ordinal));

        var element = Assert.Single(result.Elements);
        Assert.Equal("structural:test:S0002", element.Id);
        Assert.Null(element.Level); // unresolved upstream hierarchy is not guessed as level 9
        Assert.Null(element.ParentId); // the absent parent is not synthesized
    }

    [Fact]
    public void Projection_boundary_preserves_canonical_structure()
    {
        var structure = MaterializeOne();
        var before = System.Text.Json.JsonSerializer.Serialize(structure);
        var audit = CanonicalRouteAuditBoundary.Create(
            "test", 1, 1, 0, 0, [], [], [], [], ["S0001"], [], ["S0001"]) with
        {
            ValidatedStructures =
            [
                new PdfValidatedStructure("S0001", 1, null, "model-root", "requires_review"),
            ],
            HierarchyFacts =
            [
                new PdfHierarchyFactAudit(
                    "S0001", 0, 1, "document_body", "default", null, null, false, null,
                    null, null, 1, "resolved-root", ["source"])
                {
                    FactId = "S0001",
                    SourceBlockText = "Alpha",
                    HeadingText = "Alpha",
                    HeadingSpan = new TextOffsetSpan(0, 5),
                },
            ],
        };

        var projected = CanonicalProjectionBoundary.ProjectPdfFinalStructure(
            "source-sha", audit, structure);

        Assert.Single(projected.Headings);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(structure));
        Assert.Equal(1, projected.Counters.ValidatedStructures);
        Assert.Equal("grounded", projected.Headings[0].GroundingStatus);
    }

    [Fact]
    public void Audit_boundary_only_records_supplied_observations()
    {
        var blocks = new[] { new RouteBlockAudit("B1", 1, "Alpha") };
        var decisions = new[] { new RouteBlockDecisionAudit("B1", "HeadingTopic", 1) };

        var audit = CanonicalRouteAuditBoundary.Create(
            "test-route", 1, 1, 1, 1, blocks, blocks, [], decisions, ["B1"], [], ["B1"]);

        Assert.Equal("test-route", audit.Route);
        Assert.Same(blocks, audit.CandidateBlocks);
        Assert.Same(decisions, audit.BlockDecisions);
        Assert.Empty(audit.RawAnalystResponses);
        Assert.Empty(audit.ModelRequests);
    }

    private static ValidatedStructure MaterializeOne() =>
        CanonicalStructureMaterializer.Materialize(
            [Heading("S0001", 0, 5)],
            new Dictionary<string, PdfValidatedStructure>(StringComparer.Ordinal)
            {
                ["S0001"] = new("S0001", 1, null, "model-root", "requires_review"),
            },
            new Dictionary<string, CanonicalSourceOccurrence>(StringComparer.Ordinal)
            {
                ["S0001"] = new("S0001", 0, "Alpha", null),
            },
            "test",
            new HashSet<string>(["S0001"], StringComparer.Ordinal));

    private static PdfValidatedHeading Heading(string sourceId, int start, int end) =>
        new(sourceId, new TextOffsetSpan(start, end), PdfBlockRole.HeadingTopic, "document_body", "test");
}
