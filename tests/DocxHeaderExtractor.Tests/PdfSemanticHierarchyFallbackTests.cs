using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfSemanticHierarchyFallbackTests
{
    [Fact]
    public void ParseAcceptsOnlyRequestedChildIds()
    {
        var parsed = PdfSemanticHierarchyFallback.Parse(
            "{\"items\":[{\"id\":\"b2\",\"parent_id\":\"b1\"},{\"id\":\"invented\",\"parent_id\":\"b1\"}]}",
            new HashSet<string>(StringComparer.Ordinal) { "b2" });

        Assert.Equal("b1", parsed["b2"]);
        Assert.DoesNotContain("invented", parsed.Keys);
    }

    [Fact]
    public void PromptContainsOnlySourcePointersAndAllowedParents()
    {
        var line = new PdfLine(1, 700, 14, "Methods", 0.8, "", 0, 72, 420, "serif", "black");
        var context = PdfCandidateContextBuilder.Build(
            [new PdfSemanticBlock("b1", [line], PdfStyleClusterProfile.StyleOf(line), 1, 700, 700, 72, 420, line.Text)],
            [new PdfLineBlockAnnotation(line, false, false, false, false, "semantic-candidate")]);
        var prompt = PdfSemanticHierarchyFallback.BuildPrompt(
            [new PdfValidatedStructure("b1", 1, null, "unresolved", "requires_review")], context,
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["b1"] = [] });

        Assert.Contains("\"id\":\"b1\"", prompt);
        Assert.Contains("\"allowed_parent_ids\":[]", prompt);
        Assert.DoesNotContain("level", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsyncAcceptsOnlyEarlierAllowedHeadingParent()
    {
        var first = Line("Introduction", 700);
        var second = Line("Methods", 680);
        var blocks = new[] { Block("b1", first), Block("b2", second) };
        var contexts = PdfCandidateContextBuilder.Build(blocks,
        [
            new PdfLineBlockAnnotation(first, false, false, false, false, "semantic-candidate"),
            new PdfLineBlockAnnotation(second, false, false, false, false, "semantic-candidate"),
        ]);
        var headings = PdfProposalValidator.Validate(contexts,
        [
            new PdfBlockDecision("b1", PdfBlockRole.HeadingTopic, 0.9, "", new TextOffsetSpan(0, first.Text.Length)),
            new PdfBlockDecision("b2", PdfBlockRole.HeadingTopic, 0.9, "", new TextOffsetSpan(0, second.Text.Length)),
        ]);
        var structures = PdfHierarchyResolver.Resolve(headings, contexts);

        var resolved = await PdfSemanticHierarchyFallback.ResolveAsync(
            new ScriptedClassifier("{\"items\":[{\"id\":\"b1\",\"parent_id\":\"b1\"},{\"id\":\"b2\",\"parent_id\":\"b1\"}]}"),
            headings, structures, contexts);

        var firstResult = resolved.Structures.Single(item => item.SourceId == "b1");
        var secondResult = resolved.Structures.Single(item => item.SourceId == "b2");
        Assert.Null(firstResult.ParentId);
        Assert.Equal("b1", secondResult.ParentId);
        Assert.Equal("semantic-proposal-validated", secondResult.ParentResolution);
    }

    private static PdfLine Line(string text, double y) => new(
        1, y, 14, text, 0.8, "", 0, 72, 420, "serif", "black");

    private static PdfSemanticBlock Block(string id, PdfLine line) => new(
        id, [line], PdfStyleClusterProfile.StyleOf(line), line.Page, line.Y, line.Y, line.Left, line.Right, line.Text);

    private sealed class ScriptedClassifier(string response) : IHeaderClassifier
    {
        public string ModelName => "scripted";
        public int ContextSize => 4096;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default) => Task.FromResult(response);
        public void Dispose() { }
    }
}
