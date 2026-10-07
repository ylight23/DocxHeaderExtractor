using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfP3bPlacementStageTests
{
    [Fact]
    public async Task Already_placed_headings_do_not_trigger_recovery()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "Root", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Child", "parent-node:S0001"),
        };
        using var transport = new PlacementClassifier("not-used");

        var placed = await HeadingPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
            bound, transport, CancellationToken.None);

        Assert.Equal(bound, placed);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Recovery_adds_only_the_missing_relation_and_preserves_order()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "Root", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Child"),
            Heading("S0003", "p3", 3, "Already placed", "parent-node:S0001"),
        };
        using var transport = new PlacementClassifier(
            "{\"placements\":[{\"alias\":\"S0002\",\"parent\":\"S0001\"}]}");

        var placed = await HeadingPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
            bound, transport, CancellationToken.None);

        Assert.Single(transport.Requests);
        Assert.Equal(1, transport.Requests[0].ExpectedItemCount);
        Assert.Equal(["p1", "p2", "p3"], placed.Select(item => item.SourceId));
        Assert.Equal(["parent-node:ROOT"], placed[0].RelationHints);
        Assert.Equal(["parent-node:S0001"], placed[1].RelationHints);
        Assert.Equal(["parent-node:S0001"], placed[2].RelationHints);
    }

    [Fact]
    public async Task Invalid_or_failed_recovery_keeps_the_semantic_output_unchanged()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "Root", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Child"),
        };

        foreach (var response in new[] { "not-json", "{\"placements\":[]}" })
        {
            using var transport = new PlacementClassifier(response);
            var placed = await HeadingPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
                bound, transport, CancellationToken.None);
            Assert.Equal(bound, placed);
            Assert.Single(transport.Requests);
        }
    }

    [Fact]
    public async Task Recovery_call_is_one_boundary_cut_and_does_not_invoke_other_transport_paths()
    {
        var bound = new[]
        {
            Heading("S0001", "p1", 1, "Root", "parent-node:ROOT"),
            Heading("S0002", "p2", 2, "Child"),
        };
        using var transport = new PlacementClassifier(
            "{\"placements\":[{\"alias\":\"S0002\",\"parent\":\"ROOT\"}]}");

        await HeadingPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
            bound, transport, CancellationToken.None);

        Assert.Single(transport.Requests);
        Assert.Contains("toPlace", transport.Requests[0].UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, transport.Requests[0].ExpectedItemCount);
        Assert.Equal(1, transport.BoundaryCutCalls);
        Assert.Equal(0, transport.ClassifyCalls);
        Assert.Equal(0, transport.CritiqueCalls);
        Assert.Equal(0, transport.HierarchyCalls);
    }

    private static CanonicalSemanticBoundHeading Heading(
        string alias, string sourceId, int ordinal, string text, params string[] hints) =>
        new(alias, sourceId, ordinal, text, "SECTION", "Heading", "document_body",
            hints, 0, text.Length);

    private sealed class PlacementClassifier(string response) : IInferenceTransport
    {
        public List<PlacementRequest> Requests { get; } = [];
        public int BoundaryCutCalls { get; private set; }
        public int ClassifyCalls { get; private set; }
        public int CritiqueCalls { get; private set; }
        public int HierarchyCalls { get; private set; }

        public string ModelName => "p3b-fake";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "p3b fake transport";
        public int SharedPrefixTokens => 0;

        public Task<string> BoundaryCutAsync(
            string systemPrompt,
            string userMessage,
            CancellationToken ct = default,
            int expectedItemCount = 0)
        {
            BoundaryCutCalls++;
            Requests.Add(new(systemPrompt, userMessage, expectedItemCount));
            return Task.FromResult(response);
        }

        public void Dispose() { }
    }

    private sealed record PlacementRequest(
        string SystemPrompt,
        string UserMessage,
        int ExpectedItemCount);
}
