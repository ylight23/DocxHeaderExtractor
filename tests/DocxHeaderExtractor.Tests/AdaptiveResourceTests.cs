using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;

namespace DocxHeaderExtractor.Tests;

public sealed class AdaptiveResourceTests
{
    static PreparedRequest Request(int cap = 256, string text = "source") =>
        OpenRouterWire.Build("Return JSON", text, [], null, null, new WireOptions(cap, "none"));

    [Fact]
    public void Estimate_uses_enforced_completion_and_native_usage_is_not_compared_to_wire_bytes()
    {
        var budget = new AgentBudget(10, 1);
        Assert.True(budget.EstimateUsd(Request(256)) < budget.EstimateUsd(Request(32768)));
        var usage = Usage.Check(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = "{}" } } },
            usage = new { prompt_tokens = 100000, completion_tokens = 300, cost = 0.01 }
        }), 100, 256);
        Assert.DoesNotContain("PROMPT_TOKENS_EXCEED_BYTES", usage.Codes);
        Assert.Contains("COMPLETION_OVER_CAP", usage.Codes);
    }

    [Fact]
    public void Settlement_releases_only_confirmed_surplus_and_is_idempotent()
    {
        var request = Request();
        var reserve = new AgentBudget(10, 1).EstimateUsd(request);
        var budget = new AgentBudget(10, reserve * 1.5);
        Assert.Null(budget.Admit(request));
        Assert.Null(budget.Claim(request, out var attempt));
        budget.Settle(attempt!.Id, null);
        Assert.Equal(reserve, budget.ReservedUsd, 10);
        Assert.Equal("RESERVE_OVER_CEILING", budget.Admit(request));
        budget.Settle(attempt.Id, reserve / 10);
        budget.Settle(attempt.Id, reserve / 10);
        Assert.Equal(reserve / 10, budget.ActualUsd, 10);
        Assert.Equal(0, budget.ReservedUsd);
        Assert.Null(budget.Admit(request));
    }

    [Fact]
    public void Shared_run_ceiling_covers_independent_stages_and_restores_ambient_scope()
    {
        var request = Request();
        var reserve = new AgentBudget(10, 1).EstimateUsd(request);
        var run = new RunResourceBudget(reserve * 1.5, 20);
        using (run.Enter())
        {
            var selection = new AgentBudget(10, 1);
            var placement = new AgentBudget(10, 1);
            Assert.Null(selection.Admit(request));
            selection.Claim(request, out var first);
            selection.Settle(first!.Id, null);
            Assert.Equal("RUN_RESERVE_OVER_CEILING", placement.Admit(request));
            selection.Settle(first.Id, reserve / 10);
            Assert.Null(placement.Admit(request));
            Assert.Equal(2, run.Observe().Attempts);
        }
        Assert.Null(new AgentBudget(10, 1).Admit(request));
        Assert.Equal(2, run.Observe().Attempts);
    }

    [Fact]
    public void Parallel_identical_requests_get_distinct_attempts_without_overspending()
    {
        var request = Request();
        var reserve = new AgentBudget(10, 1).EstimateUsd(request);
        var budget = new AgentBudget(100, reserve * 3.1);
        Parallel.For(0, 100, _ => budget.Admit(request));
        Assert.Equal(3, budget.Calls);
        Assert.Equal(3, budget.Attempts.Select(a => a.Id).Distinct().Count());
        Assert.True(budget.ReservedUsd <= budget.CeilingUsd);
    }

    [Fact]
    public void Internal_retry_cannot_borrow_a_parallel_callers_pending_admission()
    {
        var request = Request();
        var budget = new AgentBudget(3, 1);
        Assert.Null(budget.Admit(request));
        Assert.Null(budget.Admit(request));
        budget.Claim(request, out var first);
        var other = budget.Attempts.Single(a => a.Id != first!.Id);
        budget.Settle(first!.Id, 0);
        Assert.Null(budget.Claim(request, out var retry, fresh: true));
        Assert.NotEqual(other.Id, retry!.Id);
        Assert.Null(budget.Claim(request, out var parallel));
        Assert.Equal(other.Id, parallel!.Id);
        Assert.Equal(3, budget.Calls);
    }

    [Fact]
    public void Rate_limit_retries_are_admitted_and_call_limit_covers_actual_posts()
    {
        var handler = new FakeOpenRouter((_, _) => (System.Net.HttpStatusCode.TooManyRequests, ""));
        var budget = new AgentBudget(1, 1);
        using var model = new AgentModel(handler, "sk-test", budget, rateLimitRetries: 2, rateLimitBaseDelayMs: 1);
        var error = Assert.Throws<ReplyFailure>(() => model.Turn("U", 1, Request()));
        Assert.Equal("BUDGET:CALL_LIMIT", error.Code);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(1, budget.Calls);
        Assert.Equal(0, budget.ReservedUsd);
    }

    [Fact]
    public void Plan_keeps_ordered_coverage_and_only_splits_at_declared_boundaries()
    {
        var atoms = Enumerable.Range(0, 10).Select(i => new NaturalScope(i, i + 1, "atom", [])).ToArray();
        var page = new NaturalScope(0, 10, "page", [new(0, 4, "block", atoms[..4]), new(4, 10, "block", atoms[4..])]);
        var plan = OrderedScopePlanner.Plan([page], 10, (s, e, _) => e - s <= 6);
        Assert.Equal(new[] { (0, 4), (4, 10) }, plan.Select(p => (p.Start, p.End)));
        Assert.Throws<InvalidOperationException>(() => OrderedScopePlanner.Plan([page], 10, (_, _, _) => false));
    }

    [Fact]
    public void Plan_can_bound_page_packing_to_two_complete_siblings()
    {
        var pages = Enumerable.Range(0, 6).Select(i => new NaturalScope(i, i + 1, "page", [])).ToArray();
        var document = new NaturalScope(0, 6, "document", pages);
        var plan = OrderedScopePlanner.Plan([document], 6, (s, e, _) => e - s <= 2, maxPackedSiblings: 2);
        Assert.Equal(new[] { (0, 2), (2, 4), (4, 6) }, plan.Select(p => (p.Start, p.End)));
    }

    [Fact]
    public void Continuation_growth_is_measured_again_and_refused_before_dispatch()
    {
        var initial = Request();
        var expanded = Request(text: new string('x', 2000));
        var policy = new RequestPolicy { MaxRequestBytes = 1024, ContinuationReserveBytes = 0 };
        var budget = new AgentBudget(10, 1, policy);
        Assert.Null(budget.Admit(initial));
        Assert.Equal("REQUEST_OVER_BYTE_CAP", budget.Admit(expanded));
        Assert.Equal(1, budget.Calls);
    }

    [Fact]
    public void Multimodal_estimate_uses_image_allowance_instead_of_base64_length()
    {
        PreparedRequest Image(int length)
        {
            var body = JsonSerializer.Serialize(new { max_tokens = 256, messages = new[] { new { role = "user", content = new[]
                { new { type = "image_url", image_url = new { url = "data:image/png;base64," + new string('A', length) } } } } } });
            return new(body, System.Text.Encoding.UTF8.GetByteCount(body), AgentProtocol.Sha(body));
        }
        var policy = new RequestPolicy();
        Assert.Equal(RequestResources.Measure(Image(100), policy).InputTokenEstimate,
            RequestResources.Measure(Image(10000), policy).InputTokenEstimate);
        Assert.NotEqual(RequestResources.Measure(Image(100), policy).WireBytes,
            RequestResources.Measure(Image(10000), policy).WireBytes);
    }

    [Fact]
    public void Output_and_context_constraints_can_split_a_scope_even_when_bytes_fit()
    {
        var resources = new RequestResources(100, 1000, 512, 0);
        Assert.Equal("COMPLETION_OVER_PROFILE_CAP", (new RequestPolicy { MaxCompletionTokens = 256 }).Refusal(resources));
        Assert.Equal("REQUEST_OVER_PROMPT_ESTIMATE", (new RequestPolicy { MaxPromptTokens = 500 }).Refusal(resources));
        Assert.Equal("REQUEST_OVER_CONTEXT_ESTIMATE", (new RequestPolicy { ContextTokens = 1200 }).Refusal(resources));
        var policy = new RequestPolicy();
        Assert.True(policy.SelectionOutputDemand(400) > policy.SelectionOutputDemand(76));
        Assert.Equal(16384, (policy with { MaxCompletionTokens = 16384 }).SelectionOutputDemand(48));
    }

    [Fact]
    public async Task Run_scopes_are_isolated_for_parallel_documents_and_shared_by_child_tasks()
    {
        var request = Request();
        async Task<int> Document()
        {
            var run = new RunResourceBudget(1, 2);
            using (run.Enter())
                await Task.Run(() => new AgentBudget(10, 1).Admit(request));
            return run.Observe().Attempts;
        }
        Assert.All(await Task.WhenAll(Document(), Document()), calls => Assert.Equal(1, calls));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Pdf072_smaller_envelope_splits_page15_without_dropping_required_facts()
    {
        var policy = new RequestPolicy { MaxRequestBytes = 100000, ContinuationReserveBytes = 8192 };
        var source = AgentSource.Load(TestRepository.Path("todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf"),
            out _, policy)!;
        var page = source.Lines.Select((l, i) => (l, i)).Where(x => x.l.Page == 15).Select(x => x.i).ToArray();
        Assert.True(source.Cores.Count(c => c.Start >= page[0]) > 1);
        Assert.All(source.Plan, p => Assert.True(p.FirstRequestBytes + policy.ContinuationReserveBytes <= policy.MaxRequestBytes));
        Assert.Equal(source.Lines.Count, source.Cores.Sum(c => c.End - c.Start));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Pdf072_page15_is_one_request_with_complete_facts_exact_coverage_and_replay_hash()
    {
        const string pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
        var source = AgentSource.Load(TestRepository.Path(pdf), out var failure)!;
        Assert.Equal("OK", failure);
        var page = source.Lines.Select((l, i) => (l, i)).Where(x => x.l.Page == 15).Select(x => x.i).ToArray();
        Assert.Equal(76, page.Length);
        var index = Enumerable.Range(0, source.Cores.Count).Single(i => source.Cores[i] == (page[0], page[^1] + 1));
        var prepared = AgentUnit.PrepareFirstRequest(source, index, PdfAgentProtocol.Instruction);
        var task = new SelectionTask(source, index, PdfAgentProtocol.Instruction, null, null);
        Assert.Equal(prepared, task.Build([], "auto"));
        Assert.Null(source.ResourcePolicy.Refusal(RequestResources.Measure(prepared, source.ResourcePolicy), true));
        var body = JsonNode.Parse(prepared.Body)!;
        var payload = JsonNode.Parse(body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(76, payload["targets"]!.AsArray().Count);
        Assert.Equal(76, payload["targetFacts"]!.AsArray().Count);
        Assert.Equal(Enumerable.Range(0, source.Lines.Count), source.Cores.SelectMany(c => Enumerable.Range(c.Start, c.End - c.Start)));
        Assert.NotNull(source.PlanId);
        Assert.Equal(prepared.Sha256, source.Plan[index].RequestSha256);
    }
}
