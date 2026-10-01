using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>P5M is immutable V3.1 execution evidence; current V3.2 intentionally must not replay it.</summary>
public sealed class V5P5MSparseCanaryManifestTests
{
    private const string Root = "artifacts/v5-p5m-v31-sparse-canary-manifest";

    [Fact]
    public void Preserve_exactly_four_historical_v31_sparse_bodies_without_recomposing_them()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/execution-manifest.v1.json")));
        var root = document.RootElement;
        Assert.Equal("v5-p5m-v31-sparse-provider-execution-manifest-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("v5-source-backed-decision-3.1", root.GetProperty("protocol").GetString());
        Assert.Equal(4, root.GetProperty("requestCount").GetInt32());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(96, row.GetProperty("ownedCount").GetInt32());
            var relative = row.GetProperty("providerBodyFile").GetString()!;
            var body = File.ReadAllBytes(TestRepository.Path($"{Root}/{relative}"));
            Assert.Equal(row.GetProperty("providerBodySha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(body)));
        });
    }
}
