using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.1 locks. The projection materializes validated facts and may not improve on them: it cannot
/// add a heading, recover a rejected one, rewrite source text, fill an unresolved relation, or
/// match a fact to a canonical occurrence the pipeline did not already reconcile.
/// </summary>
public sealed class PdfFinalStructureProjectionTests
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
        var grounding = new PdfCanonicalGrounding("b1", 90, "@body[1]/p[90]", new DocxTextSpan(0, 17),
            "4.3 Cache-Control and the rest of the paragraph");

        var final = PdfFinalStructureProjection.Project("sha", [Structure("b1")], [fact], [grounding]);

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
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
            new PdfCanonicalGrounding("b2", 11, "@body[1]/p[11]", new DocxTextSpan(0, 9), "1.1 Scope"),
        };

        var final = PdfFinalStructureProjection.Project("sha", structures, facts, groundings);

        var parent = final.Headings.Single(heading => heading.PdfEvidence!.BlockId == "b1");
        var child = final.Headings.Single(heading => heading.PdfEvidence!.BlockId == "b2");
        Assert.Equal(parent.Id, child.ParentId);
        Assert.StartsWith("@body[1]/p[10]", parent.Id);
        Assert.Equal("resolved", child.HierarchyStatus);
    }

    [Fact]
    public void ParentPointingOutsideTheEmittedSetIsDropped()
    {
        var final = PdfFinalStructureProjection.Project("sha",
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

        var final = PdfFinalStructureProjection.Project("sha", [Structure("b1")], [fact], []);

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.Level);
        Assert.Equal("marker_representation_conflict", heading.LevelReason);
    }

    [Fact]
    public void StructureWithoutASourceFactIsDroppedAndCounted()
    {
        var final = PdfFinalStructureProjection.Project("sha", [Structure("missing")], [], []);

        Assert.Empty(final.Headings);
        Assert.Equal(1, final.Counters.DroppedWithoutSourceFact);
    }

    /// <summary>Scope and role are carried verbatim; the projection is not the place to normalise them.</summary>
    [Fact]
    public void ScopeAndRoleAreCarriedWithoutNormalisation()
    {
        var structure = Structure("b1") with { StructuralScope = "appendix_table", DomainRole = PdfDomainRole.TableTitle };

        var final = PdfFinalStructureProjection.Project("sha", [structure], [Fact("b1", 0, "4 3 Validation", 1)], []);

        var heading = Assert.Single(final.Headings);
        Assert.Equal("appendix_table", heading.Scope);
        Assert.Equal("TableTitle", heading.Role);
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
    [Fact]
    public void ProjectsIdenticallyFromASerialisedArtifact()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1), Fact("b2", 1, "1 1 Scope", 2) };
        var structures = new[] { Structure("b1"), Structure("b2", parentId: "b1", resolution: "marker-resolved") };
        var groundings = new[]
        {
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
            new PdfCanonicalGrounding("b2", 11, "@body[1]/p[11]", new DocxTextSpan(0, 9), "1.1 Scope"),
        };
        var live = PdfFinalStructureProjection.Project("sha", structures, facts, groundings);

        var row = PdfHierarchyFactsArtifact.BuildRow("doc.pdf", "sha", facts, structures, groundings);
        var replayed = System.Text.Json.JsonSerializer.Deserialize<PdfHierarchyFactsRow>(
            System.Text.Json.JsonSerializer.Serialize(row))!;
        var offline = PdfFinalStructureProjection.Project(
            replayed.SourceDocumentSha256, replayed.ValidatedStructures, facts, replayed.CanonicalGroundings);

        Assert.Equal(live.FinalStructureFingerprint, offline.FinalStructureFingerprint);
        Assert.Equal(live.Headings.Select(h => h.Text), offline.Headings.Select(h => h.Text));
        Assert.Equal(live.Headings.Select(h => h.ParentId), offline.Headings.Select(h => h.ParentId));
    }

    /// <summary>Grounding is read from the route's own reconciliation, never recomputed here.</summary>
    [Fact]
    public void GroundingIsMaterializedFromTheRoutesReconciliation()
    {
        var heading = new HeadingRecord
        {
            Index = 90,
            Level = 2,
            SourceId = "b220",
            StableId = "@body[1]/p[90]",
            Text = "4.3 Validation",
            OriginalText = "4.3 Validation and the rest",
            HeadingSpan = new TextOffsetSpan(0, 14),
        };

        var grounding = Assert.Single(PdfCanonicalGrounding.FromGroundedHeadings([heading]));

        Assert.Equal("b220", grounding.SourceFactId);
        Assert.Equal(90, grounding.ParagraphIndex);
        Assert.Equal("@body[1]/p[90]", grounding.StableId);
        Assert.Equal("4.3 Validation and the rest", grounding.ParagraphText);
    }

    private static PdfFinalStructure Project(params (string Id, int Order, string Text, int? Level)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => Structure(item.Id)).ToArray(),
            cases.Select(item => Fact(item.Id, item.Order, item.Text, item.Level)).ToArray(),
            []);

    private static PdfValidatedStructure Structure(string id, string? parentId = null, string resolution = "unresolved") =>
        new(id, 1, parentId, resolution, "requires_review") { StructuralScope = "document_body" };

    private static PdfHierarchyFactAudit Fact(string id, int order, string text, int? resolvedLevel) =>
        new(id, order, 1, "document_body", "document_body", null, null, false, null, null, null,
            resolvedLevel, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };
}
