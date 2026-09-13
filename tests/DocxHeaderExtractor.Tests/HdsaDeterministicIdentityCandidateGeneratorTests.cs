using DocxHeaderExtractor.Core.Models;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class HdsaDeterministicIdentityCandidateGeneratorTests
{
    [Fact]
    public void Adjacent_nodes_are_retrieved_without_deciding_a_relation()
    {
        var result = HdsaDeterministicIdentityCandidateGenerator.Generate([
            Node("A", 10, "Chapter I"), Node("B", 11, "GENERAL PROVISIONS"), Node("C", 20, "Article 1")]);

        var pair = Assert.Single(result.Candidates, item => item.Left == "A" && item.Right == "B");
        Assert.Contains("ADJACENT_ORDER", pair.Reasons);
        Assert.DoesNotContain(result.Candidates, item => item is null);
        Assert.False(result.CandidateGenerationIsRecallGate);
        Assert.False(result.GoldUsed);
    }

    [Fact]
    public void Equal_text_is_retrieved_even_when_not_adjacent()
    {
        var result = HdsaDeterministicIdentityCandidateGenerator.Generate([
            Node("A", 1, "Results"), Node("B", 2, "Body"), Node("C", 3, "RESULTS")]);

        var pair = Assert.Single(result.Candidates, item => item.Left == "A" && item.Right == "B");
        Assert.Contains("ADJACENT_ORDER", pair.Reasons);
        var repeated = Assert.Single(result.Candidates, item => item.Left == "B" && item.Right == "C");
        Assert.Contains("ADJACENT_ORDER", repeated.Reasons);
        Assert.Contains(result.Candidates, item => item.Left == "A" && item.Right == "C" &&
            item.Reasons.Contains("NORMALIZED_TEXT_AFFINITY"));
    }

    [Fact]
    public void Ordering_and_reasons_are_deterministic_under_input_permutation()
    {
        var first = HdsaDeterministicIdentityCandidateGenerator.Generate([
            Node("C", 30, "C"), Node("A", 10, "A"), Node("B", 20, "B")]);
        var second = HdsaDeterministicIdentityCandidateGenerator.Generate([
            Node("B", 20, "B"), Node("C", 30, "C"), Node("A", 10, "A")]);

        Assert.Equal(JsonSerializer.Serialize(first.Candidates), JsonSerializer.Serialize(second.Candidates));
    }

    [Fact]
    public void Generator_does_not_merge_or_emit_identity_decisions()
    {
        var result = HdsaDeterministicIdentityCandidateGenerator.Generate([
            Node("A", 1, "A"), Node("B", 2, "B")]);

        Assert.Equal(HdsaDeterministicIdentityCandidateGenerator.Version, result.GeneratorVersion);
        Assert.DoesNotContain(result.Candidates.SelectMany(item => item.Reasons), reason =>
            reason is "CONTINUATION_OF" or "SAME_SEMANTIC_REPEAT" or "DISTINCT");
    }

    private static HdsaCandidateSourceNode Node(string id, int order, string text) =>
        new(id, [$"{id}-O"], text, order);
}
