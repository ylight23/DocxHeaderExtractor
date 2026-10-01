using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free closure audit of the one-time P5M sparse canary result.</summary>
public sealed class V5P5MSparseCanaryAuditTests
{
    private const string Root = "artifacts/v5-p5m-v31-sparse-canary-manifest";

    [Fact]
    public void Freeze_four_call_sparse_execution_outcome_without_gold_or_semantic_scoring()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json")));
        var root = document.RootElement;
        Assert.Equal("v5-p5m-v31-sparse-provider-canary-result-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", root.GetProperty("semanticScore").GetString());
        Assert.Equal(0, root.GetProperty("semanticRetries").GetInt32());
        Assert.False(root.GetProperty("responseRepairApplied").GetBoolean());
        Assert.Equal(0, root.GetProperty("fallbackProviderCalls").GetInt32());
        var rows = root.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row => Assert.Equal("stop", row.GetProperty("finishReason").GetString()));
        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5m-v31-sparse-canary-audit-v1",
            status = "CLOSED_AFTER_4_CALLS_NOT_SEMANTIC_SCORED",
            protocol = "v5-source-backed-decision-3.1",
            providerCalls = 4,
            goldRead = false,
            semanticScore = "NOT_RUN",
            outcome = new
            {
                maxOwnedAndMultipart = "CONTRACT_INVALID_RESPONSE_BYTE_BOUND",
                l1472OwnerOmission = "PARSER_VALID_BINDER_EXECUTED_ZERO_USABLE_CLAIMS: sourceOrdinal was supplied where ownedIndex was required",
                l1710Retyping = "PARSER_VALID_BINDER_EXECUTED_23_USABLE_CLAIMS",
                multipartRelation = "CONTRACT_INVALID_DECISION_COUNT_EXCEEDS_OWNED",
            },
            reporting = "parser acceptance and binder execution are independent of usability; usableClaims equals boundClaims and must not be inferred from a non-null binding container.",
            conclusion = "Sparse V3.1 removes exhaustive-ledger failure, but P5M reveals an addressing-interface defect and relation over-generation. Do not raise caps, shard, or call a provider again before explicit local ownedIndex/contextIndex handles are qualified provider-free. No Gold was read and no semantic conclusion is drawn.",
            repeatGuard = "result.v1.json or result.in-progress.v1.json exists; subsequent P5M invocation stops before network",
        });
    }
}
