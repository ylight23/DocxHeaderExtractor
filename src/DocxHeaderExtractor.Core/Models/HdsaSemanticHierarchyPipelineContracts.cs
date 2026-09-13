using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public sealed record HdsaSemanticNodeCatalogEntry(
    [property: JsonPropertyName("semanticNodeId")] string SemanticNodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("sourceOrder")] int SourceOrder);

/// <summary>
/// Immutable production catalog. It is constructed only from a Gold-free predicted resolver
/// result; parent reasoning receives this catalog but cannot change it.
/// </summary>
public sealed class HdsaFrozenSemanticNodeCatalog
{
    internal HdsaFrozenSemanticNodeCatalog(
        string sourceSha256,
        string preprocessingSnapshotHash,
        string resolverVersion,
        IReadOnlyList<HdsaSemanticNodeCatalogEntry> entries,
        IReadOnlyList<HdsaSemanticIdentityRelation> acceptedIdentityRelations,
        string catalogFingerprint)
    {
        SourceSha256 = sourceSha256;
        PreprocessingSnapshotHash = preprocessingSnapshotHash;
        ResolverVersion = resolverVersion;
        Entries = entries;
        AcceptedIdentityRelations = acceptedIdentityRelations;
        CatalogFingerprint = catalogFingerprint;
    }

    public string SourceSha256 { get; }
    public string PreprocessingSnapshotHash { get; }
    public string ResolverVersion { get; }
    public IReadOnlyList<HdsaSemanticNodeCatalogEntry> Entries { get; }
    public IReadOnlyList<HdsaSemanticIdentityRelation> AcceptedIdentityRelations { get; }
    public string CatalogFingerprint { get; }
    public bool GoldUsed => false;
    public bool ProductionBenchmark => true;

    public IReadOnlySet<string> SemanticNodeIds =>
        Entries.Select(item => item.SemanticNodeId).ToHashSet(StringComparer.Ordinal);
}

public static class HdsaSemanticNodeCatalogBuilder
{
    public static HdsaFrozenSemanticNodeCatalog Build(
        HdsaSemanticNodeResolutionInput input,
        HdsaSemanticNodeResolutionV3Result result)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        if (input.GoldUsed || result.GoldUsed)
            throw new InvalidOperationException("GOLD_FIREWALL: cannot build a production semantic-node catalog from Gold-derived input.");
        if (!string.Equals(input.SourceSha256, result.SourceSha256, StringComparison.Ordinal) ||
            !string.Equals(input.PreprocessingSnapshotHash, result.PreprocessingSnapshotHash, StringComparison.Ordinal))
            throw new InvalidDataException("CATALOG_SOURCE_SNAPSHOT_MISMATCH");
        if (result.Errors.Count > 0)
            throw new InvalidDataException("CATALOG_RELATION_INPUT_INVALID:" + string.Join(',', result.Errors));

        var occurrenceById = input.Occurrences.ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
        var nodeByOccurrence = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = result.Predictions.Select(prediction =>
        {
            var members = prediction.MemberOccurrenceIds.ToArray();
            if (members.Length == 0) throw new InvalidDataException("CATALOG_EMPTY_NODE_MEMBERS");
            foreach (var occurrenceId in members)
            {
                if (!occurrenceById.ContainsKey(occurrenceId)) throw new InvalidDataException("CATALOG_UNKNOWN_MEMBER:" + occurrenceId);
                if (!nodeByOccurrence.TryAdd(occurrenceId, prediction.PredictedSemanticNodeId))
                    throw new InvalidDataException("CATALOG_OCCURRENCE_IN_MULTIPLE_NODES:" + occurrenceId);
            }
            return new HdsaSemanticNodeCatalogEntry(
                prediction.PredictedSemanticNodeId,
                Array.AsReadOnly(members),
                prediction.CanonicalText,
                members.Min(member => occurrenceById[member].DocumentOrder));
        }).OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal).ToArray();

        var nodeIds = entries.Select(item => item.SemanticNodeId).ToHashSet(StringComparer.Ordinal);
        if (nodeIds.Count != entries.Length) throw new InvalidDataException("CATALOG_DUPLICATE_NODE_ID");
        if (nodeByOccurrence.Count != input.Occurrences.Count)
            throw new InvalidDataException("CATALOG_OCCURRENCE_COVERAGE_MISMATCH");
        var relations = result.AcceptedRelations
            .OrderBy(item => item.Relation)
            .ThenBy(item => item.FromOccurrenceId, StringComparer.Ordinal)
            .ThenBy(item => item.ToOccurrenceId, StringComparer.Ordinal)
            .ToArray();
        foreach (var relation in relations)
        {
            if (!nodeByOccurrence.ContainsKey(relation.FromOccurrenceId) || !nodeByOccurrence.ContainsKey(relation.ToOccurrenceId))
                throw new InvalidDataException("CATALOG_RELATION_MEMBER_UNKNOWN");
        }

        var fingerprintPayload = new
        {
            sourceSha256 = input.SourceSha256,
            preprocessingSnapshotHash = input.PreprocessingSnapshotHash,
            resolverVersion = result.ResolverVersion,
            entries = entries.Select(item => new
            {
                semanticNodeId = item.SemanticNodeId,
                memberOccurrenceIds = item.MemberOccurrenceIds,
                canonicalText = item.CanonicalText,
                sourceOrder = item.SourceOrder,
            }),
            acceptedIdentityRelations = relations,
            goldUsed = false,
            productionBenchmark = true,
        };
        var serialized = JsonSerializer.Serialize(fingerprintPayload, new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
        return new HdsaFrozenSemanticNodeCatalog(
            input.SourceSha256,
            input.PreprocessingSnapshotHash,
            result.ResolverVersion,
            new ReadOnlyCollection<HdsaSemanticNodeCatalogEntry>(entries),
            new ReadOnlyCollection<HdsaSemanticIdentityRelation>(relations),
            fingerprint);
    }
}

public sealed record HdsaSemanticParentUniverseEntry(
    [property: JsonPropertyName("semanticNodeId")] string SemanticNodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("sourceOrder")] int SourceOrder);

public sealed record HdsaSemanticNodeParentReasoningRequest(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("childSemanticNodeId")] string ChildSemanticNodeId,
    [property: JsonPropertyName("candidateParentSemanticNodeIds")] IReadOnlyList<string> CandidateParentSemanticNodeIds,
    [property: JsonPropertyName("authoritativeParentUniverse")] IReadOnlyList<HdsaSemanticParentUniverseEntry> AuthoritativeParentUniverse,
    [property: JsonPropertyName("evidence")] HdsaRelationEvidence Evidence,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

public sealed record HdsaSemanticNodeParentDecision(
    [property: JsonPropertyName("catalogFingerprint")] string CatalogFingerprint,
    [property: JsonPropertyName("childSemanticNodeId")] string ChildSemanticNodeId,
    [property: JsonPropertyName("decision")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaParentDecision Decision,
    [property: JsonPropertyName("parentSemanticNodeId")] string? ParentSemanticNodeId = null);

public sealed record HdsaSemanticNodeParentDecisionValidation(
    bool Accepted,
    string? RejectionReason,
    bool ParentWasOutsideAttentionHints);

public static class HdsaSemanticNodeParentReasoningContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            catalogFingerprint = new { type = "string", minLength = 1 },
            childSemanticNodeId = new { type = "string", minLength = 1 },
            decision = new { type = "string", @enum = new[] { "SELECT_PARENT", "ROOT", "UNRESOLVED" } },
            parentSemanticNodeId = new { type = new[] { "string", "null" } },
        },
        required = new[] { "catalogFingerprint", "childSemanticNodeId", "decision", "parentSemanticNodeId" },
    };

    public static HdsaSemanticNodeParentDecision Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("HDSA_SEMANTIC_PARENT_RESPONSE_NOT_OBJECT");
        var allowed = new HashSet<string>(["catalogFingerprint", "childSemanticNodeId", "decision", "parentSemanticNodeId"], StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new FormatException("HDSA_SEMANTIC_PARENT_RESPONSE_EXTRA_PROPERTY");
        var fingerprint = RequiredString(root, "catalogFingerprint");
        var child = RequiredString(root, "childSemanticNodeId");
        var decision = RequiredString(root, "decision") switch
        {
            "SELECT_PARENT" => HdsaParentDecision.SelectParent,
            "ROOT" => HdsaParentDecision.Root,
            "UNRESOLVED" => HdsaParentDecision.Unresolved,
            _ => throw new FormatException("HDSA_SEMANTIC_PARENT_RESPONSE_DECISION_INVALID"),
        };
        if (!root.TryGetProperty("parentSemanticNodeId", out var parent) ||
            (parent.ValueKind != JsonValueKind.Null && parent.ValueKind != JsonValueKind.String))
            throw new FormatException("HDSA_SEMANTIC_PARENT_RESPONSE_PARENT_INVALID");
        return new(fingerprint, child, decision, parent.ValueKind == JsonValueKind.Null ? null : parent.GetString());
    }

    public static HdsaSemanticNodeParentReasoningRequest CreateRequest(
        HdsaFrozenSemanticNodeCatalog catalog,
        string childSemanticNodeId,
        int window = 8)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(childSemanticNodeId)) throw new ArgumentException("Child node ID is required.", nameof(childSemanticNodeId));
        if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
        var ordered = catalog.Entries.OrderBy(item => item.SourceOrder).ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal).ToArray();
        var childIndex = Array.FindIndex(ordered, item => string.Equals(item.SemanticNodeId, childSemanticNodeId, StringComparison.Ordinal));
        if (childIndex < 0) throw new InvalidDataException("UNKNOWN_CHILD_SEMANTIC_NODE");
        var universe = ordered.Take(childIndex)
            .Select(item => new HdsaSemanticParentUniverseEntry(
                item.SemanticNodeId,
                item.MemberOccurrenceIds,
                item.CanonicalText,
                item.SourceOrder))
            .ToArray();
        var candidates = universe.TakeLast(window).Reverse().Select(item => item.SemanticNodeId).ToArray();
        var child = ordered[childIndex];
        return new(
            catalog.CatalogFingerprint,
            childSemanticNodeId,
            candidates,
            universe,
            new(
                "semantic-node-order=" + child.SourceOrder,
                null,
                null,
                null,
                child.CanonicalText,
                "catalog=" + catalog.CatalogFingerprint),
            false);
    }

    public static HdsaSemanticNodeParentDecisionValidation Validate(
        HdsaSemanticNodeParentReasoningRequest request,
        HdsaSemanticNodeParentDecision decision,
        HdsaFrozenSemanticNodeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(catalog);
        if (request.GoldDerivedInput || !string.Equals(request.CatalogFingerprint, catalog.CatalogFingerprint, StringComparison.Ordinal))
            return new(false, "CATALOG_FINGERPRINT_MISMATCH_OR_GOLD_INPUT", false);
        if (!string.Equals(decision.CatalogFingerprint, catalog.CatalogFingerprint, StringComparison.Ordinal))
            return new(false, "DECISION_CATALOG_FINGERPRINT_MISMATCH", false);
        if (!string.Equals(decision.ChildSemanticNodeId, request.ChildSemanticNodeId, StringComparison.Ordinal))
            return new(false, "CHILD_MISMATCH", false);
        if (!catalog.SemanticNodeIds.Contains(decision.ChildSemanticNodeId))
            return new(false, "UNKNOWN_CHILD_SEMANTIC_NODE", false);

        if (decision.Decision is HdsaParentDecision.Root or HdsaParentDecision.Unresolved)
            return decision.ParentSemanticNodeId is null
                ? new(true, null, false)
                : new(false, "PARENT_NOT_ALLOWED_FOR_DECISION", false);
        if (decision.Decision != HdsaParentDecision.SelectParent)
            return new(false, "UNSUPPORTED_DECISION", false);
        if (string.IsNullOrWhiteSpace(decision.ParentSemanticNodeId))
            return new(false, "PARENT_REQUIRED", false);
        if (string.Equals(decision.ChildSemanticNodeId, decision.ParentSemanticNodeId, StringComparison.Ordinal))
            return new(false, "SELF_PARENT", false);
        if (!catalog.SemanticNodeIds.Contains(decision.ParentSemanticNodeId))
            return new(false, "UNKNOWN_PARENT_SEMANTIC_NODE", false);
        if (!request.AuthoritativeParentUniverse.Any(item => string.Equals(item.SemanticNodeId, decision.ParentSemanticNodeId, StringComparison.Ordinal)))
            return new(false, "PARENT_OUTSIDE_AUTHORITATIVE_UNIVERSE", false);
        var outsideAttention = !request.CandidateParentSemanticNodeIds.Contains(decision.ParentSemanticNodeId, StringComparer.Ordinal);
        return outsideAttention
            ? new(false, "PARENT_OUTSIDE_ATTENTION_CANDIDATES", true)
            : new(true, null, false);
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"HDSA_SEMANTIC_PARENT_RESPONSE_{name.ToUpperInvariant()}_MISSING");
}

public sealed record HdsaSemanticHierarchyRunResult(
    HdsaFrozenSemanticNodeCatalog Catalog,
    IReadOnlyList<HdsaSemanticNodeParentReasoningRequest> Requests,
    IReadOnlyList<HdsaSemanticNodeParentDecision> Decisions,
    IReadOnlyList<HdsaSemanticNodeParentDecisionValidation> DecisionValidations,
    HdsaGraphValidationResult GraphValidation,
    HdsaTree Tree,
    string CatalogFingerprintBeforeParentReasoning,
    string CatalogFingerprintAfterParentReasoning,
    bool GoldReadBeforePrediction,
    bool LegacyHierarchyConsumed,
    bool LevelDerivedFromTreeDepth,
    int ModelCalls,
    int ProviderCalls)
{
    public bool CatalogWasMutated => !string.Equals(
        CatalogFingerprintBeforeParentReasoning,
        CatalogFingerprintAfterParentReasoning,
        StringComparison.Ordinal);
}

/// <summary>
/// Offline production-shaped orchestration. The supplied delegate is a deterministic fake
/// reasoner in tests; no provider client is hidden inside this plumbing task.
/// </summary>
public static class HdsaSemanticHierarchyPipeline
{
    public static HdsaSemanticHierarchyRunResult Run(
        HdsaFrozenSemanticNodeCatalog catalog,
        Func<HdsaSemanticNodeParentReasoningRequest, HdsaSemanticNodeParentDecision> reasoner)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reasoner);
        var fingerprintBefore = catalog.CatalogFingerprint;
        var requests = catalog.Entries
            .OrderBy(item => item.SourceOrder)
            .ThenBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(item => HdsaSemanticNodeParentReasoningContract.CreateRequest(catalog, item.SemanticNodeId))
            .ToArray();
        var decisions = new List<HdsaSemanticNodeParentDecision>(requests.Length);
        var validations = new List<HdsaSemanticNodeParentDecisionValidation>(requests.Length);
        var relationProposals = new List<HdsaRelationProposal>();
        foreach (var request in requests)
        {
            var decision = reasoner(request);
            decisions.Add(decision);
            var validation = HdsaSemanticNodeParentReasoningContract.Validate(request, decision, catalog);
            validations.Add(validation);
            if (validation.Accepted && decision.Decision == HdsaParentDecision.SelectParent)
                relationProposals.Add(new(decision.ParentSemanticNodeId!, decision.ChildSemanticNodeId, HdsaRelationType.ParentOf));
        }

        var nodeIds = catalog.Entries.Select(item => item.SemanticNodeId).ToArray();
        var normalized = HdsaRelationNormalizer.Normalize(nodeIds, relationProposals);
        var graph = HdsaGraphValidator.Validate(nodeIds, normalized);
        var tree = HdsaTreeConstructor.Build(nodeIds, graph);
        var fingerprintAfter = catalog.CatalogFingerprint;
        return new(
            catalog,
            new ReadOnlyCollection<HdsaSemanticNodeParentReasoningRequest>(requests),
            new ReadOnlyCollection<HdsaSemanticNodeParentDecision>(decisions),
            new ReadOnlyCollection<HdsaSemanticNodeParentDecisionValidation>(validations),
            graph,
            tree,
            fingerprintBefore,
            fingerprintAfter,
            false,
            false,
            true,
            0,
            0);
    }
}
