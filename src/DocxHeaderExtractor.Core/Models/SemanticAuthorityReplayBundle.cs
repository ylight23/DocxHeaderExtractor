using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public static class SemanticAuthorityReplaySchema
{
    public const string Version = "a99-semantic-authority-replay-bundle-v1";
}

/// <summary>
/// Experiment-owned identity supplied to the live semantic route. It describes the run but does
/// not own proposal persistence; the semantic engine emits the immutable bundle and the harness
/// decides where to store it.
/// </summary>
public sealed record SemanticAuthorityCaptureMetadata(
    string SourceType,
    string SourceUniverseHash,
    string ModelIdentity,
    string? ModelRoute,
    string PromptHash,
    string? GoldId = null,
    string? GoldHash = null,
    string? EvaluatorIdentity = null,
    string? ManifestHash = null,
    string? RunId = null,
    string? Commit = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// Immutable authority captured after model JSON has become semantic proposals and before any
/// source-aware validation or binding. Run metadata is descriptive only and is excluded from the
/// deterministic bundle hash.
/// </summary>
public sealed record SemanticAuthorityReplayBundle(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("sourceType")] string SourceType,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("sourceUniverseHash")] string SourceUniverseHash,
    [property: JsonPropertyName("aliasCatalog")] IReadOnlyList<SemanticSourceAlias> AliasCatalog,
    [property: JsonPropertyName("aliasCatalogHash")] string AliasCatalogHash,
    [property: JsonPropertyName("modelIdentity")] string ModelIdentity,
    [property: JsonPropertyName("modelRoute")] string? ModelRoute,
    [property: JsonPropertyName("promptHash")] string PromptHash,
    [property: JsonPropertyName("semanticContractHash")] string SemanticContractHash,
    [property: JsonPropertyName("rawModelResponseHash")] string RawModelResponseHash,
    [property: JsonPropertyName("proposals")] IReadOnlyList<CanonicalSemanticProposal> Proposals,
    [property: JsonPropertyName("proposalHash")] string ProposalHash,
    [property: JsonPropertyName("goldId")] string? GoldId = null,
    [property: JsonPropertyName("goldHash")] string? GoldHash = null,
    [property: JsonPropertyName("evaluatorIdentity")] string? EvaluatorIdentity = null,
    [property: JsonPropertyName("manifestHash")] string? ManifestHash = null)
{
    /// <summary>Content hash over the authority fields only; run metadata is deliberately absent.</summary>
    [JsonPropertyName("bundleHash")]
    public string BundleHash { get; init; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}

public static class SemanticAuthorityReplayBundleFactory
{
    public static SemanticAuthorityReplayBundle Create(
        string documentId,
        string sourceType,
        string sourceHash,
        string sourceUniverseHash,
        IReadOnlyList<SemanticSourceAlias> aliasCatalog,
        string modelIdentity,
        string? modelRoute,
        string promptHash,
        string rawModelResponseHash,
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        string? goldId = null,
        string? goldHash = null,
        string? evaluatorIdentity = null,
        string? manifestHash = null,
        string? runId = null,
        string? commit = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUniverseHash);
        ArgumentNullException.ThrowIfNull(aliasCatalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawModelResponseHash);
        ArgumentNullException.ThrowIfNull(proposals);

        var aliases = aliasCatalog.ToArray();
        var frozenProposals = proposals.ToArray();
        var aliasHash = SemanticAuthorityReplayHashing.AliasCatalogHash(aliases);
        var proposalHash = SemanticAuthorityReplayHashing.ProposalHash(frozenProposals);
        var bundle = new SemanticAuthorityReplayBundle(
            SemanticAuthorityReplaySchema.Version,
            documentId,
            sourceType,
            sourceHash,
            sourceUniverseHash,
            aliases,
            aliasHash,
            modelIdentity,
            modelRoute,
            promptHash,
            SemanticAuthorityReplayHashing.SemanticContractHash(),
            rawModelResponseHash,
            frozenProposals,
            proposalHash,
            goldId,
            goldHash,
            evaluatorIdentity,
            manifestHash)
        {
            RunId = runId,
            Commit = commit,
            CreatedAt = createdAt,
        };
        return bundle with { BundleHash = SemanticAuthorityReplayHashing.BundleHash(bundle) };
    }
}

public static class SemanticAuthorityReplayHashing
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string AliasCatalogHash(IReadOnlyList<SemanticSourceAlias> aliases) =>
        HashValue(aliases);

    public static string ProposalHash(IReadOnlyList<CanonicalSemanticProposal> proposals) =>
        HashValue(proposals);

    /// <summary>Stable hash of raw model responses in transport/segment order.</summary>
    public static string RawModelResponseHash(IReadOnlyList<string> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        return HashValue(responses);
    }

    public static string SemanticContractHash() => HashValue(CanonicalSemanticContract.Schema());

    public static string BundleHash(SemanticAuthorityReplayBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var deterministicPayload = new
        {
            schemaVersion = bundle.SchemaVersion,
            documentId = bundle.DocumentId,
            sourceType = bundle.SourceType,
            sourceHash = bundle.SourceHash,
            sourceUniverseHash = bundle.SourceUniverseHash,
            aliasCatalog = bundle.AliasCatalog,
            aliasCatalogHash = bundle.AliasCatalogHash,
            modelIdentity = bundle.ModelIdentity,
            modelRoute = bundle.ModelRoute,
            promptHash = bundle.PromptHash,
            semanticContractHash = bundle.SemanticContractHash,
            rawModelResponseHash = bundle.RawModelResponseHash,
            proposals = bundle.Proposals,
            proposalHash = bundle.ProposalHash,
            goldId = bundle.GoldId,
            goldHash = bundle.GoldHash,
            evaluatorIdentity = bundle.EvaluatorIdentity,
            manifestHash = bundle.ManifestHash,
        };
        return HashValue(deterministicPayload);
    }

    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string HashValue<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(json)))).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind: {value.ValueKind}");
        }
    }
}

public sealed class SemanticAuthorityReplayRejectedException : InvalidOperationException
{
    public SemanticAuthorityReplayRejectedException(IReadOnlyList<string> errors)
        : base(string.Join(",", errors)) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

public sealed record SemanticAuthorityReplayResult(CanonicalSemanticPipelineResult Pipeline)
{
    /// <summary>Canonical materialization/projection after binding and global identity resolution.</summary>
    public IReadOnlyList<CanonicalSemanticGraphOccurrence> MaterializedProjection =>
        Pipeline.Graph.OutlineProjection;
}

/// <summary>
/// Replays only deterministic post-model work. It has no classifier, provider, prompt builder, or
/// source-document dependency; the alias catalog in the bundle is the complete source authority.
/// </summary>
public static class SemanticAuthorityReplay
{
    public static SemanticAuthorityReplayResult Replay(
        SemanticAuthorityReplayBundle bundle,
        string? expectedSourceHash = null,
        string? expectedSourceUniverseHash = null)
    {
        var errors = Validate(bundle, expectedSourceHash, expectedSourceUniverseHash);
        if (errors.Count > 0) throw new SemanticAuthorityReplayRejectedException(errors);

        var pipeline = CanonicalSemanticPipeline.RunAliases(
            bundle.AliasCatalog,
            bundle.Proposals,
            bundle.SourceHash,
            bundle.SourceHash);
        return new(pipeline);
    }

    public static IReadOnlyList<string> Validate(
        SemanticAuthorityReplayBundle bundle,
        string? expectedSourceHash = null,
        string? expectedSourceUniverseHash = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var errors = new List<string>();
        if (!string.Equals(bundle.SchemaVersion, SemanticAuthorityReplaySchema.Version, StringComparison.Ordinal))
            errors.Add("SCHEMA_VERSION_MISMATCH");
        if (expectedSourceHash is not null &&
            !string.Equals(bundle.SourceHash, expectedSourceHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("SOURCE_HASH_MISMATCH");
        if (expectedSourceUniverseHash is not null &&
            !string.Equals(bundle.SourceUniverseHash, expectedSourceUniverseHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("SOURCE_UNIVERSE_HASH_MISMATCH");
        if (!string.Equals(SemanticAuthorityReplayHashing.AliasCatalogHash(bundle.AliasCatalog), bundle.AliasCatalogHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("ALIAS_CATALOG_HASH_MISMATCH");
        if (!string.Equals(SemanticAuthorityReplayHashing.SemanticContractHash(), bundle.SemanticContractHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("SEMANTIC_CONTRACT_HASH_MISMATCH");
        if (!string.Equals(SemanticAuthorityReplayHashing.ProposalHash(bundle.Proposals), bundle.ProposalHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("PROPOSAL_HASH_MISMATCH");
        if (!string.Equals(SemanticAuthorityReplayHashing.BundleHash(bundle), bundle.BundleHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("BUNDLE_HASH_MISMATCH");
        return errors;
    }
}
