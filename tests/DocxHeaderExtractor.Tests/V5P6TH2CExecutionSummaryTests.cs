using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes sanitized H2C execution provenance without retaining source-derived provider text.</summary>
public sealed class V5P6TH2CExecutionSummaryTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string CaptureRoot = Root + "/p6th2c-end-pointer-capture-20261005";
    private const string FirstRetryRoot = Root + "/p6th2c-end-pointer-retry-capture-20261005";
    private const string ClarifiedRetryRoot = Root + "/p6th2c-end-pointer-clarified-retry-20261005";
    private const string V1Manifest = Root + "/p6th2c-end-pointer-preflight/h2c-exact-end-pointer-preflight.v1.json";
    private const string V2Manifest = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string OutputRoot = Root + "/p6th2c-execution-summary";

    [Fact]
    public void Sanitized_H2C_execution_summary_freezes_the_fixed_anchor_example_failure_and_recovery()
    {
        using var manifestV1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V1Manifest)));
        using var manifestV2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V2Manifest)));
        using var primary = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/result.v1.json")));
        using var primaryFreeze = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/capture-freeze.v2.json")));
        using var firstRetry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{FirstRetryRoot}/result.v1.json")));
        using var clarified = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ClarifiedRetryRoot}/retry-until-accepted-summary.v1.json")));

        Assert.Equal(31, primary.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(29, primary.RootElement.GetProperty("acceptedLedgers").GetInt32());
        Assert.Equal(0, primary.RootElement.GetProperty("retry").GetInt32());
        Assert.Equal("RAW_CAPTURE_SET_HASH_VERIFIED_GOLD_CLOSED", primaryFreeze.RootElement.GetProperty("status").GetString());
        Assert.Equal(31, primaryFreeze.RootElement.GetProperty("captureCount").GetInt32());
        Assert.Equal(2, firstRetry.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, firstRetry.RootElement.GetProperty("acceptedLedgers").GetInt32());
        Assert.Equal("BOTH_QUARANTINED_ANCHORS_HAVE_CONTRACT_VALID_CLARIFIED_RESULTS", clarified.RootElement.GetProperty("status").GetString());
        Assert.Equal(13, clarified.RootElement.GetProperty("priorExactBodyRetries").GetInt32());
        Assert.Equal(2, clarified.RootElement.GetProperty("clarifiedRetryProviderCalls").GetInt32());
        Assert.Equal(46, clarified.RootElement.GetProperty("totalProviderCallsIncludingRetries").GetInt32());
        Assert.Equal(2, clarified.RootElement.GetProperty("acceptedAnchorCount").GetInt32());

        var frozenV1 = manifestV1.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(value => Key(value, true), StringComparer.Ordinal);
        var frozenV2 = manifestV2.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(value => Key(value, true), StringComparer.Ordinal);
        Assert.Equal(31, frozenV1.Count);
        Assert.Equal(31, frozenV2.Count);

        var primaryFiles = Directory.GetFiles(TestRepository.Path(CaptureRoot), "*.raw-capture.v1.json");
        Assert.Equal(31, primaryFiles.Length);
        foreach (var path in primaryFiles)
        {
            using var raw = JsonDocument.Parse(File.ReadAllBytes(path));
            var row = raw.RootElement;
            Assert.Equal(row.GetProperty("rawResponseSha256").GetString(), Hash(row.GetProperty("rawResponse").GetString()!));
            Assert.Equal(row.GetProperty("rawSseSha256").GetString(), Hash(row.GetProperty("rawSse").GetString()!));
            Assert.Equal(frozenV1[Key(row)].GetProperty("ProviderBodySha256").GetString(), row.GetProperty("providerBodySha256").GetString());
        }

        var priorRetryFiles = Directory.GetFiles(TestRepository.Path(FirstRetryRoot), "*.raw-capture.v1.json", SearchOption.AllDirectories);
        Assert.Equal(13, priorRetryFiles.Length);
        var echoMismatchCount = 0;
        foreach (var path in priorRetryFiles)
        {
            using var raw = JsonDocument.Parse(File.ReadAllBytes(path));
            var row = raw.RootElement;
            Assert.Equal(row.GetProperty("rawResponseSha256").GetString(), Hash(row.GetProperty("rawResponse").GetString()!));
            Assert.Equal(row.GetProperty("rawSseSha256").GetString(), Hash(row.GetProperty("rawSse").GetString()!));
            Assert.Equal(frozenV1[Key(row)].GetProperty("ProviderBodySha256").GetString(), row.GetProperty("providerBodySha256").GetString());
            var response = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            using (response)
            {
                var actualAnchor = response.RootElement.GetProperty("decisions")[0].GetProperty("anchor").GetString();
                if (actualAnchor == "O17" && row.GetProperty("anchor").GetString() is "O5" or "O1") echoMismatchCount++;
            }
        }
        Assert.Equal(13, echoMismatchCount);

        var clarifiedFiles = Directory.GetFiles(TestRepository.Path(ClarifiedRetryRoot), "*.raw-capture.v2.json", SearchOption.AllDirectories);
        Assert.Equal(2, clarifiedFiles.Length);
        foreach (var path in clarifiedFiles)
        {
            using var raw = JsonDocument.Parse(File.ReadAllBytes(path));
            var row = raw.RootElement;
            var request = frozenV2[Key(row)];
            var rawJson = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            using (rawJson)
            {
                var decision = rawJson.RootElement.GetProperty("decisions")[0];
                var actualAnchor = row.GetProperty("anchor").GetString()!;
                var issued = request.GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()!).ToArray();
                var members = decision.GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString()!).ToArray();
                Assert.Equal(actualAnchor, decision.GetProperty("anchor").GetString());
                Assert.NotEmpty(members);
                Assert.Equal(issued.Take(members.Length), members);
                Assert.Equal(members[^1], decision.GetProperty("endOccurrence").GetString());
                var outside = decision.GetProperty("firstOutsideOccurrence");
                var outsideRole = decision.GetProperty("firstOutsideRole").GetString();
                if (members.Length == issued.Length)
                {
                    Assert.Equal(JsonValueKind.Null, outside.ValueKind);
                    Assert.Equal("NO_VISIBLE_SUCCESSOR", outsideRole);
                }
                else
                {
                    Assert.Equal(issued[members.Length], outside.GetString());
                    Assert.Contains(outsideRole, new[] { "NEW_HEADING", "BODY_CONTENT", "PAGE_FURNITURE", "TABLE_OR_STRUCTURED_CONTENT", "OTHER_NON_HEADING" });
                }
                Assert.Equal("stop", row.GetProperty("finishReason").GetString());
                Assert.Equal(0, row.GetProperty("transportRetryCount").GetInt32());
                Assert.Equal(request.GetProperty("ProviderBodySha256").GetString(), row.GetProperty("providerBodySha256").GetString());
                Assert.Equal(row.GetProperty("rawResponseSha256").GetString(), Hash(row.GetProperty("rawResponse").GetString()!));
                Assert.Equal(row.GetProperty("rawSseSha256").GetString(), Hash(row.GetProperty("rawSse").GetString()!));
            }
        }

        var accepted = clarified.RootElement.GetProperty("accepted").EnumerateArray().Select(row => new
        {
            documentId = row.GetProperty("documentId").GetString(),
            packId = row.GetProperty("packId").GetString(),
            anchor = row.GetProperty("anchor").GetString(),
            providerBodySha256 = row.GetProperty("providerBodySha256").GetString(),
            acceptedRawCaptureSha256 = row.GetProperty("acceptedRawCaptureSha256").GetString(),
            acceptedRawResponseSha256 = row.GetProperty("acceptedRawResponseSha256").GetString(),
            acceptedRawSseSha256 = row.GetProperty("acceptedRawSseSha256").GetString(),
        }).ToArray();
        var primaryFreezeRow = primaryFreeze.RootElement;
        FreezeArtifact.AssertJson(OutputRoot, "h2c-sanitized-execution-summary.v1.json", new
        {
            schemaVersion = "v5-p6th2c-sanitized-execution-summary-v1",
            status = "31_PRIMARY_COMPLETE_29_INITIAL_LEDGER_ACCEPTS_2_RECOVERED_WITH_CLARIFIED_ANCHOR_ECHO",
            originalPromptFailure = "FIXED_O17_OUTPUT_EXAMPLE_WAS_REPEATED_FOR_OTHER_ISSUED_ANCHORS",
            clarification = "REMOVED_ALL_LITERAL_OCCURRENCE_HANDLES_FROM_PROMPT_AND_REQUIRED_ECHO_OF_CURRENT_INPUT_ANCHOR",
            v1RequestManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(V1Manifest))),
            v2RequestManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(V2Manifest))),
            v1PrimaryCaptureSetSha256 = primaryFreezeRow.GetProperty("captureSetSha256").GetString(),
            primaryProviderCalls = 31,
            primaryAcceptedLedgers = 29,
            primaryQuarantines = 2,
            exactBodyRetryCallsBeforeClarification = 13,
            exactBodyRetryEchoedFixedExampleForTargetAnchors = echoMismatchCount,
            clarifiedRetryCalls = 2,
            clarifiedRetryAccepted = 2,
            totalProviderCalls = 46,
            finalContractValidAnchorCount = 31,
            acceptedClarifiedResponses = accepted,
            transportRetries = 0,
            repair = false,
            fallback = false,
            goldRead = false,
            goldMutation = "NONE",
            runtimeChanged = false,
            sharedRuntime = "UNCHANGED",
            rawSourceDerivedResponsesIncluded = false,
            semanticScore = "NOT_EVALUATED_GOLD_CLOSED",
        });
    }

    private static string Key(JsonElement row) => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}|{row.GetProperty("anchor").GetString()}";
    private static string Key(JsonElement row, bool frozen) => frozen
        ? $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}"
        : Key(row);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
