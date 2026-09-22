using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Inference;

public static class SemanticAuthorityCaptureReservationSchema
{
    public const string Version = "a99-semantic-authority-capture-reservation-v1";
    public const string SlotReserved = "CAPTURE_SLOT_RESERVED";
    public const string TransportCaptureComplete = "TRANSPORT_CAPTURE_COMPLETE";
    public const string SlotFileName = "semantic-authority-capture-slot.v1.json";
}

/// <summary>One request identity derived before the classifier is allowed to transport it.</summary>
public sealed record SemanticAuthorityCaptureCallIdentity(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("packId")] string? PackId,
    [property: JsonPropertyName("requestSha256")] string RequestSha256,
    [property: JsonPropertyName("requestBytes")] int RequestBytes);

/// <summary>
/// Filesystem reservation for one complete replay capture. A reserved slot is deliberately not
/// reusable after a crash: an operator must inspect/recover it explicitly rather than silently
/// making a second provider run look like the first one.
/// </summary>
public sealed record SemanticAuthorityCaptureReservation(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("identityHash")] string IdentityHash,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("sourceUniverseHash")] string SourceUniverseHash,
    [property: JsonPropertyName("modelIdentity")] string ModelIdentity,
    [property: JsonPropertyName("modelRoute")] string? ModelRoute,
    [property: JsonPropertyName("promptHash")] string PromptHash,
    [property: JsonPropertyName("semanticContractHash")] string SemanticContractHash,
    [property: JsonPropertyName("profile")] string? Profile,
    [property: JsonPropertyName("packingPolicy")] string? PackingPolicy,
    [property: JsonPropertyName("repeatIdentity")] string? RepeatIdentity,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("calls")] IReadOnlyList<SemanticAuthorityCaptureCallIdentity> Calls);

/// <summary>
/// Experiment/harness-owned persistence request. The semantic engine creates the bundle; this
/// boundary owns filesystem policy and is only active when a caller explicitly supplies it.
/// </summary>
public sealed record SemanticAuthorityReplayCaptureRequest(
    SemanticAuthorityCaptureMetadata Metadata,
    string ArtifactDirectory,
    bool RequireReplayBundle = true,
    SemanticAuthorityCaptureReservation? Reservation = null,
    bool RequireCaptureReservation = false)
{
    /// <summary>
    /// Derives every semantic request identity and reserves the capture slot before the first
    /// classifier call. The exclusive slot is the idempotency boundary; it is intentionally kept
    /// when a process crashes, so an incomplete capture cannot be silently reused.
    /// </summary>
    public SemanticAuthorityReplayCaptureRequest Reserve(
        string documentId,
        string sourceHash,
        string semanticContractHash,
        IReadOnlyList<SemanticAuthorityCaptureCallIdentity> calls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticContractHash);
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count == 0)
            throw new InvalidOperationException("TRANSPORT_CAPTURE_RESERVATION_EMPTY");
        if (Reservation is not null)
            throw new InvalidOperationException("TRANSPORT_CAPTURE_ALREADY_RESERVED");

        Directory.CreateDirectory(ArtifactDirectory);
        var transportPath = Path.Combine(
            ArtifactDirectory,
            SemanticAuthorityReplayArtifactWriter.StableTransportFileName(
                documentId, sourceHash, Metadata.SourceUniverseHash));

        var frozenCalls = calls.OrderBy(call => call.Ordinal).ToArray();
        var identityHash = SemanticAuthorityReplayHashing.CanonicalValueHash(new
        {
            schemaVersion = SemanticAuthorityCaptureReservationSchema.Version,
            documentId,
            sourceHash,
            sourceUniverseHash = Metadata.SourceUniverseHash,
            modelIdentity = Metadata.ModelIdentity,
            modelRoute = Metadata.ModelRoute,
            promptHash = Metadata.PromptHash,
            semanticContractHash,
            profile = Metadata.Profile,
            packingPolicy = Metadata.PackingPolicy,
            repeatIdentity = Metadata.RepeatIdentity ?? Metadata.RunId,
            runId = Metadata.RunId,
            calls = frozenCalls,
        });
        var reservation = new SemanticAuthorityCaptureReservation(
            SemanticAuthorityCaptureReservationSchema.Version,
            SemanticAuthorityCaptureReservationSchema.SlotReserved,
            identityHash,
            documentId,
            sourceHash,
            Metadata.SourceUniverseHash,
            Metadata.ModelIdentity,
            Metadata.ModelRoute,
            Metadata.PromptHash,
            semanticContractHash,
            Metadata.Profile,
            Metadata.PackingPolicy,
            Metadata.RepeatIdentity ?? Metadata.RunId,
            Metadata.RunId,
            frozenCalls);
        if (File.Exists(transportPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<SemanticAuthorityTransportCapture>(
                    File.ReadAllText(transportPath), Json);
                if (existing is not null &&
                    string.Equals(existing.DocumentId, documentId, StringComparison.Ordinal) &&
                    string.Equals(existing.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.SourceUniverseHash, Metadata.SourceUniverseHash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ModelIdentity, Metadata.ModelIdentity, StringComparison.Ordinal) &&
                    string.Equals(existing.ModelRoute, Metadata.ModelRoute, StringComparison.Ordinal) &&
                    string.Equals(existing.PromptHash, Metadata.PromptHash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Profile, Metadata.Profile, StringComparison.Ordinal) &&
                    string.Equals(existing.PackingPolicy, Metadata.PackingPolicy, StringComparison.Ordinal) &&
                    string.Equals(existing.RepeatIdentity, Metadata.RepeatIdentity ?? Metadata.RunId, StringComparison.Ordinal))
                    throw new InvalidOperationException("TRANSPORT_CAPTURE_ALREADY_EXISTS");
            }
            catch (JsonException)
            {
                // A malformed existing capture is not reusable authority and is never overwritten.
            }
            throw new InvalidOperationException("TRANSPORT_CAPTURE_IDENTITY_COLLISION");
        }
        if (Directory.GetFiles(ArtifactDirectory, "*.semantic-authority-replay.v1.json").Length > 0)
            throw new InvalidOperationException("TRANSPORT_CAPTURE_INCOMPLETE");
        var slotPath = Path.Combine(ArtifactDirectory, SemanticAuthorityCaptureReservationSchema.SlotFileName);
        try
        {
            WriteReservationExclusive(slotPath, reservation);
        }
        catch (IOException) when (File.Exists(slotPath))
        {
            throw new InvalidOperationException(
                File.Exists(transportPath)
                    ? "TRANSPORT_CAPTURE_ALREADY_EXISTS"
                    : "TRANSPORT_CAPTURE_SLOT_RESERVED");
        }

        return this with
        {
            RequireReplayBundle = true,
            Reservation = reservation,
            RequireCaptureReservation = true,
        };
    }

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
            if (RequireCaptureReservation && Reservation is null)
                throw new InvalidOperationException("TRANSPORT_CAPTURE_RESERVATION_REQUIRED");
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
            ValidateReservation(bundle, calls);
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
            if (Reservation is not null)
                MarkReservationComplete(ArtifactDirectory, Reservation);
            return new(true, path, null, transportPath);
        }
        catch (Exception error) when (!RequireReplayBundle &&
            error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new(false, null, error.GetType().Name + ": " + error.Message);
        }
    }

    private void ValidateReservation(
        SemanticAuthorityReplayBundle bundle,
        IReadOnlyList<SemanticAuthorityTransportCall> calls)
    {
        if (Reservation is null) return;
        if (!string.Equals(Reservation.Status, SemanticAuthorityCaptureReservationSchema.SlotReserved,
            StringComparison.Ordinal))
            throw new InvalidOperationException("TRANSPORT_CAPTURE_RESERVATION_NOT_ACTIVE");
        if (!string.Equals(bundle.DocumentId, Reservation.DocumentId, StringComparison.Ordinal) ||
            !string.Equals(bundle.SourceHash, Reservation.SourceHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bundle.SourceUniverseHash, Reservation.SourceUniverseHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bundle.ModelIdentity, Reservation.ModelIdentity, StringComparison.Ordinal) ||
            !string.Equals(bundle.ModelRoute, Reservation.ModelRoute, StringComparison.Ordinal) ||
            !string.Equals(bundle.PromptHash, Reservation.PromptHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Metadata.Profile, Reservation.Profile, StringComparison.Ordinal) ||
            !string.Equals(Metadata.PackingPolicy, Reservation.PackingPolicy, StringComparison.Ordinal) ||
            !string.Equals(Metadata.RepeatIdentity ?? Metadata.RunId, Reservation.RepeatIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("TRANSPORT_CAPTURE_RESERVATION_IDENTITY_MISMATCH");

        var actual = calls.OrderBy(call => call.Ordinal)
            .Select(call => new SemanticAuthorityCaptureCallIdentity(
                call.Ordinal, call.Stage, call.PackId, call.RequestSha256, call.RequestBytes))
            .ToArray();
        if (!actual.SequenceEqual(Reservation.Calls))
            throw new InvalidOperationException("TRANSPORT_CAPTURE_RESERVATION_CALL_PLAN_MISMATCH");
    }

    private static void WriteReservationExclusive(
        string path,
        SemanticAuthorityCaptureReservation reservation)
    {
        var json = JsonSerializer.Serialize(reservation, Json);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void MarkReservationComplete(
        string artifactDirectory,
        SemanticAuthorityCaptureReservation reservation)
    {
        var path = Path.Combine(artifactDirectory, SemanticAuthorityCaptureReservationSchema.SlotFileName);
        var completed = reservation with
        {
            Status = SemanticAuthorityCaptureReservationSchema.TransportCaptureComplete,
        };
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(completed, Json));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
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
        return StableTransportFileName(capture.DocumentId, capture.SourceHash, capture.SourceUniverseHash);
    }

    public static string StableTransportFileName(
        string documentId,
        string sourceHash,
        string sourceUniverseHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUniverseHash);
        return string.Join('.',
            SafeSegment(documentId),
            "source-" + Prefix(sourceHash),
            "universe-" + Prefix(sourceUniverseHash),
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
