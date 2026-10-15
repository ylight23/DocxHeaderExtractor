using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Core facts coverage. get_line_facts gives every target its core facts; the runner keeps a ledger of the targets it returned them for, apart from
/// the detail evidence the model chose to read. A DOCX unit is COMPLETE only when every target's core facts were returned: an answer that falls short
/// goes back once, naming the targets, and if they stay uncovered or the unit has no room left to read them the unit is INCOMPLETE and none of its
/// decisions is used. Which detail a decision needs is never the program's call.
/// </summary>
public sealed class FactsCoverageTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-coverage-" + Guid.NewGuid().ToString("N"));

    public FactsCoverageTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    string Long(int paragraphs = 48) => DocxTestDocument.CreateLong(Path.Combine(dir, "long.docx"), paragraphs);

    static object[] FactsPage(string[] targets, int offset) => [new
    {
        index = 0, id = "call_facts_" + offset, type = "function",
        function = new { name = "get_line_facts", arguments = JsonSerializer.Serialize(new { first = targets[0], last = targets[^1], offset }) },
    }];

    static (HttpStatusCode, string) Answer(JsonObject body, string[] targets) => (HttpStatusCode.OK, FakeOpenRouter.Stream(new
    {
        content = JsonSerializer.Serialize(new
        {
            protocolVersion = FakeOpenRouter.AnswerVersion(body), status = "COMPLETE",
            decisions = targets.Select((a, i) => i == 0 ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } } : new { alias = a, decision = "NOT_SELECT" }),
        }),
    }, "stop"));

    /// <summary>Reads the first page of facts only, then answers. After the program sends the answer back it either repeats it or, when
    /// <paramref name="readsTheRest"/>, reads the rest first.</summary>
    static Func<JsonObject, int, (HttpStatusCode, string)> FirstPageOnly(bool readsTheRest) => (body, post) =>
    {
        var messages = body["messages"]!.AsArray();
        var targets = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
        if (!messages.Any(m => m!["role"]!.GetValue<string>() == "tool"))
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { tool_calls = FactsPage(targets, 0) }, "tool_calls"));
        var repair = messages.Select((m, i) => (m, i)).LastOrDefault(x => x.m!["role"]!.GetValue<string>() == "user" && x.m["content"] is JsonValue v
            && v.GetValue<string>().Contains("TOOL_FACTS_INCOMPLETE", StringComparison.Ordinal));
        if (repair.m is not null && readsTheRest && !messages.Skip(repair.i + 1).Any(m => m!["role"]!.GetValue<string>() == "tool"))
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { tool_calls = FactsPage(targets, FakeOpenRouter.MaxFactsPerCall) }, "tool_calls"));
        return Answer(body, targets);
    };

    [Fact]
    public async Task A_complete_answer_that_leaves_targets_without_core_facts_goes_back_once_and_then_the_unit_is_incomplete()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FirstPageOnly(readsTheRest: false));

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        var decide = DocxAgentSource.Load(path, null, out _)!.Scope(0).Decide;
        // facts, answer (sent back), the same answer again: refused, and none of its decisions is used.
        Assert.Equal(3, handler.Posts);
        Assert.Equal(0, selection.UnitsComplete);
        Assert.Empty(selection.SelectedAliases);
        var failure = Assert.Single(selection.UnitFailures);
        Assert.Contains(":INCOMPLETE:COVERAGE:CORE_FACTS_MISSING:", failure, StringComparison.Ordinal);
        Assert.EndsWith(":" + (decide.Length - FakeOpenRouter.MaxFactsPerCall) + "_TARGETS_WITHOUT_CORE_FACTS", failure, StringComparison.Ordinal);
        var repair = handler.Bodies[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("PROTOCOL:TOOL_FACTS_INCOMPLETE", repair, StringComparison.Ordinal);
        Assert.Contains(decide[FakeOpenRouter.MaxFactsPerCall], repair, StringComparison.Ordinal);
        Assert.All(decide.Take(FakeOpenRouter.MaxFactsPerCall), a => Assert.DoesNotContain(a, repair, StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_model_reads_the_missing_facts_after_the_refusal_its_answer_is_taken()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FirstPageOnly(readsTheRest: true));

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        var decide = DocxAgentSource.Load(path, null, out _)!.Scope(0).Decide;
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(1, selection.UnitsComplete);
        Assert.Equal(new HashSet<string> { decide[0] }, selection.SelectedAliases);
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!;
        Assert.Empty(audit["coreFactsCoverage"]!["targetsWithoutFacts"]!.AsArray());
        Assert.Equal(1, audit["coreFactsCoverage"]!["factsResultsCut"]!.GetValue<int>());
    }

    [Fact]
    public async Task Facts_that_cover_every_target_send_nothing_back()
    {
        var path = Long(10);
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        Assert.Equal(2, handler.Posts);
        Assert.Equal(1, selection.UnitsComplete);
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!;
        Assert.Empty(audit["coreFactsCoverage"]!["targetsWithoutFacts"]!.AsArray());
        Assert.Empty(audit["detailEvidenceCoverage"]!["aliasesRead"]!.AsArray());
    }

    [Fact]
    public async Task A_unit_with_no_room_left_to_read_the_missing_facts_is_incomplete_and_is_not_sent_back()
    {
        // 48 paragraphs of about 800 characters: the unit's tool bytes go to span and context records over three rounds, and the missing core facts
        // (a facts result can be 16 KB) no longer fit in what is left of the unit's 64 KB.
        var path = Path.Combine(dir, "wide.docx");
        using (var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(Enumerable.Range(0, 48).Select(i =>
                (DocumentFormat.OpenXml.OpenXmlElement)new Paragraph(new Run(new Text($"P{i:00} " + string.Concat(Enumerable.Repeat($"word{i:00} ", 115))))))));
        }
        var handler = new FakeOpenRouter((body, post) =>
        {
            var messages = body["messages"]!.AsArray();
            var targets = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
            var rounds = messages.Count(m => m!["role"]!.GetValue<string>() == "assistant");
            object Call(string name, object args, int k) => new { index = k, id = $"call_{name}_{rounds}_{k}", type = "function", function = new { name, arguments = JsonSerializer.Serialize(args) } };
            if (rounds == 0)
                return (HttpStatusCode.OK, FakeOpenRouter.Stream(new
                {
                    tool_calls = new[] { Call("get_line_facts", new { first = targets[0], last = targets[^1], offset = 0 }, 0),
                        Call("get_source_span", new { aliases = targets.Skip(0).Take(8) }, 1), Call("get_source_span", new { aliases = targets.Skip(8).Take(8) }, 2) },
                }, "tool_calls"));
            if (rounds <= 2)
                return (HttpStatusCode.OK, FakeOpenRouter.Stream(new
                {
                    tool_calls = Enumerable.Range(0, 3).Select(k => Call("get_source_context", new { alias = targets[20 + rounds * 7 + k * 4], before = 6, after = 6 }, k)).ToArray(),
                }, "tool_calls"));
            return Answer(body, targets);
        });

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        Assert.Equal(0, selection.UnitsComplete);
        Assert.Empty(selection.SelectedAliases);
        var failure = Assert.Single(selection.UnitFailures);
        Assert.Contains(":INCOMPLETE:BUDGET:CORE_FACTS_UNCOVERED:", failure, StringComparison.Ordinal);
        // Three tool rounds and the answer; asking for more facts would only have cost a post the unit could not deliver.
        Assert.Equal(4, handler.Posts);
    }

    [Fact]
    public void The_core_facts_ledger_counts_the_lines_a_facts_call_returned_and_the_detail_ledger_the_lines_the_other_tools_showed()
    {
        var source = DocxAgentSource.Load(Long(), null, out _)!;
        var tools = DocxAgentFlavor.Instance.CreateTools(source);
        var lines = source.Lines;

        int Facts(string id, int offset)
        {
            var r = tools.Execute(id, "get_line_facts", JsonSerializer.Serialize(new { first = lines[0].Alias, last = lines[47].Alias, offset }));
            return JsonDocument.Parse(r.Content).RootElement.GetProperty("result").GetProperty("facts").GetArrayLength();
        }

        var page = Facts("E1.01", 0);
        tools.Execute("E1.02", "get_source_span", JsonSerializer.Serialize(new { aliases = new[] { lines[40].Alias } }));

        Assert.Equal(lines.Take(page).Select(l => l.Alias).ToHashSet(), tools.FactsReturnedFor.ToHashSet());
        Assert.Equal(1, tools.FactsResultsCut);
        // Detail evidence is what the model chose to read; it is not core coverage.
        Assert.Equal(new HashSet<string> { lines[40].Alias }, tools.DetailEvidenceAliases.ToHashSet());
        // The same call again is a cache hit and adds nothing; the next page adds its own lines.
        Facts("E2.01", 0);
        Assert.Equal(page, tools.FactsReturnedFor.Count);
        var next = Facts("E2.02", page);
        Assert.Equal(lines.Take(page + next).Select(l => l.Alias).ToHashSet(), tools.FactsReturnedFor.ToHashSet());
    }

    [Fact]
    public void Only_the_docx_flavor_requires_core_facts_so_the_qualified_pdf_conversation_keeps_its_bytes()
    {
        Assert.True(DocxAgentFlavor.Instance.RequiresCoreFacts);
        Assert.False(PdfAgentFlavor.Instance.RequiresCoreFacts);
    }
}
