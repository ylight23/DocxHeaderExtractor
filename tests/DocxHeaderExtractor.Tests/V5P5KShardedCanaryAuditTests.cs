using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free audit of the one-time P5K result. It never replays the four calls.</summary>
public sealed class V5P5KShardedCanaryAuditTests
{
    private const string Root = "artifacts/v5-p5k-v3-sharded-canary";

    [Fact]
    public void Freeze_p5k_32_decision_execution_outcome_without_gold_or_semantic_scoring()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json")));
        var root = result.RootElement;
        Assert.Equal("v5-p5k-v3-sharded-provider-canary-result-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(4, root.GetProperty("maximumAuthorizedProviderCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", root.GetProperty("semanticScore").GetString());
        Assert.Equal(0, root.GetProperty("semanticRetries").GetInt32());
        Assert.False(root.GetProperty("responseRepairApplied").GetBoolean());
        Assert.Equal(0, root.GetProperty("fallbackProviderCalls").GetInt32());
        Assert.Equal("CLOSED_AFTER_4_CALLS", root.GetProperty("stopGate").GetString());

        var rows = root.GetProperty("results").EnumerateArray()
            .ToDictionary(row => row.GetProperty("role").GetString()!, StringComparer.Ordinal);
        Assert.Equal(4, rows.Count);
        AssertOutcome(rows["MAX_OWNED_AND_MULTIPART"], 33, false, "decision-cardinality-invalid:expected=32:actual=33");
        AssertOutcome(rows["L1472_OWNER_OMISSION"], 33, false, "decision-cardinality-invalid:expected=32:actual=33");
        AssertOutcome(rows["L1710_RETYPING"], 32, true, null);
        AssertOutcome(rows["MULTIPART_RELATION"], 31, false, "decision-cardinality-invalid:expected=32:actual=31");
        Assert.All(rows.Values, row =>
        {
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("transportError").ValueKind);
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            Assert.True(row.GetProperty("rawResponseBytes").GetInt32() <= row.GetProperty("maxResponseUtf8Bytes").GetInt32());
        });

        // The one valid execution's role-local decision is the historical L1710 subject. It uses
        // the implicit whole-atom positional selection (no model-authored verbatimText); unrelated
        // claims in that same response may still have binder refusals and are not evidence of an
        // L1710 whole-atom retyping regression.
        var l1710 = rows["L1710_RETYPING"];
        using var l1710Response = JsonDocument.Parse(l1710.GetProperty("rawResponse").GetString()!);
        var l1710Decision = l1710Response.RootElement.GetProperty("decisions")[27];
        Assert.Equal(1, l1710Decision.GetProperty("claims").GetArrayLength());
        Assert.False(l1710Decision.GetProperty("claims")[0].TryGetProperty("subjectSelection", out _));
        Assert.Equal("STRUCTURAL_REGION", l1710Decision.GetProperty("claims")[0].GetProperty("predicate").GetString());

        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5k-v3-sharded-canary-audit-v1",
            status = "CLOSED_NOT_PROMOTED_EXHAUSTIVE_PROTOCOL_UNRELIABLE",
            sourceManifest = "artifacts/v5-p5j-v3-sharded-canary-manifest/execution-manifest.v1.json",
            providerCalls = 4,
            maximumAuthorizedProviderCalls = 4,
            goldRead = false,
            semanticScore = "NOT_RUN",
            semanticRetries = 0,
            responseRepairApplied = false,
            fallbackProviderCalls = 0,
            retryCountTotal = 0,
            transportSuccesses = 4,
            executionValidResponses = 1,
            executionInvalidResponses = 3,
            exact32DecisionResponses = 1,
            stopResponsesBelow32 = 1,
            stopResponsesAbove32 = 2,
            resultRepeatGuard = "result.v1.json exists; a subsequent P5K invocation stops before network",
            results = rows.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                role = pair.Key,
                finishReason = pair.Value.GetProperty("finishReason").GetString(),
                transportSuccess = pair.Value.GetProperty("transportError").ValueKind == JsonValueKind.Null,
                decisionCountExpected = pair.Value.GetProperty("decisionCountExpected").GetInt32(),
                decisionCountActual = NullableInt(pair.Value.GetProperty("response").GetProperty("DecisionCountActual")),
                rawResponseBytes = pair.Value.GetProperty("rawResponseBytes").GetInt32(),
                maxResponseUtf8Bytes = pair.Value.GetProperty("maxResponseUtf8Bytes").GetInt32(),
                parserAccepted = pair.Value.GetProperty("response").GetProperty("ParserAccepted").GetBoolean(),
                executionQualified = pair.Value.GetProperty("response").GetProperty("ExecutionQualified").GetBoolean(),
                error = pair.Value.GetProperty("response").GetProperty("Error").GetString(),
            }),
            roleSpecific = new
            {
                maxOwnedAndMultipart = "NOT_EVALUATED: 33/32 response is contract-invalid before binding.",
                l1472OwnerOmission = "NOT_EVALUATED: 33/32 response is contract-invalid before positional decision interpretation.",
                l1710Retyping = new
                {
                    executionQualified = true,
                    localDecisionOrdinal = 27,
                    decisionPresent = true,
                    wholeAtomSelection = "VALID_IMPLICIT_NO_VERBATIM_TEXT",
                    note = "This does not score semantics; 22 other claim-level binder refusals are preserved separately in the raw result.",
                },
                multipartRelation = "NOT_EVALUATED: 31/32 response is contract-invalid before relation target binding.",
            },
            conclusion = new
            {
                shardingHypothesis = "NOT_QUALIFIED: only 1/4 responses had exactly 32 decisions.",
                transport = "ALL_FOUR_TRANSPORT_SUCCEEDED; failures are contract-cardinality behavior, not transport.",
                hardGate = "A stop response returned 31/32, so exhaustive-decision protocol reliability is disproven at 32; do not continue sharding to 16/8 without a new architectural decision.",
                promotion = "CLOSED_NOT_PROMOTED",
                nextProviderAction = "NONE",
            },
        });
    }

    private static void AssertOutcome(JsonElement row, int actual, bool executionValid, string? error)
    {
        Assert.Equal(32, row.GetProperty("decisionCountExpected").GetInt32());
        Assert.Equal(actual, row.GetProperty("response").GetProperty("DecisionCountActual").GetInt32());
        Assert.Equal(executionValid, row.GetProperty("response").GetProperty("ExecutionQualified").GetBoolean());
        Assert.Equal(executionValid ? "EXECUTION_VALID" : "CONTRACT_INVALID", row.GetProperty("response").GetProperty("Classification").GetString());
        Assert.Equal(error, row.GetProperty("response").GetProperty("Error").GetString());
    }

    private static int? NullableInt(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();
}
