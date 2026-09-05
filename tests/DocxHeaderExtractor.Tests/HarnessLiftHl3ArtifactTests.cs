using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class HarnessLiftHl3ArtifactTests
{
    private static readonly string[] RequiredArtifacts =
    [
        "hl3-measurement-semantics-audit.v1.json",
        "trace-namespace-census.v1.json",
        "occurrence-lineage.v1.json",
        "route-ownership.v1.json",
        "candidate-loss-reconciliation.v1.json",
        "model-exposure-reconciliation.v1.json",
        "final-lineage-reconciliation.v1.json",
        "decision-allocation.v1.json",
        "positive-recall.v1.json",
        "post-model-recovery.v1.json",
        "first-loss-summary.v3.json",
        "hl3-historical-reconciliation.v1.json",
        "harness-contribution-summary.v3.json",
        "final-decision.v3.json",
    ];

    [Fact]
    public void Required_hl3_artifacts_are_valid_json()
    {
        var root = RepositoryRoot();
        foreach (var name in RequiredArtifacts)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "harness-lift", name)));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Candidate_loss_reconciliation_conserves_all_261_rows()
    {
        using var document = Read("candidate-loss-reconciliation.v1.json");
        var root = document.RootElement;
        Assert.Equal(261, root.GetProperty("total").GetInt32());
        Assert.Equal(261, root.GetProperty("rows").GetArrayLength());

        var dispositionSum = root.GetProperty("dispositions").EnumerateObject()
            .Sum(property => property.Value.GetInt32());
        Assert.Equal(261, dispositionSum);
    }

    [Fact]
    public void Frozen_hl2_inputs_match_the_recorded_sha256_values()
    {
        using var document = Read("hl3-measurement-semantics-audit.v1.json");
        var hashes = document.RootElement.GetProperty("frozenInputSha256");
        foreach (var property in hashes.EnumerateObject())
        {
            var path = Path.Combine(RepositoryRoot(), property.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(property.Value.GetString(), Sha256(path));
        }
    }

    [Fact]
    public void Human_review_and_provider_boundaries_remain_frozen()
    {
        using var decision = Read("final-decision.v3.json");
        var root = decision.RootElement;
        Assert.Equal(0, root.GetProperty("provider").GetProperty("newProviderCalls").GetInt32());
        Assert.True(root.GetProperty("noN15Rebaseline").GetBoolean());
        Assert.True(root.GetProperty("frozenHumanReviewQueue").GetBoolean());
        Assert.Equal(0, root.GetProperty("newHumanKeys").GetInt32());
        Assert.Equal(0, root.GetProperty("newHumanGold").GetInt32());
        Assert.Equal(0, root.GetProperty("newHoldoutLabels").GetInt32());
        Assert.Equal(0, root.GetProperty("newReservedLabels").GetInt32());

        using var semantics = Read("hl3-measurement-semantics-audit.v1.json");
        var review = semantics.RootElement.GetProperty("review");
        Assert.Equal(42, review.GetProperty("packetCount").GetInt32());
        Assert.Equal(0, review.GetProperty("newHumanKeys").GetInt32());
        Assert.Equal(0, review.GetProperty("newHumanGold").GetInt32());
    }

    private static JsonDocument Read(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "eval", "harness-lift", name)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("HL3 artifact tests require a repository root.");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
