using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Sanitized receipt for the authorized P6T-D run; raw responses remain local.</summary>
public sealed class V5P6TDExecutionReceiptTests
{
    private const string RawPath = "artifacts/v5-p6t-total-occurrence-role/p6td-explicit-abstention/result.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6td-explicit-abstention";

    [Fact]
    public void P6TD_execution_receipt_freezes_contract_outcome_without_raw_payload()
    {
        using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RawPath)));
        var root = raw.RootElement;
        Assert.Equal(2, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, root.GetProperty("retry").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray().Select(row =>
        {
            Assert.Equal("stop", row.GetProperty("finishReason").GetString(), ignoreCase: true);
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            Assert.Equal("TRUE", row.GetProperty("reasoningExecutionConfirmed").GetString());
            var analysis = row.GetProperty("analysis");
            Assert.True(analysis.GetProperty("parserAccepted").GetBoolean());
            Assert.Equal(96, analysis.GetProperty("returnedDecisions").GetInt32());
            Assert.Equal("TOTAL_LEDGER_ACCEPTED", analysis.GetProperty("classification").GetString());
            Assert.True(row.TryGetProperty("rawResponse", out _));
            return new
            {
                documentId = row.GetProperty("documentId").GetString(),
                packId = row.GetProperty("packId").GetString(),
                providerRequestHash = row.GetProperty("providerRequestHash").GetString(),
                rawSseSha256 = row.GetProperty("rawSseSha256").GetString(),
                rawResponseSha256 = row.GetProperty("rawResponseSha256").GetString(),
                finishReason = row.GetProperty("finishReason").GetString(),
                reasoningTokens = row.GetProperty("reasoningTokens").GetInt32(),
                reasoningExecutionConfirmed = row.GetProperty("reasoningExecutionConfirmed").GetString(),
                expectedDecisions = analysis.GetProperty("expectedDecisions").GetInt32(),
                returnedDecisions = analysis.GetProperty("returnedDecisions").GetInt32(),
                unresolvedDecisions = analysis.GetProperty("unresolvedDecisions").GetInt32(),
                parserAccepted = analysis.GetProperty("parserAccepted").GetBoolean(),
                classification = analysis.GetProperty("classification").GetString(),
            };
        }).ToArray();
        Assert.Equal(2, rows.Length);
        FreezeArtifact.AssertJson(OutputRoot, "execution-receipt.v1.json", new
        {
            schemaVersion = "v5-p6td-explicit-abstention-execution-receipt-v1", sourceCapture = RawPath, providerCalls = 2, goldRead = false, goldMutation = "NONE", semanticScore = "NOT_RUN", retry = 0, repair = false, fallback = false, sharedRuntime = "UNCHANGED", rawResponsesPublished = false, rows,
        });
    }
}
