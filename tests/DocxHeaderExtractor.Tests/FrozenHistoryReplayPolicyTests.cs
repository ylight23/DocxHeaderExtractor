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
}
