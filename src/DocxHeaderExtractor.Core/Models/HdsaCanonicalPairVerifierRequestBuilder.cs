using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The single canonical serializer for identity pair-verifier request bytes.
/// Keep this implementation shared by live execution and offline benchmark freeze.
/// </summary>
public static class HdsaCanonicalPairVerifierRequestBuilder
{
    public const string Version = "hdsa-canonical-pair-verifier-request-builder-v1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static HdsaCanonicalPairVerifierRequest Build(HdsaIdentityPairVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        return new(json, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>
    /// Prepares the invariant catalog portion once for a document. The resulting request bytes
    /// are exactly the same as <see cref="Build"/>; only repeated JSON traversal is avoided.
    /// </summary>
    public static HdsaCanonicalPairVerifierRequestTemplate CreateTemplate(
        string catalogFingerprint,
        IReadOnlyList<HdsaIdentityRoleNodeInput> nodes,
        bool goldDerivedInput = false)
    {
        ArgumentNullException.ThrowIfNull(catalogFingerprint);
        ArgumentNullException.ThrowIfNull(nodes);
        var sentinel = new HdsaIdentityCandidatePair(
            "__A99_TARGET_PAIR_SENTINEL__",
            "__A99_LEFT_SENTINEL__",
            "__A99_RIGHT_SENTINEL__");
        var prototype = new HdsaIdentityPairVerificationRequest(
            catalogFingerprint, nodes, sentinel, goldDerivedInput);
        var templateJson = JsonSerializer.Serialize(prototype, Options);
        var targetProperty = templateJson.IndexOf("\"targetPair\":", StringComparison.Ordinal);
        var marker = targetProperty < 0 ? -1 : templateJson.IndexOf('{', targetProperty);
        var goldProperty = marker < 0 ? -1 : templateJson.IndexOf("\"goldDerivedInput\"", marker, StringComparison.Ordinal);
        var endMarker = goldProperty < 0 ? -1 : templateJson.LastIndexOf('}', goldProperty);
        if (marker < 0 || endMarker < 0)
            throw new InvalidOperationException("A99_REQUEST_TEMPLATE_SENTINEL_NOT_FOUND");
        return new(templateJson[..marker], templateJson[(endMarker + 1)..], Options);
    }
}

public sealed record HdsaCanonicalPairVerifierRequest(
    string Json,
    byte[] Utf8Bytes,
    string Sha256);

public sealed class HdsaCanonicalPairVerifierRequestTemplate
{
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly JsonSerializerOptions _options;

    internal HdsaCanonicalPairVerifierRequestTemplate(string prefix, string suffix, JsonSerializerOptions options)
    {
        _prefix = prefix;
        _suffix = suffix;
        _options = options;
    }

    public HdsaCanonicalPairVerifierRequest Build(HdsaIdentityCandidatePair targetPair)
    {
        ArgumentNullException.ThrowIfNull(targetPair);
        var targetJson = JsonSerializer.Serialize(targetPair, _options);
        targetJson = targetJson.Replace("\n", "\n  ", StringComparison.Ordinal);
        var json = string.Concat(_prefix, targetJson, _suffix);
        var bytes = Encoding.UTF8.GetBytes(json);
        return new(json, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
