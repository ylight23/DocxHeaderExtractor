using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV8AHoldoutTests
{
    private static string BasePath => Path.Combine(TestRepository.Root(), "artifacts/identity-benchmark/v8/holdout/source-only-freeze-v1");

    [Fact]
    public void V8A_refuses_contaminated_corpus_and_stops_before_provider()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(BasePath, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("BLOCKED_ON_NEW_SOURCE_CORPUS", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("holdoutSelected").GetBoolean());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("goldReadCount").GetInt32());
        Assert.True(root.GetProperty("generalizationClaim").GetBoolean() == false);
        Assert.True(root.GetProperty("eligibleUniqueSourceCount").GetInt32() < root.GetProperty("requiredMinimum").GetInt32());
        using var eligibility = JsonDocument.Parse(File.ReadAllText(Path.Combine(BasePath, "eligibility-snapshot.json")));
        Assert.True(eligibility.RootElement.GetProperty("lineageDeniedCount").GetInt32() > 0);
    }

    [Fact]
    public void V8A_does_not_materialize_candidates_before_new_source_is_available()
    {
        Assert.True(File.Exists(Path.Combine(BasePath, "provenance-denylist.json")));
        Assert.True(File.Exists(Path.Combine(BasePath, "eligibility-snapshot.json")));
        Assert.False(File.Exists(Path.Combine(BasePath, "candidate-pairs.json")));
        Assert.False(File.Exists(Path.Combine(BasePath, "proposer-requests.json")));
    }
}
