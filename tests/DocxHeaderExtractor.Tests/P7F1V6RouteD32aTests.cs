using System.Text.Json;
using DocxHeaderExtractor.V6Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>Issue #9 D3.2a (offline): the route policy (only alibaba, ignore the us-east-1 variant, no fallback), the endpoint inventory
/// check that fails closed, usage.include in every request, and the provider-reported cost kept apart from the computed estimate.</summary>
public sealed class P7F1V6RouteD32aTests
{
    const string E1 = "artifacts/web-pdf-semantic-diagnostic/p7.f1.v6.d3-gate-e1-endpoints.raw.json";
    const string Usage = "artifacts/web-pdf-semantic-diagnostic/p7.f1.v6.d3.2-usage-fixtures";

    static JsonElement Root(string text) => JsonDocument.Parse(text).RootElement;

    [Fact] public void The_request_routes_to_alibaba_only_with_the_variant_ignored_no_fallback_and_usage_requested()
    {
        var env = Root(P7F1V6Protocol.Envelope(P7F1V6Protocol.SystemPrompt(P7F1V6Hier.Kind.Local), "{}", "alibaba", 32768));
        var p = env.GetProperty("provider");
        Assert.Equal(new[] { "alibaba" }, p.GetProperty("only").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(new[] { "alibaba/us-east-1" }, p.GetProperty("ignore").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(p.GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(p.GetProperty("require_parameters").GetBoolean());
        Assert.True(env.GetProperty("usage").GetProperty("include").GetBoolean());
        Assert.False(env.TryGetProperty("tools", out _));
        Assert.False(env.TryGetProperty("reasoning", out _));
    }

    public static IEnumerable<object[]> Fields() => new[] { "ignore", "usage", "fallbacks", "only", "version" }.Select(f => new object[] { f });

    [Theory] [MemberData(nameof(Fields))]
    public void Each_routing_or_usage_field_changes_the_request_template_fingerprint(string field)
    {
        var d = P7F1V6Protocol.DefaultSpec();
        var mutated = field switch
        {
            "ignore" => d with { Ignore = Array.Empty<string>() },
            "usage" => d with { IncludeUsage = false },
            "fallbacks" => d with { AllowFallbacks = true },
            "only" => d with { ProviderTag = "alibaba/us-east-1" },
            _ => d with { Model = d.Model + "-v2" },
        };
        Assert.NotEqual(P7F1V6Protocol.Sha(P7F1V6Protocol.TemplateEnvelope(d)), P7F1V6Protocol.Sha(P7F1V6Protocol.TemplateEnvelope(mutated)));
    }

    static P7F1V6Protocol.EndpointRecord Row(string tag, string model = "qwen/qwen3.7-flash", int ctx = 1000000, int maxOut = 65536) =>
        new(tag, model, ctx, maxOut);

    static IReadOnlyList<P7F1V6Protocol.EndpointRecord> E1Inventory()
    {
        var raw = Root(File.ReadAllText(TestRepository.Path(E1)));
        return raw.GetProperty("data").GetProperty("endpoints").EnumerateArray().Select(e => new P7F1V6Protocol.EndpointRecord(
            e.GetProperty("tag").GetString()!, e.GetProperty("model_id").GetString()!,
            e.GetProperty("context_length").GetInt32(), e.GetProperty("max_completion_tokens").GetInt32())).ToArray();
    }

    [Fact] public void The_approved_metadata_leaves_exactly_one_eligible_endpoint_tagged_alibaba()
    {
        var d = P7F1V6Protocol.EvaluateRoute(E1Inventory());
        Assert.True(d.Eligible);
        Assert.Equal("alibaba", d.Endpoint);
        Assert.Empty(d.Reasons);
    }

    [Fact] public void An_empty_inventory_is_never_eligible()
    {
        var d = P7F1V6Protocol.EvaluateRoute(Array.Empty<P7F1V6Protocol.EndpointRecord>());
        Assert.False(d.Eligible);
        Assert.Equal(new[] { "EMPTY_INVENTORY" }, d.Reasons);
    }

    [Fact] public void An_inventory_where_the_only_endpoint_is_the_ignored_variant_is_not_eligible()
    {
        var d = P7F1V6Protocol.EvaluateRoute(new[] { Row("alibaba/us-east-1") });
        Assert.False(d.Eligible);
        Assert.Contains("NO_ELIGIBLE_ENDPOINT", d.Reasons);
        Assert.Null(d.Endpoint);
    }

    [Fact] public void An_endpoint_outside_the_approved_inventory_is_not_eligible_and_is_named()
    {
        var d = P7F1V6Protocol.EvaluateRoute(new[] { Row("alibaba"), Row("alibaba/us-east-1"), Row("alibaba/region-new") });
        Assert.False(d.Eligible);
        Assert.Contains("UNAPPROVED:alibaba/region-new", d.Reasons);
    }

    [Fact] public void A_changed_context_or_completion_limit_makes_the_endpoint_ineligible()
    {
        var d = P7F1V6Protocol.EvaluateRoute(new[] { Row("alibaba", ctx: 512000) });
        Assert.False(d.Eligible);
        Assert.Contains("METADATA_CHANGED:alibaba:context", d.Reasons);
        var e = P7F1V6Protocol.EvaluateRoute(new[] { Row("alibaba", maxOut: 8192) });
        Assert.Contains("METADATA_CHANGED:alibaba:maxCompletion", e.Reasons);
    }

    [Fact] public void A_different_model_on_the_approved_tag_is_not_eligible()
    {
        var d = P7F1V6Protocol.EvaluateRoute(new[] { Row("alibaba", model: "qwen/qwen3-32b") });
        Assert.False(d.Eligible);
        Assert.Contains("MODEL_MISMATCH:alibaba", d.Reasons);
    }

    [Fact] public void Two_eligible_alibaba_endpoints_are_ambiguous_and_not_eligible()
    {
        var d = P7F1V6Protocol.EvaluateRoute(new[] { Row("alibaba"), Row("alibaba") });
        Assert.False(d.Eligible);
    }

    [Fact] public void The_manifest_records_the_route_policy_the_inventory_and_the_envelope_version()
    {
        var m = Root(JsonSerializer.Serialize(P7F1V6Protocol.Manifest("alibaba", 32768)));
        Assert.Equal("P7_F1_V6_REQUEST_ENVELOPE_V3", m.GetProperty("envelopeVersion").GetString());
        var policy = m.GetProperty("routePolicy");
        Assert.Equal("alibaba", policy.GetProperty("allow").GetString());
        Assert.Equal(new[] { "alibaba/us-east-1" }, policy.GetProperty("ignore").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(policy.GetProperty("allowFallbacks").GetBoolean());
        Assert.Equal(new[] { "alibaba", "alibaba/us-east-1" }, m.GetProperty("endpointInventoryApproved").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact] public void The_provider_reported_cost_is_kept_apart_from_the_computed_estimate()
    {
        var fx = Root(File.ReadAllText(TestRepository.Path(Usage + "/usage_with_provider_cost.json")));
        var audit = P7F1V6Usage.Check(fx.GetProperty("response").GetRawText(), 20000, 32768);
        Assert.Equal(0.0123, audit.ProviderCostUsd!.Value, 12);
        Assert.NotNull(audit.ComputedCostUsd);
        Assert.NotEqual(audit.ProviderCostUsd.Value, audit.ComputedCostUsd!.Value);
    }
}
