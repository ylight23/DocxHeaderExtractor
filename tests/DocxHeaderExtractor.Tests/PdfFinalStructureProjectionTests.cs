using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.1 locks. The projection materializes validated facts and may not improve on them: it cannot
/// add a heading, recover a rejected one, rewrite source text, or fill an unresolved relation.
/// </summary>
public sealed class PdfFinalStructureProjectionTests
{
    [Fact]
    public void EmitsOneHeadingPerValidatedStructureInSourceOrder()
    {
        var facts = new[] { Fact("b2", 1, "2 Overview", 1), Fact("b1", 0, "1 Introduction", 1) };
        var structures = new[] { Structure("b1"), Structure("b2") };

        var final = PdfFinalStructureProjection.Project("sha", structures, facts);

        Assert.Equal(["b1", "b2"], final.Headings.Select(heading => heading.SourceFactId));
        Assert.Equal(2, final.Counters.EmittedHeadings);
        Assert.Equal(0, final.Counters.DroppedWithoutSourceFact);
    }

    /// <summary>Text is a slice of the immutable source, never a model string.</summary>
    [Fact]
    public void TextComesFromTheValidatedSourceSlice()
    {
        var fact = Fact("b1", 0, "4 3 Validation", 1);

        var final = PdfFinalStructureProjection.Project("sha", [Structure("b1")], [fact]);

        var heading = Assert.Single(final.Headings);
        Assert.Equal(fact.HeadingText, heading.Text);
        Assert.Equal(fact.HeadingSpan.Start, heading.HeadingSpan.Start);
        Assert.Equal(fact.HeadingSpan.End, heading.HeadingSpan.End);
    }

    [Fact]
    public void UnresolvedHierarchyStaysUnresolved()
    {
        var fact = Fact("b1", 0, "Topic without a marker", null);

        var final = PdfFinalStructureProjection.Project("sha", [Structure("b1")], [fact]);

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.Level);
        Assert.Null(heading.ParentId);
        Assert.Equal("unresolved", heading.HierarchyStatus);
        Assert.Equal("no_deterministic_level_evidence", heading.LevelReason);
    }

    /// <summary>
    /// A preceding heading is not a parent. Nothing in the projection may promote source order into
    /// a relation the validated structure never claimed.
    /// </summary>
    [Fact]
    public void PrecedingHeadingIsNeverAdoptedAsParent()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1), Fact("b2", 1, "2 Overview", 1) };
        var structures = new[] { Structure("b1"), Structure("b2") };

        var final = PdfFinalStructureProjection.Project("sha", structures, facts);

        Assert.All(final.Headings, heading => Assert.Null(heading.ParentId));
        Assert.All(final.Headings, heading => Assert.Equal("parent_unresolved", heading.HierarchyStatus));
    }

    [Fact]
    public void ParentSurvivesOnlyWhenTheValidatedStructureResolvedIt()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1), Fact("b2", 1, "1 1 Scope", 2) };
        var structures = new[] { Structure("b1"), Structure("b2", parentId: "b1", resolution: "marker-resolved") };

        var final = PdfFinalStructureProjection.Project("sha", structures, facts);

        var child = final.Headings.Single(heading => heading.SourceFactId == "b2");
        Assert.Equal("p1:b1:s0-14", child.ParentId);
        Assert.Equal(2, child.Level);
        Assert.Equal("resolved", child.HierarchyStatus);
    }

    /// <summary>A parent outside the emitted set is a dangling edge, so it is dropped, not carried.</summary>
    [Fact]
    public void ParentPointingOutsideTheEmittedSetIsDropped()
    {
        var facts = new[] { Fact("b2", 1, "1 1 Scope", 2) };
        var structures = new[] { Structure("b2", parentId: "b1", resolution: "marker-resolved") };

        var final = PdfFinalStructureProjection.Project("sha", structures, facts);

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

        var final = PdfFinalStructureProjection.Project("sha", [Structure("b1")], [fact]);

        var heading = Assert.Single(final.Headings);
        Assert.Null(heading.Level);
        Assert.Equal("marker_representation_conflict", heading.LevelReason);
    }

    [Fact]
    public void StructureWithoutASourceFactIsDroppedAndCounted()
    {
        var final = PdfFinalStructureProjection.Project("sha", [Structure("missing")], []);

        Assert.Empty(final.Headings);
        Assert.Equal(1, final.Counters.DroppedWithoutSourceFact);
    }

    /// <summary>Scope and role are carried verbatim; the projection is not the place to normalise them.</summary>
    [Fact]
    public void ScopeAndRoleAreCarriedWithoutNormalisation()
    {
        var fact = Fact("b1", 0, "4 3 Validation", 1);
        var structure = Structure("b1") with { StructuralScope = "appendix_table", DomainRole = PdfDomainRole.TableTitle };

        var final = PdfFinalStructureProjection.Project("sha", [structure], [fact]);

        var heading = Assert.Single(final.Headings);
        Assert.Equal("appendix_table", heading.Scope);
        Assert.Equal("TableTitle", heading.Role);
        Assert.Equal("validated", heading.Authority);
    }

    [Fact]
    public void SameInputProducesTheSameFingerprints()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1) };
        var structures = new[] { Structure("b1") };

        var first = PdfFinalStructureProjection.Project("sha", structures, facts);
        var second = PdfFinalStructureProjection.Project("sha", structures, facts);

        Assert.Equal(first.FinalStructureFingerprint, second.FinalStructureFingerprint);
        Assert.Equal(first.ValidatedStructureFingerprint, second.ValidatedStructureFingerprint);
        Assert.NotEqual(first.FinalStructureFingerprint, first.ValidatedStructureFingerprint);
    }

    /// <summary>
    /// The projection must be reproducible from a frozen artifact, not only from a live route, so a
    /// product result can be re-derived later without re-running extraction or a model.
    /// </summary>
    [Fact]
    public void ProjectsIdenticallyFromASerialisedArtifact()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1), Fact("b2", 1, "1 1 Scope", 2) };
        var structures = new[] { Structure("b1"), Structure("b2", parentId: "b1", resolution: "marker-resolved") };
        var live = PdfFinalStructureProjection.Project("sha", structures, facts);

        var row = PdfHierarchyFactsArtifact.BuildRow("doc.pdf", "sha", facts, structures);
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        var replayed = System.Text.Json.JsonSerializer.Deserialize<PdfHierarchyFactsRow>(json)!;
        var offline = PdfFinalStructureProjection.Project(
            replayed.SourceDocumentSha256,
            replayed.ValidatedStructures,
            facts);

        Assert.Equal(live.FinalStructureFingerprint, offline.FinalStructureFingerprint);
        Assert.Equal(live.Headings.Select(h => h.ParentId), offline.Headings.Select(h => h.ParentId));
    }

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
