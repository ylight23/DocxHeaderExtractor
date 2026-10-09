using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7PublishedRawResponseTests
{
    private const string Root = "artifacts/web-pdf-semantic-diagnostic/";
    private const string Package = Root + "p7.d3.authorized-f1-raw.v1/";
    private static byte[] Bytes(string relative) => File.ReadAllBytes(TestRepository.Path(relative));
    private static JsonDocument Read(string relative) => JsonDocument.Parse(Bytes(relative));
    private static string? Str(JsonElement row, string field) => row.GetProperty(field).GetString();

    [Fact]
    public void Published_files_match_preexisting_immutable_receipts_and_safe_allowlist()
    {
        using var manifest = Read(Package + "manifest.json");
        var m = manifest.RootElement;
        Assert.Equal(SpatialCanonical.Hash(Bytes(Root + "p7.d3.authorized-f1-capture.v2.json")), Str(m, "captureReceiptSha256"));
        Assert.Equal(SpatialCanonical.Hash(Bytes(Root + "p7.d3.authorized-f1-protocol-forensics.v2.json")), Str(m, "forensicsReceiptSha256"));
        Assert.Equal(SpatialCanonical.Hash(Bytes(Package + "capture-freeze.json")), Str(m, "rawCaptureFreezeSha256"));
        var files = m.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(113, files.Length);
        Assert.Equal(files.Length, files.Select(f => Str(f, "path")).Distinct().Count());
        var allowed = new[] { "response.txt", "response.sse", "observation.json", "raw-freeze.json", "attempt-receipt.json", "parsed-decision.json", "capture-freeze.json" };
        foreach (var file in files)
        {
            var path = Str(file, "path")!;
            Assert.DoesNotContain("..", path);
            Assert.False(Path.IsPathRooted(path));
            Assert.Contains(Path.GetFileName(path), allowed);
            var bytes = Bytes(Package + path);
            Assert.Equal(file.GetProperty("length").GetInt64(), bytes.LongLength);
            Assert.Equal(Str(file, "sha256"), SpatialCanonical.Hash(bytes));
        }
        Assert.Equal(0, m.GetProperty("providerCalls").GetInt32());
        Assert.False(m.GetProperty("redacted").GetBoolean());
        Assert.True(m.GetProperty("originalBytesPreserved").GetBoolean());
    }

    [Fact]
    public void Each_attempt_remains_original_including_rejected_responses_and_retry_lineage()
    {
        using var manifest = Read(Package + "manifest.json");
        using var forensic = Read(Root + "p7.d3.authorized-f1-protocol-forensics.v2.json");
        using var capture = Read(Root + "p7.d3.authorized-f1-capture.v2.json");
        var attempts = manifest.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(21, attempts.Length);
        Assert.Equal(7, attempts.Count(a => Str(a, "status") == "ACCEPTED"));
        Assert.Equal(14, attempts.Count(a => Str(a, "status") == "CONTRACT_FAILURE"));
        foreach (var a in attempts)
        {
            var row = forensic.RootElement.GetProperty("attempts").EnumerateArray().Single(r =>
                Str(r, "callHandle") == Str(a, "callHandle") && r.GetProperty("attempt").GetInt32() == a.GetProperty("attempt").GetInt32());
            var path = Package + Str(a, "directory") + "/";
            Assert.Equal(Str(row, "responseSha256"), SpatialCanonical.Hash(Bytes(path + "response.txt")));
            Assert.Equal(Str(row, "rawSseSha256"), SpatialCanonical.Hash(Bytes(path + "response.sse")));
            Assert.Equal(Str(row, "rawObservationSha256"), SpatialCanonical.Hash(Bytes(path + "observation.json")));
            Assert.Equal(Str(row, "rawFreezeSha256"), SpatialCanonical.Hash(Bytes(path + "raw-freeze.json")));
            Assert.Equal(Str(row, "attemptReceiptSha256"), SpatialCanonical.Hash(Bytes(path + "attempt-receipt.json")));
            using var receipt = Read(path + "attempt-receipt.json");
            Assert.Equal(Str(row, "previousAttemptSha256"), Str(receipt.RootElement, "previousAttemptSha256"));
            Assert.Equal(Str(row, "status"), Str(receipt.RootElement, "status"));
            using var response = Read(path + "response.txt");
            var isB = Str(a, "callHandle")!.EndsWith(".B", StringComparison.Ordinal);
            var stage = isB ? response.RootElement.GetProperty("stageDecision") : response.RootElement;
            Assert.Equal(row.GetProperty("returnedDecisions").GetInt32(), stage.GetProperty("decisions").GetArrayLength());
            if (isB)
            {
                Assert.Equal(row.GetProperty("returnedAnalysis").GetInt32(), response.RootElement.GetProperty("analysis").GetArrayLength());
                Assert.False(File.Exists(TestRepository.Path(path + "parsed-decision.json")));
            }
        }
        Assert.Equal(0.17786042m, capture.RootElement.GetProperty("attempts").EnumerateArray()
            .Sum(a => a.GetProperty("usage").GetProperty("reportedCostUsd").GetDecimal()));
    }

    [Fact]
    public void Original_SSE_reassembles_exact_completion_without_new_inference()
    {
        using var manifest = Read(Package + "manifest.json");
        foreach (var a in manifest.RootElement.GetProperty("attempts").EnumerateArray())
        {
            var path = Package + Str(a, "directory") + "/";
            var output = new StringBuilder();
            string? finish = null;
            var done = false;
            foreach (var line in File.ReadLines(TestRepository.Path(path + "response.sse")))
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var data = line[6..];
                if (data == "[DONE]") { done = true; continue; }
                using var item = JsonDocument.Parse(data);
                var e = item.RootElement;
                if (e.TryGetProperty("model", out var model)) Assert.Equal("qwen/qwen3.7-flash", model.GetString());
                if (e.TryGetProperty("provider", out var provider)) Assert.Equal("Alibaba", provider.GetString());
                if (!e.TryGetProperty("choices", out var choices)) continue;
                foreach (var c in choices.EnumerateArray())
                {
                    Assert.Equal(0, c.GetProperty("index").GetInt32());
                    if (c.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        output.Append(content.GetString());
                    if (c.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString();
                }
            }
            Assert.True(done);
            Assert.Equal("stop", finish);
            Assert.Equal(Bytes(path + "response.txt"), Encoding.UTF8.GetBytes(output.ToString()));
            using var observation = Read(path + "observation.json");
            Assert.Equal(output.ToString(), Str(observation.RootElement, "content"));
            Assert.Equal("stop", Str(observation.RootElement, "finishReason"));
            Assert.Equal(0, observation.RootElement.GetProperty("retryCount").GetInt32());
            Assert.Equal(Bytes(path + "response.sse"), Encoding.UTF8.GetBytes(Str(observation.RootElement, "rawSse")!));
        }
    }
}
