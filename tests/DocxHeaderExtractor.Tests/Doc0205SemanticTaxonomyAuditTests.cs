using System.Security.Cryptography;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class Doc0205SemanticTaxonomyAuditTests
{
    [Theory]
    [InlineData("EXACT_GOLD", 10, 20, 10, 20)]
    [InlineData("PARTIAL_OF_GOLD", 12, 18, 10, 20)]
    [InlineData("SUPERSET_OF_GOLD", 8, 22, 10, 20)]
    [InlineData("SAME_SOURCE_DIFFERENT_SPAN", 8, 15, 10, 20)]
    public void RelationClassifierSeparatesExactBoundaryAndOverlap(string expected, int predictionStart, int predictionEnd, int goldStart, int goldEnd)
    {
        Assert.Equal(expected, Doc0205SemanticTaxonomyAuditRunner.ClassifyRelation(
            predictionStart, predictionEnd, "body/p4", goldStart, goldEnd, "body/p4"));
    }

    [Fact]
    public void DifferentSourceDoesNotBecomeAnOfficialMatch()
    {
        Assert.Equal("OTHER", Doc0205SemanticTaxonomyAuditRunner.ClassifyRelation(10, 20, "body/p5", 10, 20, "body/p4"));
    }

    [Fact]
    public void FrozenControlsRemainHashValidAndGoldWasReadAfterFreeze()
    {
        var root = RepositoryRoot();
        foreach (var relative in new[]
        {
            "eval/a99-closed-loop/heading-target-ontology/DOC-0205",
            "eval/a99-closed-loop/qwen37-flash-visual-ceiling/DOC-0205",
        })
        {
            var directory = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            using var freeze = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "freeze.v1.json")));
            var prediction = Path.Combine(directory, "prediction.v1.json");
            var result = Path.Combine(directory, "result.v1.json");
            Assert.False(freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean());
            Assert.Equal(freeze.RootElement.GetProperty("predictionSha256").GetString(), Hash(prediction));
            Assert.Equal(freeze.RootElement.GetProperty("resultSha256").GetString(), Hash(result));
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
