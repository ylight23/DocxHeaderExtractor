using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Guard against the exact incident this project just had: two Claude Code sessions on the same
/// local checkout, one silently executing N2-S live calls at the CLI default concurrency while a
/// second had already frozen <c>manifest.v1.json</c> at a different concurrency, then a further
/// attempt to retroactively rewrite the frozen manifest to match the off-protocol output instead of
/// the other way around. That rewrite was reverted, not accepted - a contract frozen before output
/// must constrain the output, never be mutated by it.
/// <para>
/// This does not rebuild the missing pre-provider-call enforcement inside the CLI itself (a real gap,
/// left as a separate, larger change) - it is the smallest thing that makes a silent repeat of this
/// specific failure fail a test immediately: the frozen manifest must still hash to what it was
/// frozen at, and once a canonical run exists, its own recorded route config must match the
/// manifest's frozen profile bit-for-bit, not merely "some run happened."
/// </para>
/// </summary>
public sealed class PdfN2SContractGuardProbe
{
    [Fact]
    public void FrozenManifestHasNotBeenMutatedSinceItWasSidecarHashed()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var manifestPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "manifest.v1.json");
        var sidecarPath = manifestPath + ".frozen-sha256";
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(sidecarPath));

        var recorded = File.ReadAllText(sidecarPath).Trim();
        var actual = Sha256(manifestPath);
        Assert.Equal(recorded, actual);
    }

    [Fact]
    public void FrozenProfilePinsSemanticConcurrencyToTwoAndInvalidRunsAreQuarantined()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var manifestPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "manifest.v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(2, manifest.RootElement.GetProperty("frozenProfile").GetProperty("semanticConcurrency").GetInt32());
        Assert.False(manifest.RootElement.TryGetProperty("profileProvenance", out _),
            "A frozen-before-output manifest must never grow a field explaining how it was reconciled to an observed run.");

        var invalidDir = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "invalid-runs", "concurrency-1");
        var invalidationPath = Path.Combine(invalidDir, "invalidation.v1.json");
        Assert.True(File.Exists(invalidationPath));
        using var invalidation = JsonDocument.Parse(File.ReadAllText(invalidationPath));
        Assert.Equal("INVALID_OFF_PROTOCOL", invalidation.RootElement.GetProperty("status").GetString());
        Assert.False(invalidation.RootElement.GetProperty("usableForOfficialN2SMetrics").GetBoolean());
        foreach (var file in invalidation.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(invalidDir, file.GetProperty("name").GetString()!);
            Assert.True(File.Exists(path));
            Assert.Equal(file.GetProperty("sha256").GetString(), Sha256(path));
        }

        // The canonical location must be clear of anything from the invalid attempt.
        var canonicalDir = Path.Combine(root, "eval", "benchmark-n0", "n2-s");
        Assert.False(File.Exists(Path.Combine(canonicalDir, "003-n2s-run.v1.json")));
        Assert.False(File.Exists(Path.Combine(canonicalDir, "057-n2s-run.v1.json")));
    }

    [Theory]
    [InlineData("003")]
    [InlineData("057")]
    public void CanonicalRunIfPresentMatchesTheFrozenProfileExactly(string stem)
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var runPath = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "runs", $"{stem}-n2-s-run.v1.json");
        if (!File.Exists(runPath)) return; // canonical execution not yet materialized in this checkout

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "benchmark-n0", "n2-s", "manifest.v1.json")));
        var expectedModel = manifest.RootElement.GetProperty("frozenProfile").GetProperty("model").GetString();
        var expectedRouteHash = DeterministicHash("analystBudget=160|wide=True|supplement=True|semanticHierarchy=False|semanticConcurrency=2");

        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        var generation = run.RootElement.GetProperty("generation");
        Assert.Equal(expectedModel, generation.GetProperty("model").GetString());
        Assert.Equal("OpenRouter", generation.GetProperty("backend").GetString());
        Assert.Equal(expectedRouteHash, generation.GetProperty("routeConfigSha256").GetString());
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string DeterministicHash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
