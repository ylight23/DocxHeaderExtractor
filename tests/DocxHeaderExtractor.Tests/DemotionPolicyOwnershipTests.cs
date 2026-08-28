using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class DemotionPolicyOwnershipTests
{
    [Fact]
    public void Ownership_artifact_resolves_all_three_operations_without_moving_them()
    {
        var artifact = LoadArtifact();
        Assert.Equal(3, artifact.RootElement.GetProperty("demotionOperationsAudited").GetInt32());
        Assert.True(artifact.RootElement.GetProperty("ownershipResolved").GetBoolean());
        Assert.Empty(artifact.RootElement.GetProperty("moveNow").EnumerateArray());
        Assert.Empty(artifact.RootElement.GetProperty("ambiguous").EnumerateArray());
        Assert.Equal(3, artifact.RootElement.GetProperty("deferred").GetArrayLength());
    }

    [Fact]
    public void Frozen_order_and_behavior_deltas_are_zero()
    {
        var root = LoadArtifact().RootElement;
        Assert.True(root.GetProperty("demotionOrderEquivalent").GetBoolean());
        Assert.Equal(0, root.GetProperty("candidateDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("roleDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("scoreDelta").GetInt32());
        Assert.Equal(0, root.GetProperty("levelDelta").GetInt32());
        Assert.False(root.GetProperty("sourceFactMutation").GetBoolean());
        Assert.False(root.GetProperty("derivedFactMutation").GetBoolean());
    }

    [Fact]
    public void Demotion_ownership_does_not_import_forbidden_layers()
    {
        var extractor = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "DocxHeaderExtractor.Core",
            "OpenXmlLayer", "DocxSlimExtractor.cs"));
        Assert.DoesNotContain("PdfFirstValidatedFallback", extractor, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidatedHeading", extractor, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelProposal", extractor, StringComparison.Ordinal);
    }

    private static JsonDocument LoadArtifact() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        FindRepositoryRoot(), "eval", "architecture", "demotion-policy-ownership.v1.json")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
