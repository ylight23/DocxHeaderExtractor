using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
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
        Assert.Equal(new DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan(0, 7), decision.HeadingSpan);
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
        Assert.Equal(new DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan(0, 7), span.Span);
    }

    [Fact]
    public void PointerSpanParserSkipsNullItemsButKeepsKnownObjectItems()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[null,{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":7}}]}",
            [Block("b1", "Heading body text")]);

        var span = Assert.Single(spans);
        Assert.Equal("b1", span.Id);
        Assert.Equal(new DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan(0, 7), span.Span);
    }

    [Fact]
    public void PointerSpanParserKeepsNullOrMalformedSpansUnresolved()
    {
        var spans = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b1\",\"heading_span\":null},{\"id\":\"b2\",\"heading_span\":\"bad\"}]}",
            [Block("b1", "Heading one"), Block("b2", "Heading two")]);

        Assert.Equal(2, spans.Count);
        Assert.All(spans, item => Assert.Null(item.Span));
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
    public void PointerSpanParserPreservesFailClosedMalformedJsonBehavior()
    {
        Assert.Empty(PdfBlockAnalyst.ParsePointerSpans("not json", [Block("b1", "Heading")]));
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
    public void SemanticExtentPromptUsesBoundedParserOwnedCandidatesForHighValueRole()
    {
        var block = Block("b1", "Article 4. Scope of regulation");
        var menu = PdfSemanticExtentCandidateMenu.For(block.Text);
        var whole = Assert.Single(menu.Where(candidate => candidate.Kind == "whole_paragraph"));
        using var prompt = JsonDocument.Parse(PdfBlockAnalyst.BuildPointerSpanPrompt(
            [block], new Dictionary<string, PdfCandidateContext>(), new HashSet<string> { "b1" }));
        var payload = prompt.RootElement.GetProperty("blocks")[0];

        Assert.False(payload.TryGetProperty("allowed_start_offsets", out _));
        var candidates = payload.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.InRange(candidates.Length, 1, PdfSemanticExtentCandidateMenu.MaxCandidatesPerSource);
        Assert.Contains(candidates, candidate =>
            candidate.GetProperty("id").GetString() == whole.Id &&
            candidate.GetProperty("start").GetInt32() == whole.Start &&
            candidate.GetProperty("end").GetInt32() == whole.End &&
            candidate.GetProperty("preview").GetString() == block.Text[whole.Start..whole.End]);
    }

    [Fact]
    public void SemanticExtentParserResolvesCandidateIdAndRejectsModelText()
    {
        var block = Block("b1", "Article 4. Scope of regulation");
        var menu = PdfSemanticExtentCandidateMenu.For(block.Text);
        var selected = Assert.Single(menu.Where(candidate => candidate.Kind == "whole_paragraph"));
        var menus = new Dictionary<string, IReadOnlyList<PdfSemanticExtentCandidate>>
        {
            ["b1"] = menu,
        };

        var valid = PdfBlockAnalyst.ParsePointerSpans(
            $"{{\"blocks\":[{{\"id\":\"b1\",\"candidate_id\":\"{selected.Id}\"}}]}}",
            [block], new HashSet<string> { "b1" }, menus);
        var unknown = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b1\",\"candidate_id\":\"c404\"}]}",
            [block], new HashSet<string> { "b1" }, menus);
        var injected = PdfBlockAnalyst.ParsePointerSpans(
            "{\"blocks\":[{\"id\":\"b1\",\"candidate_id\":\"c1\",\"heading_text\":\"invented\"}]}",
            [block], new HashSet<string> { "b1" }, menus);

        Assert.Equal(new DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan(selected.Start, selected.End),
            Assert.Single(valid).Span);
        Assert.Null(Assert.Single(unknown).Span);
        Assert.Null(Assert.Single(injected).Span);
    }

    [Fact]
    public void SemanticExtentCandidateMenuIsBoundedAndSourceGrounded()
    {
        var source = "Section 1. A concise title followed by body prose. More body.";
        var candidates = PdfSemanticExtentCandidateMenu.For(source);

        Assert.InRange(candidates.Count, 1, PdfSemanticExtentCandidateMenu.MaxCandidatesPerSource);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal(source[candidate.Start..candidate.End], candidate.Preview);
            Assert.InRange(candidate.Start, 0, source.Length);
            Assert.InRange(candidate.End, candidate.Start + 1, source.Length);
        });
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
            Assert.Equal(2, analysis.SpanRequestInstrumentation.Count);
            var requestFailure = analysis.SpanRequestInstrumentation[0];
            Assert.Equal("provider-fault", requestFailure.Outcome);
            Assert.Equal("System.InvalidOperationException", requestFailure.ExceptionType);
            Assert.False(requestFailure.ResponseReceived);
            Assert.Equal(4, requestFailure.SourceCount);
            Assert.Equal(4, requestFailure.SourceIds.Count);
            Assert.True(requestFailure.PromptUtf8Bytes > 0);
            Assert.All(analysis.SpanRequestInstrumentation, item => Assert.True(item.ElapsedMs >= 0));

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
}
