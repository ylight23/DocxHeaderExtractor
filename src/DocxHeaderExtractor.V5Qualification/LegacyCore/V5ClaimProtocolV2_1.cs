using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Source-backed V5 claim protocol v2.1. This is the single, currently-evolving implementation of
/// the <c>v5-source-backed-claim-2.1</c> label: historical reproducibility is owned by the Git
/// commit, the schema/prompt/request hashes and the frozen artifacts under
/// <c>artifacts/v5-provider-canary-*</c>, not by a parallel source file per hardening pass. v2 (the
/// file this superseded, since deleted - see git history at or before commit 72bb954) is gone; its
/// hashes remain reproducible from history alone.
/// <para>
/// A real 3-pack canary against qwen/qwen3.7-flash at commit 72bb954 found three wire-contract gaps,
/// closed here in place:
/// </para>
/// <list type="bullet">
/// <item>the model paired <c>selectionMode: WHOLE_ALIAS</c> with a <c>verbatimText</c> quote, which
/// the exact binder correctly refused on every claim of one pack - <c>selectionMode</c> is removed
/// from the provider wire entirely; the harness derives it deterministically from whether
/// <c>verbatimText</c> is present (<see cref="ProviderSourcePartNormalization"/>);</item>
/// <item>the model emitted OPEN claims with no <c>evidenceNeeds</c>, which the v2 schema allowed to
/// omit - <c>evidenceNeeds</c> is now mandatory on every claim: <c>[]</c> for RESOLVED, non-empty for
/// OPEN/CONFLICTED;</item>
/// <item>a response was truncated mid-JSON under the legacy boundary-cut completion budget - v2.1 now
/// computes its own budget (<see cref="V5SemanticCompletionBudget"/>) instead of borrowing that
/// formula.</item>
/// </list>
/// </summary>
public sealed record SemanticClaimProposalV2_1(
    [property: JsonPropertyName("subject")] ClaimSourceEndpointV2_1 Subject,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("object")] ClaimSourceEndpointV2_1? Object = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null,
    [property: JsonPropertyName("existingClaimId")] string? ExistingClaimId = null);

public sealed record SemanticClaimResponseV2_1(
    [property: JsonPropertyName("claims")] IReadOnlyList<SemanticClaimProposalV2_1> Claims);

/// <summary>
/// One provider-facing source reference. Deliberately has no <c>selectionMode</c>: a bare
/// <c>sourceAlias</c> means the whole occurrence, and a <c>verbatimText</c> quote means an exact
/// substring of it. There is exactly one way to say each thing, so the model cannot express the
/// contradictory pair (whole alias, but also a quoted substring) the v2.1 canary found it emitting.
/// </summary>
public sealed record ProviderSourcePartV2_1(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("verbatimText")] string? VerbatimText = null,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null,
    [property: JsonPropertyName("leftExactContext")] string? LeftExactContext = null,
    [property: JsonPropertyName("rightExactContext")] string? RightExactContext = null);

public sealed record ClaimSourceEndpointV2_1(
    [property: JsonPropertyName("sourceParts")] IReadOnlyList<ProviderSourcePartV2_1> SourceParts);

/// <summary>
/// Deterministic, harness-owned translation from the provider's wire shape to the exact binder's
/// canonical shape. This is syntax interpretation, not semantic repair: it never fuzzy-matches text,
/// corrects text, trims arbitrary text, infers a different alias, or widens a span. Given the same
/// provider part it always produces the same canonical part.
/// </summary>
public static class ProviderSourcePartNormalization
{
    public static SemanticSourcePart ToCanonical(ProviderSourcePartV2_1 part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.VerbatimText is null
            ? new SemanticSourcePart(part.SourceAlias, CanonicalSemanticSelectionMode.WholeAlias,
                null, part.Occurrence, part.LeftExactContext, part.RightExactContext)
            : new SemanticSourcePart(part.SourceAlias, CanonicalSemanticSelectionMode.VerbatimText,
                part.VerbatimText, part.Occurrence, part.LeftExactContext, part.RightExactContext);
    }

    public static IReadOnlyList<SemanticSourcePart> ToCanonical(IReadOnlyList<ProviderSourcePartV2_1>? parts) =>
        (parts ?? []).Select(ToCanonical).ToArray();
}

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
/// Ownership/visibility enforced at binding time. <see cref="OwnedAliases"/> is the only allowed
/// source for a claim subject, whether the claim is initial or a refinement - a refinement never
/// widens its subject onto evidence discovered later. <see cref="VisibleAliases"/> (owned plus halo)
/// is the allowed source for a relation object. <see cref="KnownClaims"/> carries the durable identity
/// and original subject/predicate of every claim a refinement proposal may reference.
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
/// Durable harness identity. Deliberately excludes state, evidenceNeeds, value and relation target -
/// none of those may change which claim this is, only what is currently known about it. The
/// proposal's ordinal within its response is included so two distinct initial proposals that happen
/// to share a subject and predicate never collide.
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
/// A proposal paired with its position in the raw response it came from - provenance, not a
/// post-filtering array index. <see cref="OriginalOrdinal"/> is what
/// <see cref="HarnessClaimIdentityV2_1.Create"/> must hash: a proposal's durable identity may never
/// depend on which other proposals happen to survive alongside it in whatever list is handed to the
/// binder. Zero-based, matching the ordinal the binder has always used for a full, unfiltered batch.
/// </summary>
public sealed record IndexedSemanticClaimProposalV2_1(int OriginalOrdinal, SemanticClaimProposalV2_1 Proposal);

/// <summary>
/// Exact binder. Normalizes the provider's wire shape to the canonical shape before ever calling
/// <see cref="SemanticSourcePartBinder"/> - it shares that binder's exact-coordinate rules unchanged
/// (never repairs, never widens a span) - and adds durable-identity/refinement handling and ownership
/// enforcement on top.
/// </summary>
public static class ExactClaimBinderV2_1
{
    /// <summary>
    /// Unfiltered-batch convenience: every proposal's identity ordinal is its own position, 0..N-1 -
    /// identical to this method's own behavior before <see cref="IndexedSemanticClaimProposalV2_1"/>
    /// existed. A caller that has already excluded some proposals (claim quarantine, for one) must use
    /// the indexed overload instead and supply each survivor's ORIGINAL position, never call this one
    /// with a filtered list - doing so would silently renumber identities by array position again.
    /// </summary>
    public static ClaimBindingResultV2_1 Bind(
        string requestId,
        IReadOnlyList<SemanticClaimProposalV2_1> proposals,
        IReadOnlyList<SemanticSourceAtom> atoms,
        ClaimBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        return Bind(requestId, proposals.Select((p, i) => new IndexedSemanticClaimProposalV2_1(i, p)).ToArray(), atoms, scope);
    }

    public static ClaimBindingResultV2_1 Bind(
        string requestId,
        IReadOnlyList<IndexedSemanticClaimProposalV2_1> indexedProposals,
        IReadOnlyList<SemanticSourceAtom> atoms,
        ClaimBindingScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(indexedProposals);
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(scope);
        var bound = new List<BoundSemanticClaimV2_1>();
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var indexed in indexedProposals)
        {
            var proposal = indexed.Proposal;
            var originalOrdinal = indexed.OriginalOrdinal;
            var key = proposal.ExistingClaimId ?? $"proposal-{originalOrdinal + 1}";

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

            var subjectParts = ProviderSourcePartNormalization.ToCanonical(proposal.Subject?.SourceParts);
            var subject = SemanticSourcePartBinder.Bind(atoms, subjectParts);
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
                var objectParts = ProviderSourcePartNormalization.ToCanonical(proposal.Object.SourceParts);
                var objectBinding = SemanticSourcePartBinder.Bind(atoms, objectParts);
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
                : HarnessClaimIdentityV2_1.Create(requestId, originalOrdinal, subjectEndpoint, proposal.Predicate);
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
/// Recursive source-backed schema and semantic validation. The codec below uses the same field sets
/// and schema construction, so schema and decoder cannot silently drift apart.
/// </summary>
public static class SemanticClaimContractV2_1
{
    public const string SchemaVersion = "v5-source-backed-claim-2.1";

    /// <summary>
    /// The provider-facing subset of <see cref="ClaimResolutionState"/>. EXHAUSTED is a runtime
    /// terminal state the harness assigns when a budget is spent; the model is never asked to reason
    /// about budget exhaustion and may not originate it (harness-owned, never provider-owned).
    /// </summary>
    internal static IReadOnlyList<string> ProviderFacingStates { get; } =
        Enum.GetNames<ClaimResolutionState>().Where(name => name != nameof(ClaimResolutionState.EXHAUSTED)).ToArray();

    internal static IReadOnlySet<string> ResponseFields { get; } = new HashSet<string>(["claims"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> ClaimFields { get; } = new HashSet<string>(
        ["subject", "predicate", "value", "object", "state", "evidenceNeeds", "existingClaimId"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> EndpointFields { get; } = new HashSet<string>(["sourceParts"], StringComparer.Ordinal);

    /// <summary>
    /// No <c>selectionMode</c>: removed from the provider wire after the canary showed the model
    /// pairing WHOLE_ALIAS with a verbatimText quote, a state the exact binder correctly refuses.
    /// </summary>
    internal static IReadOnlySet<string> PartFields { get; } = new HashSet<string>(
        ["sourceAlias", "verbatimText", "occurrence", "leftExactContext", "rightExactContext"], StringComparer.Ordinal);

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
        // evidenceNeeds is required on every claim - RESOLVED sends [], OPEN/CONFLICTED send at least one need.
        required = new[] { "subject", "predicate", "state", "evidenceNeeds" },
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
                        verbatimText = new { type = "string", minLength = 1 },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "sourceAlias" },
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

            // evidenceNeeds is mandatory on every claim, not only OPEN/CONFLICTED: RESOLVED must send
            // an explicit empty array. A missing field is never treated as an implicit [].
            if (claim.EvidenceNeeds is null)
                issues.Add("evidence-needs-missing");
            else if (claim.State == ClaimResolutionState.RESOLVED && claim.EvidenceNeeds.Count > 0)
                issues.Add("resolved-claim-must-not-carry-evidence-needs");
            else if (claim.State is ClaimResolutionState.OPEN or ClaimResolutionState.CONFLICTED && claim.EvidenceNeeds.Count == 0)
                issues.Add($"{claim.State.ToString().ToLowerInvariant()}-claim-without-evidence-need");

            var isRelation = relations.ContainsKey(claim.Predicate);
            if (isRelation && claim.Object is null)
            {
                // A RESOLVED relation must name its object. An OPEN relation may omit it only when
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
/// What a per-claim quarantine decode found: which raw claims are eligible for binding, tagged with
/// their <see cref="IndexedSemanticClaimProposalV2_1.OriginalOrdinal"/>, and which were refused, keyed
/// by that same original ordinal with the exact reason <see cref="SemanticClaimResponseCodecV2_1.Parse"/>
/// would have thrown for that claim alone. <c>Eligible.Count + ContractRefusals.Count == RawClaimCount</c>
/// always - nothing is ever silently dropped without a recorded reason.
/// </summary>
public sealed record ClaimQuarantineResultV2_1(
    int RawClaimCount,
    IReadOnlyList<IndexedSemanticClaimProposalV2_1> Eligible,
    IReadOnlyDictionary<int, string> ContractRefusals);

/// <summary>
/// Strict JSON decoder. Unknown fields (including a provider-supplied <c>selectionMode</c>), model
/// claim ids, empty parts, an empty verbatimText and a model-originated EXHAUSTED all fail closed.
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

    /// <summary>
    /// The same strict rules as <see cref="Parse"/> - it calls no different check, invents no new
    /// leniency - applied per claim instead of per response. Root-level malformation (a non-object
    /// payload, an unknown top-level field, a missing or non-array <c>claims</c>) remains exactly as
    /// fatal as it always was: quarantine only ever isolates a claim-CONTAINED defect, never a
    /// structural one, because a response that is not even shaped like a claim list has no claims to
    /// isolate. A claim that fails any check - structural or contract - is recorded as refused with
    /// its exact reason and excluded as-is; nothing about it is mutated, coerced, or inferred to make
    /// it pass, and its surviving siblings are never touched by its failure.
    /// </summary>
    public static ClaimQuarantineResultV2_1 ParseWithClaimQuarantine(JsonElement payload, DocumentTaskContract contract)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("claim-payload-not-object");
        EnsureFields(payload, SemanticClaimContractV2_1.ResponseFields, "response");
        if (!payload.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("claims-array-missing");

        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var eligible = new List<IndexedSemanticClaimProposalV2_1>();
        var contractRefusals = new Dictionary<int, string>();

        var ordinal = 0;
        foreach (var claim in claims.EnumerateArray())
        {
            try
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

                var proposal = claim.Deserialize<SemanticClaimProposalV2_1>(options)
                    ?? throw new InvalidOperationException("claim-payload-empty");
                // The same arity/vocabulary rules Validate applies to a full batch, applied to this one
                // claim alone - a single-claim response is not a special case Validate needs to know
                // about, since it already checks nothing but each claim's own fields.
                var issues = SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([proposal]), contract);
                if (issues.Count > 0) throw new InvalidOperationException(string.Join(",", issues));

                eligible.Add(new IndexedSemanticClaimProposalV2_1(ordinal, proposal));
            }
            catch (InvalidOperationException ex)
            {
                contractRefusals[ordinal] = ex.Message;
            }
            ordinal++;
        }

        return new ClaimQuarantineResultV2_1(claims.GetArrayLength(), eligible, contractRefusals);
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
            // A "selectionMode" field is not in PartFields any more, so it fails here as an unknown field.
            EnsureFields(part, SemanticClaimContractV2_1.PartFields, "source-part");
            if (!part.TryGetProperty("sourceAlias", out var alias) || alias.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(alias.GetString()))
                throw new InvalidOperationException("source-part-alias-missing");
            if (part.TryGetProperty("verbatimText", out var verbatim) && verbatim.ValueKind == JsonValueKind.String && verbatim.GetString()!.Length == 0)
                throw new InvalidOperationException("source-part-verbatim-text-empty");
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
/// One predicate's or relation's arity, generated deterministically from the task contract so the
/// model does not have to infer unary-vs-relation shape from the separate Predicates/Relations lists.
/// A real canary found a unary predicate carrying an object on every one of 58 claims; the contract
/// distinction existed but was not structurally explicit in the wire.
/// </summary>
public sealed record V5ClaimShapeV2_1(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("valueAllowed")] bool ValueAllowed,
    [property: JsonPropertyName("objectAllowed")] bool ObjectAllowed,
    [property: JsonPropertyName("resolvedObjectRequired")] bool ResolvedObjectRequired);

/// <summary>
/// Generates <see cref="V5ClaimShapeV2_1"/> from a <see cref="DocumentTaskContract"/>. Mirrors exactly
/// the arity <see cref="SemanticClaimContractV2_1.Validate"/> already enforces - a unary predicate
/// never carries an object, a relation never carries a value and requires an object once RESOLVED -
/// so this never becomes a second, drifting source of truth. Carries no document-specific vocabulary:
/// the contract's own predicate and relation names are the only input.
/// </summary>
public static class V5ClaimShapesV2_1
{
    public const string Unary = "UNARY";
    public const string Relation = "RELATION";

    public static IReadOnlyList<V5ClaimShapeV2_1> Generate(DocumentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var unary = contract.Predicates.Select(item =>
            new V5ClaimShapeV2_1(item.Name, Unary, ValueAllowed: true, ObjectAllowed: false, ResolvedObjectRequired: false));
        var relation = contract.Relations.Select(item =>
            new V5ClaimShapeV2_1(item.Name, Relation, ValueAllowed: false, ObjectAllowed: true, ResolvedObjectRequired: true));
        return unary.Concat(relation).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public static string Hash(DocumentTaskContract contract) =>
        Hashing.Sha256(JsonSerializer.Serialize(Generate(contract), CanonicalJson.Options));
}
