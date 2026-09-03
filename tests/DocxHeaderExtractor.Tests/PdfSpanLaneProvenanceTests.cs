using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// C1.4 locks. A heading cannot validate without a resolved span, and the span lane's outcome used to
/// vanish once the run ended - so a lane that timed out produced an artifact identical to a healthy
/// one, and C1.4 measured exactly that on 001: `semanticLaneStatus: complete` beside zero validated
/// headings from 158 correct role answers.
/// <para>
/// The status is carried in its own field rather than folded into `semanticLaneStatus`, which already
/// means something and is already in the runbook. Widening that field's meaning silently would break
/// every reader relying on it.
/// </para>
/// </summary>
public sealed class PdfSpanLaneProvenanceTests
{
    [Fact]
    public void SpanTimeoutIsPreservedInTheArtifact()
    {
        var row = Row(semantic: "complete", span: "partial_timeout");

        Assert.Equal("partial_timeout", row.SpanLaneStatus);
    }

    [Fact]
    public void CompleteSpanLaneIsPreservedInTheArtifact()
    {
        Assert.Equal("complete", Row(semantic: "complete", span: "complete").SpanLaneStatus);
    }

    /// <summary>
    /// The defect this exists for. The semantic lane may honestly report `complete` while the span
    /// lane degraded, and the two must stay separately readable - merging them would reintroduce the
    /// ambiguity, in the opposite direction.
    /// </summary>
    [Fact]
    public void SemanticLaneMayBeCompleteWhileTheSpanLaneTimedOut()
    {
        var row = Row(semantic: "complete", span: "partial_timeout");

        Assert.Equal("complete", row.SemanticLaneStatus);
        Assert.NotEqual(row.SemanticLaneStatus, row.SpanLaneStatus);
    }

    /// <summary>A lane that never ran is not a lane that succeeded.</summary>
    [Fact]
    public void ASpanLaneThatNeverRanIsNotReportedAsComplete()
    {
        var row = Row(semantic: "partial_timeout", span: "not_run");

        Assert.Equal("not_run", row.SpanLaneStatus);
        Assert.NotEqual("complete", row.SpanLaneStatus);
    }

    [Fact]
    public void MissingLegacySpanStatusMeansUnknownNotComplete()
    {
        var legacy = """
            {"file":"d.docx","sourceDocumentSha256":"sha","occurrenceFingerprint":"fp",
             "counters":{"validatedHeadings":0,"markerPathFacts":0,"deterministicLevelResolved":0,
                         "deterministicParentResolved":0,"unresolvedRelationships":0,"conflicts":"not_measured"},
             "items":[],"validatedStructures":[],"canonicalGroundings":[],
             "semanticLaneStatus":"complete"}
            """;

        var row = JsonSerializer.Deserialize<PdfHierarchyFactsRow>(legacy)!;

        Assert.Equal("complete", row.SemanticLaneStatus);
        Assert.Null(row.SpanLaneStatus);
        Assert.NotEqual("complete", row.SpanLaneStatus);
    }

    /// <summary>Behavioural neutrality: recording the lane must not move the product.</summary>
    [Fact]
    public void SpanStatusDoesNotChangeValidationOrProductOutput()
    {
        var facts = new[] { Fact("b1", "1 Introduction") };
        var structures = new[] { new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review") };
        var groundings = new[]
        {
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
        };

        var healthy = Product(PdfHierarchyFactsArtifact.BuildRow(
            "d", "sha", facts, structures, groundings, "complete", null, "complete"));
        var degraded = Product(PdfHierarchyFactsArtifact.BuildRow(
            "d", "sha", facts, structures, groundings, "complete", null, "partial_timeout"));

        Assert.Equal(
            healthy.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId)),
            degraded.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId)));
    }

    [Fact]
    public void SerialisationOfSpanProvenanceIsDeterministic()
    {
        var row = Row(semantic: "complete", span: "partial_timeout");

        var first = JsonSerializer.Serialize(row);
        Assert.Equal(first, JsonSerializer.Serialize(row));
        Assert.Equal("partial_timeout",
            JsonSerializer.Deserialize<PdfHierarchyFactsRow>(first)!.SpanLaneStatus);
    }

    private static PdfHierarchyFactsRow Row(string semantic, string span) =>
        PdfHierarchyFactsArtifact.BuildRow("d.docx", "sha", [], [], [], semantic, null, span);

    private static PdfProductOutput Product(PdfHierarchyFactsRow row)
    {
        var facts = row.Items.Select(item => item.ToFactAudit()).ToArray();
        var final = PdfFinalStructureProjection.Project(
            row.SourceDocumentSha256, row.ValidatedStructures, facts, row.CanonicalGroundings);
        return PdfProductOutputSerializer.Serialize(final, PdfOutputDecisionPolicy.Decide(final));
    }

    private static PdfHierarchyFactAudit Fact(string id, string text) =>
        new(id, 0, 1, "document_body", "document_body", null, null, false, null, null, null,
            1, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };
}
