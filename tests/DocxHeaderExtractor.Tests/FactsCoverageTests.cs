using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The coverage ledger: the runner records which targets a successful facts call returned facts for. An answer that decides targets whose facts
/// never arrived goes back once, naming them; the model may read them or send the same answer, which is then taken as it is, with the uncovered
/// targets in the audit. The program never decides whether a decision needed the facts.
/// </summary>
public sealed class FactsCoverageTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-coverage-" + Guid.NewGuid().ToString("N"));

    public FactsCoverageTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Reads only the first page of facts (a page holds what fits the result cap, fewer than the 48 targets) and answers; answers the
    /// same after any refusal.</summary>
    static (HttpStatusCode, string) OnePageOnly(JsonObject body, int post)
    {
        var messages = body["messages"]!.AsArray();
        var payload = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!;
        var targets = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).ToArray();
        if (!messages.Any(m => m!["role"]!.GetValue<string>() == "tool"))
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { tool_calls = FakeOpenRouter.FactsCalls(targets, pages: 1) }, "tool_calls"));
        var answer = JsonSerializer.Serialize(new
        {
            protocolVersion = FakeOpenRouter.AnswerVersion(body), status = "COMPLETE",
            decisions = targets.Select((a, i) => i == 0 ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } } : new { alias = a, decision = "NOT_SELECT" }),
        });
        return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = answer }, "stop"));
    }

    [Fact]
    public async Task An_answer_before_every_targets_facts_arrived_goes_back_once_naming_them_and_the_same_answer_is_then_taken()
    {
        var path = DocxTestDocument.CreateLong(Path.Combine(dir, "long.docx"), 48);
        var handler = new FakeOpenRouter(OnePageOnly);

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        var source = DocxAgentSource.Load(path, null, out _)!;
        var (decide, _) = source.Scope(0);
        Assert.Equal(48, decide.Length);
        // facts, answer (sent back), answer again (taken): the decisions are the model's.
        Assert.Equal(3, handler.Posts);
        Assert.Equal(1, selection.UnitsComplete);
        Assert.Equal(new HashSet<string> { decide[0] }, selection.SelectedAliases);
        // The ledger stays in the audit: the targets after the first page, whose facts never arrived, and the cut result.
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!["factsCoverage"]!;
        var uncovered = audit["targetsWithoutFacts"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        var page = decide.Length - uncovered.Length;
        Assert.InRange(page, 1, 24);
        Assert.Equal(decide.Skip(page), uncovered);
        Assert.Equal(1, audit["factsResultsCut"]!.GetValue<int>());
        var repair = handler.Bodies[^1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Contains("PROTOCOL:TOOL_FACTS_INCOMPLETE", repair, StringComparison.Ordinal);
        Assert.Contains("these targets: " + uncovered[0] + ", ", repair, StringComparison.Ordinal);
        Assert.All(decide.Take(page), a => Assert.DoesNotContain(a, repair, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Facts_that_cover_every_target_send_nothing_back()
    {
        var path = DocxTestDocument.CreateLong(Path.Combine(dir, "short.docx"), 10);
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);

        var selection = await new DocxHeadingAgentSelector(() => handler, parallelism: 1).SelectAsync(path, "sk-test", CancellationToken.None);

        Assert.Equal(2, handler.Posts);
        var audit = JsonNode.Parse(selection.ModelAnswers[0])!["factsCoverage"]!;
        Assert.Empty(audit["targetsWithoutFacts"]!.AsArray());
    }

    [Fact]
    public void A_facts_call_enters_the_ledger_with_the_lines_it_returned_facts_for_not_the_lines_it_only_mentions()
    {
        var path = DocxTestDocument.CreateLong(Path.Combine(dir, "long.docx"), 48);
        var source = DocxAgentSource.Load(path, null, out _)!;
        var tools = DocxAgentFlavor.Instance.CreateTools(source);
        var lines = source.Lines;

        int Facts(string id, int offset)
        {
            var r = tools.Execute(id, "get_line_facts", JsonSerializer.Serialize(new { first = lines[0].Alias, last = lines[47].Alias, offset }));
            return JsonDocument.Parse(r.Content).RootElement.GetProperty("result").GetProperty("facts").GetArrayLength();
        }

        var page = Facts("E1.01", 0);
        tools.Execute("E1.02", "get_source_context", JsonSerializer.Serialize(new { alias = lines[40].Alias, before = 2, after = 2 }));

        Assert.Equal(lines.Take(page).Select(l => l.Alias).ToHashSet(), tools.FactsReturnedFor.ToHashSet());
        Assert.Equal(1, tools.FactsResultsCut);
        // The same call again is a cache hit and adds nothing; the next page adds its own lines.
        Facts("E2.01", 0);
        Assert.Equal(page, tools.FactsReturnedFor.Count);
        var next = Facts("E2.02", page);
        Assert.Equal(lines.Take(page + next).Select(l => l.Alias).ToHashSet(), tools.FactsReturnedFor.ToHashSet());
    }
}
