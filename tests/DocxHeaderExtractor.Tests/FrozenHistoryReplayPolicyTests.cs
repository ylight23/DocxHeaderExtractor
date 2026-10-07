namespace DocxHeaderExtractor.Tests;

/// <summary>The retirement of a live replay is narrow: one document, the named tests, nothing else.</summary>
public sealed class FrozenHistoryReplayPolicyTests
{
    [Theory]
    [InlineData("SRC-041")]
    [InlineData("SRC-095")]
    [InlineData("DOC-0252")]
    [InlineData("DOC-0256")]
    [InlineData("SRC-029")]
    public void Every_other_document_keeps_its_live_replay(string documentId) =>
        Assert.Equal(HistoricalReplayStatus.LiveReplay, FrozenHistoryReplayPolicy.RichGeometry(documentId));

    [Fact]
    public void Only_SRC_089_rich_geometry_replay_is_frozen_evidence_only() =>
        Assert.Equal(HistoricalReplayStatus.FrozenEvidenceOnly, FrozenHistoryReplayPolicy.RichGeometry("SRC-089"));

    [Fact]
    public void A_test_the_manifest_does_not_name_cannot_use_the_exemption() =>
        Assert.ThrowsAny<Exception>(() => FrozenHistoryReplayPolicy.AssertFrozenEvidenceOnly("SRC-089", "SomeOtherTests"));

    [Fact]
    public void A_document_with_no_entry_cannot_be_exempted() =>
        Assert.ThrowsAny<Exception>(() => FrozenHistoryReplayPolicy.AssertFrozenEvidenceOnly("SRC-041", "V5P6TH2CEvidenceCompletePreflightTests"));

    [Theory]
    [InlineData("V5P6TH2CEvidenceCompletePreflightTests", 2)]
    [InlineData("V5P6TH2CTypographyOnlyScreenPreflightTests", 2)]
    [InlineData("V5P6TH2CTypographyOnlyScreenGoldScoreTests", 1)]
    [InlineData("V5P6TH2CCleanBoundarySeparabilityAuditTests", 0)]
    public void A_test_is_accountable_only_for_the_frozen_artifacts_it_owns(string testClass, int owned) =>
        Assert.Equal(owned, FrozenHistoryReplayPolicy.OwnedFrozenArtifacts("SRC-089", testClass).Count);

    [Fact]
    public void A_frozen_artifact_is_a_committed_file_never_a_pending_one()
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TestRepository.Path(FrozenHistoryReplayPolicy.ManifestPath)));
        var scope = manifest.RootElement.GetProperty("entries")[0].GetProperty("scope");
        Assert.All(scope.GetProperty("frozenArtifacts").EnumerateArray(), artifact =>
        {
            Assert.False(artifact.TryGetProperty("committed", out _));
            Assert.True(File.Exists(TestRepository.Path(artifact.GetProperty("path").GetString()!)));
        });
        Assert.Empty(scope.GetProperty("frozenArtifacts").EnumerateArray().Select(a => a.GetProperty("path").GetString())
            .Intersect(scope.GetProperty("pendingArtifacts").EnumerateArray().Select(a => a.GetProperty("path").GetString())));
    }
}
