using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfR3AOfflineQualificationProbe
{
    [Fact]
    public void R1_plus_R3A_replay_preserves_recovery_but_fails_collateral_gate()
    {
        var path = FindRepoFile("eval/benchmark-n3/n3.4/reports/004-r3-a-offline-qualification.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var baseline = root.GetProperty("baselineR1");
        var replay = root.GetProperty("counterfactualR1PlusR3A");

        Assert.False(root.GetProperty("usesModel").GetBoolean());
        Assert.False(root.GetProperty("providerCallsMade").GetBoolean());
        Assert.Equal(83, baseline.GetProperty("emitted").GetInt32());
        Assert.Equal(47, baseline.GetProperty("exactSupported").GetInt32());
        Assert.Equal(12, baseline.GetProperty("unmatched").GetInt32());
        Assert.Equal(6, replay.GetProperty("ownerARejected").GetInt32());
        Assert.Equal(0, replay.GetProperty("ownerBRejected").GetInt32());
        Assert.Equal(77, replay.GetProperty("emitted").GetInt32());
        Assert.Equal(47, replay.GetProperty("exactSupported").GetInt32());
        Assert.Equal(6, replay.GetProperty("unmatched").GetInt32());
        Assert.Equal(0, replay.GetProperty("supportedLost").GetInt32());
        Assert.Equal("BLOCK", root.GetProperty("promotionGate").GetProperty("overall").GetString());
        Assert.Equal("BLOCKED", root.GetProperty("decision").GetProperty("r1PlusR3APromotion").GetString());
        Assert.Equal("NOT_OPENED", root.GetProperty("decision").GetProperty("n4").GetString());
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
