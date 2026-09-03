using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfSemanticClusterAnalystTests
{
    [Fact]
    public async Task AnalystPromotesOnlyValidatedHeadingTopicClusters()
    {
        var lines = new List<PdfLine>();
        for (var i = 0; i < 12; i++)
            lines.Add(Line("Recipients should retain this information statement for future reference.", 10, "body", page: i / 3 + 1, y: 500));

        lines.AddRange([
            Line("AVAILABILITY OF INFORMATION", 14, "bold", page: 1, y: 700),
            Line("CASH AND INVESTMENTS", 14, "bold", page: 2, y: 700),
            Line("PROMISSORY NOTES RECEIVABLE", 14, "bold", page: 3, y: 700),
            Line("TOTAL 42 71 89", 12, "chart", page: 1, y: 430),
            Line("YoY 12% 13%", 12, "chart", page: 2, y: 430),
            Line("Top 10 Countries", 12, "chart", page: 3, y: 430),
        ]);

        var profile = PdfStyleClusterProfile.Learn(lines);
        var classifier = new ScriptedClassifier("""
        {"clusters":[
          {"id":"c1","role":"heading_topic","confidence":0.91,"reason":"parallel noun phrases"},
          {"id":"c2","role":"table_or_chart_label","confidence":0.88,"reason":"metric labels"},
          {"id":"unknown","role":"heading_topic","confidence":1.0,"reason":"ignored"}
        ]}
        """);

        var analysis = await PdfSemanticClusterAnalyst.AnalyzeAsync(classifier, profile, lines);

        Assert.Contains(new PdfStyleKey(14, "bold", ""), analysis.HeadingStyles);
        Assert.DoesNotContain(new PdfStyleKey(12, "chart", ""), analysis.HeadingStyles);
        Assert.DoesNotContain(analysis.Decisions, d => d.Id == "unknown");
        Assert.Contains("AVAILABILITY OF INFORMATION", classifier.UserPrompt);
        Assert.Contains("Recipients should", classifier.UserPrompt);
    }

    [Fact]
    public void ParserReturnsNoDecisionForMalformedJson()
    {
        var samples = new[]
        {
            new PdfSemanticClusterSample(
                "c1",
                new PdfStyleKey(14, "bold", ""),
                Lines: 3,
                Pages: 3,
                Characters: 60,
                Examples: ["AVAILABILITY OF INFORMATION"]),
        };

        var decisions = PdfSemanticClusterAnalyst.ParseDecisions("not json", samples);

        Assert.Empty(decisions);
    }

    private static PdfLine Line(string text, double fontSize, string font, int page, double y) => new(
        Page: page,
        Y: y,
        FontSize: fontSize,
        Text: text,
        BoldRatio: font == "bold" ? 1 : 0,
        LeadingBoldPrefix: "",
        ItalicRatio: 0,
        Left: 72,
        Right: 480,
        FontName: font,
        FillColorKey: "");

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
