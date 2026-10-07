using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Materialization;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.1 locks. The projection materializes validated facts and may not improve on them: it cannot
/// add a heading, recover a rejected one, rewrite source text, fill an unresolved relation, or
/// match a fact to a canonical occurrence the pipeline did not already reconcile.
/// </summary>
public sealed class CanonicalFinalStructureProjectionTests
{
    [Fact]
    public void EmitsOneHeadingPerValidatedStructureInSourceOrder()
    {
        var final = Project(("b2", 1, "2 Overview", 1), ("b1", 0, "1 Introduction", 1));

        Assert.Equal(["b1", "b2"], final.Headings.Select(heading => heading.PdfEvidence!.BlockId));
        Assert.Equal(2, final.Counters.EmittedHeadings);
        Assert.Equal(0, final.Counters.DroppedWithoutSourceFact);
    }

    /// <summary>
    /// The document is the authority, so a grounded heading's text is a slice of its canonical
    /// paragraph rather than of the PDF block that observed it. Where extraction damaged the
    /// rendered text, the product still shows what the document says.
    /// </summary>
    [Fact]
    public void GroundedTextComesFromTheCanonicalParagraph()
    {
        var fact = Fact("b1", 0, "4 3 Ca che-Control", 1);
        var grounding = new CanonicalGrounding("b1", 90, "@body[1]/p[90]", new DocxTextSpan(0, 17),
            "4.3 Cache-Control and the rest of the paragraph");

        var final = CanonicalFinalStructureProjection.Project("sha", [Structure("b1")], [fact], [grounding]);

        var heading = Assert.Single(final.Headings);
        Assert.Equal("4.3 Cache-Control", heading.Text);
        Assert.Equal("grounded", heading.GroundingStatus);
        Assert.Equal(90, heading.SourceAnchor!.ParagraphIndex);
        Assert.Equal("@body[1]/p[90]", heading.SourceAnchor.StableId);
        Assert.Equal("4 3 Ca che-Control", heading.PdfEvidence!.ObservedText);
    }

    /// <summary>
    /// A fact the pipeline never reconciled to a paragraph stays ungrounded. The projection does not
    /// search the document for a matching title, because a guessed occurrence is what M8 showed to
    /// be dangerous, and it is what a writeback would act on.
    /// </summary>
    [Fact]
    public void UngroundedFactIsReportedRatherThanMatched()
    {
        var final = Project(("b1", 0, "4 3 Validation", 1));

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.SourceAnchor);
        Assert.Equal("grounding_unresolved", heading.GroundingStatus);
        Assert.Equal(1, final.Counters.GroundingUnresolved);
    }

    [Fact]
    public void UnresolvedHierarchyStaysUnresolved()
    {
        var final = Project(("b1", 0, "Topic without a marker", null));

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.Level);
        Assert.Null(heading.ParentId);
        Assert.Equal("unresolved", heading.HierarchyStatus);
        Assert.Equal("no_deterministic_level_evidence", heading.LevelReason);
    }

    /// <summary>
    /// A preceding heading is not a parent. Nothing here may promote source order into a relation
    /// the validated structure never claimed.
    /// </summary>
    [Fact]
    public void PrecedingHeadingIsNeverAdoptedAsParent()
    {
        var final = Project(("b1", 0, "1 Introduction", 1), ("b2", 1, "2 Overview", 1));

        Assert.All(final.Headings, heading => Assert.Null(heading.ParentId));
        Assert.All(final.Headings, heading => Assert.Equal("parent_unresolved", heading.HierarchyStatus));
    }

    /// <summary>A parent is referenced by canonical identity, not by the block that observed it.</summary>
    [Fact]
    public void ResolvedParentIsReferencedByCanonicalIdentity()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1), Fact("b2", 1, "1 1 Scope", 2) };
        var structures = new[] { Structure("b1"), Structure("b2", parentId: "b1", resolution: "marker-resolved") };
        var groundings = new[]
        {
            new CanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
            new CanonicalGrounding("b2", 11, "@body[1]/p[11]", new DocxTextSpan(0, 9), "1.1 Scope"),
        };

        var final = CanonicalFinalStructureProjection.Project("sha", structures, facts, groundings);

        var parent = final.Headings.Single(heading => heading.PdfEvidence!.BlockId == "b1");
        var child = final.Headings.Single(heading => heading.PdfEvidence!.BlockId == "b2");
        Assert.Equal(parent.Id, child.ParentId);
        Assert.StartsWith("@body[1]/p[10]", parent.Id);
        Assert.Equal("resolved", child.HierarchyStatus);
    }

    [Fact]
    public void ParentPointingOutsideTheEmittedSetIsDropped()
    {
        var final = CanonicalFinalStructureProjection.Project("sha",
            [Structure("b2", parentId: "b1", resolution: "marker-resolved")], [Fact("b2", 1, "1 1 Scope", 2)], []);

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.ParentId);
        Assert.Equal("parent_not_in_emitted_set", heading.ParentReason);
    }

    /// <summary>
    /// Where the strict path and the observed components disagree the source lost its separators, so
    /// the strict depth is short. The projection reports that rather than asserting a wrong level.
    /// </summary>
    [Fact]
    public void ConflictingMarkerRepresentationSuppressesTheLevel()
    {
        var fact = Fact("b1", 0, "4 3 2 Handling a Received Validation Request", 1) with
        {
            MarkerComponents = [4, 3, 2],
        };

        var final = CanonicalFinalStructureProjection.Project("sha", [Structure("b1")], [fact], []);

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.Level);
        Assert.Equal("marker_representation_conflict", heading.LevelReason);
    }

    [Fact]
    public void StructureWithoutASourceFactIsDroppedAndCounted()
    {
        var final = CanonicalFinalStructureProjection.Project("sha", [Structure("missing")], [], []);

        Assert.Empty(final.Headings);
        Assert.Equal(1, final.Counters.DroppedWithoutSourceFact);
    }

    /// <summary>Scope is carried verbatim; role is no longer inferred from parser-side domain policy.</summary>
    [Fact]
    public void ScopeIsCarriedWithoutParserSideRoleNormalisation()
    {
        var structure = Structure("b1") with { StructuralScope = "appendix_table" };

        var final = CanonicalFinalStructureProjection.Project("sha", [structure], [Fact("b1", 0, "4 3 Validation", 1)], []);

        var heading = Assert.Single(final.Headings);
        Assert.Equal("appendix_table", heading.Scope);
        Assert.Equal("Heading", heading.Role);
        Assert.Equal("validated", heading.Authority);
    }

    [Fact]
    public void SameInputProducesTheSameFingerprints()
    {
        var first = Project(("b1", 0, "1 Introduction", 1));
        var second = Project(("b1", 0, "1 Introduction", 1));

        Assert.Equal(first.FinalStructureFingerprint, second.FinalStructureFingerprint);
        Assert.NotEqual(first.FinalStructureFingerprint, first.ValidatedStructureFingerprint);
    }

    /// <summary>
    /// The projection must be reproducible from a frozen artifact, so a product result can be
    /// re-derived later without re-running extraction, reconciliation or a model.
    /// </summary>
    /// <summary>
    /// <see cref="ResolvedHeadingHierarchy"/> declares its own <c>[JsonPropertyName]</c>s so it survives a
    /// camelCase round-trip: without them a case-sensitive reader silently leaves <c>SourceId</c> null
    /// instead of throwing, which then throws much later and further away, inside <c>Project</c>.
    /// </summary>
    [Fact]
    public void ValidatedStructureRoundTripsUnderTheCamelCaseNamingPolicyTheCliWrites()
    {
        var structure = Structure("b1", parentId: "b2", resolution: "marker-resolved") with
        {
            StructuralScope = "appendix_table",
        };
        var camelCase = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(structure, camelCase);
        var replayed = System.Text.Json.JsonSerializer.Deserialize<ResolvedHeadingHierarchy>(json, camelCase);

        Assert.Equal(structure, replayed);
        Assert.NotNull(replayed!.SourceId);
    }

    private static CanonicalFinalStructure Project(params (string Id, int Order, string Text, int? Level)[] cases) =>
        CanonicalFinalStructureProjection.Project(
            "sha",
            cases.Select(item => Structure(item.Id)).ToArray(),
            cases.Select(item => Fact(item.Id, item.Order, item.Text, item.Level)).ToArray(),
            []);

    private static ResolvedHeadingHierarchy Structure(string id, string? parentId = null, string resolution = "unresolved") =>
        new(id, 1, parentId, resolution, "requires_review") { StructuralScope = "document_body" };

    private static HeadingHierarchyFactAudit Fact(string id, int order, string text, int? resolvedLevel) =>
        new(id, order, 1, "document_body", "document_body", null, null, false, null, null, null,
            resolvedLevel, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };
}
