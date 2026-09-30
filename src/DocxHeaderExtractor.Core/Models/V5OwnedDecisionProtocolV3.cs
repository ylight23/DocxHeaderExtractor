using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Provider-facing V3 prototype in which every model decision is positionally owned by exactly one
/// subjectEvidence item. The model never echoes a subject alias. contextOnlyEvidence can only be
/// referenced from relation objects, so a halo occurrence cannot become a claim subject.
/// This protocol is provider-free until a caller explicitly chooses to transport it.
/// </summary>
public static class V5OwnedDecisionProtocolV3
{
    public const string ProtocolVersion = "v5-owned-subject-decision-3.0";
    public const string ComposerVersion = "v5-owned-decision-request-composer-3.0";
}

public static class V5OwnedDecisionKindsV3
{
    public const string None = "NONE";
    public const string Claims = "CLAIMS";
    public const string Consumed = "CONSUMED";

    public static IReadOnlyList<string> All { get; } = [None, Claims, Consumed];
}

public static class V5EvidenceReferenceScopesV3
{
    public const string Owned = "OWNED";
    public const string Context = "CONTEXT";

    public static IReadOnlyList<string> All { get; } = [Owned, Context];
}

/// <summary>
/// A later owned atom that participates in the same source occurrence as the positional anchor.
/// The index addresses subjectEvidence only; context/halo has no subject-reference representation.
/// </summary>
public sealed record V5OwnedAdditionalSubjectPartV3(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText = null);

/// <summary>
/// Relation-object source part. Objects may point to owned evidence or context-only evidence, but
/// the namespace is explicit and index-based. No model-facing sourceAlias exists.
/// </summary>
public sealed record V5VisibleSourcePartRefV3(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText = null);

public sealed record V5OwnedDecisionClaimV3(
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("objectParts")] IReadOnlyList<V5VisibleSourcePartRefV3>? ObjectParts = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null,
    [property: JsonPropertyName("existingClaimId")] string? ExistingClaimId = null);

/// <summary>
/// decisions[i] is, by definition, the decision for packet.subjectEvidence[i].
/// CLAIMS may select a strict substring of the anchor and may extend through later owned atoms.
/// NONE is an explicit semantic negative. CONSUMED marks an owned slot already included by an
/// earlier multi-atom CLAIMS decision.
/// </summary>
public sealed record V5OwnedSubjectDecisionV3(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("claims")] IReadOnlyList<V5OwnedDecisionClaimV3>? Claims,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText = null,
    [property: JsonPropertyName("additionalSubjectParts")] IReadOnlyList<V5OwnedAdditionalSubjectPartV3>? AdditionalSubjectParts = null,
    [property: JsonPropertyName("consumedBySubjectIndex")] int? ConsumedBySubjectIndex = null);

public sealed record V5OwnedDecisionResponseV3(
    [property: JsonPropertyName("decisions")] IReadOnlyList<V5OwnedSubjectDecisionV3> Decisions);

public sealed record CanonicalOwnedDecisionRequestV3(
    [property: JsonPropertyName("composerVersion")] string ComposerVersion,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("contract")] DocumentTaskContract Contract,
    [property: JsonPropertyName("responseSchema")] object ResponseSchema,
    [property: JsonPropertyName("sourceSelectionPolicy")] object SourceSelectionPolicy,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("packet")] V5EvidencePacketV2_1 Packet);

/// <summary>
/// Packet-local schema and strict validator for V3. The schema has exactly one decision slot per
/// owned subject and contains no subject sourceAlias field. Runtime validation repeats the same
/// invariants because json_object transports do not prove server-side JSON Schema enforcement.
/// </summary>
public static class V5OwnedDecisionContractV3
{
    private static readonly IReadOnlySet<string> RootFields = new HashSet<string>(["decisions"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> DecisionFields = new HashSet<string>(
        ["kind", "claims", "verbatimText", "additionalSubjectParts", "consumedBySubjectIndex"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ClaimFields = new HashSet<string>(
        ["predicate", "value", "objectParts", "state", "evidenceNeeds", "existingClaimId"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> AdditionalPartFields = new HashSet<string>(
        ["ownedIndex", "verbatimText"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ObjectPartFields = new HashSet<string>(
        ["scope", "index", "verbatimText"], StringComparer.Ordinal);

    public static object Schema(DocumentTaskContract contract, int ownedCount, int contextCount)
    {
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        if (ownedCount < 0 || contextCount < 0) throw new ArgumentOutOfRangeException(nameof(ownedCount));

        var predicates = contract.Predicates.Select(item => item.Name)
            .Concat(contract.Relations.Select(item => item.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        object IndexSchema(int count) => new
        {
            type = "integer",
            minimum = 0,
            maximum = Math.Max(0, count - 1),
        };

        object AdditionalPartSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                ownedIndex = IndexSchema(ownedCount),
                verbatimText = new { type = "string", minLength = 1 },
            },
            required = new[] { "ownedIndex" },
        };

        var objectAlternatives = new List<object>();
        if (ownedCount > 0)
        {
            objectAlternatives.Add(new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    scope = new { type = "string", @const = V5EvidenceReferenceScopesV3.Owned },
                    index = IndexSchema(ownedCount),
                    verbatimText = new { type = "string", minLength = 1 },
                },
                required = new[] { "scope", "index" },
            });
        }
        if (contextCount > 0)
        {
            objectAlternatives.Add(new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    scope = new { type = "string", @const = V5EvidenceReferenceScopesV3.Context },
                    index = IndexSchema(contextCount),
                    verbatimText = new { type = "string", minLength = 1 },
                },
                required = new[] { "scope", "index" },
            });
        }

        var claimSchema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                predicate = new { type = "string", @enum = predicates },
                value = new { type = "string" },
                objectParts = new
                {
                    type = "array",
                    minItems = 1,
                    items = new { oneOf = objectAlternatives.ToArray() },
                },
                state = new { type = "string", @enum = SemanticClaimContractV2_1.ProviderFacingStates },
                evidenceNeeds = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
                existingClaimId = new { type = "string", minLength = 1 },
            },
            required = new[] { "predicate", "state", "evidenceNeeds" },
        };

        var decisionSchema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                kind = new { type = "string", @enum = V5OwnedDecisionKindsV3.All },
                claims = new { type = "array", items = claimSchema },
                verbatimText = new { type = "string", minLength = 1 },
                additionalSubjectParts = new
                {
                    type = "array",
                    items = AdditionalPartSchema(),
                },
                consumedBySubjectIndex = IndexSchema(ownedCount),
            },
            required = new[] { "kind", "claims" },
        };

        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                decisions = new
                {
                    type = "array",
                    minItems = ownedCount,
                    maxItems = ownedCount,
                    items = decisionSchema,
                },
            },
            required = new[] { "decisions" },
        };
    }

    public static string SchemaHash(DocumentTaskContract contract, int ownedCount, int contextCount) =>
        Hashing.Sha256(JsonSerializer.Serialize(Schema(contract, ownedCount, contextCount), CanonicalJson.Options));

    public static IReadOnlyList<string> Validate(
        V5OwnedDecisionResponseV3 response,
        DocumentTaskContract contract,
        V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();

        var issues = new List<string>();
        var owned = packet.SubjectEvidence ?? [];
        var context = packet.ContextOnlyEvidence ?? [];
        var decisions = response.Decisions ?? [];

        var ownedAliases = owned.Select(item => item.SourceAlias).ToArray();
        if (ownedAliases.Distinct(StringComparer.Ordinal).Count() != ownedAliases.Length)
            issues.Add("owned-alias-not-unique");
        if (context.Select(item => item.SourceAlias).Intersect(ownedAliases, StringComparer.Ordinal).Any())
            issues.Add("owned-context-alias-overlap");

        if (decisions.Count != owned.Count)
            issues.Add($"decision-count-mismatch:{decisions.Count}:{owned.Count}");

        var predicateNames = contract.Predicates.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var relations = contract.Relations.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var consumedBy = new Dictionary<int, int>();

        var count = Math.Min(decisions.Count, owned.Count);
        for (var i = 0; i < count; i++)
        {
            var decision = decisions[i];
            var claims = decision.Claims ?? [];
            var additional = decision.AdditionalSubjectParts ?? [];

            if (!V5OwnedDecisionKindsV3.All.Contains(decision.Kind, StringComparer.Ordinal))
            {
                issues.Add($"decision-kind-invalid:{i}:{decision.Kind}");
                continue;
            }

            if (decision.Kind == V5OwnedDecisionKindsV3.None)
            {
                if (claims.Count != 0) issues.Add($"none-decision-has-claims:{i}");
                if (decision.ConsumedBySubjectIndex is not null) issues.Add($"none-decision-has-consumer:{i}");
                if (decision.VerbatimText is not null || additional.Count != 0) issues.Add($"none-decision-has-selection:{i}");
                continue;
            }

            if (decision.Kind == V5OwnedDecisionKindsV3.Consumed)
            {
                if (claims.Count != 0) issues.Add($"consumed-decision-has-claims:{i}");
                if (decision.VerbatimText is not null || additional.Count != 0) issues.Add($"consumed-decision-has-selection:{i}");
                if (decision.ConsumedBySubjectIndex is null || decision.ConsumedBySubjectIndex < 0 ||
                    decision.ConsumedBySubjectIndex >= i)
                    issues.Add($"consumed-by-invalid:{i}");
                continue;
            }

            if (claims.Count == 0) issues.Add($"claims-decision-empty:{i}");
            if (decision.ConsumedBySubjectIndex is not null) issues.Add($"claims-decision-has-consumer:{i}");
            ValidateStrictSubstring(decision.VerbatimText, owned[i].Text, $"subject:{i}", issues);

            var previous = i;
            var seenAdditional = new HashSet<int>();
            foreach (var part in additional)
            {
                if (part.OwnedIndex <= i || part.OwnedIndex >= owned.Count)
                {
                    issues.Add($"additional-subject-index-out-of-range:{i}:{part.OwnedIndex}");
                    continue;
                }
                if (!seenAdditional.Add(part.OwnedIndex))
                    issues.Add($"additional-subject-index-duplicate:{i}:{part.OwnedIndex}");
                if (part.OwnedIndex <= previous)
                    issues.Add($"additional-subject-order-invalid:{i}:{part.OwnedIndex}");
                previous = part.OwnedIndex;
                ValidateStrictSubstring(part.VerbatimText, owned[part.OwnedIndex].Text,
                    $"subject:{i}:additional:{part.OwnedIndex}", issues);

                if (consumedBy.TryGetValue(part.OwnedIndex, out var existing) && existing != i)
                    issues.Add($"owned-subject-consumed-by-multiple-anchors:{part.OwnedIndex}:{existing}:{i}");
                else
                    consumedBy[part.OwnedIndex] = i;
            }

            foreach (var claim in claims)
                ValidateClaim(i, claim, predicateNames, relations, owned, context, issues);
        }

        foreach (var (index, anchor) in consumedBy)
        {
            if (index >= decisions.Count) continue;
            var decision = decisions[index];
            if (decision.Kind != V5OwnedDecisionKindsV3.Consumed ||
                decision.ConsumedBySubjectIndex != anchor)
                issues.Add($"consumed-counterpart-missing:{index}:{anchor}");
        }

        for (var i = 0; i < count; i++)
        {
            var decision = decisions[i];
            if (decision.Kind != V5OwnedDecisionKindsV3.Consumed) continue;
            if (!consumedBy.TryGetValue(i, out var anchor) || anchor != decision.ConsumedBySubjectIndex)
                issues.Add($"orphan-consumed-decision:{i}");
        }

        return issues;
    }

    private static void ValidateClaim(
        int subjectIndex,
        V5OwnedDecisionClaimV3 claim,
        IReadOnlySet<string> predicates,
        IReadOnlyDictionary<string, SemanticRelationDefinition> relations,
        IReadOnlyList<EvidenceNode> owned,
        IReadOnlyList<EvidenceNode> context,
        List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(claim.Predicate))
        {
            issues.Add($"claim-predicate-missing:{subjectIndex}");
            return;
        }
        if (!predicates.Contains(claim.Predicate) && !relations.ContainsKey(claim.Predicate))
            issues.Add($"predicate-not-in-contract:{subjectIndex}:{claim.Predicate}");
        if (claim.State == ClaimResolutionState.EXHAUSTED)
            issues.Add($"model-may-not-originate-exhausted-state:{subjectIndex}");

        if (claim.EvidenceNeeds is null)
            issues.Add($"evidence-needs-missing:{subjectIndex}:{claim.Predicate}");
        else if (claim.State == ClaimResolutionState.RESOLVED && claim.EvidenceNeeds.Count > 0)
            issues.Add($"resolved-claim-must-not-carry-evidence-needs:{subjectIndex}:{claim.Predicate}");
        else if (claim.State is ClaimResolutionState.OPEN or ClaimResolutionState.CONFLICTED && claim.EvidenceNeeds.Count == 0)
            issues.Add($"{claim.State.ToString().ToLowerInvariant()}-claim-without-evidence-need:{subjectIndex}:{claim.Predicate}");

        var isRelation = relations.ContainsKey(claim.Predicate);
        var objectParts = claim.ObjectParts ?? [];
        if (isRelation && objectParts.Count == 0)
        {
            var explainsMissingObject =
                (claim.State == ClaimResolutionState.OPEN && (claim.EvidenceNeeds?.Contains(EvidenceNeed.GLOBAL_TARGET) ?? false)) ||
                (claim.State == ClaimResolutionState.CONFLICTED && claim.EvidenceNeeds is { Count: > 0 });
            if (!explainsMissingObject)
                issues.Add($"relation-missing-object:{subjectIndex}:{claim.Predicate}");
        }
        if (isRelation && claim.Value is not null)
            issues.Add($"relation-has-value:{subjectIndex}:{claim.Predicate}");
        if (!isRelation && objectParts.Count > 0)
            issues.Add($"unary-claim-has-object:{subjectIndex}:{claim.Predicate}");

        foreach (var part in objectParts)
        {
            EvidenceNode? node = null;
            if (part.Scope == V5EvidenceReferenceScopesV3.Owned)
            {
                if (part.Index < 0 || part.Index >= owned.Count)
                    issues.Add($"object-owned-index-out-of-range:{subjectIndex}:{part.Index}");
                else
                    node = owned[part.Index];
            }
            else if (part.Scope == V5EvidenceReferenceScopesV3.Context)
            {
                if (part.Index < 0 || part.Index >= context.Count)
                    issues.Add($"object-context-index-out-of-range:{subjectIndex}:{part.Index}");
                else
                    node = context[part.Index];
            }
            else
            {
                issues.Add($"object-scope-invalid:{subjectIndex}:{part.Scope}");
            }

            if (node is not null)
                ValidateStrictSubstring(part.VerbatimText, node.Text,
                    $"object:{subjectIndex}:{part.Scope}:{part.Index}", issues);
        }
    }

    private static void ValidateStrictSubstring(string? verbatimText, string sourceText, string label, List<string> issues)
    {
        if (verbatimText is null) return;
        if (verbatimText.Length == 0)
        {
            issues.Add($"verbatim-empty:{label}");
            return;
        }
        if (!sourceText.Contains(verbatimText, StringComparison.Ordinal))
        {
            issues.Add($"verbatim-not-exact:{label}");
            return;
        }
        if (verbatimText.Length == sourceText.Length)
            issues.Add($"whole-atom-verbatim-forbidden:{label}");
    }

    internal static IReadOnlySet<string> AllowedRootFields => RootFields;
    internal static IReadOnlySet<string> AllowedDecisionFields => DecisionFields;
    internal static IReadOnlySet<string> AllowedClaimFields => ClaimFields;
    internal static IReadOnlySet<string> AllowedAdditionalPartFields => AdditionalPartFields;
    internal static IReadOnlySet<string> AllowedObjectPartFields => ObjectPartFields;
}

/// <summary>
/// Strict V3 decoder. Unknown fields fail closed, so legacy subject/sourceAlias payloads cannot be
/// silently ignored by deserialization. Packet-local cardinality and index ownership are validated
/// before any adaptation to the existing exact binder.
/// </summary>
public static class V5OwnedDecisionResponseCodecV3
{
    public static V5OwnedDecisionResponseV3 Parse(
        JsonElement payload,
        DocumentTaskContract contract,
        V5EvidencePacketV2_1 packet)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("decision-payload-not-object");
        EnsureFields(payload, V5OwnedDecisionContractV3.AllowedRootFields, "response");
        if (!payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("decisions-array-missing");

        foreach (var decision in decisions.EnumerateArray())
        {
            EnsureFields(decision, V5OwnedDecisionContractV3.AllowedDecisionFields, "decision");
            if (!decision.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("decision-kind-missing");
            if (!decision.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("decision-claims-array-missing");

            if (decision.TryGetProperty("additionalSubjectParts", out var additional) &&
                additional.ValueKind != JsonValueKind.Null)
            {
                if (additional.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("additional-subject-parts-not-array");
                foreach (var part in additional.EnumerateArray())
                    EnsureFields(part, V5OwnedDecisionContractV3.AllowedAdditionalPartFields, "additional-subject-part");
            }

            foreach (var claim in claims.EnumerateArray())
            {
                EnsureFields(claim, V5OwnedDecisionContractV3.AllowedClaimFields, "claim");
                if (claim.TryGetProperty("objectParts", out var objectParts) && objectParts.ValueKind != JsonValueKind.Null)
                {
                    if (objectParts.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("object-parts-not-array");
                    foreach (var part in objectParts.EnumerateArray())
                        EnsureFields(part, V5OwnedDecisionContractV3.AllowedObjectPartFields, "object-part");
                }
            }
        }

        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var response = payload.Deserialize<V5OwnedDecisionResponseV3>(options)
            ?? throw new InvalidOperationException("decision-payload-empty");
        var issues = V5OwnedDecisionContractV3.Validate(response, contract, packet);
        if (issues.Count > 0)
            throw new InvalidOperationException(string.Join(",", issues));
        return response;
    }

    private static void EnsureFields(JsonElement element, IReadOnlySet<string> allowed, string kind)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{kind}-not-object");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{kind}-unknown-field:{property.Name}");
    }
}

/// <summary>
/// Lossless harness-owned translation into the already-proven v2.1 exact binder input. The model
/// never supplies an alias; the adapter resolves every positional reference from the packet.
/// </summary>
public static class V5OwnedDecisionAdapterV3
{
    public static IReadOnlyList<SemanticClaimProposalV2_1> ToV2_1(
        V5OwnedDecisionResponseV3 response,
        DocumentTaskContract contract,
        V5EvidencePacketV2_1 packet)
    {
        var issues = V5OwnedDecisionContractV3.Validate(response, contract, packet);
        if (issues.Count > 0)
            throw new InvalidOperationException("v3-owned-decision-invalid:" + string.Join(",", issues));

        var proposals = new List<SemanticClaimProposalV2_1>();
        for (var i = 0; i < response.Decisions.Count; i++)
        {
            var decision = response.Decisions[i];
            if (decision.Kind != V5OwnedDecisionKindsV3.Claims) continue;

            var subjectParts = new List<ProviderSourcePartV2_1>
            {
                ToPart(packet.SubjectEvidence[i], decision.VerbatimText),
            };
            foreach (var additional in decision.AdditionalSubjectParts ?? [])
                subjectParts.Add(ToPart(packet.SubjectEvidence[additional.OwnedIndex], additional.VerbatimText));

            foreach (var claim in decision.Claims ?? [])
            {
                ClaimSourceEndpointV2_1? target = null;
                if (claim.ObjectParts is { Count: > 0 })
                {
                    var parts = claim.ObjectParts.Select(part =>
                    {
                        var node = part.Scope == V5EvidenceReferenceScopesV3.Owned
                            ? packet.SubjectEvidence[part.Index]
                            : packet.ContextOnlyEvidence[part.Index];
                        return ToPart(node, part.VerbatimText);
                    }).ToArray();
                    target = new ClaimSourceEndpointV2_1(parts);
                }

                proposals.Add(new SemanticClaimProposalV2_1(
                    new ClaimSourceEndpointV2_1(subjectParts.ToArray()),
                    claim.Predicate,
                    claim.Value,
                    target,
                    claim.State,
                    claim.EvidenceNeeds,
                    claim.ExistingClaimId));
            }
        }
        return proposals;
    }

    private static ProviderSourcePartV2_1 ToPart(EvidenceNode node, string? verbatimText) =>
        new(node.SourceAlias, verbatimText);
}

public static class V5OwnedDecisionRequestComposerV3
{
    private static string Instructions(int ownedCount) => string.Join("\n", new[]
    {
        "You are a task-defined semantic reasoner.",
        $"Return exactly {ownedCount} decisions, one positionally for every subjectEvidence item and in the same order.",
        "Do not return subject aliases or subject coordinates. The harness owns subject identity.",
        "Use kind NONE for an explicit semantic negative, CLAIMS when this owned subject originates claims, and CONSUMED when an earlier multi-atom CLAIMS decision already includes this owned subject.",
        "contextOnlyEvidence can be read as context and can be referenced only as a relation object through scope CONTEXT; it has no subject-reference form.",
        "For a whole subject atom omit verbatimText. Use verbatimText only for a strict exact substring copied character-for-character.",
        "For a multi-atom subject, additionalSubjectParts may reference later subjectEvidence indices only; mark each included later slot CONSUMED by the anchor index.",
        "Relations address objects by scope/index. OPEN is preferable to inventing a target.",
        "The task contract vocabulary is authoritative. Return only the declared decision schema.",
    });

    public static CanonicalOwnedDecisionRequestV3 BuildCanonical(
        DocumentTaskContract contract,
        V5EvidencePacketV2_1 packet)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(packet);
        contract.Validate();
        var instructions = Instructions(packet.SubjectEvidence.Count);
        return new CanonicalOwnedDecisionRequestV3(
            V5OwnedDecisionProtocolV3.ComposerVersion,
            V5OwnedDecisionProtocolV3.ProtocolVersion,
            contract,
            V5OwnedDecisionContractV3.Schema(contract, packet.SubjectEvidence.Count, packet.ContextOnlyEvidence.Count),
            V5SourceSelectionPolicy.Generate(),
            instructions,
            packet);
    }

    public static V5ComposedSemanticRequest Serialize(CanonicalOwnedDecisionRequestV3 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(CanonicalJson.Options)
        {
            WriteIndented = false,
        }).ReplaceLineEndings("\n");
        var bytes = Encoding.UTF8.GetBytes(json);
        return new V5ComposedSemanticRequest(
            request.ComposerVersion,
            json,
            Hashing.Sha256(request.Instructions.ReplaceLineEndings("\n")),
            Hashing.Sha256(JsonSerializer.Serialize(request.ResponseSchema, CanonicalJson.Options)),
            Hashing.Sha256(json),
            bytes.Length);
    }

    public static V5ComposedSemanticRequest Compose(DocumentTaskContract contract, V5EvidencePacketV2_1 packet) =>
        Serialize(BuildCanonical(contract, packet));
}
