using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Docx;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A DOCX paragraph can hold a heading and the body text after it ("Article 8. Prohibited acts", a line break, "1. Using cyberspace ..."). The
/// model says where the heading ends (headingExtent on its SELECT); the program shows it the offsets of the line breaks and the formatting
/// runs, checks the extent against the paragraph's text without moving it, and the heading text is that extent, not the whole paragraph.
/// </summary>
public sealed class HeadingExtentTests : IDisposable
{
    const string Heading = "Article 8. Prohibited acts";
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-extent-" + Guid.NewGuid().ToString("N"));

    public HeadingExtentTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Chapter I; the article heading and its body in one paragraph, split by w:br, the heading bold; a closing paragraph.</summary>
    string Mixed()
    {
        var path = Path.Combine(dir, "mixed.docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(
            new Paragraph(new Run(new Text("Chapter I"))),
            new Paragraph(
                new Run(new RunProperties(new Bold()), new Text(Heading)),
                new Run(new Break()),
                new Run(new Text("1. Using cyberspace to do the following:")),
                new Run(new Break()),
                new Run(new Text("a) An act under clause 1 of Article 18."))),
            new Paragraph(new Run(new Text("Closing paragraph.")))));
        return path;
    }

    static string Answer(string alias, object? extent, string version = AgentProtocol.SelectExtentVersion) => JsonSerializer.Serialize(new
    {
        protocolVersion = version, status = "COMPLETE",
        decisions = new object[] { extent is null ? new { alias, decision = "SELECT", evidenceRefs = new[] { alias } } : new { alias, decision = "SELECT", evidenceRefs = new[] { alias }, headingExtent = extent } },
    });

    const string Text = Heading + " 1. Using cyberspace to do the following:";
    static readonly Dictionary<string, int> Shown = new() { ["L1"] = 0 };
    static readonly Dictionary<string, string> Texts = new() { ["L1"] = Text };

    // ---------------- the answer contract ----------------

    [Fact]
    public void An_extent_whose_text_is_the_target_text_at_its_offsets_is_taken_as_given()
    {
        var reply = AgentProtocol.ParseSelect(Answer("L1", new { start = 0, end = Heading.Length, text = Heading }), Shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts);

        Assert.True(reply.Ok, reply.Failure);
        Assert.Equal(new AgentProtocol.HeadingExtent(0, Heading.Length, Heading), reply.Extents!["L1"]);
    }

    [Fact]
    public void A_select_without_an_extent_is_still_a_whole_line_heading()
    {
        var reply = AgentProtocol.ParseSelect(Answer("L1", null), Shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts);

        Assert.True(reply.Ok, reply.Failure);
        Assert.Empty(reply.Extents!);
    }

    [Theory]
    [InlineData(0, 29, Heading, "HEADING_EXTENT_TEXT_MISMATCH")]
    [InlineData(0, 400, Heading, "HEADING_EXTENT_OUT_OF_RANGE")]
    [InlineData(5, 3, Heading, "HEADING_EXTENT_OUT_OF_RANGE")]
    [InlineData(0, 27, Heading + " ", "HEADING_EXTENT_EDGE_WHITESPACE")]
    [InlineData(0, 24, "Article 8. Prohibited ac", "HEADING_EXTENT_NOT_ON_BOUNDARY")]
    public void An_extent_that_does_not_match_the_text_is_refused_and_never_moved(int start, int end, string text, string code)
    {
        var reply = AgentProtocol.ParseSelect(Answer("L1", new { start, end, text }), Shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts);

        Assert.False(reply.Ok);
        Assert.Equal(code, reply.Failure);
        Assert.Null(reply.Extents);
    }

    [Fact]
    public void A_refused_extent_is_answered_with_what_the_program_measured()
    {
        var reply = AgentProtocol.ParseSelect(Answer("L1", new { start = 0, end = 29, text = Heading }), Shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts);
        var message = AgentProtocol.RepairMessage("PROTOCOL:" + reply.Failure, reply.Missing, reply.Detail);

        Assert.Contains("the text at [0,29) is \"Article 8. Prohibited acts 1.\"", message, StringComparison.Ordinal);
        Assert.Contains("occurs in the target's text at [0,26)", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"start\":0,\"end\":26}")]
    [InlineData("{\"start\":\"0\",\"end\":26,\"text\":\"x\"}")]
    [InlineData("{\"start\":0,\"end\":26,\"text\":\"Article 8. Prohibited acts\",\"level\":1}")]
    public void An_extent_of_the_wrong_shape_is_refused(string extent)
    {
        var reply = AgentProtocol.ParseSelect(Answer("L1", JsonNode.Parse(extent)), Shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts);

        Assert.False(reply.Ok);
        Assert.Equal("HEADING_EXTENT_INVALID", reply.Failure);
    }

    [Fact]
    public void An_extent_on_a_line_that_is_not_selected_is_refused()
    {
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.SelectExtentVersion, status = "COMPLETE",
            decisions = new[] { new { alias = "L1", decision = "NOT_SELECT", headingExtent = new { start = 0, end = Heading.Length, text = Heading } } },
        });

        Assert.Equal("HEADING_EXTENT_NOT_ON_SELECT", AgentProtocol.ParseSelect(answer, Shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts).Failure);
    }

    [Fact]
    public void An_unknown_alias_with_an_extent_is_an_unexpected_alias_not_an_exception()
    {
        var extent = new { start = 0, end = Heading.Length, text = Heading };
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.SelectExtentVersion, status = "COMPLETE",
            decisions = new object[] { new { alias = "L1", decision = "NOT_SELECT" }, new { alias = "L9", decision = "SELECT", evidenceRefs = new[] { "L1" }, headingExtent = extent } },
        });
        var shown = new Dictionary<string, int> { ["L1"] = 0, ["L9"] = 1 };

        Assert.Equal("UNEXPECTED_ALIAS", AgentProtocol.ParseSelect(answer, shown, ["L1"], AgentProtocol.SelectExtentVersion, Texts).Failure);
        // A target the caller gave no text for is refused the same way, never looked up blind.
        Assert.Equal("UNEXPECTED_ALIAS", AgentProtocol.ParseSelect(Answer("L1", extent), Shown, ["L1"], AgentProtocol.SelectExtentVersion, new Dictionary<string, string>()).Failure);
    }

    [Fact]
    public void The_pdf_answer_format_has_no_extent_and_is_unchanged()
    {
        var extent = new { start = 0, end = Heading.Length, text = Heading };

        Assert.Equal("DECISION_KEYS_INVALID", AgentProtocol.ParseSelect(Answer("L1", extent, AgentProtocol.SelectObjectsVersion), Shown, ["L1"]).Failure);
        Assert.True(AgentProtocol.ParseSelect(Answer("L1", null, AgentProtocol.SelectObjectsVersion), Shown, ["L1"]).Ok);
        Assert.Equal("PROTOCOL_VERSION_INVALID", AgentProtocol.ParseSelect(Answer("L1", null), Shown, ["L1"]).Failure);
    }

    // ---------------- the source representation ----------------

    [Fact]
    public void The_facts_give_the_line_breaks_the_pieces_between_them_and_the_formatting_runs_with_their_offsets()
    {
        var source = DocxAgentSource.Load(Mixed(), null, out _)!;
        var line = source.Lines.Single(l => l.Text.StartsWith(Heading, StringComparison.Ordinal));
        var text = source.Facts[line.Alias]["text"]!.AsObject();

        Assert.Equal(2, text["lineBreaks"]!.GetValue<int>());
        Assert.Equal(Heading.Length, text["lineBreakOffsets"]![0]!.GetValue<int>());
        Assert.Equal(' ', line.Text[Heading.Length]);
        var first = text["pieces"]![0]!;
        Assert.Equal(0, first["start"]!.GetValue<int>());
        Assert.Equal(Heading.Length, first["end"]!.GetValue<int>());
        Assert.Equal(Heading, first["text"]!.GetValue<string>());
        Assert.Equal(3, text["pieces"]!.AsArray().Count);
        foreach (var piece in text["pieces"]!.AsArray())
            Assert.Equal(line.Text[piece!["start"]!.GetValue<int>()..piece["end"]!.GetValue<int>()], piece["text"]!.GetValue<string>());
        var bold = text["formatRuns"]!.AsArray().First()!;
        Assert.True(bold["bold"]!.GetValue<bool>());
        Assert.Equal(0, bold["start"]!.GetValue<int>());
        Assert.Equal(Heading.Length, bold["end"]!.GetValue<int>());
        Assert.False(text["formatRuns"]!.AsArray()[1]!["bold"]!.GetValue<bool>());
    }

    [Fact]
    public void A_paragraph_of_one_piece_and_one_format_carries_no_offsets()
    {
        var source = DocxAgentSource.Load(Mixed(), null, out _)!;
        var text = source.Facts[source.Lines.Single(l => l.Text == "Chapter I").Alias]["text"]!.AsObject();

        Assert.Null(text["lineBreakOffsets"]);
        Assert.Null(text["pieces"]);
        Assert.Null(text["formatRuns"]);
    }

    // ---------------- source binding: the heading text is the extent ----------------

    [Fact]
    public async Task The_heading_of_a_mixed_paragraph_is_its_extent_and_the_binder_accepts_it()
    {
        var path = Mixed();
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot;
        var article = source.Atoms.Single(a => a.Text.StartsWith(Heading, StringComparison.Ordinal));
        var selector = new Fixed(new HeadingSelection(new HashSet<string> { article.Alias }, 1, 1, [], ["{}"], 1, 0.0,
            HeadingExtents: new Dictionary<string, TextOffsetSpan> { [article.Alias] = new(0, Heading.Length) }));

        var decided = await new SelectedLinesHeadingAuthority(selector, path, "sk-test", new Dictionary<string, string>(), null, () => { })
            .DecideAsync(source, CancellationToken.None);

        var decision = Assert.Single(decided.Decisions);
        Assert.Equal(new TextOffsetSpan(0, Heading.Length), decision.HeadingSpan);
        Assert.Equal(Heading, Assert.Single(decision.Parts!).Text);
        Assert.Equal(Heading, Assert.Single(decided.BoundHeadings).Text);
        var validated = Assert.Single(HeadingDecisionBinder.BindAndValidate(source.OccurrenceContexts, decided.Decisions));
        Assert.Equal(Heading, source.OccurrenceContexts[validated.SourceId].RawText[validated.HeadingSpan.Start..validated.HeadingSpan.End]);
    }

    [Fact]
    public async Task A_selected_line_without_an_extent_stays_a_whole_line_heading()
    {
        var path = Mixed();
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot;
        var chapter = source.Atoms.Single(a => a.Text == "Chapter I");

        var decided = await new SelectedLinesHeadingAuthority(new Fixed(new HeadingSelection(new HashSet<string> { chapter.Alias }, 1, 1, [], ["{}"], 1, 0.0)),
            path, "sk-test", new Dictionary<string, string>(), null, () => { }).DecideAsync(source, CancellationToken.None);

        Assert.Equal(new TextOffsetSpan(0, "Chapter I".Length), Assert.Single(decided.Decisions).HeadingSpan);
    }

    // ---------------- the three cases of an extent at the authority ----------------

    static HeadingSelection Selected(IEnumerable<string> aliases, Dictionary<string, TextOffsetSpan>? extents = null, IReadOnlyList<IReadOnlyList<string>>? groups = null) =>
        new(new HashSet<string>(aliases), 1, 1, [], ["{}"], 1, 0.0, Groups: groups, HeadingExtents: extents);

    async Task<HeadingAuthorityResult> Decide(HeadingSelection selection)
    {
        var path = Mixed();
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot;
        return await new SelectedLinesHeadingAuthority(new Fixed(selection), path, "sk-test", new Dictionary<string, string>(), null, () => { })
            .DecideAsync(source, CancellationToken.None);
    }

    string AliasOf(string text)
    {
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(Mixed())).Snapshot;
        return source.Atoms.Single(a => a.Text.StartsWith(text, StringComparison.Ordinal)).Alias;
    }

    [Fact]
    public async Task No_extent_is_the_whole_line_and_a_valid_extent_is_used_exactly()
    {
        var article = AliasOf(Heading);
        var whole = await Decide(Selected([article]));
        var exact = await Decide(Selected([article], new() { [article] = new(0, Heading.Length) }));

        Assert.Equal(new TextOffsetSpan(0, Assert.Single(whole.BoundHeadings).Text.Length), Assert.Single(whole.Decisions).HeadingSpan);
        Assert.StartsWith(Heading + " 1. Using cyberspace", Assert.Single(whole.BoundHeadings).Text, StringComparison.Ordinal);
        Assert.Equal(Heading, Assert.Single(exact.BoundHeadings).Text);
        Assert.Equal(new TextOffsetSpan(0, Heading.Length), Assert.Single(exact.Decisions).HeadingSpan);
    }

    [Theory]
    [InlineData(0, 4000)]
    [InlineData(-1, 10)]
    [InlineData(30, 30)]
    [InlineData(40, 12)]
    public async Task An_extent_outside_its_line_is_rejected_and_never_replaced_by_the_whole_line(int start, int end)
    {
        var article = AliasOf(Heading);

        var decided = await Decide(Selected([article], new() { [article] = new(start, end) }));

        Assert.Empty(decided.Decisions);
        Assert.Empty(decided.BoundHeadings);
    }

    [Fact]
    public async Task A_rejected_extent_drops_only_its_own_line_from_a_heading_over_several_lines()
    {
        var chapter = AliasOf("Chapter I");
        var article = AliasOf(Heading);
        var selection = Selected([chapter, article], new() { [article] = new(5, 9999) }, [[chapter, article]]);

        var decided = await Decide(selection);

        var heading = Assert.Single(decided.BoundHeadings);
        Assert.Equal("Chapter I", heading.Text);
        Assert.Equal(chapter, Assert.Single(heading.Parts).Alias);
    }

    [Fact]
    public async Task An_extent_for_a_line_that_is_not_selected_makes_no_heading()
    {
        var chapter = AliasOf("Chapter I");
        var article = AliasOf(Heading);

        var decided = await Decide(Selected([chapter], new() { [article] = new(0, Heading.Length) }));

        Assert.Equal("Chapter I", Assert.Single(decided.BoundHeadings).Text);
    }

    // ---------------- end to end over a fake provider ----------------

    /// <summary>Reads the facts, then selects the mixed paragraph. The first answer's extent runs three characters past the heading when
    /// <paramref name="firstWrong"/>; after the refusal the model answers with the heading alone.</summary>
    static Func<JsonObject, int, (HttpStatusCode, string)> SelectsTheArticle(bool firstWrong) => (body, post) =>
    {
        var messages = body["messages"]!.AsArray();
        var payload = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!;
        var targets = payload["targets"]!.AsArray().Select(t => (Alias: t!["alias"]!.GetValue<string>(), Text: t["text"]!.GetValue<string>())).ToArray();
        if (!messages.Any(m => m!["role"]!.GetValue<string>() == "tool"))
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { tool_calls = FakeOpenRouter.FactsCalls(targets.Select(t => t.Alias).ToArray()) }, "tool_calls"));
        var refused = messages.Any(m => m!["role"]!.GetValue<string>() == "user" && m["content"] is JsonValue v && v.GetValue<string>().Contains("HEADING_EXTENT", StringComparison.Ordinal));
        var end = firstWrong && !refused ? Heading.Length + 3 : Heading.Length;
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = FakeOpenRouter.AnswerVersion(body), status = "COMPLETE",
            decisions = targets.Select(t => t.Text.StartsWith(Heading, StringComparison.Ordinal)
                ? (object)new { alias = t.Alias, decision = "SELECT", evidenceRefs = new[] { t.Alias }, headingExtent = new { start = 0, end, text = Heading } }
                : new { alias = t.Alias, decision = "NOT_SELECT" }),
        });
        return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = answer }, "stop"));
    };

    [Fact]
    public async Task The_selector_hands_the_models_extent_to_the_route()
    {
        var path = Mixed();
        var handler = new FakeOpenRouter(SelectsTheArticle(firstWrong: false));

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        var article = DocxAgentSource.Load(path, null, out _)!.Lines.Single(l => l.Text.StartsWith(Heading, StringComparison.Ordinal)).Alias;
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(new TextOffsetSpan(0, Heading.Length), selection.HeadingExtents![article]);
        Assert.Contains("\"headingExtents\":{\"" + article + "\":{\"start\":0,\"end\":26,\"text\":\"Article 8. Prohibited acts\"}}", selection.ModelAnswers[0], StringComparison.Ordinal);
        Assert.Equal(2, handler.Posts);
    }

    [Fact]
    public async Task A_wrong_extent_goes_back_to_the_model_with_the_measured_text_and_its_own_answer_is_taken()
    {
        var path = Mixed();
        var handler = new FakeOpenRouter(SelectsTheArticle(firstWrong: true));

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        var article = DocxAgentSource.Load(path, null, out _)!.Lines.Single(l => l.Text.StartsWith(Heading, StringComparison.Ordinal)).Alias;
        Assert.Equal(3, handler.Posts);
        var repair = handler.Bodies[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("PROTOCOL:HEADING_EXTENT_TEXT_MISMATCH", repair, StringComparison.Ordinal);
        Assert.Contains("the text at [0,29) is \"Article 8. Prohibited acts 1.\"", repair, StringComparison.Ordinal);
        Assert.Equal(new TextOffsetSpan(0, Heading.Length), selection.HeadingExtents![article]);
    }

    private sealed class Fixed(HeadingSelection selection) : IHeadingSelector
    {
        public string SelectorId => "fixed-extent-test";
        public Task<HeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct) => Task.FromResult(selection);
    }
}
