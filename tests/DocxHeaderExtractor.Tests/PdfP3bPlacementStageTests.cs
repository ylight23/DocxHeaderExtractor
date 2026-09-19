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
        using var classifier = new PlacementClassifier("not-used");

        var placed = await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
            bound, classifier, CancellationToken.None);

        Assert.Equal(bound, placed);
        Assert.Empty(classifier.Requests);
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
        using var classifier = new PlacementClassifier(
            "{\"placements\":[{\"alias\":\"S0002\",\"parent\":\"S0001\"}]}");

        var placed = await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
            bound, classifier, CancellationToken.None);

        Assert.Single(classifier.Requests);
        Assert.Equal(1, classifier.Requests[0].ExpectedItemCount);
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
            using var classifier = new PlacementClassifier(response);
            var placed = await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
                bound, classifier, CancellationToken.None);
            Assert.Equal(bound, placed);
            Assert.Single(classifier.Requests);
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
        using var classifier = new PlacementClassifier(
            "{\"placements\":[{\"alias\":\"S0002\",\"parent\":\"ROOT\"}]}");

        await CanonicalSemanticPlacementCoordinator.PlaceUnresolvedHeadingsAsync(
            bound, classifier, CancellationToken.None);

        Assert.Single(classifier.Requests);
        Assert.Contains("toPlace", classifier.Requests[0].UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, classifier.Requests[0].ExpectedItemCount);
        Assert.Equal(1, classifier.BoundaryCutCalls);
        Assert.Equal(0, classifier.ClassifyCalls);
        Assert.Equal(0, classifier.CritiqueCalls);
        Assert.Equal(0, classifier.HierarchyCalls);
    }

    private static CanonicalSemanticBoundHeading Heading(
        string alias, string sourceId, int ordinal, string text, params string[] hints) =>
        new(alias, sourceId, ordinal, text, "SECTION", "Heading", "document_body",
            hints, 0, text.Length);

    private sealed class PlacementClassifier(string response) : IHeaderClassifier
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

        public Task<ChunkResult> ClassifyAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default)
        {
            ClassifyCalls++;
            throw new NotSupportedException();
        }

        public Task<ChunkResult> CritiqueAsync(
            string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default)
        {
            CritiqueCalls++;
            throw new NotSupportedException();
        }

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context,
            IReadOnlyList<HierarchyItem> headings,
            CancellationToken ct = default)
        {
            HierarchyCalls++;
            throw new NotSupportedException();
        }

        public void Dispose() { }
    }

    private sealed record PlacementRequest(
        string SystemPrompt,
        string UserMessage,
        int ExpectedItemCount);
}
