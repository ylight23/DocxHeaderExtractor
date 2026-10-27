using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The production selector end to end over a fake OpenRouter: streamed replies, the audit, the budget and the parallel units. No network.
/// </summary>
[Trait("Category", "Slow")]
public sealed class PdfSelectorTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public async Task Every_segment_reads_its_facts_then_answers_and_the_selection_collects_the_selected_lines()
    {
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget);
        var selector = new PdfSelector(() => handler, parallelism: 4);

        var selection = await selector.SelectAsync(TestRepository.Path(Pdf), "sk-test", CancellationToken.None);

        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        Assert.Equal(source.Cores.Count, selection.UnitsTotal);
        Assert.Equal(selection.UnitsTotal, selection.UnitsComplete);
        Assert.Empty(selection.UnitFailures);
        Assert.Equal(Enumerable.Range(0, source.Cores.Count).Select(n => source.Scope(n).Decide[0]).ToHashSet(), selection.SelectedAliases);
        Assert.DoesNotContain(handler.Bodies, FakeOpenRouter.IsVerification);
        Assert.Equal(2 * selection.UnitsTotal, selection.ProviderCalls);
        Assert.Equal(2 * selection.UnitsTotal, handler.Posts);
        Assert.Equal(0.001 * handler.Posts, selection.ProviderCostUsd, 6);
        Assert.All(handler.Keys, k => Assert.Equal("Bearer sk-test", k));
        Assert.Equal(selection.UnitsTotal, selection.ModelAnswers.Count(a => a.StartsWith("{\"unit\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Adjacent_selected_lines_are_divided_into_headings_by_a_group_request_and_the_division_reaches_the_selection()
    {
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTwoTargetsThenGroup);
        var selector = new PdfSelector(() => handler, parallelism: 4);

        var selection = await selector.SelectAsync(TestRepository.Path(Pdf), "sk-test", CancellationToken.None);

        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        Assert.Equal(selection.UnitsTotal, selection.UnitsComplete);
        Assert.Empty(selection.UnitFailures);
        // Every segment selected its first two targets; the fake group answer puts each pair together.
        Assert.Equal(source.Cores.Count, selection.Groups!.Count(g => g.Count == 2));
        Assert.Equal(selection.SelectedAliases.Order(), selection.Groups!.SelectMany(g => g).Order());
        Assert.DoesNotContain(handler.Bodies, FakeOpenRouter.IsVerification);
        Assert.Equal(2 * selection.UnitsTotal + (int)Math.Ceiling(source.Cores.Count / (double)AgentGrouping.MaxRunsPerRequest), selection.ProviderCalls);
        Assert.Contains(selection.ModelAnswers, a => a.StartsWith("{\"grouping\"", StringComparison.Ordinal));
        Assert.NotNull(selection.Placement);
        Assert.All(selection.SelectedAliases, alias =>
        {
            var state = selection.AliasStates![alias];
            Assert.Equal("recorded", state.ProvenanceAvailability);
            Assert.Contains(state.Provenance, p => p.Stage == "selection" && p.Decision == "SELECT");
            Assert.Contains(state.Provenance, p => p.Stage == "grouping" && p.Decision == "DIVIDED");
            Assert.DoesNotContain(state.Provenance, p => p.Stage.StartsWith("verification-", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_is_retried_three_times_per_unit_and_the_units_fail()
    {
        var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.TooManyRequests, ""));
        var selector = new PdfSelector(() => handler, parallelism: 1);
        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;

        // retryDelayMs is fixed at 5 s in production; one unit shows the rule.
        var budget = PdfSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget);
        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);

        Assert.Equal("FAILED", outcome.Status);
        Assert.Equal("CALL:TRANSPORT:RATE_LIMITED_NO_RETRY", outcome.Failure);
        Assert.Equal(3, outcome.TransportRetries);
        Assert.Equal(4, handler.Posts);
        Assert.Empty(outcome.Decisions.Where(d => d.Value == "SELECT"));
        await Task.CompletedTask;
    }

    [Fact]
    public void An_account_without_credit_is_named_and_not_retried()
    {
        var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":402,\"message\":\"Insufficient credits\"}}"));
        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        var budget = PdfSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget);

        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);

        Assert.Equal("FAILED", outcome.Status);
        Assert.Equal("CALL:TRANSPORT:PAYMENT_REQUIRED", outcome.Failure);
        Assert.Equal(0, outcome.TransportRetries);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public void Selection_does_not_repeat_a_transport_managed_retry_sequence()
    {
        using var handler = new FakeOpenRouter((_, _) => (HttpStatusCode.TooManyRequests, ""));
        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        var budget = PdfSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget, rateLimitRetries: 3, rateLimitBaseDelayMs: 0,
            requestGate: new ProviderRequestGate(6));
        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);
        Assert.Equal("FAILED", outcome.Status);
        Assert.Equal("CALL:TRANSPORT:RATE_LIMITED_NO_RETRY", outcome.Failure);
        Assert.Equal(0, outcome.TransportRetries);
        Assert.Equal(4, handler.Posts);
        Assert.Equal(4, budget.Calls);
    }

    [Fact]
    public void A_reply_from_another_endpoint_halts_the_document()
    {
        var handler = new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTarget, provider: "SomeoneElse");
        var source = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        var budget = PdfSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget);
        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);
        Assert.Equal("CALL:ENDPOINT_OR_MODEL_UNEXPECTED", outcome.Failure);
        Assert.Equal("ENDPOINT_OR_MODEL_UNEXPECTED", budget.HaltReason);
    }
}
