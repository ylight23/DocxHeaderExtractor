using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.V6Qualification;

/// <summary>
/// The cache-only evidence source that the comparative canaries (hierarchy and boundary-label-swap) load from a manifest: the manifest
/// record, its scope record, and the loader that refuses any transcription it would have to make live.
/// </summary>
internal static class CanaryEvidenceSource
{
internal sealed record Manifest(string SourceDocument, string OcrEvidence, string EvidenceEntry, string StoreRoot, Scope[] Cases, int Pairs = 2);
    
internal sealed record Scope(string Id, string[] Aliases);

    sealed class CacheOnly : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("QUALIFICATION_TRANSCRIPTION_CACHE_MISS");
    }

internal static ScanAgentSource LoadSource(Manifest manifest, CancellationToken ct)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var evidence = JsonSerializer.Deserialize<ScanOcrEvidence>(File.ReadAllText(manifest.OcrEvidence), json)!;
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest.SourceDocument))) != evidence.SourceSha256)
            throw new InvalidOperationException("CANARY_SOURCE_HASH_MISMATCH");
        var transcription = new ScanTranscriptionPreflight(manifest.StoreRoot, () => new CacheOnly())
            .Run(evidence, manifest.EvidenceEntry, "qualification-cache-only", ct: ct);
        if (transcription.ProviderCalls != 0 || transcription.Failures.Count != 0)
            throw new InvalidOperationException("CANARY_REQUIRES_COMPLETE_CACHED_TRANSCRIPTION");
        var typography = ScanTypography.MeasureAll(evidence, manifest.EvidenceEntry, TesseractOcr.FindImageMagick(), ct);
        return new(evidence, transcriptions: transcription, typography: typography, evidenceEntry: manifest.EvidenceEntry);
    }
}
