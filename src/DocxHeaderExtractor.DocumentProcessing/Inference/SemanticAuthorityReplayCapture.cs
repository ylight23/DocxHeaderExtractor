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
    public SemanticAuthorityReplayPersistenceResult Persist(SemanticAuthorityReplayBundle? bundle)
    {
        if (bundle is null)
        {
            if (RequireReplayBundle)
                throw new InvalidOperationException("REPLAY_BUNDLE_REQUIRED_BUT_NOT_CAPTURED");
            return new(false, null, "REPLAY_BUNDLE_NOT_CAPTURED");
        }

        try
        {
            var path = SemanticAuthorityReplayArtifactWriter.WriteAtomic(bundle, ArtifactDirectory);
            return new(true, path, null);
        }
        catch (Exception error) when (!RequireReplayBundle &&
            error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, null, error.GetType().Name + ": " + error.Message);
        }
    }
}

public sealed record SemanticAuthorityReplayPersistenceResult(
    bool Persisted,
    string? ArtifactPath,
    string? Error);

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

    private static SemanticAuthorityReplayBundle Read(string path)
    {
        var bundle = JsonSerializer.Deserialize<SemanticAuthorityReplayBundle>(File.ReadAllText(path), Json);
        return bundle ?? throw new InvalidOperationException("REPLAY_ARTIFACT_EMPTY");
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
