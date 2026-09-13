using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public enum HdsaSemanticIdentityInferenceDecision
{
    [JsonStringEnumMemberName("SAME_SEMANTIC_REPEAT")]
    SameSemanticRepeat,

    [JsonStringEnumMemberName("CONTINUATION_OF")]
    ContinuationOf,

    [JsonStringEnumMemberName("DISTINCT")]
    Distinct,

    [JsonStringEnumMemberName("UNRESOLVED")]
    Unresolved,
}

public enum HdsaSemanticIdentityResolutionMode
{
    HistoricalV4,
    ConservativePromotion,
}

/// <summary>One parser-owned pair from the exhaustive within-source identity universe.</summary>
public sealed record HdsaSemanticIdentityPair(
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("left")] HdsaSemanticNodeSourceOccurrence Left,
    [property: JsonPropertyName("right")] HdsaSemanticNodeSourceOccurrence Right,
    [property: JsonPropertyName("leftNormalizedText")] string LeftNormalizedText,
    [property: JsonPropertyName("rightNormalizedText")] string RightNormalizedText);

/// <summary>
/// Gold-free, identity-only request. It intentionally contains no parent, level, or Gold field.
/// Pair discovery is exhaustive within the supplied source snapshot; ranking can be added later,
/// but this contract does not silently turn cheap evidence into a recall gate.
/// </summary>
public sealed record HdsaSemanticIdentityInferenceRequest(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("pair")] HdsaSemanticIdentityPair Pair,
    [property: JsonPropertyName("goldDerivedInput")] bool GoldDerivedInput = false);

/// <summary>Strict model-facing response. The model chooses a relation; it never merges nodes.</summary>
public sealed record HdsaSemanticIdentityInferenceResponse(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("pairId")] string PairId,
    [property: JsonPropertyName("leftOccurrenceId")] string LeftOccurrenceId,
    [property: JsonPropertyName("rightOccurrenceId")] string RightOccurrenceId,
    [property: JsonPropertyName("decision")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaSemanticIdentityInferenceDecision Decision);

/// <summary>Provider-independent observation wrapper used to preserve inference provenance.</summary>
public sealed record HdsaSemanticIdentityInferenceObservation(
    [property: JsonPropertyName("request")] HdsaSemanticIdentityInferenceRequest Request,
    [property: JsonPropertyName("response")] HdsaSemanticIdentityInferenceResponse Response,
    [property: JsonPropertyName("requestHash")] string RequestHash,
    [property: JsonPropertyName("responseHash")] string ResponseHash,
    [property: JsonPropertyName("inferenceSource")] string InferenceSource,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed = false,
    [property: JsonPropertyName("legacyUsed")] bool LegacyUsed = false,
    [property: JsonPropertyName("parserEvidenceHash")] string? ParserEvidenceHash = null,
    [property: JsonPropertyName("structuralBoundaryCompatible")] bool StructuralBoundaryCompatible = false);

/// <summary>All decision provenance, including DISTINCT and UNRESOLVED outcomes.</summary>
public sealed record HdsaSemanticIdentityInferenceRelationRecord(
    [property: JsonPropertyName("relationId")] string RelationId,
    [property: JsonPropertyName("relationType")]
    [property: JsonConverter(typeof(JsonStringEnumConverter))] HdsaSemanticIdentityInferenceDecision RelationType,
    [property: JsonPropertyName("leftOccurrenceId")] string LeftOccurrenceId,
    [property: JsonPropertyName("rightOccurrenceId")] string RightOccurrenceId,
    [property: JsonPropertyName("sourceSnapshotIdentity")] string SourceSnapshotIdentity,
    [property: JsonPropertyName("inputEvidenceHash")] string InputEvidenceHash,
    [property: JsonPropertyName("requestHash")] string RequestHash,
    [property: JsonPropertyName("responseHash")] string ResponseHash,
    [property: JsonPropertyName("inferenceContractVersion")] string InferenceContractVersion,
    [property: JsonPropertyName("inferenceSource")] string InferenceSource,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("acceptedByValidator")] bool AcceptedByValidator,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed);

public sealed record HdsaSemanticIdentityInferenceValidation(
    bool Accepted,
    string? RejectionReason);

public sealed record HdsaSemanticNodeResolutionV4Result(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("predictions")] IReadOnlyList<HdsaSemanticNodePrediction> Predictions,
    [property: JsonPropertyName("acceptedRelations")] IReadOnlyList<HdsaSemanticIdentityRelation> AcceptedRelations,
    [property: JsonPropertyName("relationRecords")] IReadOnlyList<HdsaSemanticIdentityInferenceRelationRecord> RelationRecords,
    [property: JsonPropertyName("rejectedRelations")] IReadOnlyList<HdsaSemanticIdentityInferenceRelationRecord> RejectedRelations,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("resolverVersion")] string ResolverVersion,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Semantic identity inference v4. The resolver validates explicit model/parser decisions and
/// constructs components deterministically. It never infers a relation from text, adjacency,
/// style, layout, or transitive closure, and UNRESOLVED always remains split.
/// </summary>
public static class HdsaSemanticNodeResolverV4
{
    public const string Version = "semantic-identity-inference-v4";

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            sourceSha256 = new { type = "string", minLength = 1 },
            preprocessingSnapshotHash = new { type = "string", minLength = 1 },
            pairId = new { type = "string", minLength = 1 },
            leftOccurrenceId = new { type = "string", minLength = 1 },
            rightOccurrenceId = new { type = "string", minLength = 1 },
            decision = new { type = "string", @enum = new[]
            {
                "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "DISTINCT", "UNRESOLVED",
            } },
        },
        required = new[]
        {
            "sourceSha256", "preprocessingSnapshotHash", "pairId",
            "leftOccurrenceId", "rightOccurrenceId", "decision",
        },
    };

    public static IReadOnlyList<HdsaSemanticIdentityPair> DiscoverPairs(HdsaSemanticNodeResolutionInput input)
    {
        ValidateInput(input);
        var ordered = OrderOccurrences(input.Occurrences);
        var pairs = new List<HdsaSemanticIdentityPair>(checked(ordered.Length * Math.Max(0, ordered.Length - 1) / 2));
        for (var leftIndex = 0; leftIndex < ordered.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < ordered.Length; rightIndex++)
            {
                var left = ordered[leftIndex];
                var right = ordered[rightIndex];
                var pairId = StablePairId(input, left.OccurrenceId, right.OccurrenceId);
                pairs.Add(new(pairId, left, right, NormalizeText(left.Text), NormalizeText(right.Text)));
            }
        }

        return new ReadOnlyCollection<HdsaSemanticIdentityPair>(pairs);
    }

    public static HdsaSemanticIdentityInferenceRequest CreateRequest(
        HdsaSemanticNodeResolutionInput input,
        string leftOccurrenceId,
        string rightOccurrenceId)
    {
        var pair = DiscoverPairs(input).SingleOrDefault(item =>
            string.Equals(item.Left.OccurrenceId, leftOccurrenceId, StringComparison.Ordinal) &&
            string.Equals(item.Right.OccurrenceId, rightOccurrenceId, StringComparison.Ordinal));
        return pair is null
            ? throw new InvalidDataException("UNKNOWN_OR_NONCANONICAL_IDENTITY_PAIR")
            : new(input.SourceSha256, input.PreprocessingSnapshotHash, pair, false);
    }

    public static string SerializeRequest(HdsaSemanticIdentityInferenceRequest request) =>
        JsonSerializer.Serialize(request, HashOptions);

    public static string RequestHash(HdsaSemanticIdentityInferenceRequest request) =>
        Sha256(Encoding.UTF8.GetBytes(SerializeRequest(request)));

    public static HdsaSemanticIdentityInferenceResponse Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("HDSA_IDENTITY_V4_RESPONSE_NOT_OBJECT");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "sourceSha256", "preprocessingSnapshotHash", "pairId",
            "leftOccurrenceId", "rightOccurrenceId", "decision",
        };
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new FormatException("HDSA_IDENTITY_V4_RESPONSE_EXTRA_PROPERTY");

        var decisionText = RequiredString(root, "decision");
        var decision = decisionText switch
        {
            "SAME_SEMANTIC_REPEAT" => HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat,
            "CONTINUATION_OF" => HdsaSemanticIdentityInferenceDecision.ContinuationOf,
            "DISTINCT" => HdsaSemanticIdentityInferenceDecision.Distinct,
            "UNRESOLVED" => HdsaSemanticIdentityInferenceDecision.Unresolved,
            _ => throw new FormatException("HDSA_IDENTITY_V4_RESPONSE_DECISION_INVALID"),
        };
        return new(
            RequiredString(root, "sourceSha256"),
            RequiredString(root, "preprocessingSnapshotHash"),
            RequiredString(root, "pairId"),
            RequiredString(root, "leftOccurrenceId"),
            RequiredString(root, "rightOccurrenceId"),
            decision);
    }

    public static HdsaSemanticIdentityInferenceValidation Validate(
        HdsaSemanticIdentityInferenceRequest request,
        HdsaSemanticIdentityInferenceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        var response = observation.Response;
        if (request.GoldDerivedInput || observation.GoldUsed || observation.LegacyUsed)
            return Reject("GOLD_OR_LEGACY_PROVENANCE");
        if (!string.Equals(SerializeRequest(observation.Request), SerializeRequest(request), StringComparison.Ordinal))
            return Reject("REQUEST_SNAPSHOT_MISMATCH");
        if (!string.Equals(request.SourceSha256, response.SourceSha256, StringComparison.Ordinal) ||
            !string.Equals(request.PreprocessingSnapshotHash, response.PreprocessingSnapshotHash, StringComparison.Ordinal))
            return Reject("SOURCE_SNAPSHOT_MISMATCH");
        if (!string.Equals(request.Pair.PairId, response.PairId, StringComparison.Ordinal) ||
            !string.Equals(request.Pair.Left.OccurrenceId, response.LeftOccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(request.Pair.Right.OccurrenceId, response.RightOccurrenceId, StringComparison.Ordinal))
            return Reject("PAIR_IDENTITY_MISMATCH");
        if (!string.Equals(observation.RequestHash, RequestHash(request), StringComparison.Ordinal))
            return Reject("STALE_REQUEST_HASH");
        if (string.IsNullOrWhiteSpace(observation.ResponseHash))
            return Reject("MISSING_RESPONSE_HASH");
        if (!string.Equals(observation.InferenceSource, "MODEL", StringComparison.Ordinal) &&
            !string.Equals(observation.InferenceSource, "PARSER", StringComparison.Ordinal))
            return Reject("INFERENCE_SOURCE_INVALID");
        if (string.Equals(observation.InferenceSource, "MODEL", StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(observation.Model) || string.IsNullOrWhiteSpace(observation.Provider)))
            return Reject("MODEL_PROVIDER_PROVENANCE_MISSING");
        return new(true, null);
    }

    public static HdsaSemanticNodeResolutionV4Result Resolve(
        HdsaSemanticNodeResolutionInput input,
        IEnumerable<HdsaSemanticIdentityInferenceObservation> observations)
        => Resolve(input, observations, HdsaSemanticIdentityResolutionMode.HistoricalV4);

    public static HdsaSemanticNodeResolutionV4Result Resolve(
        HdsaSemanticNodeResolutionInput input,
        IEnumerable<HdsaSemanticIdentityInferenceObservation> observations,
        HdsaSemanticIdentityResolutionMode resolutionMode)
    {
        ValidateInput(input);
        ArgumentNullException.ThrowIfNull(observations);
        var pairRequests = DiscoverPairs(input).ToDictionary(item => item.PairId, item =>
            new HdsaSemanticIdentityInferenceRequest(input.SourceSha256, input.PreprocessingSnapshotHash, item, false), StringComparer.Ordinal);
        var raw = observations.ToArray();
        var records = new List<HdsaSemanticIdentityInferenceRelationRecord>();
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var validByPair = new Dictionary<string, (HdsaSemanticIdentityInferenceObservation Observation, HdsaSemanticIdentityInferenceRequest Request)>();

        foreach (var observation in raw)
        {
            if (observation is null)
            {
                errors.Add("MALFORMED_RESPONSE");
                continue;
            }

            var response = observation.Response;
            var request = pairRequests.TryGetValue(response.PairId, out var knownRequest)
                ? knownRequest
                : observation.Request;
            var validation = pairRequests.ContainsKey(response.PairId)
                ? Validate(request, observation)
                : Reject("UNKNOWN_PAIR");
            var pair = pairRequests.TryGetValue(response.PairId, out var knownPair)
                ? knownPair.Pair
                : observation.Request.Pair;
            var record = CreateRecord(input, pair, response.Decision, observation, validation);
            records.Add(record);
            if (!validation.Accepted)
            {
                errors.Add(validation.RejectionReason!);
                continue;
            }

            if (!validByPair.TryAdd(response.PairId, (observation, request)))
            {
                errors.Add("CONFLICTING_RELATIONS");
                validByPair.Remove(response.PairId);
            }
        }

        var acceptedCandidates = validByPair.Values
            .Select(item => (item.Observation, item.Observation.Response, Record: records.Single(record =>
                string.Equals(record.RequestHash, item.Observation.RequestHash, StringComparison.Ordinal) &&
                string.Equals(record.ResponseHash, item.Observation.ResponseHash, StringComparison.Ordinal))))
            .Where(item => item.Record.AcceptedByValidator)
            .ToArray();

        var acceptedIdentity = new List<HdsaSemanticIdentityRelation>();
        foreach (var candidate in acceptedCandidates)
        {
            if (candidate.Response.Decision is HdsaSemanticIdentityInferenceDecision.Distinct or HdsaSemanticIdentityInferenceDecision.Unresolved)
                continue;
            var promotion = HdsaSemanticIdentityPromotionPolicy.Evaluate(
                candidate.Response.Decision,
                candidate.Record.InferenceSource,
                candidate.Observation.ParserEvidenceHash,
                candidate.Observation.StructuralBoundaryCompatible,
                candidate.Observation.GoldUsed,
                candidate.Observation.LegacyUsed);
            if (resolutionMode == HdsaSemanticIdentityResolutionMode.ConservativePromotion && !promotion.CollapseAuthorized)
                continue;
            var relation = candidate.Response.Decision == HdsaSemanticIdentityInferenceDecision.SameSemanticRepeat
                ? new HdsaSemanticIdentityRelation(
                    candidate.Response.LeftOccurrenceId,
                    candidate.Response.RightOccurrenceId,
                    HdsaSemanticIdentityRelationType.SameSemanticRepeat,
                    resolutionMode == HdsaSemanticIdentityResolutionMode.ConservativePromotion
                        ? candidate.Observation.ParserEvidenceHash!
                        : candidate.Record.InputEvidenceHash,
                    resolutionMode == HdsaSemanticIdentityResolutionMode.ConservativePromotion
                        ? candidate.Observation.StructuralBoundaryCompatible
                        : true)
                : new HdsaSemanticIdentityRelation(
                    candidate.Response.RightOccurrenceId,
                    candidate.Response.LeftOccurrenceId,
                    HdsaSemanticIdentityRelationType.ContinuationOf,
                    resolutionMode == HdsaSemanticIdentityResolutionMode.ConservativePromotion
                        ? candidate.Observation.ParserEvidenceHash!
                        : candidate.Record.InputEvidenceHash,
                    resolutionMode == HdsaSemanticIdentityResolutionMode.ConservativePromotion
                        ? candidate.Observation.StructuralBoundaryCompatible
                        : true);
            acceptedIdentity.Add(relation);
        }

        RejectIdentityConflicts(acceptedIdentity, records, errors);
        RejectContinuationCycles(acceptedIdentity, records, errors);

        // Apply accepted relations through the merge-isolation contract. This is deliberately a
        // local projection: unrelated component IDs and their source order are never rebuilt just
        // because one semantic component is collapsed.
        var occurrenceById = input.Occurrences.ToDictionary(item => item.OccurrenceId, StringComparer.Ordinal);
        var isolatedComponents = input.Occurrences
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .Select(item => new HdsaSemanticCatalogComponent(
                HdsaSemanticMergeIsolation.StableMergedNodeId([item.OccurrenceId]),
                [item.OccurrenceId], item.Text, item.DocumentOrder))
            .ToList();
        var isolatedRelations = new List<HdsaSemanticIdentityRelation>();
        foreach (var relation in acceptedIdentity)
        {
            var left = isolatedComponents.Single(component => component.MemberOccurrenceIds.Contains(relation.FromOccurrenceId, StringComparer.Ordinal));
            var right = isolatedComponents.Single(component => component.MemberOccurrenceIds.Contains(relation.ToOccurrenceId, StringComparer.Ordinal));
            if (string.Equals(left.SemanticNodeId, right.SemanticNodeId, StringComparison.Ordinal))
            {
                isolatedRelations.Add(relation);
                continue;
            }

            var mergedNodeId = HdsaSemanticMergeIsolation.StableMergedNodeId(
                left.MemberOccurrenceIds.Concat(right.MemberOccurrenceIds));
            var projection = HdsaSemanticMergeIsolation.Apply(isolatedComponents,
                new HdsaSemanticMergeOperation(mergedNodeId, [left.SemanticNodeId, right.SemanticNodeId], "MODEL_ACCEPTED"));
            if (!projection.IsValid)
            {
                errors.UnionWith(projection.Errors.Select(error => "MERGE_ISOLATION_" + error));
                continue;
            }
            isolatedComponents = projection.Components.ToList();
            isolatedRelations.Add(relation);
        }

        acceptedIdentity = isolatedRelations;
        var predictions = isolatedComponents
            .OrderBy(component => component.SourceOrder)
            .ThenBy(component => component.SemanticNodeId, StringComparer.Ordinal)
            .Select(component =>
            {
                var members = component.MemberOccurrenceIds.ToArray();
                var canonicalOccurrence = members
                    .Select(member => occurrenceById[member])
                    .OrderBy(item => item.DocumentOrder)
                    .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
                    .First();
                var relationNames = acceptedIdentity
                    .Where(item => members.Contains(item.FromOccurrenceId, StringComparer.Ordinal) && members.Contains(item.ToOccurrenceId, StringComparer.Ordinal))
                    .Select(item => item.Relation == HdsaSemanticIdentityRelationType.SameSemanticRepeat ? "SAME_SEMANTIC_REPEAT" : "CONTINUATION_OF")
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);
                var evidence = relationNames.Any()
                    ? "EXPLICIT_RELATIONS:" + string.Join(',', relationNames)
                    : "IDENTITY_ONLY_NO_RELATION";
                return new HdsaSemanticNodePrediction(component.SemanticNodeId, members, canonicalOccurrence.Text, evidence, Version, false);
            })
            .ToArray();

        return new(
            input.SourceSha256,
            input.PreprocessingSnapshotHash,
            predictions,
            acceptedIdentity.OrderBy(item => item.Relation).ThenBy(item => item.FromOccurrenceId, StringComparer.Ordinal).ThenBy(item => item.ToOccurrenceId, StringComparer.Ordinal).ToArray(),
            records.OrderBy(item => item.RelationId, StringComparer.Ordinal).ToArray(),
            records.Where(item => !item.AcceptedByValidator).OrderBy(item => item.RelationId, StringComparer.Ordinal).ToArray(),
            errors.Order(StringComparer.Ordinal).ToArray(),
            Version,
            false);
    }

    public static string InputEvidenceHash(HdsaSemanticIdentityPair pair) =>
        Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pair, HashOptions)));

    private static HdsaSemanticIdentityInferenceRelationRecord CreateRecord(
        HdsaSemanticNodeResolutionInput input,
        HdsaSemanticIdentityPair pair,
        HdsaSemanticIdentityInferenceDecision decision,
        HdsaSemanticIdentityInferenceObservation observation,
        HdsaSemanticIdentityInferenceValidation validation)
    {
        var relationKey = string.Join('\u001f', input.SourceSha256, input.PreprocessingSnapshotHash,
            pair.PairId, decision.ToString(), Version);
        return new(
            "IR-" + Sha256(Encoding.UTF8.GetBytes(relationKey))[..16],
            decision,
            pair.Left.OccurrenceId,
            pair.Right.OccurrenceId,
            input.SourceSha256 + ":" + input.PreprocessingSnapshotHash,
            InputEvidenceHash(pair),
            observation.RequestHash,
            observation.ResponseHash,
            Version,
            observation.InferenceSource,
            observation.Model,
            observation.Provider,
            validation.Accepted,
            validation.RejectionReason,
            false);
    }

    private static void RejectIdentityConflicts(
        ICollection<HdsaSemanticIdentityRelation> accepted,
        ICollection<HdsaSemanticIdentityInferenceRelationRecord> records,
        ISet<string> errors)
    {
        var conflicts = accepted
            .GroupBy(item => string.Join('\u001f', item.FromOccurrenceId, item.ToOccurrenceId), StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Relation).Distinct().Count() > 1)
            .ToArray();
        foreach (var conflict in conflicts)
        {
            foreach (var relation in conflict.ToArray())
                accepted.Remove(relation);
            errors.Add("CONFLICTING_RELATIONS");
        }

        var continuationParents = accepted
            .Where(item => item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf)
            .GroupBy(item => item.FromOccurrenceId, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.ToOccurrenceId).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToArray();
        foreach (var group in continuationParents)
        {
            foreach (var relation in group.ToArray())
                accepted.Remove(relation);
            errors.Add("MULTIPLE_INCOMPATIBLE_CONTINUATION_PARENTS");
        }
    }

    private static void RejectContinuationCycles(
        ICollection<HdsaSemanticIdentityRelation> accepted,
        ICollection<HdsaSemanticIdentityInferenceRelationRecord> records,
        ISet<string> errors)
    {
        var edges = accepted
            .Where(item => item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf)
            .GroupBy(item => item.FromOccurrenceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ToOccurrenceId).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cycleNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in edges.Keys)
            DetectCycle(node, edges, visiting, visited, cycleNodes);
        if (cycleNodes.Count == 0) return;
        foreach (var relation in accepted.Where(item =>
                     item.Relation == HdsaSemanticIdentityRelationType.ContinuationOf &&
                     cycleNodes.Contains(item.FromOccurrenceId) && cycleNodes.Contains(item.ToOccurrenceId)).ToArray())
            accepted.Remove(relation);
        errors.Add("CONTINUATION_CYCLE");
    }

    private static bool DetectCycle(
        string node,
        IReadOnlyDictionary<string, string[]> edges,
        ISet<string> visiting,
        ISet<string> visited,
        ISet<string> cycleNodes)
    {
        if (visiting.Contains(node))
        {
            cycleNodes.Add(node);
            return true;
        }
        if (!visited.Add(node)) return false;
        visiting.Add(node);
        var found = false;
        if (edges.TryGetValue(node, out var targets))
            foreach (var target in targets)
                if (DetectCycle(target, edges, visiting, visited, cycleNodes))
                {
                    cycleNodes.Add(node);
                    found = true;
                }
        visiting.Remove(node);
        return found;
    }

    private static HdsaSemanticIdentityInferenceValidation Reject(string reason) => new(false, reason);

    private static void ValidateInput(HdsaSemanticNodeResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceSha256)) throw new ArgumentException("Source hash is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PreprocessingSnapshotHash)) throw new ArgumentException("Preprocessing snapshot hash is required.", nameof(input));
        if (input.GoldUsed) throw new InvalidOperationException("GOLD_FIREWALL: identity v4 input is marked as Gold-derived.");
        ArgumentNullException.ThrowIfNull(input.Occurrences);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in input.Occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId)) throw new InvalidDataException("EMPTY_OCCURRENCE_ID");
            if (!ids.Add(occurrence.OccurrenceId)) throw new InvalidDataException("DUPLICATE_OCCURRENCE_ID:" + occurrence.OccurrenceId);
            if (occurrence.DocumentOrder < 0) throw new InvalidDataException("INVALID_DOCUMENT_ORDER:" + occurrence.OccurrenceId);
            if (occurrence.Text is null) throw new InvalidDataException("NULL_OCCURRENCE_TEXT:" + occurrence.OccurrenceId);
        }
    }

    private static HdsaSemanticNodeSourceOccurrence[] OrderOccurrences(IReadOnlyList<HdsaSemanticNodeSourceOccurrence> occurrences) =>
        occurrences.OrderBy(item => item.DocumentOrder).ThenBy(item => item.OccurrenceId, StringComparer.Ordinal).ToArray();

    private static string StablePairId(HdsaSemanticNodeResolutionInput input, string left, string right) =>
        "IP-" + Sha256(Encoding.UTF8.GetBytes(string.Join('\u001f', input.SourceSha256, input.PreprocessingSnapshotHash, left, right)))[..16];

    private static string NormalizeText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"HDSA_IDENTITY_V4_RESPONSE_{name.ToUpperInvariant()}_MISSING");

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

}
