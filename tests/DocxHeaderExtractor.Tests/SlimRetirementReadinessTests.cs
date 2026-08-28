using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class SlimRetirementReadinessTests
{
    [Fact]
    public void Readiness_artifact_keeps_source_authority_and_explains_normal_slim_reads()
    {
        var root = LoadArtifact().RootElement;
        Assert.True(root.GetProperty("sourceAuthorityCutover").GetBoolean());
        Assert.Equal("READY_FOR_PARTIAL_DEPRECATION", root.GetProperty("retirementReadiness").GetString());
        var debt = root.GetProperty("normalPathDebt");
        Assert.Equal(0, debt.GetProperty("normalSourceFactMirrorReads").GetInt32());
        Assert.Equal(0, debt.GetProperty("unexplainedNormalSlimReads").GetInt32());
        Assert.True(root.GetProperty("slimDemotionResponsibilityRemaining").GetBoolean());
    }

    [Fact]
    public void Slim_retirement_has_explicit_exit_criteria_and_no_provider_work()
    {
        var root = LoadArtifact().RootElement;
        Assert.Equal(6, root.GetProperty("exitCriteria").GetArrayLength());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("productionCodeChanged").GetBoolean());
    }

    private static JsonDocument LoadArtifact() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "eval", "architecture", "slim-retirement-readiness.v1.json")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
