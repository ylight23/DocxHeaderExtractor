using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M11-B2 locks. Determinism is claimed <b>from the validated authority boundary downward</b> and no
/// further: the same validated structures, hierarchy facts and canonical groundings must always yield
/// the same final structure, the same output decisions and the same product output.
/// <para>
/// Nothing here claims that the same DOCX yields the same model result. The model lane may be
/// stochastic; it is upstream of this boundary and is not re-run.
/// </para>
/// <para>
/// Each stage already has its own determinism lock. What was missing, and what these add, is the
/// composition: a stage can be deterministic alone and still perturb what follows it, and only
/// running the whole chain twice and comparing the end product catches that.
/// </para>
/// </summary>
public sealed class PdfProductReplayDeterminismTests
{
    /// <summary>The ordinary case: emitted headings, resolved levels, real anchors.</summary>
    [Fact]
    public void FullChainIsDeterministicForAnEmittableProduct()
    {
        AssertChainIsStable(Grounded());
    }

    /// <summary>
    /// Unresolved hierarchy must survive the whole chain as unresolved. A level or parent invented
    /// anywhere between projection and serialization would show up here as a non-null value.
    /// <para>
    /// Level and parent are independent: a heading can have a resolved parent and an unresolved
    /// level, and the fixture here leaves both unresolved so the assertion means what it says.
    /// </para>
    /// </summary>
    [Fact]
    public void UnresolvedHierarchySurvivesTheChainUnchanged()
    {
        var input = Unresolved();
        var (_, _, product) = RunChain(input);

        Assert.All(product.Headings, heading =>
        {
            Assert.Null(heading.Level);
            Assert.Null(heading.ParentId);
        });
        AssertChainIsStable(input);
    }

    /// <summary>An empty validated set stays an honest empty product, replay included.</summary>
    [Fact]
    public void EmptyValidatedSetStaysEmptyAcrossReplay()
    {
        var input = new Input("sha-empty", [], [], []);
        var (final, decisions, product) = RunChain(input);

        Assert.Empty(final.Headings);
        Assert.Empty(decisions);
        Assert.Empty(product.Headings);
        Assert.Equal("sha-empty", product.SourceDocumentSha256);
        AssertChainIsStable(input);
    }

    /// <summary>
    /// The replay that matters for a restart: the authority is frozen to an artifact, reloaded, and
    /// the whole chain re-run off the reloaded values. Projection alone is already locked for this;
    /// this carries it through decisions and serialization, which is where a resumed run actually
    /// produces its output.
    /// </summary>
    [Fact]
    public void ChainReplaysIdenticallyFromAFrozenArtifact()
    {
        var input = Grounded();
        var (_, _, live) = RunChain(input);

        var row = PdfHierarchyFactsArtifact.BuildRow("doc.pdf", input.Sha, input.Facts, input.Structures, input.Groundings);
        var replayed = JsonSerializer.Deserialize<PdfHierarchyFactsRow>(JsonSerializer.Serialize(row))!;
        var (_, _, offline) = RunChain(new Input(
            replayed.SourceDocumentSha256, replayed.ValidatedStructures, input.Facts, replayed.CanonicalGroundings));

        AssertSameProduct(live, offline);
    }

    private static void AssertChainIsStable(Input input)
    {
        var (firstFinal, firstDecisions, firstProduct) = RunChain(input);
        var (secondFinal, secondDecisions, secondProduct) = RunChain(input);

        Assert.Equal(firstFinal.FinalStructureFingerprint, secondFinal.FinalStructureFingerprint);
        Assert.Equal(
            firstFinal.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId, h.Scope, h.Role, h.GroundingStatus)),
            secondFinal.Headings.Select(h => (h.Id, h.Text, h.Level, h.ParentId, h.Scope, h.Role, h.GroundingStatus)));
        Assert.Equal(
            firstDecisions.Select(d => (d.HeadingId, d.Emit, d.RequiresReview, string.Join('|', d.Reasons))),
            secondDecisions.Select(d => (d.HeadingId, d.Emit, d.RequiresReview, string.Join('|', d.Reasons))));
        AssertSameProduct(firstProduct, secondProduct);
    }

    private static void AssertSameProduct(PdfProductOutput first, PdfProductOutput second)
    {
        Assert.Equal(first.SourceDocumentSha256, second.SourceDocumentSha256);
        Assert.Equal(
            first.Headings.Select(h => (h.Id, h.ParagraphIndex, h.StableId, h.Text, h.Level, h.ParentId, h.RequiresReview)),
            second.Headings.Select(h => (h.Id, h.ParagraphIndex, h.StableId, h.Text, h.Level, h.ParentId, h.RequiresReview)));
        Assert.Equal(
            first.Headings.Select(h => (h.Span.Start, h.Span.End)),
            second.Headings.Select(h => (h.Span.Start, h.Span.End)));
    }

    private static (PdfFinalStructure Final, IReadOnlyList<PdfOutputDecision> Decisions, PdfProductOutput Product)
        RunChain(Input input)
    {
        var final = PdfFinalStructureProjection.Project(input.Sha, input.Structures, input.Facts, input.Groundings);
        var decisions = PdfOutputDecisionPolicy.Decide(final);
        return (final, decisions, PdfProductOutputSerializer.Serialize(final, decisions));
    }

    private static Input Unresolved() => new(
        "sha-unresolved",
        [Structure("b1"), Structure("b2")],
        [Fact("b1", 0, "Introduction", null), Fact("b2", 1, "Scope", null)],
        [
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 12), "Introduction"),
            new PdfCanonicalGrounding("b2", 11, "@body[1]/p[11]", new DocxTextSpan(0, 5), "Scope"),
        ]);

    private static Input Grounded(int? resolvedLevel = 1) => new(
        "sha-grounded",
        [Structure("b1"), Structure("b2", parentId: "b1", resolution: "marker-resolved")],
        [Fact("b1", 0, "1 Introduction", resolvedLevel), Fact("b2", 1, "1 1 Scope", resolvedLevel is null ? null : 2)],
        [
            new PdfCanonicalGrounding("b1", 10, "@body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
            new PdfCanonicalGrounding("b2", 11, "@body[1]/p[11]", new DocxTextSpan(0, 9), "1.1 Scope"),
        ]);

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

    private sealed record Input(
        string Sha,
        IReadOnlyList<PdfValidatedStructure> Structures,
        IReadOnlyList<PdfHierarchyFactAudit> Facts,
        IReadOnlyList<PdfCanonicalGrounding> Groundings);
}
