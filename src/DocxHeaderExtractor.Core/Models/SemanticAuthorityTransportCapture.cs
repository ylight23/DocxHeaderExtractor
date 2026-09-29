using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public static class SemanticAuthorityTransportCaptureSchema
{
    public const string Version = "a99-semantic-authority-transport-capture-v1";
    public const string TransportCaptureComplete = "TRANSPORT_CAPTURE_COMPLETE";
    public const string ReplayMaterializationComplete = "REPLAY_MATERIALIZATION_COMPLETE";
}

/// <summary>
/// Immutable evidence for one semantic transport call. The request and response payloads are
/// retained as UTF-8 bytes encoded in base64 so scoring can replay the exact bytes captured at the
/// semantic transport boundary instead of relying on a response hash alone.
/// </summary>
public sealed record SemanticAuthorityTransportCall(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("packId")] string? PackId,
    [property: JsonPropertyName("requestSha256")] string RequestSha256,
    [property: JsonPropertyName("requestBytes")] int RequestBytes,
    [property: JsonPropertyName("requestUtf8Base64")] string RequestUtf8Base64,
    [property: JsonPropertyName("rawResponseSha256")] string RawResponseSha256,
    [property: JsonPropertyName("rawResponseBytes")] int RawResponseBytes,
    [property: JsonPropertyName("rawResponseUtf8Base64")] string RawResponseUtf8Base64)
{
    public static SemanticAuthorityTransportCall Create(
        int ordinal,
        string stage,
        string? packId,
        string requestPayload,
        string rawResponse)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(requestPayload);
        ArgumentNullException.ThrowIfNull(rawResponse);
        var requestBytes = Encoding.UTF8.GetBytes(requestPayload);
        var responseBytes = Encoding.UTF8.GetBytes(rawResponse);
        return new(
            ordinal,
            stage,
            packId,
            Sha256(requestBytes),
            requestBytes.Length,
            Convert.ToBase64String(requestBytes),
            Sha256(responseBytes),
            responseBytes.Length,
            Convert.ToBase64String(responseBytes));
    }

    public static string Sha256Utf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Sha256(Encoding.UTF8.GetBytes(value));
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Ordinal <= 0) errors.Add("TRANSPORT_CALL_ORDINAL_INVALID");
        if (string.IsNullOrWhiteSpace(Stage)) errors.Add("TRANSPORT_CALL_STAGE_MISSING");
        ValidatePayload(RequestUtf8Base64, RequestBytes, RequestSha256, "REQUEST", errors);
        ValidatePayload(RawResponseUtf8Base64, RawResponseBytes, RawResponseSha256, "RAW_RESPONSE", errors);
        return errors;
    }

    private static void ValidatePayload(
        string base64,
        int expectedBytes,
        string expectedHash,
        string label,
        ICollection<string> errors)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            errors.Add($"TRANSPORT_CALL_{label}_BASE64_INVALID");
            return;
        }

        if (bytes.Length != expectedBytes)
            errors.Add($"TRANSPORT_CALL_{label}_BYTE_COUNT_MISMATCH");
        if (!string.Equals(Sha256(bytes), expectedHash, StringComparison.OrdinalIgnoreCase))
            errors.Add($"TRANSPORT_CALL_{label}_HASH_MISMATCH");
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>One immutable, replay-complete capture envelope for a semantic run.</summary>
public sealed record SemanticAuthorityTransportCapture(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("sourceType")] string SourceType,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("sourceUniverseHash")] string SourceUniverseHash,
    [property: JsonPropertyName("modelIdentity")] string ModelIdentity,
    [property: JsonPropertyName("modelRoute")] string? ModelRoute,
    [property: JsonPropertyName("promptHash")] string PromptHash,
    [property: JsonPropertyName("semanticContractHash")] string SemanticContractHash,
    [property: JsonPropertyName("profile")] string? Profile,
    [property: JsonPropertyName("packingPolicy")] string? PackingPolicy,
    [property: JsonPropertyName("repeatIdentity")] string? RepeatIdentity,
    [property: JsonPropertyName("evaluatorIdentity")] string? EvaluatorIdentity,
    [property: JsonPropertyName("manifestHash")] string? ManifestHash,
    [property: JsonPropertyName("bundleHash")] string BundleHash,
    [property: JsonPropertyName("transportCaptureStatus")] string TransportCaptureStatus,
    [property: JsonPropertyName("replayMaterializationStatus")] string ReplayMaterializationStatus,
    [property: JsonPropertyName("calls")] IReadOnlyList<SemanticAuthorityTransportCall> Calls,
    [property: JsonPropertyName("captureHash")] string CaptureHash);

public static class SemanticAuthorityTransportCaptureFactory
{
    public static SemanticAuthorityTransportCapture Create(
        SemanticAuthorityReplayBundle bundle,
        SemanticAuthorityCaptureMetadata metadata,
        IReadOnlyList<SemanticAuthorityTransportCall> calls)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(calls);
        var frozenCalls = calls.OrderBy(call => call.Ordinal).ToArray();
        var payload = new
        {
            schemaVersion = SemanticAuthorityTransportCaptureSchema.Version,
            documentId = bundle.DocumentId,
            sourceType = metadata.SourceType,
            sourceHash = bundle.SourceHash,
            sourceUniverseHash = bundle.SourceUniverseHash,
            modelIdentity = bundle.ModelIdentity,
            modelRoute = bundle.ModelRoute,
            promptHash = bundle.PromptHash,
            semanticContractHash = bundle.SemanticContractHash,
            profile = metadata.Profile,
            packingPolicy = metadata.PackingPolicy,
            repeatIdentity = metadata.RepeatIdentity ?? metadata.RunId,
            evaluatorIdentity = bundle.EvaluatorIdentity,
            manifestHash = bundle.ManifestHash,
            bundleHash = bundle.BundleHash,
            transportCaptureStatus = SemanticAuthorityTransportCaptureSchema.TransportCaptureComplete,
            replayMaterializationStatus = SemanticAuthorityTransportCaptureSchema.ReplayMaterializationComplete,
            calls = frozenCalls,
        };
        return new(
            SemanticAuthorityTransportCaptureSchema.Version,
            bundle.DocumentId,
            metadata.SourceType,
            bundle.SourceHash,
            bundle.SourceUniverseHash,
            bundle.ModelIdentity,
            bundle.ModelRoute,
            bundle.PromptHash,
            bundle.SemanticContractHash,
            metadata.Profile,
            metadata.PackingPolicy,
            metadata.RepeatIdentity ?? metadata.RunId,
            bundle.EvaluatorIdentity,
            bundle.ManifestHash,
            bundle.BundleHash,
            SemanticAuthorityTransportCaptureSchema.TransportCaptureComplete,
            SemanticAuthorityTransportCaptureSchema.ReplayMaterializationComplete,
            frozenCalls,
            SemanticAuthorityReplayHashing.CanonicalValueHash(payload));
    }

    public static IReadOnlyList<string> Validate(SemanticAuthorityTransportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var errors = new List<string>();
        if (!string.Equals(capture.SchemaVersion, SemanticAuthorityTransportCaptureSchema.Version, StringComparison.Ordinal))
            errors.Add("TRANSPORT_CAPTURE_SCHEMA_VERSION_MISMATCH");
        if (!string.Equals(capture.TransportCaptureStatus,
            SemanticAuthorityTransportCaptureSchema.TransportCaptureComplete, StringComparison.Ordinal))
            errors.Add("TRANSPORT_CAPTURE_INCOMPLETE");
        if (!string.Equals(capture.ReplayMaterializationStatus,
            SemanticAuthorityTransportCaptureSchema.ReplayMaterializationComplete, StringComparison.Ordinal))
            errors.Add("REPLAY_MATERIALIZATION_INCOMPLETE");
        if (capture.Calls.Count != capture.Calls.Select(call => call.Ordinal).Distinct().Count())
            errors.Add("TRANSPORT_CAPTURE_DUPLICATE_ORDINAL");
        foreach (var call in capture.Calls)
            errors.AddRange(call.Validate());
        var payload = new
        {
            schemaVersion = capture.SchemaVersion,
            documentId = capture.DocumentId,
            sourceType = capture.SourceType,
            sourceHash = capture.SourceHash,
            sourceUniverseHash = capture.SourceUniverseHash,
            modelIdentity = capture.ModelIdentity,
            modelRoute = capture.ModelRoute,
            promptHash = capture.PromptHash,
            semanticContractHash = capture.SemanticContractHash,
            profile = capture.Profile,
            packingPolicy = capture.PackingPolicy,
            repeatIdentity = capture.RepeatIdentity,
            evaluatorIdentity = capture.EvaluatorIdentity,
            manifestHash = capture.ManifestHash,
            bundleHash = capture.BundleHash,
            transportCaptureStatus = capture.TransportCaptureStatus,
            replayMaterializationStatus = capture.ReplayMaterializationStatus,
            calls = capture.Calls.OrderBy(call => call.Ordinal).ToArray(),
        };
        if (!string.Equals(
            SemanticAuthorityReplayHashing.CanonicalValueHash(payload),
            capture.CaptureHash,
            StringComparison.OrdinalIgnoreCase))
            errors.Add("TRANSPORT_CAPTURE_HASH_MISMATCH");
        return errors;
    }
}
