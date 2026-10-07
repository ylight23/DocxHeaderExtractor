using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>How a frozen historical experiment may be replayed for one document.</summary>
public enum HistoricalReplayStatus
{
    /// <summary>The experiment is rebuilt live from the document and compared with its frozen artifact.</summary>
    LiveReplay,

    /// <summary>
    /// The frozen artifact is immutable authority and is verified by hash; the live rebuild is retired for this document
    /// because the geometry it was derived from can no longer be reconstructed. Nothing else is retired.
    /// </summary>
    FrozenEvidenceOnly,
}

/// <summary>
/// The explicit, user-decided record of which historical experiments cannot be live-replayed for which document, and why.
/// <c>gold-history/pdf-universe-v1/replay-policy.v1.json</c> is the only place this is decided; a test asks here, it never
/// special-cases a document id. An exemption is narrow: one document, one capability (rich line/glyph geometry), the tests the
/// manifest names. Frozen artifacts, the v1 Gold vintage used to score them, and every other document keep their full gate.
/// </summary>
public static class FrozenHistoryReplayPolicy
{
    public const string ManifestPath = "eval/a99-closed-loop/gold-history/pdf-universe-v1/replay-policy.v1.json";
    public const string RichGeometryCapability = "RICH_GEOMETRY_LIVE_REPLAY";
    public const string ReasonCode = "LIVE_REPLAY_UNAVAILABLE_AFTER_SOURCE_GEOMETRY_MIGRATION";

    public static HistoricalReplayStatus RichGeometry(string documentId)
    {
        using var manifest = Read();
        var entry = Find(manifest.RootElement, documentId);
        return entry is { } e && e.GetProperty("status").GetString() == "FROZEN_EVIDENCE_ONLY"
            ? HistoricalReplayStatus.FrozenEvidenceOnly
            : HistoricalReplayStatus.LiveReplay;
    }

    /// <summary>
    /// What a retired live replay must still prove, in place of the rebuild: the right document, the right historical universe,
    /// frozen artifacts byte-identical to their pinned hashes, a current geometry profile that really is the new one, the
    /// recorded reason - and that nothing is rebuilt, rewritten or sent to a provider.
    /// </summary>
    public static void AssertFrozenEvidenceOnly(string documentId, string testClass)
    {
        using var manifest = Read();
        var entry = Find(manifest.RootElement, documentId)
                    ?? throw new InvalidOperationException($"{documentId} has no replay policy entry.");
        Assert.Equal(RichGeometryCapability, entry.GetProperty("capability").GetString());
        Assert.Equal("FROZEN_EVIDENCE_ONLY", entry.GetProperty("status").GetString());
        Assert.Equal(ReasonCode, entry.GetProperty("reasonCode").GetString());
        Assert.Equal("USER", entry.GetProperty("decidedBy").GetString());
        Assert.Contains(testClass, entry.GetProperty("scope").GetProperty("tests").EnumerateArray().Select(item => item.GetString()));

        // Nothing is rewritten: a freeze update cannot turn a retired replay into a new baseline.
        Assert.False(FreezeArtifact.UpdateRequested, "a retired live replay must not be refreshed");

        // The historical universe is the frozen v1 snapshot's, and the current one is a different universe.
        var historical = SnapshotUniverse(entry.GetProperty("historicalSnapshot").GetString()!);
        var current = SnapshotUniverse(entry.GetProperty("currentSnapshot").GetString()!);
        Assert.Equal(entry.GetProperty("historicalUniverseSha256").GetString(), historical);
        Assert.Equal(entry.GetProperty("currentUniverseSha256").GetString(), current);
        Assert.NotEqual(historical, current);

        // The document is read by the current geometry, which is the migrated one.
        Assert.Equal(PdfGeometryPolicy.Version, entry.GetProperty("currentGeometryProfile").GetString());
        var pdf = documentId == "SRC-089"
            ? SourcePdfCorpus.Src089
            : throw new InvalidOperationException($"{documentId} is not a known replay-policy source.");
        using (var document = PdfDocument.Open(TestRepository.Path(pdf)))
            Assert.Equal(PdfGeometryMode.FontIndependent, PdfFontEmbedding.ModeFor(document));

        // The frozen artifacts are immutable authority and still say what they said: pinned bytes, v1 universe.
        var seenHistoricalUniverse = false;
        foreach (var artifact in entry.GetProperty("scope").GetProperty("frozenArtifacts").EnumerateArray())
        {
            var path = TestRepository.Path(artifact.GetProperty("path").GetString()!);
            Assert.True(File.Exists(path), $"frozen artifact {artifact.GetProperty("path").GetString()} is missing");
            Assert.Equal(artifact.GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(path));
            seenHistoricalUniverse |= File.ReadAllText(path).Contains(historical, StringComparison.Ordinal);
        }
        Assert.True(seenHistoricalUniverse, "none of the frozen artifacts records the historical universe");
    }

    private static string SnapshotUniverse(string relativePath)
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(relativePath)));
        return snapshot.RootElement.GetProperty("SourceAliasUniverseSha256").GetString()!;
    }

    private static JsonElement? Find(JsonElement root, string documentId) =>
        root.GetProperty("entries").EnumerateArray()
            .Where(item => item.GetProperty("documentId").GetString() == documentId &&
                           item.GetProperty("capability").GetString() == RichGeometryCapability)
            .Select(item => (JsonElement?)item).SingleOrDefault();

    private static JsonDocument Read() => JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ManifestPath)));
}
