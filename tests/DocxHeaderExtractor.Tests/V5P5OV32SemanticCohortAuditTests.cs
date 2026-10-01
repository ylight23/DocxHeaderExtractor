using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free audit of P5O transport/parser/binder evidence. It never reads Gold or replays calls.</summary>
public sealed class V5P5OV32SemanticCohortAuditTests
{
    private const string Root = "artifacts/v5-p5o-v32-semantic-cohort-manifest";

    [Fact]
    public void Freeze_v32_survival_measurements_without_semantic_scoring()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/result.v1.json")));
        var root = document.RootElement;
        Assert.Equal("v5-p5o-v32-semantic-cohort-result-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", root.GetProperty("semanticScore").GetString());
        Assert.Equal(0, root.GetProperty("semanticRetries").GetInt32());
        Assert.False(root.GetProperty("responseRepairApplied").GetBoolean());
        Assert.Equal(0, root.GetProperty("fallbackProviderCalls").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row => Assert.Equal("stop", row.GetProperty("finishReason").GetString()));
        Assert.All(rows, row => Assert.True(row.GetProperty("response").GetProperty("ParserValid").GetBoolean()));
        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5o-v32-semantic-cohort-audit-v1",
            status = "CLOSED_AFTER_4_CALLS_GOLD_NOT_READ",
            protocol = "v5-source-backed-decision-3.2",
            providerCalls = 4,
            goldRead = false,
            semanticScore = "NOT_RUN",
            measured = new
            {
                transportValid = "4/4",
                parserValid = "4/4",
                maxOwnedAndMultipart = new { usableSparseDecisions = 14, boundClaims = 14, binderRefusals = 7 },
                l1472OwnerOmission = new { usableSparseDecisions = 8, boundClaims = 0, binderRefusals = 10 },
                l1710Retyping = new { usableSparseDecisions = 22, boundClaims = 3, binderRefusals = 19 },
                multipartRelation = new { usableSparseDecisions = 53, boundClaims = 13, binderRefusals = 40 },
            },
            conclusion = "Explicit V3.2 handles eliminate P5M's response-wide cardinality/byte failures: all four responses parse. Bound-claim survival remains variable and is not semantic quality; Gold comparison and projection scoring are deliberately not run here.",
            nextGate = "Freeze bound claims, then explicitly authorize an offline Gold audit before TP/FP/FN/F1 or projection-quality claims.",
        });
    }
}
