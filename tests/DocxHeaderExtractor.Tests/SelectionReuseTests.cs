using System.Text.Json;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using static DocxHeaderExtractor.HeadingAgent.Protocol.AgentProtocol;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Budget: a selection answer already paid for is reused when the unit's first request is byte for byte the same, and a refused reply that comes
/// back word for word after a repair ends the repairs. C# changes no decision in either case. No network.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SelectionReuseTests : IDisposable
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _)!);
    private const int Units = 3;

    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-selcache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A scripted model: a selection request is answered COMPLETE (first target SELECT, the rest NOT_SELECT), or with
    /// <paramref name="selectionReply"/> when given; a grouping request gets every line as its own heading. Counts the selection calls.</summary>
    private static AgentUnit.Model Scripted(Action onSelection, string? selectionReply = null) => (unit, turn, request) =>
    {
        using var body = JsonDocument.Parse(request.Body);
        var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
        using var payload = JsonDocument.Parse(text);
        var root = payload.RootElement;
        if (root.GetProperty("stage").GetString() == "GROUP")
            return new ModelTurn(JsonSerializer.Serialize(new
            {
                protocolVersion = AgentGrouping.GroupVersion,
                runs = root.GetProperty("runs").EnumerateArray().Select(r => new
                {
                    run = r.GetProperty("run").GetString(),
                    headings = r.GetProperty("lines").EnumerateArray().Select(l => new[] { l.GetProperty("alias").GetString()! }),
                }),
            }), [], "stop");
        onSelection();
        if (selectionReply is not null) return new ModelTurn(selectionReply, [], "stop");
        var targets = root.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("alias").GetString()!).ToArray();
        return new ModelTurn(JsonSerializer.Serialize(new
        {
            protocolVersion = Source.Value.Flavor.AnswerVersion,
            status = "COMPLETE",
            decisions = targets.Select((a, i) => i == 0 ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } }
                : new { alias = a, decision = "NOT_SELECT" }),
        }), [], "stop");
    };

    private static HeadingSelection Run(AgentUnit.Model model, SelectionCache? cache, string instruction = Instruction) =>
        AgentSelector.Select(Source.Value, model, new AgentBudget(1000, 100), 1, CancellationToken.None, retryDelayMs: 0, () => 0,
            instruction, firstSegments: Units, cache: cache);

    [Fact]
    public void A_second_run_with_the_same_requests_reuses_every_answer_and_calls_no_selection()
    {
        var calls = 0;
        var cache = new SelectionCache(dir);

        var first = Run(Scripted(() => calls++), cache);
        Assert.Equal(Units, calls);

        var second = Run(Scripted(() => calls++), cache);

        Assert.Equal(Units, calls);   // no call in the second run
        Assert.Equal(first.SelectedAliases.Order(StringComparer.Ordinal), second.SelectedAliases.Order(StringComparer.Ordinal));
        Assert.Equal(Units, second.UnitsComplete);
        var reuse = JsonDocument.Parse(second.ModelAnswers.Single(a => a.Contains("\"selection-reuse\"", StringComparison.Ordinal))).RootElement;
        Assert.Equal(Units, reuse.GetProperty("units").GetArrayLength());
        Assert.DoesNotContain(first.ModelAnswers, a => a.Contains("\"selection-reuse\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_instruction_changes_the_first_request_so_the_model_is_asked_again()
    {
        var calls = 0;
        var cache = new SelectionCache(dir);
        Run(Scripted(() => calls++), cache);

        Assert.NotEqual(Instruction, D412Prompt.Text);
        Run(Scripted(() => calls++), cache, D412Prompt.Text);

        Assert.Equal(2 * Units, calls);
    }

    [Fact]
    public void Without_a_cache_every_run_asks_the_model()
    {
        var calls = 0;
        Run(Scripted(() => calls++), cache: null);
        Run(Scripted(() => calls++), cache: null);
        Assert.Equal(2 * Units, calls);
    }

    [Fact]
    public void A_unit_that_failed_is_not_stored_and_is_asked_again()
    {
        var calls = 0;
        var cache = new SelectionCache(dir);
        var failed = Run(Scripted(() => calls++, selectionReply: "not json"), cache);
        Assert.Equal(0, failed.UnitsComplete);

        calls = 0;
        Run(Scripted(() => calls++), cache);

        Assert.Equal(Units, calls);
    }

    [Fact]
    public void A_selection_reply_refused_twice_word_for_word_ends_the_unit_after_one_repair()
    {
        var calls = 0;
        var outcome = AgentUnit.Run(Source.Value, 0, Scripted(() => calls++, selectionReply: "here are the headings"), new AgentBudget(1000, 100), retryDelayMs: 0);

        Assert.Equal(2, calls);
        Assert.Equal("FAILED", outcome.Status);
        Assert.Equal("PROTOCOL:REPLY_NOT_JSON:UNCHANGED", outcome.Failure);
        Assert.All(outcome.Decisions.Values, d => Assert.Equal("UNRESOLVED", d));
    }

    [Fact]
    public void The_cache_key_is_the_hash_of_the_first_request_the_unit_really_sends()
    {
        var outcome = AgentUnit.Run(Source.Value, 0, Scripted(() => { }), new AgentBudget(1000, 100), retryDelayMs: 0);
        Assert.Equal(outcome.RequestSha256s[0], AgentUnit.FirstRequestSha256(Source.Value, 0));
    }
}
