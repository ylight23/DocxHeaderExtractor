using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfBlockAnalystTests
{
    [Fact]
    public void ClosedSemanticRoleIsKeptSeparateFromRoutingProjection()
    {
        var block = Block("b1", "Article 4. Scope of regulation");

        var decision = Assert.Single(PdfBlockAnalyst.ParseDecisions(
            "{\"blocks\":[{\"id\":\"b1\",\"role\":\"legal_article\",\"confidence\":0.9}]}", [block]));

        Assert.Equal(PdfSemanticRole.LegalArticle, decision.SemanticRole);
        Assert.Equal(PdfBlockRole.HeadingTopic, decision.Role);
    }

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

    [Fact]
    public void ParserReadsPointerSpanButNeverLetsItCreateUnknownSourceIds()
    {
        var decisions = PdfBlockAnalyst.ParseDecisions("""
        {"blocks":[
          {"id":"b1","role":"heading_topic","confidence":0.9,"heading_span":{"start":0,"end":7}},
          {"id":"b404","role":"heading_topic","confidence":1,"heading_span":{"start":0,"end":7}}
        ]}
        """, [Block("b1", "Heading text")]);

        var decision = Assert.Single(decisions);
        Assert.Equal("b1", decision.Id);
        Assert.Equal(new DocxHeaderExtractor.Core.Models.TextOffsetSpan(0, 7), decision.HeadingSpan);
    }

    [Fact]
    public void PointerSpanParserKeepsOnlyOffsetsForKnownSourceIds()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans("""
        {"blocks":[
          {"id":"b1","heading_span":{"start":0,"end":7}},
          {"id":"b404","heading_span":{"start":0,"end":7}}
        ]}
        """, [Block("b1", "Heading body text")]);

        var span = Assert.Single(spans);
        Assert.Equal("b1", span.Id);
        Assert.Equal(new DocxHeaderExtractor.Core.Models.TextOffsetSpan(0, 7), span.Span);
    }

    [Fact]
    public void CriticParserAcceptsOnlyClosedVerdictsForKnownIds()
    {
        var verdicts = PdfBlockAnalyst.ParseCriticDecisions("""
        {"blocks":[
          {"id":"b1","decision":"keep"},
          {"id":"b2","decision":"invent_new_role"},
          {"id":"b404","decision":"reject"}
        ]}
        """, [Block("b1", "Heading"), Block("b2", "Body")]);

        var verdict = Assert.Single(verdicts);
        Assert.Equal(("b1", "keep"), verdict);
    }

    [Fact]
    public async Task AnalystTurnsOmittedIdsIntoExplicitUncertainDecision()
    {
        var blocks = new[] { Block("b1", "First heading"), Block("b2", "Second heading") };
        var classifier = new ScriptedClassifier("""{"blocks":[{"id":"b1","role":"heading_topic","confidence":0.9}]}""");

        var analysis = await PdfBlockAnalyst.AnalyzeAsync(classifier, blocks);

        Assert.Contains(analysis.Decisions, d => d.Id == "b1" && d.Role == PdfBlockRole.HeadingTopic);
        Assert.Contains(analysis.Decisions, d => d.Id == "b2" && d.Role == PdfBlockRole.Uncertain && d.Reason == "missing-model-decision");
        Assert.Equal(2, analysis.RawResponses.Count);
    }

    [Fact]
    public async Task BatchDeadlineMaterializesEveryAffectedBlockAsUncertain()
    {
        var blocks = Enumerable.Range(1, 13).Select(index => Block($"b{index}", $"Heading {index}")).ToArray();
        var classifier = new HangingClassifier();

        var analysis = await PdfBlockAnalyst.AnalyzeAsync(classifier, blocks, laneOptions: new SemanticLaneOptions(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(15), TimeSpan.FromSeconds(1)));

        Assert.Equal(13, analysis.Decisions.Count);
        Assert.All(analysis.Decisions, decision =>
        {
            Assert.Equal(PdfBlockRole.Uncertain, decision.Role);
            Assert.Equal("semantic_batch_timeout", decision.Reason);
        });
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

    private sealed class HangingClassifier : IHeaderClassifier
    {
        public string ModelName => "hanging";
        public int ContextSize => 4096;
        public string RuntimeDescription => "hanging";
        public int SharedPrefixTokens => 0;
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => "", ct);
        public void Dispose() { }
    }
}
