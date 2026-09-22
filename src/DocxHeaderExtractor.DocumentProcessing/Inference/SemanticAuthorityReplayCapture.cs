using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>
/// Experiment/harness-owned persistence request. The semantic engine creates the bundle; this
/// boundary owns filesystem policy and is only active when a caller explicitly supplies it.
/// </summary>
public sealed record SemanticAuthorityReplayCaptureRequest(
    SemanticAuthorityCaptureMetadata Metadata,
    string ArtifactDirectory,
    bool RequireReplayBundle = true)
{
    public SemanticAuthorityReplayPersistenceResult Persist(SemanticAuthorityReplayBundle? bundle) =>
        Persist(bundle, []);

    public SemanticAuthorityReplayPersistenceResult Persist(
        SemanticAuthorityReplayBundle? bundle,
        IReadOnlyList<SemanticAuthorityTransportCall>? transportCalls)
    {
        if (bundle is null)
        {
            if (RequireReplayBundle)
                throw new InvalidOperationException("REPLAY_BUNDLE_REQUIRED_BUT_NOT_CAPTURED");
            return new(false, null, "REPLAY_BUNDLE_NOT_CAPTURED");
        }

        try
        {
            var calls = transportCalls ?? throw new InvalidOperationException(
                "REPLAY_CAPTURE_TRANSPORT_CALLS_NOT_CAPTURED");
            if (calls.Count > 0 &&
                (string.IsNullOrWhiteSpace(Metadata.Profile) ||
                 string.IsNullOrWhiteSpace(Metadata.PackingPolicy) ||
                 string.IsNullOrWhiteSpace(Metadata.RepeatIdentity ?? Metadata.RunId)))
                throw new InvalidOperationException("REPLAY_CAPTURE_LINEAGE_METADATA_MISSING");
            if (calls.Count == 0 && !string.Equals(
                bundle.RawModelResponseHash,
                SemanticAuthorityReplayHashing.RawModelResponseHash([]),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("REPLAY_CAPTURE_RAW_RESPONSE_NOT_PERSISTED");
            var capture = SemanticAuthorityTransportCaptureFactory.Create(bundle, Metadata, calls);
            var captureErrors = SemanticAuthorityTransportCaptureFactory.Validate(capture);
            if (captureErrors.Count > 0)
                throw new InvalidOperationException("TRANSPORT_CAPTURE_INVALID: " + string.Join(",", captureErrors));

            if (calls.Count > 0)
            {
                var rawResponses = calls
                    .OrderBy(call => call.Ordinal)
                    .Select(call => Encoding.UTF8.GetString(Convert.FromBase64String(call.RawResponseUtf8Base64)))
                    .ToArray();
                var expectedRawHash = SemanticAuthorityReplayHashing.RawModelResponseHash(rawResponses);
                if (!string.Equals(expectedRawHash, bundle.RawModelResponseHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("REPLAY_CAPTURE_RAW_RESPONSE_HASH_MISMATCH");
            }

            var path = SemanticAuthorityReplayArtifactWriter.WriteAtomic(bundle, ArtifactDirectory);
            // The replay bundle is not considered complete until the transport envelope containing
            // every raw response body has also reached durable storage. Callers receive only after
            // both artifacts have passed their independent round-trip validation.
            var transportPath = SemanticAuthorityReplayArtifactWriter.WriteTransportAtomic(capture, ArtifactDirectory);
            return new(true, path, null, transportPath);
        }
        catch (Exception error) when (!RequireReplayBundle &&
            error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new(false, null, error.GetType().Name + ": " + error.Message);
        }
    }
}

public sealed record SemanticAuthorityReplayPersistenceResult(
    bool Persisted,
    string? ArtifactPath,
    string? Error,
    string? TransportArtifactPath = null);

/// <summary>Writes one validated replay authority artifact with a stable identity and atomic move.</summary>
public static class SemanticAuthorityReplayArtifactWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string WriteAtomic(
        SemanticAuthorityReplayBundle bundle,
        string artifactDirectory)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        var errors = SemanticAuthorityReplay.Validate(bundle);
        if (errors.Count > 0)
            throw new InvalidOperationException("REPLAY_BUNDLE_INVALID: " + string.Join(",", errors));

        Directory.CreateDirectory(artifactDirectory);
        var path = Path.Combine(artifactDirectory, StableFileName(bundle));
        if (File.Exists(path))
        {
            var existing = Read(path);
            if (string.Equals(existing.BundleHash, bundle.BundleHash, StringComparison.OrdinalIgnoreCase))
                return path;
            throw new InvalidOperationException("REPLAY_ARTIFACT_ID_COLLISION");
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(bundle, Json);
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                stream, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Deserialize the exact bytes that are about to become authoritative. This catches
            // serializer/configuration drift before the atomic publication point.
            var roundTrip = Read(temp);
            var roundTripErrors = SemanticAuthorityReplay.Validate(roundTrip);
            if (roundTripErrors.Count > 0)
                throw new InvalidOperationException("REPLAY_ARTIFACT_ROUNDTRIP_INVALID: " +
                    string.Join(",", roundTripErrors));

            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                var raced = Read(path);
                if (!string.Equals(raced.BundleHash, bundle.BundleHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("REPLAY_ARTIFACT_ID_COLLISION");
            }
            return path;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string WriteTransportAtomic(
        SemanticAuthorityTransportCapture capture,
        string artifactDirectory)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        var errors = SemanticAuthorityTransportCaptureFactory.Validate(capture);
        if (errors.Count > 0)
            throw new InvalidOperationException("TRANSPORT_CAPTURE_INVALID: " + string.Join(",", errors));

        Directory.CreateDirectory(artifactDirectory);
        var path = Path.Combine(artifactDirectory, StableTransportFileName(capture));
        if (File.Exists(path))
        {
            var existing = ReadTransport(path);
            if (string.Equals(existing.CaptureHash, capture.CaptureHash, StringComparison.OrdinalIgnoreCase))
                return path;
            throw new InvalidOperationException("TRANSPORT_CAPTURE_ID_COLLISION");
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(capture, Json);
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new StreamWriter(
                stream, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            var roundTrip = ReadTransport(temp);
            var roundTripErrors = SemanticAuthorityTransportCaptureFactory.Validate(roundTrip);
            if (roundTripErrors.Count > 0)
                throw new InvalidOperationException("TRANSPORT_CAPTURE_ROUNDTRIP_INVALID: " +
                    string.Join(",", roundTripErrors));

            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                var raced = ReadTransport(path);
                if (!string.Equals(raced.CaptureHash, capture.CaptureHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("TRANSPORT_CAPTURE_ID_COLLISION");
            }
            return path;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string StableFileName(SemanticAuthorityReplayBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return string.Join('.',
            SafeSegment(bundle.DocumentId),
            "source-" + Prefix(bundle.SourceHash),
            "universe-" + Prefix(bundle.SourceUniverseHash),
            "proposal-" + Prefix(bundle.ProposalHash),
            "contract-" + Prefix(bundle.SemanticContractHash),
            "semantic-authority-replay.v1.json");
    }

    public static string StableTransportFileName(SemanticAuthorityTransportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return string.Join('.',
            SafeSegment(capture.DocumentId),
            "source-" + Prefix(capture.SourceHash),
            "universe-" + Prefix(capture.SourceUniverseHash),
            "transport-capture.v1.json");
    }

    private static SemanticAuthorityReplayBundle Read(string path)
    {
        var bundle = JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(File.ReadAllText(path), Json);
        return bundle ?? throw new InvalidOperationException("REPLAY_ARTIFACT_EMPTY");
    }

    private static SemanticAuthorityTransportCapture ReadTransport(string path)
    {
        var capture = JsonSerializer.Deserialize<SemanticAuthorityTransportCapture>(File.ReadAllText(path), Json);
        return capture ?? throw new InvalidOperationException("TRANSPORT_CAPTURE_EMPTY");
    }

    private static string Prefix(string value) =>
        value.Length <= 12 ? value : value[..12];

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }
}
