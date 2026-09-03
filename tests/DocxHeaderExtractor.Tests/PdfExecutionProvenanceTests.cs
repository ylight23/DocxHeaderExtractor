using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M12-A2 locks. The route already computes a semantic lane status; the artifact used to drop it, so
/// two operationally different executions became indistinguishable once the run was over.
/// <para>
/// This propagates the existing fact and nothing else. No status is derived, normalised or invented,
/// and no product behaviour changes - the point is that evidence survives the boundary, not that the
/// pipeline decides differently.
/// </para>
/// </summary>
public sealed class PdfExecutionProvenanceTests
{
    [Fact]
    public void PartialTimeoutIsPreservedInTheArtifact()
    {
        var row = PdfHierarchyFactsArtifact.BuildRow("doc.docx", "sha", [], [], [], "partial_timeout");

        Assert.Equal("partial_timeout", row.SemanticLaneStatus);
    }

    [Fact]
    public void CompleteLaneStatusIsPreservedInTheArtifact()
    {
        var row = PdfHierarchyFactsArtifact.BuildRow("doc.docx", "sha", [], [], [], "complete");

        Assert.Equal("complete", row.SemanticLaneStatus);
    }

    /// <summary>
    /// The defect this exists for. Both rows carry zero validated structures and identical counters;
    /// before the status was carried through, nothing in the artifact could tell an honest empty run
    /// from a degraded one. Same cardinality is not the same execution.
    /// </summary>
    [Fact]
    public void EmptySuccessAndPartialTimeoutRemainDistinguishable()
    {
        var healthy = PdfHierarchyFactsArtifact.BuildRow("doc.docx", "sha", [], [], [], "complete");
        var degraded = PdfHierarchyFactsArtifact.BuildRow("doc.docx", "sha", [], [], [], "partial_timeout");

        Assert.Equal(healthy.Counters.ValidatedHeadings, degraded.Counters.ValidatedHeadings);
        Assert.Equal(healthy.OccurrenceFingerprint, degraded.OccurrenceFingerprint);
        Assert.NotEqual(healthy.SemanticLaneStatus, degraded.SemanticLaneStatus);
    }

    /// <summary>An artifact written before the field existed reads as unknown, never as complete.</summary>
    [Fact]
    public void AnArtifactWithoutTheFieldReadsAsUnknownRatherThanComplete()
    {
        var legacy = """
            {"file":"doc.docx","sourceDocumentSha256":"sha","occurrenceFingerprint":"fp",
             "counters":{"validatedHeadings":0,"markerPathFacts":0,"deterministicLevelResolved":0,
                         "deterministicParentResolved":0,"unresolvedRelationships":0,"conflicts":"not_measured"},
             "items":[],"validatedStructures":[],"canonicalGroundings":[]}
            """;

        var row = JsonSerializer.Deserialize<PdfHierarchyFactsRow>(legacy)!;

        Assert.Null(row.SemanticLaneStatus);
        Assert.NotEqual("complete", row.SemanticLaneStatus);
    }

    /// <summary>
    /// Behavioural neutrality: carrying the status must not move the product. The projection,
    /// decisions and serialization are driven from the same row twice, once with each status.
    /// </summary>
    [Fact]
    public void CarryingTheStatusDoesNotChangeTheProductProjection()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction") };
        var structures = new[] { new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review") };
        var groundings = new[]
        {
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
        };

        var healthy = PdfHierarchyFactsArtifact.BuildRow("d", "sha", facts, structures, groundings, "complete");
        var degraded = PdfHierarchyFactsArtifact.BuildRow("d", "sha", facts, structures, groundings, "partial_timeout");

        var first = Product(healthy);
        var second = Product(degraded);

        Assert.Equal(first.SourceDocumentSha256, second.SourceDocumentSha256);
        Assert.Equal(
            first.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId)),
            second.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId)));
    }

    /// <summary>Serialising the same execution provenance twice must produce the same value.</summary>
    [Fact]
    public void SerialisingTheSameExecutionProvenanceIsDeterministic()
    {
        var row = PdfHierarchyFactsArtifact.BuildRow("doc.docx", "sha", [], [], [], "partial_timeout");

        var first = JsonSerializer.Serialize(row);
        var second = JsonSerializer.Serialize(row);
        var round = JsonSerializer.Deserialize<PdfHierarchyFactsRow>(first)!;

        Assert.Equal(first, second);
        Assert.Equal("partial_timeout", round.SemanticLaneStatus);
    }

    /// <summary>
    /// M12-A4. `partial_timeout` says the run degraded; it does not say against what. Two runs
    /// identical in every other recorded field - source, model, revision, route config hash, status -
    /// mean different things at a 10-second threshold and a 300-second one, and before the thresholds
    /// were carried the artifacts were indistinguishable.
    /// </summary>
    [Fact]
    public void SameStatusUnderDifferentThresholdsRemainsDistinguishable()
    {
        var tight = PdfHierarchyFactsArtifact.BuildRow("d", "sha", [], [], [], "partial_timeout",
            new SemanticLaneOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)));
        var generous = PdfHierarchyFactsArtifact.BuildRow("d", "sha", [], [], [], "partial_timeout",
            new SemanticLaneOptions(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)));

        Assert.Equal(tight.SemanticLaneStatus, generous.SemanticLaneStatus);
        Assert.Equal(tight.OccurrenceFingerprint, generous.OccurrenceFingerprint);
        Assert.NotEqual(tight.SemanticLaneTimeouts, generous.SemanticLaneTimeouts);
    }

    /// <summary>The recorded thresholds are the ones the lane was handed, not defaults re-read later.</summary>
    [Fact]
    public void RecordedThresholdsAreTheOnesTheLaneWasGiven()
    {
        var options = new SemanticLaneOptions(
            TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(150));

        var row = PdfHierarchyFactsArtifact.BuildRow("d", "sha", [], [], [], "complete", options);

        Assert.Equal(45, row.SemanticLaneTimeouts!.RequestSeconds);
        Assert.Equal(60, row.SemanticLaneTimeouts.BatchSeconds);
        Assert.Equal(150, row.SemanticLaneTimeouts.LaneDeadlineSeconds);
    }

    /// <summary>Absent thresholds are unknown, never assumed to be the defaults.</summary>
    [Fact]
    public void AbsentThresholdsAreUnknownRatherThanDefaults()
    {
        var row = PdfHierarchyFactsArtifact.BuildRow("d", "sha", [], [], [], "complete");

        Assert.Null(row.SemanticLaneTimeouts);
    }

    /// <summary>Carrying thresholds must not move the product either.</summary>
    [Fact]
    public void CarryingThresholdsDoesNotChangeTheProductProjection()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction") };
        var structures = new[] { new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review") };
        var groundings = new[]
        {
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
        };

        var without = Product(PdfHierarchyFactsArtifact.BuildRow("d", "sha", facts, structures, groundings, "complete"));
        var with = Product(PdfHierarchyFactsArtifact.BuildRow("d", "sha", facts, structures, groundings, "complete",
            new SemanticLaneOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7))));

        Assert.Equal(
            without.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId)),
            with.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId)));
    }

    private static PdfProductOutput Product(PdfHierarchyFactsRow row)
    {
        var facts = row.Items.Select(item => item.ToFactAudit()).ToArray();
        var final = PdfFinalStructureProjection.Project(
            row.SourceDocumentSha256, row.ValidatedStructures, facts, row.CanonicalGroundings);
        return PdfProductOutputSerializer.Serialize(final, PdfOutputDecisionPolicy.Decide(final));
    }

    private static PdfHierarchyFactAudit Fact(string id, int order, string text) =>
        new(id, order, 1, "document_body", "document_body", null, null, false, null, null, null,
            1, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };
}
