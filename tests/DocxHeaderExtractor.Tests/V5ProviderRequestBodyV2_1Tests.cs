using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The complete, deterministic provider request body v2.1 now freezes end to end - not only the
/// semantic prompt bytes, which never carried <c>max_tokens</c>, model, provider or reasoning and so
/// let those change unnoticed by a preflight that hashed only the prompt.
/// </summary>
public sealed class V5ProviderRequestBodyV2_1Tests
{
    private static V5ProviderEnvelope Envelope() => new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    {
        UsageInclude = true,
    };

    [Fact]
    public void The_same_inputs_produce_the_same_provider_request_hash()
    {
        var first = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        var second = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.PayloadBytes, second.PayloadBytes);
    }

    [Fact]
    public void Changing_max_tokens_changes_the_provider_request_hash()
    {
        var a = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        var b = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 2048, Envelope());
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Changing_model_changes_the_provider_request_hash()
    {
        var a = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        var b = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope() with { Model = "other/model" });
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Changing_provider_route_changes_the_provider_request_hash()
    {
        var a = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        var b = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope() with { Provider = "OtherRoute" });
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Changing_reasoning_effort_changes_the_provider_request_hash()
    {
        var a = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        var b = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope() with { Reasoning = "low" });
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void The_frozen_body_carries_no_api_key()
    {
        var body = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, "user-message", 1024, Envelope());
        var text = System.Text.Encoding.UTF8.GetString(body.PayloadBytes);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>V5's own completion budget - never the legacy boundary-cut formula.</summary>
public sealed class V5SemanticCompletionBudgetTests
{
    [Fact]
    public void Budget_is_never_below_the_configured_minimum()
    {
        Assert.Equal(V5SemanticCompletionBudget.MinCompletionTokens,
            V5SemanticCompletionBudget.Compute(ownedCount: 0, visibleCount: 0, requestBytes: 0, providerMaxTokens: 32768));
    }

    [Fact]
    public void Budget_is_never_above_the_provider_ceiling()
    {
        Assert.Equal(4096, V5SemanticCompletionBudget.Compute(ownedCount: 1_000_000, visibleCount: 1_000_000, requestBytes: 0, providerMaxTokens: 4096));
    }

    [Fact]
    public void Budget_does_not_assume_exactly_one_claim_per_owned_item()
    {
        // The formula this replaces was 96 + ownedCount * 128; the SRC-095 PACK_001 canary failure
        // showed that undersized for a claim-shaped reply. The new per-item allowance is larger.
        Assert.True(V5SemanticCompletionBudget.PerOwnedItemTokens > 128);
    }

    [Fact]
    public void A_large_request_with_few_owned_items_still_gets_a_meaningful_budget()
    {
        var budget = V5SemanticCompletionBudget.Compute(ownedCount: 2, visibleCount: 50, requestBytes: 200_000, providerMaxTokens: 32768);
        Assert.True(budget > 10_000, $"expected a byte-driven floor for a small-owned/large-context pack, got {budget}");
    }
}
