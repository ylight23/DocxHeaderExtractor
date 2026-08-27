using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M9.4 compatibility half. The comparison joins on the shared source fact id - never on the DOCX
/// anchor either lane resolved to - so a disagreement about WHICH occurrence a fact grounds to shows
/// up as <c>AnchorMismatch</c> instead of silently matching two different paragraphs.
/// </summary>
public sealed class PdfShadowLaneComparisonTests
{
    [Fact]
    public void MatchingHeadingsCountAsSameOccurrence()
    {
        var structure = Project(("b1", 0, "1 Introduction"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        var legacy = new[] { Legacy("b1", index: 0, stableId: "@body[1]/p[0]", text: "1 Introduction") };

        var report = PdfShadowLaneComparison.CompareCompatibility(legacy, structure, decisions);

        Assert.Equal(1, report.LegacyEmitted);
        Assert.Equal(1, report.NewEmitted);
        Assert.Equal(1, report.SameOccurrence);
        Assert.False(report.HasUnexplainedDiff);
    }

    [Fact]
    public void FactTheNewLaneDidNotEmitIsMissingInNew()
    {
        var structure = Project((Structure("b1") with { StructuralScope = "appendix_table" }, "1 Introduction"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        var legacy = new[] { Legacy("b1", index: 0, stableId: "@body[1]/p[0]", text: "1 Introduction") };

        var report = PdfShadowLaneComparison.CompareCompatibility(legacy, structure, decisions);

        Assert.Equal("b1", Assert.Single(report.MissingInNew));
        Assert.True(report.HasUnexplainedDiff);
    }

    [Fact]
    public void FactOnlyTheNewLaneEmittedIsExtraInNew()
    {
        var structure = Project(("b1", 0, "1 Introduction"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);

        var report = PdfShadowLaneComparison.CompareCompatibility([], structure, decisions);

        Assert.Equal("b1", Assert.Single(report.ExtraInNew));
    }

    /// <summary>Same fact, two different DOCX occurrences - a grounding disagreement, not a text edit.</summary>
    [Fact]
    public void SameFactGroundedToADifferentParagraphIsAnchorMismatch()
    {
        var structure = Project(("b1", 0, "1 Introduction"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        var legacy = new[] { Legacy("b1", index: 7, stableId: "@body[1]/p[7]", text: "1 Introduction") };

        var report = PdfShadowLaneComparison.CompareCompatibility(legacy, structure, decisions);

        Assert.Equal("b1", Assert.Single(report.AnchorMismatch));
    }

    [Fact]
    public void SameAnchorDifferentTextIsTextMismatch()
    {
        var structure = Project(("b1", 0, "1 Introduction"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        var legacy = new[] { Legacy("b1", index: 0, stableId: "@body[1]/p[0]", text: "1. Introduction (legacy wording)") };

        var report = PdfShadowLaneComparison.CompareCompatibility(legacy, structure, decisions);

        Assert.Equal("b1", Assert.Single(report.TextMismatch));
    }

    [Fact]
    public void DifferentReviewStatusIsReviewMismatch()
    {
        var structure = Project(("b1", 0, "1 Introduction"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        var stray = Legacy("b1", index: 0, stableId: "@body[1]/p[0]", text: "1 Introduction");
        stray.DecisionStatus = HeadingDecisionStatus.AutoAcceptedEvidence;
        var legacy = new[] { stray };

        var report = PdfShadowLaneComparison.CompareCompatibility(legacy, structure, decisions);

        Assert.Equal("b1", Assert.Single(report.ReviewMismatch));
    }

    [Fact]
    public void RelativeOrderDisagreementIsOrderMismatch()
    {
        var structure = Project(("b1", 0, "1 Introduction"), ("b2", 1, "2 Overview"));
        var decisions = PdfOutputDecisionPolicy.Decide(structure);
        // Legacy sees these paragraphs in the opposite order from the frozen fact order.
        var legacy = new[]
        {
            Legacy("b1", index: 5, stableId: "@body[1]/p[5]", text: "1 Introduction"),
            Legacy("b2", index: 2, stableId: "@body[1]/p[2]", text: "2 Overview"),
        };

        var report = PdfShadowLaneComparison.CompareCompatibility(legacy, structure, decisions);

        Assert.Equal(["b1", "b2"], report.OrderMismatch.OrderBy(x => x));
    }

    /// <summary>
    /// Real StableIds (as <c>ParagraphWalker</c> produces them) carry no <c>@</c>; real gold files
    /// (the <c>.key</c>-file authoring convention <see cref="AnswerKey"/> also strips) always do.
    /// This test deliberately mismatches the two prefixes to lock the normalization - a canary run
    /// against 076 found <c>GoldMatched</c> silently stuck at 0 before this was added.
    /// </summary>
    [Fact]
    public void HierarchyIsGradedAgainstGoldNeverAgainstTheLegacyLane()
    {
        var facts = new[] { Fact("b1", 0, "1 Introduction", 1), Fact("b2", 1, "1 1 Scope", 2) };
        var structures = new[] { Structure("b1"), Structure("b2", parentId: "b1", resolution: "marker-resolved") };
        var groundings = new[]
        {
            new PdfCanonicalGrounding("b1", 10, "body[1]/p[10]", new DocxTextSpan(0, 14), "1. Introduction"),
            new PdfCanonicalGrounding("b2", 11, "body[1]/p[11]", new DocxTextSpan(0, 9), "1.1 Scope"),
        };
        var structure = PdfFinalStructureProjection.Project("sha", structures, facts, groundings);

        var gold = Gold(
            ("@body[1]/p[10]", "root", 1, null),
            ("@body[1]/p[11]", "child", 1, "root")); // gold disagrees on the child's level on purpose

        var report = PdfShadowLaneComparison.CompareHierarchy(structure, gold);

        Assert.Equal(2, report.GoldMatched);
        Assert.Equal(2, report.ResolvedLevels);
        Assert.Equal(0.5, report.LevelAccuracyGivenResolved); // root matches (1==1), child does not (2!=1)
        Assert.Equal(1, report.ResolvedParents);
        Assert.Equal(1, report.ParentAccuracyGivenResolved);
        Assert.Equal(1, report.EdgeMetrics.TruePositiveEdges);
    }

    private static PdfFinalStructure Project(params (string Id, int Order, string Text)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => Structure(item.Id)).ToArray(),
            cases.Select(item => Fact(item.Id, item.Order, item.Text, level: 1)).ToArray(),
            cases.Select(item => new PdfCanonicalGrounding(item.Id, item.Order,
                $"@body[1]/p[{item.Order}]", new DocxTextSpan(0, item.Text.Length), item.Text)).ToArray());

    private static PdfFinalStructure Project(params (PdfValidatedStructure Structure, string Text)[] cases) =>
        PdfFinalStructureProjection.Project(
            "sha",
            cases.Select(item => item.Structure).ToArray(),
            cases.Select((item, index) => Fact(item.Structure.SourceId, index, item.Text, level: 1)).ToArray(),
            cases.Select((item, index) => new PdfCanonicalGrounding(item.Structure.SourceId, index,
                $"@body[1]/p[{index}]", new DocxTextSpan(0, item.Text.Length), item.Text)).ToArray());

    private static PdfValidatedStructure Structure(string id, string? parentId = null, string resolution = "unresolved") =>
        new(id, 1, parentId, resolution, "requires_review") { StructuralScope = "document_body" };

    private static PdfHierarchyFactAudit Fact(string id, int order, string text, int? level) =>
        new(id, order, 1, "document_body", "document_body", null, null, false, null, null, null,
            level, "relationship_unresolved", [])
        {
            FactId = $"p1:{id}:s0-{text.Length}",
            HeadingSpan = new TextOffsetSpan(0, text.Length),
            SourceBlockText = text,
            HeadingText = text,
        };

    private static HeadingRecord Legacy(string sourceId, int index, string stableId, string text) => new()
    {
        Index = index,
        StableId = stableId,
        SourceId = sourceId,
        Level = 1,
        Text = text,
        HeadingSpan = new TextOffsetSpan(0, text.Length),
        Source = HeadingSource.Model,
        DecisionStatus = HeadingDecisionStatus.RequiresReview,
    };

    private static PdfHierarchyGold Gold(params (string SourceAnchor, string HeadingId, int Level, string? ParentHeadingId)[] items)
    {
        var json = $$"""
        {
          "evaluationOnly": true,
          "goldVersion": "test-v1",
          "headings": [
            {{string.Join(",\n", items.Select(item =>
                $$"""{ "headingId": "{{item.HeadingId}}", "sourceFactId": null, "sourceAnchor": "{{item.SourceAnchor}}", "goldLevel": {{item.Level}}, "goldParentId": {{(item.ParentHeadingId is null ? "null" : $"\"{item.ParentHeadingId}\"")}} }"""))}}
          ]
        }
        """;
        return PdfHierarchyGold.Load(json);
    }
}
