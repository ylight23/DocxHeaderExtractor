using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;
using System.Text.Json;

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
    public void EquivalentLegalSectionHeadingAliasNormalizesToCanonicalLegalSection()
    {
        var decision = Assert.Single(PdfBlockAnalyst.ParseDecisions(
            "{\"blocks\":[{\"id\":\"b1\",\"role\":\"legal_section_heading\",\"confidence\":0.9}]}",
            [Block("b1", "Điều 3. Scope of regulation")]));

        Assert.Equal(PdfSemanticRole.LegalSection, decision.SemanticRole);
        Assert.Equal(PdfBlockRole.HeadingTopic, decision.Role);
        Assert.Equal("legal_section_heading", decision.RawRole);
        Assert.True(decision.AliasNormalized);
    }

    [Fact]
    public void UnrelatedRoleStringRemainsUnknown()
    {
        var decision = Assert.Single(PdfBlockAnalyst.ParseDecisions(
            "{\"blocks\":[{\"id\":\"b1\",\"role\":\"legal_section_heading_variant\",\"confidence\":0.9}]}",
            [Block("b1", "Section-like text")]));

        Assert.Equal(PdfSemanticRole.Unknown, decision.SemanticRole);
        Assert.Equal(PdfBlockRole.Uncertain, decision.Role);
    }

    [Fact]
    public void ListItemSemanticRoleProjectsToListItemRoute()
    {
        var block = Block("b1", "1. First requirement");

        var decision = Assert.Single(PdfBlockAnalyst.ParseDecisions(
            "{\"blocks\":[{\"id\":\"b1\",\"role\":\"list_item_topic\",\"confidence\":0.9}]}", [block]));

        Assert.Equal(PdfSemanticRole.ListItemTopic, decision.SemanticRole);
        Assert.Equal(PdfBlockRole.ListItem, decision.Role);
    }

    [Fact]
    public void FigureTitleAndFigureCaptionRemainDistinctSemanticRoles()
    {
        var blocks = new[] { Block("title", "Figure 1. Architecture"), Block("caption", "Source: World Bank") };
        var decisions = PdfBlockAnalyst.ParseDecisions(
            "{\"blocks\":[" +
            "{\"id\":\"title\",\"role\":\"figure_title\",\"confidence\":0.9}," +
            "{\"id\":\"caption\",\"role\":\"figure_caption\",\"confidence\":0.9}" +
            "]}", blocks);

        Assert.Equal(PdfSemanticRole.FigureTitle, decisions.Single(item => item.Id == "title").SemanticRole);
        Assert.Equal(PdfSemanticRole.FigureCaption, decisions.Single(item => item.Id == "caption").SemanticRole);
        Assert.DoesNotContain(decisions, item => item.SemanticRole == PdfSemanticRole.FigureTitle &&
            item.Role == PdfBlockRole.HeadingTopic);
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
    public void PointerSpanParserRejectsCoordinateInsideSourceToken()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":3}}]}",
            [Block("b1", "Heading body text")]);

        Assert.Null(Assert.Single(spans).Span);
    }

    [Fact]
    public void PointerSpanPromptProvidesParserOwnedBoundaryMap()
    {
        var block = Block("b1", "Điều 1. Phạm vi điều chỉnh");
        using var prompt = JsonDocument.Parse(PdfBlockAnalyst.BuildPointerSpanPrompt(
            [block], new Dictionary<string, PdfCandidateContext>()));
        var payload = prompt.RootElement.GetProperty("blocks")[0];

        Assert.Equal(block.Text.Length, payload.GetProperty("source_length").GetInt32());
        var starts = payload.GetProperty("allowed_start_offsets").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray();
        var ends = payload.GetProperty("allowed_end_offsets").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray();
        Assert.Contains(0, starts);
        Assert.Contains(block.Text.Length, ends);
        Assert.Contains(block.Text.IndexOf(' ') + 1, ends);
    }

    [Fact]
    public void HighValuePointerSpanPromptProvidesExplicitSourceSpanPairs()
    {
        var block = Block("b1", "Điều 1. Phạm vi điều chỉnh");
        using var prompt = JsonDocument.Parse(PdfBlockAnalyst.BuildPointerSpanPrompt(
            [block], new Dictionary<string, PdfCandidateContext>(), new HashSet<string> { "b1" }));
        var payload = prompt.RootElement.GetProperty("blocks")[0];

        Assert.True(payload.TryGetProperty("allowed_spans", out var allowedSpans));
        Assert.False(payload.TryGetProperty("allowed_start_offsets", out _));
        var exact = allowedSpans.EnumerateArray().Single(item => item.GetProperty("end").GetInt32() == block.Text.Length);
        Assert.Equal(0, exact.GetProperty("start").GetInt32());
        Assert.Equal(block.Text, exact.GetProperty("source_slice").GetString());
    }

    [Fact]
    public void ExplicitPointerSpanParserAcceptsOnlySuppliedPair()
    {
        var block = Block("b1", "Điều 1. Phạm vi điều chỉnh");
        var expected = new TextOffsetSpan(0, 7);
        var parsed = PdfBlockAnalyst.ParsePointerSpanResponses(
            "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":7}}]}",
            [block], new HashSet<string> { "b1" });

        Assert.Equal("valid-boundary", parsed.StatusById["b1"]);
        Assert.Equal(expected, parsed.Spans.Single(item => item.Id == "b1").Span);
        var candidate = PdfSpanCandidateMenu.For(block.Text).Single(item => item.Start == expected.Start && item.End == expected.End);
        Assert.Equal(block.Text[expected.Start..expected.End], candidate.SourceSlice);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(0, 3)]
    public void ExplicitPointerSpanParserRejectsPairOutsideMenu(int start, int end)
    {
        var block = Block("b1", "Điều 1. Phạm vi điều chỉnh");
        var parsed = PdfBlockAnalyst.ParsePointerSpanResponses(
            $"{{\"blocks\":[{{\"id\":\"b1\",\"heading_span\":{{\"start\":{start},\"end\":{end}}}}}]}}",
            [block], new HashSet<string> { "b1" });

        Assert.Equal("invalid-pair", parsed.StatusById["b1"]);
        Assert.Null(parsed.Spans.Single(item => item.Id == "b1").Span);
    }

    [Fact]
    public void ExplicitPointerSpanParserRejectsModelRewrittenText()
    {
        var block = Block("b1", "Điều 1. Phạm vi điều chỉnh");
        var parsed = PdfBlockAnalyst.ParsePointerSpanResponses(
            "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":7},\"source_slice\":\"invented\"}]}",
            [block], new HashSet<string> { "b1" });

        Assert.Equal("malformed", parsed.StatusById["b1"]);
        Assert.Null(parsed.Spans.Single(item => item.Id == "b1").Span);
    }

    [Fact]
    public void Historical010ShortPrefixSpansAreRepresentableByExplicitMenu()
    {
        var path = Path.Combine(FindRepositoryRoot(), "eval", "accuracy99", "adjudication", "development", "010.review.jsonl");
        var expected = new List<(string Text, int Start, int End)>();
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("recordType", out var recordType) || recordType.GetString() != "occurrence" ||
                !root.TryGetProperty("historicalProvenanceStatus", out var status) || status.GetString() != "EXACT_REBOUND")
                continue;
            var sourceText = root.GetProperty("rawSourceText").GetString() ?? "";
            var historicalText = root.GetProperty("historicalPositiveReferences")[0].GetProperty("historicalText").GetString() ?? "";
            if (historicalText.Length >= sourceText.Length || !sourceText.StartsWith(historicalText, StringComparison.Ordinal))
                continue;
            expected.Add((sourceText, 0, historicalText.Length));
        }

        Assert.Equal(8, expected.Count);
        Assert.All(expected, item => Assert.Contains(PdfSpanCandidateMenu.For(item.Text),
            candidate => candidate.Start == item.Start && candidate.End == item.End &&
                candidate.SourceSlice == item.Text[item.Start..item.End]));
    }

    [Fact]
    public void PointerSpanParserSkipsNullItems()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[null]}", [Block("b1", "Heading body text")]);

        Assert.Empty(spans);
    }

    [Fact]
    public void PointerSpanParserSkipsNullItemsButKeepsKnownObjectItems()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[null,{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":7}}]}",
            [Block("b1", "Heading body text")]);

        var span = Assert.Single(spans);
        Assert.Equal("b1", span.Id);
        Assert.Equal(new DocxHeaderExtractor.Core.Models.TextOffsetSpan(0, 7), span.Span);
    }

    [Fact]
    public void PointerSpanParserTreatsNullSpanAsUnresolved()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":null}]}",
            [Block("b1", "Heading body text")]);

        var span = Assert.Single(spans);
        Assert.Equal("b1", span.Id);
        Assert.Null(span.Span);
    }

    [Fact]
    public void PointerSpanParserTreatsNonObjectSpanAsUnresolved()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":\"bad\"}]}",
            [Block("b1", "Heading body text")]);

        var span = Assert.Single(spans);
        Assert.Equal("b1", span.Id);
        Assert.Null(span.Span);
    }

    [Fact]
    public void PointerSpanParserIgnoresUnknownIds()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b404\",\"heading_span\":{\"start\":0,\"end\":7}}]}",
            [Block("b1", "Heading body text")]);

        Assert.Empty(spans);
    }

    [Fact]
    public void PointerSpanParserRemainsFailClosedForMalformedJson()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[", [Block("b1", "Heading body text")]);

        Assert.Empty(spans);
    }

    [Fact]
    public void PointerSpanObservabilitySeparatesNullMalformedBoundaryAndValidResponses()
    {
        var blocks = new[]
        {
            Block("null", "Heading body text"),
            Block("bad", "Heading body text"),
            Block("boundary", "Heading body text"),
            Block("valid", "Heading body text"),
        };
        var parsed = PdfBlockAnalyst.ParsePointerSpanResponses("""
        {"blocks":[
          {"id":"null","heading_span":null},
          {"id":"bad","heading_span":"bad"},
          {"id":"boundary","heading_span":{"start":0,"end":3}},
          {"id":"valid","heading_span":{"start":0,"end":7}}
        ]}
        """, blocks);

        Assert.Equal("null", parsed.StatusById["null"]);
        Assert.Equal("malformed", parsed.StatusById["bad"]);
        Assert.Equal("invalid-boundary", parsed.StatusById["boundary"]);
        Assert.Equal("valid-boundary", parsed.StatusById["valid"]);
        Assert.Equal(new DocxHeaderExtractor.Core.Models.TextOffsetSpan(0, 3), parsed.ProposedSpanById["boundary"]);
        Assert.Equal(new DocxHeaderExtractor.Core.Models.TextOffsetSpan(0, 7), parsed.Spans.Single(item => item.Id == "valid").Span);
    }

    [Fact]
    public async Task PointerSpanRequestInstrumentationRecordsPayloadTimingAndResults()
    {
        var block = Block("b1", "Điều 1. Phạm vi điều chỉnh");
        var decision = new PdfBlockDecision(
            block.Id, PdfBlockRole.HeadingTopic, 0.9, "test", SemanticRole: PdfSemanticRole.LegalArticle);
        var response = "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":" +
            block.Text.Length + "}}]}";

        var analysis = await PdfBlockAnalyst.ResolveHeadingSpansAsync(
            new ScriptedClassifier(response),
            [block], [decision], new Dictionary<string, PdfCandidateContext>());

        var request = Assert.Single(analysis.SpanRequestInstrumentation);
        Assert.False(string.IsNullOrWhiteSpace(request.RequestId));
        Assert.Equal(1, request.BatchIndex);
        Assert.Equal(["b1"], request.SourceIds);
        Assert.Equal(["LegalArticle"], request.SemanticRoles);
        Assert.Equal(1, request.SourceCount);
        Assert.True(request.PromptChars > 0);
        Assert.True(request.PromptUtf8Bytes >= request.PromptChars);
        Assert.True(request.AllowedSpanCountTotal > 0);
        Assert.True(request.AllowedSpanCountPerSource["b1"] > 0);
        Assert.True(request.SourceSliceCharsTotal > 0);
        Assert.True(request.CompletedUtc >= request.StartedUtc);
        Assert.Equal("success", request.Outcome);
        Assert.True(request.ResponseReceived);
        Assert.True(request.ResponseBytes > 0);
        Assert.Equal(["b1"], request.ReturnedIds);
        Assert.Empty(request.NullSpanIds);
        Assert.Empty(request.MalformedIds);
        Assert.Empty(request.InvalidBoundaryIds);
        Assert.Empty(request.InvalidPairIds);
    }

    [Fact]
    public async Task PointerSpanRequestInstrumentationRecordsHttpFailureWithoutChangingFailClosedBehavior()
    {
        var block = Block("b1", "Heading body text");
        var decision = new PdfBlockDecision(block.Id, PdfBlockRole.HeadingTopic, 0.9, "test");

        var analysis = await PdfBlockAnalyst.ResolveHeadingSpansAsync(
            new SpanHttpFailureClassifier(), [block], [decision], new Dictionary<string, PdfCandidateContext>());

        var request = Assert.Single(analysis.SpanRequestInstrumentation);
        Assert.Equal("provider-http-error", request.Outcome);
        Assert.Equal(429, request.HttpStatus);
        Assert.True(request.ExceptionType?.Contains("HttpRequestException", StringComparison.Ordinal));
        Assert.True(request.ResponseReceived);
        Assert.Equal("request-failed", Assert.Single(analysis.Decisions).SpanResponseStatus);
        Assert.Null(Assert.Single(analysis.Decisions).HeadingSpan);
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

    [Fact]
    public async Task SpanBatchExceptionIsCheckpointedForOnlyThatBatchAndLaterBatchesContinue()
    {
        var blocks = Enumerable.Range(1, 8).Select(index => Block($"b{index}", $"Heading {index}")).ToArray();
        var decisions = blocks.Select(block => new PdfBlockDecision(
            block.Id, PdfBlockRole.HeadingTopic, 0.9, "test")).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"dhx-span-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using var checkpoint = new PdfStageCheckpoint(path, resume: false, "test.pdf");
            var analysis = await PdfBlockAnalyst.ResolveHeadingSpansAsync(
                new FirstSpanBatchFailsClassifier(), blocks, decisions,
                new Dictionary<string, PdfCandidateContext>(), checkpoint: checkpoint);

            Assert.All(analysis.Decisions.Where(d => d.Id is "b1" or "b2" or "b3" or "b4"),
                decision => Assert.Null(decision.HeadingSpan));
            Assert.All(analysis.Decisions.Where(d => d.Id is "b5" or "b6" or "b7" or "b8"),
                decision => Assert.NotNull(decision.HeadingSpan));

            var entries = File.ReadLines(path).Select(line => JsonDocument.Parse(line)).ToArray();
            Assert.Equal(2, entries.Length);
            var failed = entries.Single(entry => entry.RootElement.GetProperty("status").GetString() == "failed");
            Assert.Equal("InvalidOperationException", failed.RootElement.GetProperty("payload").GetProperty("failureClass").GetString());
            Assert.Equal(4, failed.RootElement.GetProperty("payload").GetProperty("blocks").GetArrayLength());
            Assert.All(failed.RootElement.GetProperty("payload").GetProperty("blocks").EnumerateArray(), block =>
            {
                Assert.False(block.GetProperty("resolved").GetBoolean());
                Assert.True(block.GetProperty("lineIds").GetArrayLength() > 0);
            });
            Assert.Equal(4, entries.Single(entry => entry.RootElement.GetProperty("status").GetString() == "completed")
                .RootElement.GetProperty("payload").GetProperty("blocks").GetArrayLength());
            foreach (var entry in entries) entry.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ACompletedSpanBatchDoesNotDependOnALaterBatch()
    {
        var blocks = Enumerable.Range(1, 8).Select(index => Block($"b{index}", $"Heading {index}")).ToArray();
        var decisions = blocks.Select(block => new PdfBlockDecision(block.Id, PdfBlockRole.HeadingTopic, 0.9, "test")).ToArray();

        var analysis = await PdfBlockAnalyst.ResolveHeadingSpansAsync(
            new SecondSpanBatchFailsClassifier(), blocks, decisions, new Dictionary<string, PdfCandidateContext>());

        Assert.All(analysis.Decisions.Where(decision => decision.Id is "b1" or "b2" or "b3" or "b4"),
            decision => Assert.NotNull(decision.HeadingSpan));
        Assert.All(analysis.Decisions.Where(decision => decision.Id is "b5" or "b6" or "b7" or "b8"),
            decision => Assert.Null(decision.HeadingSpan));
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
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

    private sealed class FirstSpanBatchFailsClassifier : IHeaderClassifier
    {
        private int _spanCalls;
        public string ModelName => "scripted";
        public int ContextSize => 4096;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            if (!systemPrompt.Contains("source pointer span", StringComparison.Ordinal)) throw new NotSupportedException();
            if (Interlocked.Increment(ref _spanCalls) == 1) throw new InvalidOperationException("test span failure");
            return Task.FromResult("""{"blocks":[{"id":"b5","heading_span":{"start":0,"end":9}},{"id":"b6","heading_span":{"start":0,"end":9}},{"id":"b7","heading_span":{"start":0,"end":9}},{"id":"b8","heading_span":{"start":0,"end":9}}]}""");
        }
        public void Dispose() { }
    }

    private sealed class SecondSpanBatchFailsClassifier : IHeaderClassifier
    {
        private int _spanCalls;
        public string ModelName => "scripted";
        public int ContextSize => 4096;
        public string RuntimeDescription => "scripted";
        public int SharedPrefixTokens => 0;
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _spanCalls) == 2) throw new InvalidOperationException("test later span failure");
            return Task.FromResult("""{"blocks":[{"id":"b1","heading_span":{"start":0,"end":9}},{"id":"b2","heading_span":{"start":0,"end":9}},{"id":"b3","heading_span":{"start":0,"end":9}},{"id":"b4","heading_span":{"start":0,"end":9}}]}""");
        }
        public void Dispose() { }
    }

    private sealed class SpanHttpFailureClassifier : IHeaderClassifier
    {
        public string ModelName => "http-failure";
        public int ContextSize => 4096;
        public string RuntimeDescription => "http-failure";
        public int SharedPrefixTokens => 0;
        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default) =>
            throw new HttpRequestException("simulated 429", null, System.Net.HttpStatusCode.TooManyRequests);
        public void Dispose() { }
    }
}
