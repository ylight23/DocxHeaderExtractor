using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7AuthorizedF1CaptureReceiptTests
{
    private static byte[] Bytes(string file) => File.ReadAllBytes(TestRepository.Path("artifacts/web-pdf-semantic-diagnostic/" + file));
    [Fact] public void Amended_forensics_groups_dictionary_rows_without_changing_raw_capture()
    {
        using var amended = JsonDocument.Parse(Bytes("p7.d3.authorized-f1-protocol-forensics.v2.json"));
        using var original = JsonDocument.Parse(Bytes("p7.d3.authorized-f1-protocol-forensics.v1.json"));
        Assert.True(JsonElement.DeepEquals(original.RootElement.GetProperty("attempts"), amended.RootElement.GetProperty("attempts")));
        var counts = amended.RootElement.GetProperty("failureCounts").EnumerateArray().ToDictionary(r => r.GetProperty("code").GetString()!, r => r.GetProperty("count").GetInt32());
        Assert.Equal(10, counts["interpretation-analysis-cardinality"]);
        Assert.Equal(4, counts["interpretation-assertion-value-mismatch"]);
        Assert.Equal(SpatialCanonical.Hash(Bytes("p7.d3.authorized-f1-protocol-forensics.v1.json")), amended.RootElement.GetProperty("supersedesDiagnosticReportSha256").GetString());
    }
    [Fact] public void Received_attempts_preserve_cap_identical_retry_lineage_and_total_cost()
    {
        using var capture = JsonDocument.Parse(Bytes("p7.d3.authorized-f1-capture.v2.json"));
        using var forensic = JsonDocument.Parse(Bytes("p7.d3.authorized-f1-protocol-forensics.v1.json"));
        var root = capture.RootElement; var rows = root.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(21, rows.Length); Assert.Equal(14, root.GetProperty("primaryCalls").GetInt32());
        Assert.Equal(7, root.GetProperty("retries").GetInt32()); Assert.Equal(7, root.GetProperty("acceptedRequests").GetInt32());
        Assert.Equal(0.17786042m, rows.Sum(r => r.GetProperty("usage").GetProperty("reportedCostUsd").GetDecimal()));
        Assert.Equal(0, root.GetProperty("unknownChargeAttempts").GetInt32());
        foreach (var group in rows.GroupBy(r => r.GetProperty("callHandle").GetString()))
        {
            Assert.InRange(group.Count(), 1, 2); var ordered = group.OrderBy(r => r.GetProperty("attempt").GetInt32()).ToArray();
            Assert.Equal(1, ordered[0].GetProperty("attempt").GetInt32());
            if (ordered.Length == 2)
            {
                Assert.NotEqual("ACCEPTED", ordered[0].GetProperty("status").GetString());
                Assert.Equal(ordered[0].GetProperty("bodySha256").GetString(), ordered[1].GetProperty("bodySha256").GetString());
                var primary = forensic.RootElement.GetProperty("attempts").EnumerateArray().Single(r =>
                    r.GetProperty("callHandle").GetString() == group.Key && r.GetProperty("attempt").GetInt32() == 1);
                Assert.Equal(primary.GetProperty("attemptReceiptSha256").GetString(), ordered[1].GetProperty("previousAttemptSha256").GetString());
            }
        }
        Assert.Equal("CLOSED", root.GetProperty("providerExecution").GetString());
        Assert.False(root.GetProperty("goldReadDuringExecution").GetBoolean());
        Assert.Equal(0, root.GetProperty("downstreamCalls").GetInt32());
    }
    [Fact] public void Protocol_failures_are_not_semantic_accuracy_or_implicit_OTHER()
    {
        using var score = JsonDocument.Parse(Bytes("p7.d3.authorized-f1-gold-score.v1.json")); var r = score.RootElement;
        Assert.Equal(213, r.GetProperty("control").GetProperty("evaluated").GetInt32());
        Assert.Equal(194, r.GetProperty("control").GetProperty("correct").GetInt32());
        Assert.Equal(0, r.GetProperty("treatmentB").GetProperty("evaluated").GetInt32());
        Assert.Equal(213, r.GetProperty("treatmentB").GetProperty("missing").GetInt32());
        Assert.Equal(0, r.GetProperty("paired").GetProperty("evaluable").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("treatmentB").GetProperty("establishes").GetProperty("recall").ValueKind);
        Assert.False(r.GetProperty("failuresBecomeOTHER").GetBoolean());
        Assert.Equal("UNKNOWN_UNSCORED", r.GetProperty("outsideScopeStatus").GetString());
        Assert.Equal(0, r.GetProperty("providerCallsDuringScoring").GetInt32());
        Assert.Equal(SpatialCanonical.Hash(Bytes("p7.d3.authorized-f1-capture.v2.json")), r.GetProperty("captureReceiptSha256").GetString());
    }
}
