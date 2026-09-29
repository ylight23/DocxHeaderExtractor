using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Source-backed V5 claim protocol v2.1. v2 (<see cref="SemanticClaimContractV2"/> and its neighbors in
/// V5ClaimProtocolV2.cs) is kept byte-for-byte as a frozen historical checkpoint; nothing here mutates
/// it, and nothing in v2 depends on this file. v2.1 closes four gaps a pre-canary audit of v2 found:
/// <list type="bullet">
/// <item>claim identity was not durable across OPEN to RESOLVED refinement (C1);</item>
/// <item>an OPEN relation with an unknown target had no valid representation (C2);</item>
/// <item>owned vs. visible-only (halo) evidence was not enforced at binding time (C3);</item>
/// <item><c>selectionMode</c> was an open string instead of the binder's closed vocabulary (C4).</item>
/// </list>
/// It also makes EXHAUSTED harness-only: the model may never originate it (C5).
/// </summary>
public sealed record SemanticClaimProposalV2_1(
    [property: JsonPropertyName("subject")] ClaimSourceEndpoint Subject,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("object")] ClaimSourceEndpoint? Object = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null,
    [property: JsonPropertyName("existingClaimId")] string? ExistingClaimId = null);

public sealed record SemanticClaimResponseV2_1(
    [property: JsonPropertyName("claims")] IReadOnlyList<SemanticClaimProposalV2_1> Claims);

public sealed record BoundSemanticClaimV2_1(
    BoundSemanticClaim Claim,
    string? ExistingClaimId);

public sealed record ClaimBindingResultV2_1(
    IReadOnlyList<BoundSemanticClaimV2_1> Bound,
    IReadOnlyDictionary<string, string> Refusals)
{
    public bool IsComplete => Refusals.Count == 0;
}

/// <summary>The original subject/predicate a refinement's <c>existingClaimId</c> must still match.</summary>
public sealed record KnownClaimReference(string SubjectIdentity, string Predicate);

/// <summary>
/// Ownership/visibility enforced at binding time (C3). <see cref="OwnedAliases"/> is the only allowed
/// source for a claim subject, whether the claim is initial or a refinement - a refinement never
/// widens its subject onto evidence discovered later. <see cref="VisibleAliases"/> (owned plus halo) is
/// the allowed source for a relation object. <see cref="KnownClaims"/> carries the durable identity and
/// original subject/predicate of every claim a refinement proposal may reference.
/// </summary>
public sealed record ClaimBindingScope(
    IReadOnlySet<string> OwnedAliases,
    IReadOnlySet<string> VisibleAliases,
    IReadOnlyDictionary<string, KnownClaimReference> KnownClaims)
{
    public static ClaimBindingScope Create(
        IEnumerable<string> ownedAliases,
        IEnumerable<string> visibleAliases,
        IReadOnlyDictionary<string, KnownClaimReference>? knownClaims = null) => new(
        new HashSet<string>(ownedAliases, StringComparer.Ordinal),
        new HashSet<string>(visibleAliases, StringComparer.Ordinal),
        knownClaims ?? new Dictionary<string, KnownClaimReference>(StringComparer.Ordinal));

    public static ClaimBindingScope Empty { get; } = Create([], []);
}

/// <summary>
/// Durable harness identity (C1). Deliberately excludes state, evidenceNeeds, value and relation
/// target - none of those may change which claim this is, only what is currently known about it. The
/// proposal's ordinal within its response is included so two distinct initial proposals that happen to
/// share a subject and predicate never collide.
/// </summary>
public static class HarnessClaimIdentityV2_1
{
    public const string Prefix = "v5claim21-";

    public static string Create(string requestId, int proposalOrdinal, BoundClaimEndpoint subject, string predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        var canonical = string.Join("\n", [
            V5Protocol.ClaimSchemaVersionV2_1,
            requestId,
            proposalOrdinal.ToString(CultureInfo.InvariantCulture),
            subject.Identity,
            predicate,
        ]);
        return Prefix + Hashing.Sha256(canonical)[..32];
    }
}

/// <summary>
/// Exact binder for v2.1. Shares the v2 exact-coordinate rules (never repairs, never widens a span) and
/// adds durable-identity/refinement handling (C1) and ownership enforcement (C3).
/// </summary>
public static class ExactClaimBinderV2_1
{
    public static ClaimBindingResultV2_1 Bind(
        string requestId,
        IReadOnlyList<SemanticClaimProposalV2_1> proposals,
        IReadOnlyList<SemanticSourceAtom> atoms,
        ClaimBindingScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(scope);
        var bound = new List<BoundSemanticClaimV2_1>();
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            var key = proposal.ExistingClaimId ?? $"proposal-{index + 1}";

            if (string.IsNullOrWhiteSpace(proposal.Predicate))
            {
                refusals[key] = "predicate-missing";
                continue;
            }

            KnownClaimReference? known = null;
            if (proposal.ExistingClaimId is not null && !scope.KnownClaims.TryGetValue(proposal.ExistingClaimId, out known))
            {
                refusals[key] = "unknown-existing-claim-id";
                continue;
            }

            if (proposal.State == ClaimResolutionState.EXHAUSTED)
            {
                refusals[key] = "model-may-not-originate-exhausted-state";
                continue;
            }

            var subject = SemanticSourcePartBinder.Bind(atoms, proposal.Subject?.SourceParts ?? []);
            if (!subject.IsBound)
            {
                refusals[key] = subject.Reason ?? subject.Status.ToString();
                continue;
            }
            var subjectOffOwned = subject.Parts.Select(part => part.Alias).FirstOrDefault(alias => !scope.OwnedAliases.Contains(alias));
            if (subjectOffOwned is not null)
            {
                refusals[key] = $"subject-alias-not-owned:{subjectOffOwned}";
                continue;
            }
            var subjectEndpoint = new BoundClaimEndpoint(subject.Parts);

            if (known is not null &&
                (!string.Equals(known.SubjectIdentity, subjectEndpoint.Identity, StringComparison.Ordinal) ||
                 !string.Equals(known.Predicate, proposal.Predicate, StringComparison.Ordinal)))
            {
                refusals[key] = "existing-claim-id-mismatch";
                continue;
            }

            BoundClaimEndpoint? target = null;
            if (proposal.Object is not null)
            {
                var objectBinding = SemanticSourcePartBinder.Bind(atoms, proposal.Object.SourceParts);
                if (!objectBinding.IsBound)
                {
                    refusals[key] = objectBinding.Reason ?? objectBinding.Status.ToString();
                    continue;
                }
                var objectOffVisible = objectBinding.Parts.Select(part => part.Alias).FirstOrDefault(alias => !scope.VisibleAliases.Contains(alias));
                if (objectOffVisible is not null)
                {
                    refusals[key] = $"object-alias-not-visible:{objectOffVisible}";
                    continue;
                }
                target = new BoundClaimEndpoint(objectBinding.Parts);
            }

            var claimId = known is not null
                ? proposal.ExistingClaimId!
                : HarnessClaimIdentityV2_1.Create(requestId, index, subjectEndpoint, proposal.Predicate);
            if (!ids.Add(claimId))
            {
                refusals[key] = "duplicate-harness-claim-id";
                continue;
            }

            var claim = new BoundSemanticClaim(
                claimId,
                subjectEndpoint,
                proposal.Predicate,
                proposal.Value,
                target,
                proposal.State,
                (proposal.EvidenceNeeds ?? []).Distinct().ToArray());
            bound.Add(new BoundSemanticClaimV2_1(claim, proposal.ExistingClaimId));
        }

        return new ClaimBindingResultV2_1(bound, new ReadOnlyDictionary<string, string>(refusals));
    }
}

/// <summary>
/// Recursive source-backed schema and semantic validation for v2.1. The codec below uses the same
/// field sets and schema construction, so schema and decoder cannot silently drift apart.
/// </summary>
public static class SemanticClaimContractV2_1
{
    public const string SchemaVersion = "v5-source-backed-claim-2.1";

    /// <summary>
    /// The provider-facing subset of <see cref="ClaimResolutionState"/> (C5). EXHAUSTED is a runtime
    /// terminal state the harness assigns when a budget is spent; the model is never asked to reason
    /// about budget exhaustion and may not originate it.
    /// </summary>
    internal static IReadOnlyList<string> ProviderFacingStates { get; } =
        Enum.GetNames<ClaimResolutionState>().Where(name => name != nameof(ClaimResolutionState.EXHAUSTED)).ToArray();

    /// <summary>The binder's closed selection-mode vocabulary (C4) - never an open string.</summary>
    internal static IReadOnlyList<string> SelectionModes { get; } =
        [CanonicalSemanticSelectionMode.WholeAlias, CanonicalSemanticSelectionMode.VerbatimText];

    internal static IReadOnlySet<string> ResponseFields { get; } = new HashSet<string>(["claims"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> ClaimFields { get; } = new HashSet<string>(
        ["subject", "predicate", "value", "object", "state", "evidenceNeeds", "existingClaimId"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> EndpointFields { get; } = new HashSet<string>(["sourceParts"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> PartFields { get; } = new HashSet<string>(
        ["sourceAlias", "selectionMode", "verbatimText", "occurrence", "leftExactContext", "rightExactContext"], StringComparer.Ordinal);

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            claims = new
            {
                type = "array",
                items = ClaimSchema(),
            },
        },
        required = new[] { "claims" },
    };

    private static object ClaimSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            subject = EndpointSchema(),
            predicate = new { type = "string", minLength = 1 },
            value = new { type = "string" },
            @object = EndpointSchema(),
            state = new { type = "string", @enum = ProviderFacingStates },
            evidenceNeeds = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
            existingClaimId = new { type = "string", minLength = 1 },
        },
        required = new[] { "subject", "predicate", "state" },
    };

    private static object EndpointSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            sourceParts = new
            {
                type = "array",
                minItems = 1,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        sourceAlias = new { type = "string", minLength = 1 },
                        selectionMode = new { type = "string", @enum = SelectionModes },
                        verbatimText = new { type = "string", minLength = 1 },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "sourceAlias", "selectionMode" },
                },
            },
        },
        required = new[] { "sourceParts" },
    };

    public static string SchemaHash() => Hashing.Sha256(JsonSerializer.Serialize(Schema(), CanonicalJson.Options));

    public static IReadOnlyList<string> Validate(SemanticClaimResponseV2_1 response, DocumentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var predicates = contract.Predicates.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var relations = contract.Relations.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var issues = new List<string>();
        foreach (var claim in response.Claims ?? [])
        {
            if (string.IsNullOrWhiteSpace(claim.Predicate))
            {
                issues.Add("claim-predicate-missing");
                continue;
            }
            if (!predicates.Contains(claim.Predicate) && !relations.ContainsKey(claim.Predicate))
                issues.Add($"predicate-not-in-contract:{claim.Predicate}");
            if (claim.Subject is null || claim.Subject.SourceParts is null || claim.Subject.SourceParts.Count == 0)
                issues.Add("claim-subject-missing");

            var isRelation = relations.ContainsKey(claim.Predicate);
            var isUnresolved = claim.State is ClaimResolutionState.OPEN or ClaimResolutionState.CONFLICTED;
            if (isUnresolved && (claim.EvidenceNeeds is null || claim.EvidenceNeeds.Count == 0))
                issues.Add($"{claim.State.ToString().ToLowerInvariant()}-claim-without-evidence-need");

            if (isRelation && claim.Object is null)
            {
                // C2: a RESOLVED relation must name its object. An OPEN relation may omit it only when
                // GLOBAL_TARGET says why - the target is unknown, not forgotten. A CONFLICTED relation
                // may omit it as long as it carries some actionable evidence need.
                var explainsMissingObject =
                    (claim.State == ClaimResolutionState.OPEN && (claim.EvidenceNeeds?.Contains(EvidenceNeed.GLOBAL_TARGET) ?? false)) ||
                    (claim.State == ClaimResolutionState.CONFLICTED && claim.EvidenceNeeds is { Count: > 0 });
                if (!explainsMissingObject)
                    issues.Add("relation-missing-object");
            }
            if (isRelation && claim.Value is not null)
                issues.Add("relation-has-value");
            if (!isRelation && claim.Object is not null)
                issues.Add("unary-claim-has-object");
        }
        return issues;
    }
}

/// <summary>
/// Strict JSON decoder for v2.1. Unknown fields, model claim ids, empty parts, an open selectionMode
/// and a model-originated EXHAUSTED all fail closed.
/// </summary>
public static class SemanticClaimResponseCodecV2_1
{
    public static SemanticClaimResponseV2_1 Parse(JsonElement payload, DocumentTaskContract contract)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("claim-payload-not-object");
        EnsureFields(payload, SemanticClaimContractV2_1.ResponseFields, "response");
        if (!payload.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("claims-array-missing");
        foreach (var claim in claims.EnumerateArray())
        {
            EnsureFields(claim, SemanticClaimContractV2_1.ClaimFields, "claim");
            EnsureRequiredString(claim, "predicate");
            var state = EnsureRequiredString(claim, "state");
            if (string.Equals(state, nameof(ClaimResolutionState.EXHAUSTED), StringComparison.Ordinal))
                throw new InvalidOperationException("model-may-not-originate-exhausted-state");
            if (!SemanticClaimContractV2_1.ProviderFacingStates.Contains(state, StringComparer.Ordinal))
                throw new InvalidOperationException($"claim-state-not-provider-facing:{state}");
            EnsureEndpoint(claim, "subject", required: true);
            if (claim.TryGetProperty("object", out var target) && target.ValueKind != JsonValueKind.Null)
                EnsureEndpoint(target, "object", required: false);
        }
        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var response = payload.Deserialize<SemanticClaimResponseV2_1>(options)
            ?? throw new InvalidOperationException("claim-payload-empty");
        var issues = SemanticClaimContractV2_1.Validate(response, contract);
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(",", issues));
        return response;
    }

    private static void EnsureEndpoint(JsonElement claim, string property, bool required)
    {
        if (!claim.TryGetProperty(property, out var endpoint) || endpoint.ValueKind != JsonValueKind.Object)
        {
            if (required) throw new InvalidOperationException("claim-subject-missing");
            return;
        }
        EnsureFields(endpoint, SemanticClaimContractV2_1.EndpointFields, property);
        if (!endpoint.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
            throw new InvalidOperationException($"{property}-parts-missing");
        foreach (var part in parts.EnumerateArray())
        {
            EnsureFields(part, SemanticClaimContractV2_1.PartFields, "source-part");
            if (!part.TryGetProperty("sourceAlias", out var alias) || alias.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(alias.GetString()))
                throw new InvalidOperationException("source-part-alias-missing");
            if (!part.TryGetProperty("selectionMode", out var mode) || mode.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("source-part-selection-mode-missing");
            var modeValue = mode.GetString();
            if (!SemanticClaimContractV2_1.SelectionModes.Contains(modeValue, StringComparer.Ordinal))
                throw new InvalidOperationException($"source-part-selection-mode-not-in-contract:{modeValue}");
        }
    }

    private static void EnsureFields(JsonElement element, IReadOnlySet<string> allowed, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{path}-not-object");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{path}-field-not-in-contract:{property.Name}");
    }

    private static string EnsureRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"claim-{property}-missing");
        return value.GetString()!;
    }
}

/// <summary>
/// Hard-pins the provider-free canary to exactly three requests and requires an explicit
/// authorization flag before anything may be allowed to call a provider. It never performs a network
/// call itself and never falls back to the full cohort.
/// </summary>
public static class V5CanaryGate
{
    public const int CanaryRequestCount = 3;

    /// <summary>Throws unless there are exactly three requests and the caller explicitly authorized execution.</summary>
    public static void Authorize(int requestCount, bool providerExecutionAuthorized)
    {
        if (requestCount != CanaryRequestCount)
            throw new InvalidOperationException($"canary-request-count-must-be-exactly-{CanaryRequestCount}:{requestCount}");
        if (!providerExecutionAuthorized)
            throw new InvalidOperationException("canary-provider-execution-not-authorized");
    }
}
