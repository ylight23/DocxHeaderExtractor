using System.Text.Json;
using DocxHeaderExtractor.Core.Eval;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfHierarchyFactsArtifactEvaluatorTests
{
    [Fact]
    public void SeparatesCoverageFromAccuracyAndScoresOccurrenceStableParentEdge()
    {
        var artifact = Artifact([
            Fact("root", 0, 1, 1, null),
            Fact("child", 1, 1, 2, "root"),
            Fact("plain", 2, 2, null, null),
        ]);
        var gold = Gold([
            new { headingId = "h-root", sourceFactId = "root", sourceAnchor = "@body[1]/p[1]", goldLevel = 1, goldParentId = (string?)null },
            new { headingId = "h-child", sourceFactId = "child", sourceAnchor = "@body[1]/p[2]", goldLevel = 2, goldParentId = "h-root" },
            new { headingId = "h-plain", sourceFactId = "plain", sourceAnchor = "@body[1]/p[3]", goldLevel = 2, goldParentId = "h-root" },
        ]);

        var result = PdfHierarchyFactsArtifactEvaluator.Evaluate(artifact, gold);

        Assert.Equal(3, result.InventoryHeadings);
        Assert.Equal(3, result.GoldHeadings);
        Assert.Equal(1, result.BridgeResolvedGoldHeadings);
        Assert.Equal(2, result.ResolvedLevels);
        Assert.Equal(2, result.ResolvedLevelsGoldMatched);
        Assert.Equal(0, result.ResolvedLevelsNotGoldMatched);
        Assert.Equal(1, result.LevelAccuracyGivenResolvedGold);
        Assert.Equal(1, result.DeterministicParentResolved);
        Assert.Equal(1, result.ParentAccuracyGivenResolved);
        Assert.Equal("measured", result.EdgeMetrics.Status);
        Assert.Equal(1, result.EdgeMetrics.TruePositiveEdges);
        Assert.Equal("unresolved", result.Items.Single(item => item.SourceFactId == "plain").LevelOutcome);
        Assert.Equal("unresolved", result.Items.Single(item => item.SourceFactId == "plain").ParentOutcome);
    }

    [Fact]
    public void LeavesEdgePrecisionAndParentAccuracyNotMeasuredWhenNoParentWasPredicted()
    {
        var artifact = Artifact([Fact("root", 0, 1, 1, null), Fact("child", 1, 1, 2, null)]);
        var gold = Gold([
            new { headingId = "h-root", sourceFactId = "root", sourceAnchor = "@body[1]/p[1]", goldLevel = 1, goldParentId = (string?)null },
            new { headingId = "h-child", sourceFactId = "child", sourceAnchor = "@body[1]/p[2]", goldLevel = 2, goldParentId = "h-root" },
        ]);

        var result = PdfHierarchyFactsArtifactEvaluator.Evaluate(artifact, gold);

        Assert.Equal(0, result.DeterministicParentResolved);
        Assert.Null(result.ParentAccuracyGivenResolved);
        Assert.Equal("no_predicted_edges", result.EdgeMetrics.Status);
        Assert.Null(result.EdgeMetrics.Precision);
        Assert.Null(result.EdgeMetrics.F1);
        Assert.Equal(0, result.EdgeMetrics.Recall);
    }

    [Fact]
    public void DoesNotUseTitleMatchingForMissingGoldIdentity()
    {
        var artifact = Artifact([Fact("same-title-different-occurrence", 0, 1, 1, null)]);
        var gold = Gold([
            new { headingId = "h-other", sourceFactId = "other-occurrence", sourceAnchor = "@body[1]/p[1]", goldLevel = 1, goldParentId = (string?)null },
        ]);

        var result = PdfHierarchyFactsArtifactEvaluator.Evaluate(artifact, gold);

        Assert.Equal(0, result.GoldIdentityResolved);
        Assert.Contains(result.Items, item => item.SourceFactId == "same-title-different-occurrence" && item.LevelOutcome == "gold_missing");
        Assert.Contains(result.Items, item => item.SourceFactId == "other-occurrence" && item.LevelOutcome == "gold_missing");
    }

    [Fact]
    public void ReportsResolvedLevelsThatCannotBeBridgedToGoldSeparately()
    {
        var artifact = Artifact([Fact("root", 0, 1, 1, null), Fact("upstream-fp", 1, 1, 2, null)]);
        var gold = Gold([
            new { headingId = "h-root", sourceFactId = "root", sourceAnchor = "@body[1]/p[1]", goldLevel = 1, goldParentId = (string?)null },
        ]);

        var result = PdfHierarchyFactsArtifactEvaluator.Evaluate(artifact, gold);

        Assert.Equal(2, result.ResolvedLevels);
        Assert.Equal(1, result.ResolvedLevelsGoldMatched);
        Assert.Equal(1, result.ResolvedLevelsNotGoldMatched);
        Assert.Equal(1, result.CorrectResolvedLevels);
        Assert.Equal(1, result.LevelAccuracyGivenResolvedGold);
    }

    private static string Artifact(object[] facts) => JsonSerializer.Serialize(new
    {
        hierarchyFacts = new { items = facts },
    });

    private static object Fact(string id, int sourceOrder, int page, int? resolvedLevel, string? parent) => new
    {
        Id = id,
        SourceOrder = sourceOrder,
        Page = page,
        StructuralScope = "document_body",
        DocumentRegime = "document_body",
        MarkerFamily = "arabic",
        MarkerDepth = resolvedLevel,
        MarkerIsPath = true,
        MarkerPath = resolvedLevel?.ToString(),
        PreviousValidatedId = (string?)null,
        MarkerPrefixParentCandidate = parent,
        ResolvedLevel = resolvedLevel,
        ParentResolution = parent is null ? "relationship_unresolved" : "marker_prefix_parent_candidate",
        Evidence = new[] { "validated_source_span" },
    };

    private static string Gold(object[] headings) => JsonSerializer.Serialize(new
    {
        evaluationOnly = true,
        goldVersion = "test-v1",
        headings,
    });
}
