using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Version 3 moves semantic subjects to harness-owned positional decisions.</summary>
public sealed record V5SemanticDecisionRequestPacketV3(
    [property: JsonPropertyName("subjectEvidence")] IReadOnlyList<EvidenceNode> SubjectEvidence,
    [property: JsonPropertyName("contextOnlyEvidence")] IReadOnlyList<EvidenceNode> ContextOnlyEvidence,
    [property: JsonPropertyName("openOrConflictedClaims")] IReadOnlyList<BoundSemanticClaim> OpenOrConflictedClaims,
    [property: JsonPropertyName("retrievedEvidence")] IReadOnlyList<EvidenceCandidate> RetrievedEvidence,
    [property: JsonPropertyName("layoutEvidence")] IReadOnlyList<EvidenceNode> LayoutEvidence,
    [property: JsonPropertyName("visualEvidence")] IReadOnlyList<EvidenceNode> VisualEvidence);

public sealed record CanonicalSemanticDecisionRequestV3(
    [property: JsonPropertyName("composerVersion")] string ComposerVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("contract")] DocumentTaskContract Contract,
    [property: JsonPropertyName("claimShapes")] IReadOnlyList<V5ClaimShapeV2_1> ClaimShapes,
    [property: JsonPropertyName("responseSchema")] object ResponseSchema,
    [property: JsonPropertyName("responseBounds")] V5SemanticDecisionResponseBoundsV3 ResponseBounds,
    [property: JsonPropertyName("sourceSelectionPolicy")] object SourceSelectionPolicy,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("packet")] V5SemanticDecisionRequestPacketV3 Packet);

public sealed record V5ComposedSemanticDecisionRequestV3(
    string ComposerVersion,
    string Prompt,
    string PromptHash,
    string SchemaHash,
    string RequestHash,
    int Utf8Bytes,
    V5SemanticDecisionResponseBoundsV3 ResponseBounds);

/// <summary>
/// Finite v3 output contract. The item/string maxima are the smallest bounds supported by the
/// current task vocabulary and the frozen 31-pack historical response envelope; the aggregate
/// canonical UTF-8 byte ceiling is tied to the completion-token formula as a conservative guard.
/// </summary>
public sealed record V5SemanticDecisionResponseBoundsV3(
    int MaxDecisions,
    int MaxClaimsPerDecision,
    int MaxClaimsTotal,
    int MaxSubjectParts,
    int MaxTargetParts,
    int MaxEvidenceNeeds,
    int MaxValueUtf8Bytes,
    int MaxSelectionStringUtf8Bytes,
    int MaxExistingClaimIdUtf8Bytes,
    int MaxResponseUtf8Bytes)
{
    public const int HistoricalMaxClaimsPerSubject = 10;
    public const int HistoricalMaxClaimsPerResponse = 129;
    public const int HistoricalMaxSourceParts = 6;
    public const int HistoricalMaxValueUtf8Bytes = 543;
    public const int HistoricalMaxSelectionStringUtf8Bytes = 318;
    public const int DurableClaimIdUtf8Bytes = 42;
    public const int ProviderCompletionCeiling = 32768;
    public const int ResponseByteBase = 1536;
    public const int ResponseBytePerOwnedDecision = 496;

    public static V5SemanticDecisionResponseBoundsV3 ForOwnedCount(int ownedCount)
        => ForEvidenceCounts(ownedCount, ownedCount);

    public static V5SemanticDecisionResponseBoundsV3 ForEvidenceCounts(int ownedCount, int visibleCount)
    {
        if (ownedCount < 0) throw new ArgumentOutOfRangeException(nameof(ownedCount));
        if (visibleCount < ownedCount) throw new ArgumentOutOfRangeException(nameof(visibleCount));
        // Bytes and tokens are separate units. The earlier P5E model incorrectly made a byte
        // ceiling numerically no larger than max_tokens, which rejected two P5F 96-decision
        // completions that were within their provider token budget. 48 KiB is the smallest 1 KiB
        // aligned ceiling that contains the historical 48,705-byte response and P5F's largest
        // cardinality-correct v3 response (42,923 bytes). The provider's independent max_tokens
        // remains enforced by the carrier and is never inferred from a bytes/4 heuristic.
        var maxResponseBytes = checked(ResponseByteBase + ownedCount * ResponseBytePerOwnedDecision);
        return new V5SemanticDecisionResponseBoundsV3(
            ownedCount,
            HistoricalMaxClaimsPerSubject,
            Math.Min(HistoricalMaxClaimsPerResponse, checked(ownedCount * HistoricalMaxClaimsPerSubject)),
            Math.Min(HistoricalMaxSourceParts, Math.Max(1, ownedCount)),
            Math.Min(HistoricalMaxSourceParts, visibleCount),
            Enum.GetValues<EvidenceNeed>().Length,
            HistoricalMaxValueUtf8Bytes,
            HistoricalMaxSelectionStringUtf8Bytes,
            DurableClaimIdUtf8Bytes,
            maxResponseBytes);
    }
}

public static class V5SemanticDecisionComposerV3
{
    public const string Version = "v5-semantic-decision-composer-3.0";

    private static readonly string Instructions = string.Join("\n", [
        "You are a task-defined semantic reasoner.",
        "Return exactly one decision for every item in subjectEvidence, in the same order. decisions[i] belongs only to subjectEvidence[i].",
        "Every decision must be present. If its owned item has no claim under the task contract, return that decision with claims: [].",
        "A claim subject is implicit: it is the owned item for this decision. Never emit a subject alias, source id, coordinate, or subject index.",
        "For a subject that spans multiple atoms, use additionalSubjectParts with ownedIndex values only. List each additional part once in increasing source order, after the decision's primary owned item.",
        "For a strict substring of the primary or additional atom, provide verbatimText exactly as it appears. For a whole atom, omit verbatimText. Never retype a whole atom.",
        "A relation target uses targetParts with sourceGroup OWNED or CONTEXT_ONLY and the zero-based index within that request list. Targets may use contextOnlyEvidence when the task permits.",
        "For a multi-atom relation target, list parts in source order. Use verbatimText only for a strict substring of that exact atom.",
        "Use only declared predicates and relations. A UNARY predicate has a value and no targetParts; a RELATION has targetParts when RESOLVED and never has a value.",
        "Every claim must contain evidenceNeeds explicitly: RESOLVED sends []; OPEN and CONFLICTED send at least one need.",
        "A relation with an unknown target remains OPEN with evidenceNeeds including GLOBAL_TARGET.",
        "The harness owns claim identity. Do not emit claimId. existingClaimId may appear only for an explicitly supplied claim being refined.",
        "Return only the declared semantic decision schema.",
    ]);

    public static CanonicalSemanticDecisionRequestV3 BuildCanonical(
        DocumentTaskContract contract,
        V5SemanticDecisionRequestPacketV3 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        ValidatePacket(packet);
        var responseBounds = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(
            packet.SubjectEvidence.Count, packet.SubjectEvidence.Count + packet.ContextOnlyEvidence.Count);
        return new CanonicalSemanticDecisionRequestV3(
            Version,
            V5Protocol.ClaimSchemaVersionV3,
            contract,
            V5ClaimShapesV2_1.Generate(contract),
            V5SemanticDecisionContractV3.Schema(contract, packet.SubjectEvidence.Count, packet.ContextOnlyEvidence.Count, responseBounds),
            responseBounds,
            V5SourceSelectionPolicy.Generate(),
            string.Join("\n", Instructions,
                $"The complete response must serialize to at most {responseBounds.MaxResponseUtf8Bytes} UTF-8 bytes and contain at most {responseBounds.MaxClaimsTotal} claims total. Do not omit a supported claim just to meet a cap; an oversized complete response will be rejected. Never truncate, repair, or add commentary."),
            packet);
    }

    public static V5ComposedSemanticDecisionRequestV3 Compose(
        DocumentTaskContract contract,
        V5SemanticDecisionRequestPacketV3 packet) => Serialize(BuildCanonical(contract, packet));

    public static V5ComposedSemanticDecisionRequestV3 Serialize(CanonicalSemanticDecisionRequestV3 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = JsonSerializer.Serialize(request, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = false })
            .ReplaceLineEndings("\n");
        var schema = JsonSerializer.Serialize(request.ResponseSchema, CanonicalJson.Options);
        return new V5ComposedSemanticDecisionRequestV3(
            request.ComposerVersion,
            prompt,
            Hashing.Sha256(request.Instructions.ReplaceLineEndings("\n")),
            Hashing.Sha256(schema),
            Hashing.Sha256(prompt),
            Encoding.UTF8.GetByteCount(prompt),
            request.ResponseBounds);
    }

    private static void ValidatePacket(V5SemanticDecisionRequestPacketV3 packet)
    {
        var ownedAliases = packet.SubjectEvidence.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
        if (ownedAliases.Count != packet.SubjectEvidence.Count)
            throw new InvalidOperationException("decision-packet-duplicate-owned-alias");
        if (packet.ContextOnlyEvidence.Any(node => ownedAliases.Contains(node.SourceAlias)) ||
            packet.ContextOnlyEvidence.Select(node => node.SourceAlias).Distinct(StringComparer.Ordinal).Count() != packet.ContextOnlyEvidence.Count)
            throw new InvalidOperationException("decision-packet-owned-context-overlap");
        foreach (var node in packet.SubjectEvidence.Concat(packet.ContextOnlyEvidence)) node.Validate();
    }
}

public sealed record V5SemanticDecisionResponseV3(
    [property: JsonPropertyName("decisions")] IReadOnlyList<V5SemanticSubjectDecisionV3> Decisions);

public sealed record V5SemanticSubjectDecisionV3(
    [property: JsonPropertyName("claims")] IReadOnlyList<V5SemanticDecisionClaimV3> Claims);

/// <summary>
/// Subject identity is supplied by the enclosing positional decision. Extra subject atoms and
/// relation targets use request-local integer references and never model-authored aliases.
/// </summary>
public sealed record V5SemanticDecisionClaimV3(
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("subjectSelection")] V5DecisionTextSelectionV3? SubjectSelection = null,
    [property: JsonPropertyName("additionalSubjectParts")] IReadOnlyList<V5AdditionalOwnedSubjectPartV3>? AdditionalSubjectParts = null,
    [property: JsonPropertyName("targetParts")] IReadOnlyList<V5VisibleTargetPartV3>? TargetParts = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null,
    [property: JsonPropertyName("existingClaimId")] string? ExistingClaimId = null);

public sealed record V5DecisionTextSelectionV3(
    [property: JsonPropertyName("verbatimText")] string? VerbatimText = null,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null);

public sealed record V5AdditionalOwnedSubjectPartV3(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("selection")] V5DecisionTextSelectionV3? Selection = null);

public sealed record V5VisibleTargetPartV3(
    [property: JsonPropertyName("sourceGroup")] string SourceGroup,
    [property: JsonPropertyName("sourceIndex")] int SourceIndex,
    [property: JsonPropertyName("selection")] V5DecisionTextSelectionV3? Selection = null);

public sealed record V5DecisionBindingResultV3(ClaimBindingResultV2_1? Binding, IReadOnlyDictionary<string, string> ResponseRefusals)
{
    public IReadOnlyList<BoundSemanticClaimV2_1> Bound => Binding?.Bound ?? [];
    public IReadOnlyDictionary<string, string> Refusals => Binding is null ? ResponseRefusals : Merge(ResponseRefusals, Binding.Refusals);

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Concat(right).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

/// <summary>Dynamic response schema and deterministic mapping to the existing exact source binder.</summary>
public static class V5SemanticDecisionContractV3
{
    public const string SchemaVersion = "v5-source-backed-decision-3.0";

    public static object Schema(DocumentTaskContract contract, int ownedCount, int contextOnlyCount) =>
        Schema(contract, ownedCount, contextOnlyCount,
            V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(ownedCount, ownedCount + contextOnlyCount));

    public static object Schema(DocumentTaskContract contract, int ownedCount, int contextOnlyCount,
        V5SemanticDecisionResponseBoundsV3 bounds)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(bounds);
        if (ownedCount < 0 || contextOnlyCount < 0) throw new ArgumentOutOfRangeException(nameof(ownedCount));
        if (bounds.MaxDecisions != ownedCount) throw new InvalidOperationException("decision-bounds-cardinality-mismatch");
        var ownedIndices = Enumerable.Range(0, ownedCount).ToArray();
        var contextIndices = Enumerable.Range(0, contextOnlyCount).ToArray();
        object SelectionSchema() => new
        {
            type = "object", additionalProperties = false,
            properties = new
            {
                verbatimText = new { type = "string", minLength = 1, maxLength = bounds.MaxSelectionStringUtf8Bytes },
                occurrence = new { type = "integer", minimum = 1, maximum = int.MaxValue },
                leftExactContext = new { type = "string", maxLength = bounds.MaxSelectionStringUtf8Bytes },
                rightExactContext = new { type = "string", maxLength = bounds.MaxSelectionStringUtf8Bytes },
            },
        };
        object OwnedPartSchema() => new
        {
            type = "object", additionalProperties = false,
            properties = new { ownedIndex = new { type = "integer", @enum = ownedIndices }, selection = SelectionSchema() },
            required = new[] { "ownedIndex" },
        };
        object TargetPartSchema(string sourceGroup, int[] indices) => new
        {
            type = "object", additionalProperties = false,
            properties = new
            {
                sourceGroup = new { type = "string", @enum = new[] { sourceGroup } },
                sourceIndex = new { type = "integer", @enum = indices },
                selection = SelectionSchema(),
            },
            required = new[] { "sourceGroup", "sourceIndex" },
        };
        var targetVariants = new List<object>();
        if (ownedIndices.Length > 0) targetVariants.Add(TargetPartSchema("OWNED", ownedIndices));
        if (contextIndices.Length > 0) targetVariants.Add(TargetPartSchema("CONTEXT_ONLY", contextIndices));
        object ClaimSchema() => new
        {
            type = "object", additionalProperties = false,
            properties = new
            {
                predicate = new { type = "string", minLength = 1, @enum = contract.Predicates.Select(item => item.Name).Concat(contract.Relations.Select(item => item.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() },
                value = new { type = "string", maxLength = bounds.MaxValueUtf8Bytes },
                subjectSelection = SelectionSchema(),
                additionalSubjectParts = new { type = "array", maxItems = Math.Max(0, bounds.MaxSubjectParts - 1), items = OwnedPartSchema() },
                targetParts = new { type = "array", minItems = 1, maxItems = bounds.MaxTargetParts, items = new { oneOf = targetVariants } },
                state = new { type = "string", @enum = Enum.GetNames<ClaimResolutionState>().Where(state => state != nameof(ClaimResolutionState.EXHAUSTED)).ToArray() },
                evidenceNeeds = new { type = "array", maxItems = bounds.MaxEvidenceNeeds, items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
                existingClaimId = new { type = "string", minLength = 1, maxLength = bounds.MaxExistingClaimIdUtf8Bytes },
            },
            required = new[] { "predicate", "state", "evidenceNeeds" },
        };
        return new
        {
            type = "object", additionalProperties = false,
            maxSerializedUtf8Bytes = bounds.MaxResponseUtf8Bytes,
            maxClaimsTotal = bounds.MaxClaimsTotal,
            properties = new
            {
                decisions = new
                {
                    type = "array", minItems = ownedCount, maxItems = ownedCount,
                    items = new
                    {
                        type = "object", additionalProperties = false,
                        properties = new { claims = new { type = "array", maxItems = bounds.MaxClaimsPerDecision, items = ClaimSchema() } },
                        required = new[] { "claims" },
                    },
                },
            },
            required = new[] { "decisions" },
        };
    }

    public static V5SemanticDecisionResponseV3 Parse(JsonElement payload, DocumentTaskContract contract, int ownedCount, int contextOnlyCount)
    {
        var bounds = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(ownedCount, ownedCount + contextOnlyCount);
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > bounds.MaxResponseUtf8Bytes)
            throw new InvalidOperationException($"decision-response-byte-budget-exceeded:max={bounds.MaxResponseUtf8Bytes}");
        if (payload.ValueKind != JsonValueKind.Object || !HasOnly(payload, "decisions") || !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("decision-response-shape-invalid");
        if (decisions.GetArrayLength() != ownedCount)
            throw new InvalidOperationException($"decision-cardinality-invalid:expected={ownedCount}:actual={decisions.GetArrayLength()}");
        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter() },
        };
        V5SemanticDecisionResponseV3 response;
        try
        {
            response = payload.Deserialize<V5SemanticDecisionResponseV3>(options) ?? throw new InvalidOperationException("decision-response-empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("decision-response-schema-invalid", ex);
        }
        if (response.Decisions is null || response.Decisions.Count != ownedCount || response.Decisions.Any(decision => decision is null))
            throw new InvalidOperationException("decision-response-schema-invalid:null-or-missing-decision");
        ValidateBounds(response, contract, ownedCount, contextOnlyCount, bounds);
        var issueOrdinal = 0;
        foreach (var (decision, decisionIndex) in response.Decisions.Select((decision, index) => (decision, index)))
        {
            if (decision.Claims is null)
                throw new InvalidOperationException($"decision-response-schema-invalid:decision-{decisionIndex}:claims-missing");
            var priorOwned = decisionIndex;
            foreach (var claim in decision.Claims)
            {
                if (claim is null)
                    throw new InvalidOperationException($"decision-response-schema-invalid:decision-{decisionIndex}:claim-null");
                var key = claim.ExistingClaimId ?? $"decision-{decisionIndex}-claim-{issueOrdinal++}";
                foreach (var additional in claim.AdditionalSubjectParts ?? [])
                {
                    if (additional.OwnedIndex <= priorOwned || additional.OwnedIndex >= ownedCount)
                        throw new InvalidOperationException($"decision-response-schema-invalid:{key}:additional-owned-index-out-of-range-or-order");
                    priorOwned = additional.OwnedIndex;
                }
                foreach (var target in claim.TargetParts ?? [])
                {
                    if (target is null)
                        throw new InvalidOperationException($"decision-response-schema-invalid:{key}:target-part-null");
                    if ((target.SourceGroup == "OWNED" && (target.SourceIndex < 0 || target.SourceIndex >= ownedCount)) ||
                        (target.SourceGroup == "CONTEXT_ONLY" && (target.SourceIndex < 0 || target.SourceIndex >= contextOnlyCount)) ||
                        target.SourceGroup is not ("OWNED" or "CONTEXT_ONLY"))
                        throw new InvalidOperationException($"decision-response-schema-invalid:{key}:target-visible-index-out-of-range");
                }
                var subject = new ClaimSourceEndpointV2_1([new ProviderSourcePartV2_1("S0")]);
                var targetParts = (claim.TargetParts ?? []).Select((_, index) => new ProviderSourcePartV2_1($"T{index}")).ToArray();
                var proposal = new SemanticClaimProposalV2_1(subject, claim.Predicate, claim.Value,
                    targetParts.Length == 0 ? null : new ClaimSourceEndpointV2_1(targetParts), claim.State,
                    claim.EvidenceNeeds, claim.ExistingClaimId);
                var contractIssues = SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([proposal]), contract);
                if (contractIssues.Count > 0)
                    throw new InvalidOperationException($"decision-response-schema-invalid:{key}:{string.Join(",", contractIssues)}");
            }
        }
        return response;
    }

    /// <summary>
    /// The runtime repeats these checks even for typed reasoners, so bypassing JSON Schema or Parse
    /// cannot bypass the finite response contract. Any overflow rejects the whole response.
    /// </summary>
    public static void ValidateBounds(
        V5SemanticDecisionResponseV3 response,
        DocumentTaskContract contract,
        int ownedCount,
        int contextOnlyCount,
        V5SemanticDecisionResponseBoundsV3? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(contract);
        bounds ??= V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(ownedCount, ownedCount + contextOnlyCount);
        if (bounds.MaxDecisions != ownedCount || response.Decisions is null || response.Decisions.Count != ownedCount)
            throw new InvalidOperationException("decision-cardinality-invalid");
        if (response.Decisions.Any(decision => decision is null))
            throw new InvalidOperationException("decision-response-schema-invalid:null-decision");

        static int Utf8Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
        void CheckSelection(V5DecisionTextSelectionV3? selection, string key)
        {
            if (selection is null) return;
            if (Utf8Bytes(selection.VerbatimText) > bounds.MaxSelectionStringUtf8Bytes ||
                Utf8Bytes(selection.LeftExactContext) > bounds.MaxSelectionStringUtf8Bytes ||
                Utf8Bytes(selection.RightExactContext) > bounds.MaxSelectionStringUtf8Bytes)
                throw new InvalidOperationException($"decision-response-string-bound-exceeded:{key}:selection");
            if (selection.Occurrence is < 1)
                throw new InvalidOperationException($"decision-response-integer-bound-exceeded:{key}:occurrence");
        }

        var totalClaims = 0;
        for (var decisionIndex = 0; decisionIndex < response.Decisions.Count; decisionIndex++)
        {
            var decision = response.Decisions[decisionIndex];
            if (decision.Claims is null || decision.Claims.Count > bounds.MaxClaimsPerDecision)
                throw new InvalidOperationException($"decision-response-cardinality-bound-exceeded:decision-{decisionIndex}:claims");
            totalClaims = checked(totalClaims + decision.Claims.Count);
            for (var claimIndex = 0; claimIndex < decision.Claims.Count; claimIndex++)
            {
                var claim = decision.Claims[claimIndex];
                var key = $"decision-{decisionIndex}-claim-{claimIndex}";
                if (claim is null) throw new InvalidOperationException($"decision-response-schema-invalid:{key}:null");
                if (Utf8Bytes(claim.Value) > bounds.MaxValueUtf8Bytes)
                    throw new InvalidOperationException($"decision-response-string-bound-exceeded:{key}:value");
                if (Utf8Bytes(claim.ExistingClaimId) > bounds.MaxExistingClaimIdUtf8Bytes)
                    throw new InvalidOperationException($"decision-response-string-bound-exceeded:{key}:existingClaimId");
                if (claim.EvidenceNeeds is null || claim.EvidenceNeeds.Count > bounds.MaxEvidenceNeeds ||
                    claim.EvidenceNeeds.Distinct().Count() != claim.EvidenceNeeds.Count)
                    throw new InvalidOperationException($"decision-response-cardinality-bound-exceeded:{key}:evidenceNeeds");
                if (claim.AdditionalSubjectParts is { } subjectParts && subjectParts.Count > bounds.MaxSubjectParts - 1)
                    throw new InvalidOperationException($"decision-response-cardinality-bound-exceeded:{key}:additionalSubjectParts");
                if (claim.AdditionalSubjectParts?.Any(part => part is null) == true)
                    throw new InvalidOperationException($"decision-response-schema-invalid:{key}:null-additional-part");
                if (claim.TargetParts is { } targetParts && targetParts.Count > bounds.MaxTargetParts)
                    throw new InvalidOperationException($"decision-response-cardinality-bound-exceeded:{key}:targetParts");
                if (claim.TargetParts?.Any(part => part is null) == true)
                    throw new InvalidOperationException($"decision-response-schema-invalid:{key}:null-target-part");
                CheckSelection(claim.SubjectSelection, $"{key}:subject");
                foreach (var part in claim.AdditionalSubjectParts ?? [])
                    CheckSelection(part?.Selection, $"{key}:additional");
                foreach (var part in claim.TargetParts ?? [])
                    CheckSelection(part?.Selection, $"{key}:target");
            }
        }
        if (totalClaims > bounds.MaxClaimsTotal)
            throw new InvalidOperationException($"decision-response-cardinality-bound-exceeded:total-claims:max={bounds.MaxClaimsTotal}:actual={totalClaims}");

        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(response, CanonicalJson.Options).Length;
        if (canonicalBytes > bounds.MaxResponseUtf8Bytes)
            throw new InvalidOperationException($"decision-response-byte-budget-exceeded:max={bounds.MaxResponseUtf8Bytes}:actual={canonicalBytes}");
    }

    public static V5DecisionBindingResultV3 Bind(
        string requestId,
        V5SemanticDecisionResponseV3 response,
        DocumentTaskContract contract,
        IReadOnlyList<EvidenceNode> ownedEvidence,
        IReadOnlyList<EvidenceNode> contextOnlyEvidence,
        IReadOnlyList<SemanticSourceAtom> atoms,
        ClaimBindingScope scope)
        => Bind(requestId, response, contract, ownedEvidence, contextOnlyEvidence, atoms, scope, originalOwnedOrdinals: null);

    /// <summary>
    /// Binds a sharded decision response with original-pack ownership ordinals. Local decision
    /// indexes remain wire-only: harness claim identity uses the stable original ordinal and the
    /// claim's bounded ordinal within that original subject.
    /// </summary>
    public static V5DecisionBindingResultV3 Bind(
        string requestId,
        V5SemanticDecisionResponseV3 response,
        DocumentTaskContract contract,
        IReadOnlyList<EvidenceNode> ownedEvidence,
        IReadOnlyList<EvidenceNode> contextOnlyEvidence,
        IReadOnlyList<SemanticSourceAtom> atoms,
        ClaimBindingScope scope,
        IReadOnlyList<int>? originalOwnedOrdinals)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (originalOwnedOrdinals is not null &&
            (originalOwnedOrdinals.Count != ownedEvidence.Count || originalOwnedOrdinals.Any(ordinal => ordinal < 0) ||
             originalOwnedOrdinals.Distinct().Count() != originalOwnedOrdinals.Count))
            throw new InvalidOperationException("decision-original-owned-ordinal-map-invalid");
        try
        {
            ValidateBounds(response, contract, ownedEvidence.Count, contextOnlyEvidence.Count);
        }
        catch (InvalidOperationException ex)
        {
            return new(null, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["response-bounds"] = ex.Message,
            });
        }
        var atomsByAlias = atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var visible = ownedEvidence.Concat(contextOnlyEvidence).ToArray();
        var proposals = new List<IndexedSemanticClaimProposalV2_1>();
        var responseRefusals = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordinal = 0;
        for (var decisionIndex = 0; decisionIndex < response.Decisions.Count; decisionIndex++)
        {
            var decision = response.Decisions[decisionIndex];
            var primary = ownedEvidence[decisionIndex];
            for (var claimIndex = 0; claimIndex < decision.Claims.Count; claimIndex++)
            {
                var claim = decision.Claims[claimIndex];
                var key = claim.ExistingClaimId ?? $"proposal-{ordinal + 1}";
                try
                {
                    var subjectParts = new List<ProviderSourcePartV2_1> { Part(primary.SourceAlias, claim.SubjectSelection, atomsByAlias) };
                    var previousOwnedIndex = decisionIndex;
                    foreach (var additional in claim.AdditionalSubjectParts ?? [])
                    {
                        if (additional.OwnedIndex <= previousOwnedIndex || additional.OwnedIndex >= ownedEvidence.Count)
                            throw new InvalidOperationException("additional-owned-index-out-of-range-or-order");
                        previousOwnedIndex = additional.OwnedIndex;
                        subjectParts.Add(Part(ownedEvidence[additional.OwnedIndex].SourceAlias, additional.Selection, atomsByAlias));
                    }
                    ClaimSourceEndpointV2_1? target = null;
                    if (claim.TargetParts is { Count: > 0 })
                    {
                        var targetParts = new List<ProviderSourcePartV2_1>();
                        foreach (var targetPart in claim.TargetParts)
                        {
                            var source = targetPart.SourceGroup switch
                            {
                                "OWNED" when targetPart.SourceIndex >= 0 && targetPart.SourceIndex < ownedEvidence.Count => ownedEvidence[targetPart.SourceIndex],
                                "CONTEXT_ONLY" when targetPart.SourceIndex >= 0 && targetPart.SourceIndex < contextOnlyEvidence.Count => contextOnlyEvidence[targetPart.SourceIndex],
                                "OWNED" or "CONTEXT_ONLY" => throw new InvalidOperationException("target-visible-index-out-of-range"),
                                _ => throw new InvalidOperationException("target-source-group-invalid"),
                            };
                            targetParts.Add(Part(source.SourceAlias, targetPart.Selection, atomsByAlias));
                        }
                        target = new ClaimSourceEndpointV2_1(targetParts);
                    }
                    var proposal = new SemanticClaimProposalV2_1(new ClaimSourceEndpointV2_1(subjectParts), claim.Predicate,
                        claim.Value, target, claim.State, claim.EvidenceNeeds, claim.ExistingClaimId);
                    var contractIssues = SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([proposal]), contract);
                    if (contractIssues.Count > 0) throw new InvalidOperationException(string.Join(",", contractIssues));
                    // Ensure every model reference resolves through this request's identity tables.
                    if (subjectParts.Any(part => !atomsByAlias.ContainsKey(part.SourceAlias)))
                        throw new InvalidOperationException("subject-alias-not-in-request");
                    if (target?.SourceParts.Any(part => !scope.VisibleAliases.Contains(part.SourceAlias)) == true)
                        throw new InvalidOperationException("target-alias-not-visible");
                    var identityOrdinal = originalOwnedOrdinals is null
                        ? ordinal
                        : checked(originalOwnedOrdinals[decisionIndex] * V5SemanticDecisionResponseBoundsV3.HistoricalMaxClaimsPerSubject + claimIndex);
                    proposals.Add(new IndexedSemanticClaimProposalV2_1(identityOrdinal, proposal));
                }
                catch (InvalidOperationException ex)
                {
                    responseRefusals[key] = ex.Message;
                }
                ordinal++;
            }
        }
        var exact = ExactClaimBinderV2_1.Bind(requestId, proposals, atoms, scope);
        return new V5DecisionBindingResultV3(exact, responseRefusals);
    }

    private static ProviderSourcePartV2_1 Part(string alias, V5DecisionTextSelectionV3? selection,
        IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        if (selection?.VerbatimText is { } quoted && atoms.TryGetValue(alias, out var atom) && string.Equals(quoted, atom.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("whole-atom-must-omit-verbatim-text");
        return new ProviderSourcePartV2_1(alias, selection?.VerbatimText, selection?.Occurrence,
            selection?.LeftExactContext, selection?.RightExactContext);
    }

    private static bool HasOnly(JsonElement element, string allowed) =>
        element.EnumerateObject().All(property => property.NameEquals(allowed));
}
