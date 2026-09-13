using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Immutable prepare-phase identity for a live run. The execute phase must verify this exact
/// identity before making any provider call; it must never silently rebuild a similar request.
/// </summary>
public sealed record HdsaLiveRunManifest(
    string SchemaVersion,
    string RunId,
    string SourceSha256,
    string CatalogFingerprint,
    string ResolverVersion,
    string ContractVersion,
    IReadOnlyDictionary<string, string> RequestHashes,
    bool GoldReadBeforeFreeze,
    bool GoldDerivedInput,
    bool LegacyHierarchyConsumed)
{
    public bool IsProductionSafe =>
        string.Equals(SchemaVersion, "a99-live-run-manifest-v1", StringComparison.Ordinal) &&
        !GoldReadBeforeFreeze && !GoldDerivedInput && !LegacyHierarchyConsumed;
}

public sealed record HdsaLiveRunManifestVerification(
    bool Accepted,
    string? RejectionReason)
{
    public int ProviderCallsAllowed => Accepted ? 1 : 0;
}

public static class HdsaLiveRunManifestGuard
{
    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static HdsaLiveRunManifest Prepare(
        string runId,
        string sourceSha256,
        string catalogFingerprint,
        string resolverVersion,
        string contractVersion,
        IReadOnlyDictionary<string, string> canonicalRequestBodies)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Run ID is required.", nameof(runId));
        if (string.IsNullOrWhiteSpace(sourceSha256)) throw new ArgumentException("Source hash is required.", nameof(sourceSha256));
        if (string.IsNullOrWhiteSpace(catalogFingerprint)) throw new ArgumentException("Catalog fingerprint is required.", nameof(catalogFingerprint));
        if (string.IsNullOrWhiteSpace(resolverVersion)) throw new ArgumentException("Resolver version is required.", nameof(resolverVersion));
        if (string.IsNullOrWhiteSpace(contractVersion)) throw new ArgumentException("Contract version is required.", nameof(contractVersion));
        ArgumentNullException.ThrowIfNull(canonicalRequestBodies);

        var hashes = canonicalRequestBodies
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => Sha256(item.Value), StringComparer.Ordinal);
        return new(
            "a99-live-run-manifest-v1",
            runId,
            sourceSha256,
            catalogFingerprint,
            resolverVersion,
            contractVersion,
            new ReadOnlyDictionary<string, string>(hashes),
            false,
            false,
            false);
    }

    public static string Serialize(HdsaLiveRunManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsProductionSafe) throw new InvalidDataException("LIVE_MANIFEST_FIREWALL_FAILED");
        return JsonSerializer.Serialize(manifest, HashOptions);
    }

    public static HdsaLiveRunManifestVerification Verify(
        HdsaLiveRunManifest manifest,
        string sourceSha256,
        string catalogFingerprint,
        string resolverVersion,
        string contractVersion,
        IReadOnlyDictionary<string, string> canonicalRequestBodies)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(canonicalRequestBodies);
        if (!manifest.IsProductionSafe) return Reject("MANIFEST_FIREWALL_FAILED");
        if (!string.Equals(manifest.SourceSha256, sourceSha256, StringComparison.Ordinal)) return Reject("SOURCE_FINGERPRINT_MISMATCH");
        if (!string.Equals(manifest.CatalogFingerprint, catalogFingerprint, StringComparison.Ordinal)) return Reject("CATALOG_FINGERPRINT_MISMATCH");
        if (!string.Equals(manifest.ResolverVersion, resolverVersion, StringComparison.Ordinal)) return Reject("RESOLVER_VERSION_MISMATCH");
        if (!string.Equals(manifest.ContractVersion, contractVersion, StringComparison.Ordinal)) return Reject("CONTRACT_VERSION_MISMATCH");

        var expected = canonicalRequestBodies
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => Sha256(item.Value), StringComparer.Ordinal);
        if (manifest.RequestHashes.Count != expected.Count ||
            manifest.RequestHashes.Any(item => !expected.TryGetValue(item.Key, out var hash) || !string.Equals(hash, item.Value, StringComparison.Ordinal)))
            return Reject("REQUEST_FINGERPRINT_MISMATCH");
        return new(true, null);
    }

    private static HdsaLiveRunManifestVerification Reject(string reason) => new(false, reason);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value))))).ToLowerInvariant();
}
