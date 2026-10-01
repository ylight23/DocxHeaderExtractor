using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free closure audit for the already-completed four-attempt P5F2 canary.</summary>
public sealed class V5P5F2V3CanaryAuditTests
{
    private const string Root = "artifacts/v5-p5f2-v3-canary";

    [Fact]
    public void Freeze_p5f2_post_p5g_transport_and_contract_outcome_without_gold_or_scoring()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json")));
        var root = result.RootElement;
        Assert.Equal("v5-p5f2-v3-provider-canary-result-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", root.GetProperty("semanticScore").GetString());
        Assert.Equal(0, root.GetProperty("semanticRetries").GetInt32());
        Assert.False(root.GetProperty("responseRepairApplied").GetBoolean());
        Assert.Equal("CLOSED_AFTER_4_CALLS", root.GetProperty("stopGate").GetString());

        var rows = root.GetProperty("results").EnumerateArray()
            .ToDictionary(row => row.GetProperty("role").GetString()!, StringComparer.Ordinal);
        Assert.Equal(4, rows.Count);
        AssertOutcome(rows["MAX_OWNED_AND_MULTIPART"], "CONTRACT_INVALID", "stop", 61, 49560, "byte-budget-exceeded");
        AssertOutcome(rows["L1472_OWNER_OMISSION"], "JSON_INVALID", "length", null, 96196, "end of data");
        AssertOutcome(rows["L1710_RETYPING"], "CONTRACT_INVALID", "stop", 65, 19039, "expected=96:actual=65");
        AssertOutcome(rows["MULTIPART_RELATION"], "TRANSPORT_ERROR", null, null, 0, "Error while copying content to a stream.");
        Assert.All(rows.Values, row => Assert.Equal(0, row.GetProperty("retryCount").GetInt32()));
        Assert.All(rows.Values, row => Assert.False(row.GetProperty("response").GetProperty("ParserAccepted").GetBoolean()));
        Assert.All(rows.Values, row => Assert.False(row.GetProperty("response").GetProperty("BinderAccepted").GetBoolean()));

        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5f2-v3-provider-canary-audit-v1",
            status = "CLOSED_NOT_PROMOTED",
            sourceManifest = "artifacts/v5-p5h-v3-manifest-refresh/execution-manifest.v1.json",
            providerCalls = 4,
            maximumAuthorizedProviderCalls = 4,
            goldRead = false,
            semanticScore = "NOT_RUN",
            semanticRetries = 0,
            responseRepairApplied = false,
            fallbackProviderCalls = 0,
            retryCountTotal = 0,
            repeatInvocationGuard = "VERIFIED: result already exists stops before network",
            results = rows.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                role = pair.Key,
                classification = pair.Value.GetProperty("response").GetProperty("Classification").GetString(),
                finishReason = pair.Value.GetProperty("finishReason").GetString(),
                decisionCountActual = pair.Value.GetProperty("response").GetProperty("DecisionCountActual").ValueKind == JsonValueKind.Null
                    ? (int?)null : pair.Value.GetProperty("response").GetProperty("DecisionCountActual").GetInt32(),
                decisionCountExpected = pair.Value.GetProperty("decisionCountExpected").GetInt32(),
                rawResponseBytes = pair.Value.GetProperty("rawResponseBytes").GetInt32(),
                error = pair.Value.GetProperty("response").GetProperty("Error").GetString(),
            }),
            interpretation = new
            {
                exhaustiveDecisionCompliance = "FAILED: 61/96, JSON truncated at length, 65/96, and transport failure",
                boundedResponseCompliance = "FAILED: one stop response was 49,560 bytes, exceeding the 49,152-byte P5G cap; it was also cardinality-short",
                semanticQualification = "NOT_RUN: no response passed strict local parsing, so binder/Gold/semantic quality were not reached",
                promotion = "NOT_PROMOTED",
                nextProviderAction = "NONE without a new explicit authorization after offline audit",
            },
        });
    }

    private static void AssertOutcome(JsonElement row, string classification, string? finishReason, int? actual,
        int rawBytes, string errorFragment)
    {
        Assert.Equal(classification, row.GetProperty("response").GetProperty("Classification").GetString());
        Assert.Equal(finishReason, row.GetProperty("finishReason").GetString());
        Assert.Equal(actual, NullableInt(row.GetProperty("response").GetProperty("DecisionCountActual")));
        Assert.Equal(rawBytes, row.GetProperty("rawResponseBytes").GetInt32());
        Assert.Contains(errorFragment, row.GetProperty("response").GetProperty("Error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
}
