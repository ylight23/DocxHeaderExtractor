using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Publishes a small sanitized receipt for the already-authorized P6T-C raw capture.</summary>
public sealed class V5P6TCExecutionReceiptTests
{
    private const string RawPath = "artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence/result.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence";

    [Fact]
    public void P6TC_execution_receipt_is_hash_verified_and_contains_no_raw_payload()
    {
        using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RawPath)));
        var root = raw.RootElement;
        Assert.Equal(2, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal("NOT_RUN", root.GetProperty("semanticScore").GetString());
        Assert.Equal(0, root.GetProperty("retry").GetInt32());
        Assert.False(root.GetProperty("repair").GetBoolean());
        Assert.False(root.GetProperty("fallback").GetBoolean());
        var rows = root.GetProperty("rows").EnumerateArray().Select(row =>
        {
            Assert.Equal("stop", row.GetProperty("finishReason").GetString(), ignoreCase: true);
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            Assert.True(row.GetProperty("reasoningTokens").GetInt32() > 0);
            Assert.Equal("TRUE", row.GetProperty("reasoningExecutionConfirmed").GetString());
            Assert.True(row.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean());
            Assert.Equal(96, row.GetProperty("analysis").GetProperty("returnedDecisions").GetInt32());
            Assert.Equal("TOTAL_LEDGER_ACCEPTED", row.GetProperty("analysis").GetProperty("classification").GetString());
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
                expectedDecisions = row.GetProperty("analysis").GetProperty("expectedDecisions").GetInt32(),
                returnedDecisions = row.GetProperty("analysis").GetProperty("returnedDecisions").GetInt32(),
                parserAccepted = row.GetProperty("analysis").GetProperty("parserAccepted").GetBoolean(),
                classification = row.GetProperty("analysis").GetProperty("classification").GetString(),
            };
        }).ToArray();
        Assert.Equal(2, rows.Length);
        FreezeArtifact.AssertJson(OutputRoot, "execution-receipt.v1.json", new
        {
            schemaVersion = "v5-p6tc-correspondence-evidence-execution-receipt-v1", sourceCapture = RawPath, providerCalls = 2, goldRead = false, goldMutation = "NONE", semanticScore = "NOT_RUN", retry = 0, repair = false, fallback = false, sharedRuntime = "UNCHANGED", rawResponsesPublished = false, rows,
        });
    }
}
