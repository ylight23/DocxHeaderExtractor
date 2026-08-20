using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfBlockAnalystTests
{
    [Fact]
    public async Task AnalystAcceptsOnlyKnownBlockIdsAndWhitelistedRoles()
    {
        var blocks = new[]
        {
            Block("b1", "AVAILABILITY OF INFORMATION"),
            Block("b2", "Recipients should retain this Information Statement for future reference."),
            Block("b3", "IDA Net Commitments Disbursements"),
        };
        var classifier = new ScriptedClassifier("""
        {"blocks":[
          {"id":"b1","role":"heading_topic","confidence":0.93,"reason":"noun phrase"},
          {"id":"b2","role":"body_sentence","confidence":0.87,"reason":"finite verb"},
          {"id":"b3","role":"table_or_chart_label","confidence":0.81,"reason":"metric label"},
          {"id":"b404","role":"heading_topic","confidence":1,"reason":"ignored"}
        ]}
        """);

        var analysis = await PdfBlockAnalyst.AnalyzeAsync(classifier, blocks);

        Assert.Contains("b1", analysis.HeadingBlockIds);
        Assert.DoesNotContain("b2", analysis.HeadingBlockIds);
        Assert.DoesNotContain("b3", analysis.HeadingBlockIds);
        Assert.DoesNotContain(analysis.Decisions, d => d.Id == "b404");
        Assert.Contains("AVAILABILITY OF INFORMATION", classifier.UserPrompt);
    }

    [Fact]
    public void ParserReturnsEmptyForMalformedJson()
    {
        var decisions = PdfBlockAnalyst.ParseDecisions("not json", [Block("b1", "Heading")]);

        Assert.Empty(decisions);
    }

    private static PdfSemanticBlock Block(string id, string text)
    {
        var line = new PdfLine(
            Page: 1,
            Y: 700,
            FontSize: 14,
            Text: text,
            BoldRatio: 0.8,
            LeadingBoldPrefix: "",
            ItalicRatio: 0,
            Left: 72,
            Right: 420,
            FontName: "serif",
            FillColorKey: "0.00,0.20,0.40");
        return new PdfSemanticBlock(
            id,
            [line],
            PdfStyleClusterProfile.StyleOf(line),
            Page: 1,
            TopY: 700,
            BottomY: 700,
            Left: 72,
            Right: 420,
            Text: text);
    }

    private sealed class ScriptedClassifier(string response) : IHeaderClassifier
    {
        public string UserPrompt { get; private set; } = "";
        public string ModelName => "scripted";
        public int ContextSize => 4096;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            UserPrompt = userMessage;
            return Task.FromResult(response);
        }

        public void Dispose()
        {
        }
    }
}
