using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Core facts coverage: request-supplied facts cover every target before the first reply. Optional facts calls still paginate and record
/// their returned aliases; detail evidence remains separate. C# does not decide which additional detail the model needs.
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
    public async Task An_answer_after_only_one_optional_facts_page_is_complete_because_the_request_already_covers_every_target()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FirstPageOnly(readsTheRest: false));
        var selection = await new DocxSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);
        Assert.Equal(2, handler.Posts - handler.Bodies.Count(FakeOpenRouter.IsVerification));
        Assert.Equal(1, selection.UnitsComplete);
        Assert.Single(selection.SelectedAliases);
        Assert.Empty(selection.UnitFailures);
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!;
        Assert.Empty(audit["coreFactsCoverage"]!["targetsWithoutFacts"]!.AsArray());
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("TOOL_FACTS_INCOMPLETE", body.ToJsonString()));
    }

    [Fact]
    public async Task Optional_facts_pagination_does_not_remove_the_request_coverage()
    {
        var path = Long();
        var handler = new FakeOpenRouter(FirstPageOnly(readsTheRest: true));

        var selection = await new DocxSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

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

        var selection = await new DocxSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        Assert.Equal(2, handler.Posts - handler.Bodies.Count(FakeOpenRouter.IsVerification));
        Assert.Equal(1, selection.UnitsComplete);
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!;
        Assert.Empty(audit["coreFactsCoverage"]!["targetsWithoutFacts"]!.AsArray());
        Assert.Empty(audit["detailEvidenceCoverage"]!["aliasesRead"]!.AsArray());
    }

    [Fact]
    public async Task Optional_detail_calls_still_obey_the_request_budget_and_do_not_trigger_a_core_facts_repair()
    {
        var path = Long();
        var handler = new FakeOpenRouter((body, post) =>
        {
            var messages = body["messages"]!.AsArray();
            var payload = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!;
            var targets = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
            var rounds = messages.Count(m => m!["role"]!.GetValue<string>() == "assistant");
            if (rounds < AgentProtocol.MaxToolTurns)
                return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { tool_calls = new[] { new
                {
                    index = 0, id = "detail_" + rounds, type = "function",
                    function = new { name = "get_source_span", arguments = JsonSerializer.Serialize(new { aliases = targets.Take(8) }) },
                } } }, "tool_calls"));
            return Answer(body, targets);
        });
        var selection = await new DocxSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);
        Assert.Equal(1, selection.UnitsComplete);
        Assert.Single(selection.SelectedAliases);
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(4, handler.Posts - handler.Bodies.Count(FakeOpenRouter.IsVerification));
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!;
        Assert.Empty(audit["coreFactsCoverage"]!["targetsWithoutFacts"]!.AsArray());
        Assert.NotEmpty(audit["detailEvidenceCoverage"]!["aliasesRead"]!.AsArray());
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("TOOL_FACTS_INCOMPLETE", body.ToJsonString()));
    }

    [Fact]
    public void The_core_facts_ledger_counts_the_lines_a_facts_call_returned_and_the_detail_ledger_the_lines_the_other_tools_showed()
    {
        var source = DocxAgentSource.Load(Long(), null, out _)!;
        var tools = DocxFormat.Instance.CreateTools(source);
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
    public void Core_facts_requirements_are_unchanged_by_how_the_facts_are_delivered()
    {
        Assert.True(DocxFormat.Instance.RequiresCoreFacts);
        Assert.False(PdfFormat.Instance.RequiresCoreFacts);
    }
}
