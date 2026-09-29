using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// What one pack's provider response amounts to, in layers that are never collapsed into a single
/// pass/fail. <see cref="RESPONSE_FATAL"/> is the only layer that discards a whole response; every
/// other outcome is a structurally usable response (<c>WIRE_PASS</c>) that the runtime processes
/// claim by claim.
/// </summary>
public enum V5PackBindingOutcome
{
    /// <summary>Transport, <c>finish_reason=length</c>, JSON, schema or vocabulary failure: nothing from the response is used.</summary>
    RESPONSE_FATAL,

    /// <summary>Usable response and every proposal bound.</summary>
    BINDING_COMPLETE,

    /// <summary>Usable response, at least one proposal bound and at least one claim-local refusal.</summary>
    PARTIAL_BINDING,

    /// <summary>Usable response, zero proposals bound (including a response that proposed nothing).</summary>
    BINDING_EMPTY,
}

/// <summary>
/// Per-pack qualification metrics that mirror exactly what <see cref="DocumentAgentRuntime"/> does
/// with a response: a response-fatal problem discards everything, a claim-local refusal discards only
/// that proposal and is recorded explicitly, and valid siblings are kept. A perfect pack and a partial
/// pack stay distinguishable through <see cref="BindingComplete"/> and the refusal counts - the
/// refusals are never hidden - but a claim-local refusal never makes a response unusable.
/// </summary>
public sealed record V5PackBindingQualification
{
    public const string WirePass = "WIRE_PASS";

    public required bool ResponseUsable { get; init; }

    /// <summary>Why the whole response was discarded, or null when <see cref="ResponseUsable"/>.</summary>
    public string? ResponseFatalReason { get; init; }

    public required int ProposalCount { get; init; }
    public required int BoundCount { get; init; }
    public required int RefusalCount { get; init; }
    public required int OwnershipRefusalCount { get; init; }
    public required int OtherRefusalCount { get; init; }

    /// <summary>Harness refusal key to the binder's exact reason, in proposal order.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Refusals { get; init; }

    /// <summary>Refusal category (the reason up to its first <c>:</c>) to count.</summary>
    public required IReadOnlyDictionary<string, int> RefusalTaxonomy { get; init; }

    /// <summary>A bound claim whose subject, object or predicate differs from its proposal. Must be zero.</summary>
    public required int UnsafeRepairCount { get; init; }

    /// <summary>A bound claim whose subject is not owned by, or whose object is not visible to, this pack. Must be zero.</summary>
    public required int OutOfScopeAcceptedCount { get; init; }

    /// <summary><c>boundCount / proposalCount</c>; 1 for a usable response with no proposals, 0 for a fatal one.</summary>
    public decimal BoundFraction => ProposalCount == 0 ? (ResponseUsable ? 1m : 0m) : (decimal)BoundCount / ProposalCount;

    /// <summary>Every proposal bound. Says nothing about whether the response was processed safely.</summary>
    public bool BindingComplete => ResponseUsable && RefusalCount == 0;

    public bool HasAcceptedClaims => BoundCount > 0;

    /// <summary>
    /// The response was usable, every accepted claim came from <see cref="ExactClaimBinderV2_1"/>
    /// unrepaired and in scope, every refused proposal was excluded, and every refusal is preserved
    /// explicitly. Deliberately does NOT require zero refusals: excluding a bad proposal while keeping
    /// its valid siblings is exactly the safe behavior.
    /// </summary>
    public bool RuntimeProcessedSafely =>
        ResponseUsable &&
        BoundCount + RefusalCount == ProposalCount &&
        Refusals.Count == RefusalCount &&
        UnsafeRepairCount == 0 &&
        OutOfScopeAcceptedCount == 0;

    public string WireStatus => ResponseUsable ? WirePass : nameof(V5PackBindingOutcome.RESPONSE_FATAL);

    public V5PackBindingOutcome Outcome =>
        !ResponseUsable ? V5PackBindingOutcome.RESPONSE_FATAL
        : BoundCount == 0 ? V5PackBindingOutcome.BINDING_EMPTY
        : RefusalCount == 0 ? V5PackBindingOutcome.BINDING_COMPLETE
        : V5PackBindingOutcome.PARTIAL_BINDING;

    /// <summary>
    /// The per-pack metrics frozen before any cohort run, in one serializable shape shared by the
    /// qualification runner and the provider-free replay so the two can be compared byte for byte.
    /// </summary>
    public object ToReport() => new
    {
        wireStatus = WireStatus,
        outcome = Outcome.ToString(),
        responseUsable = ResponseUsable,
        responseFatalReason = ResponseFatalReason,
        proposalCount = ProposalCount,
        boundCount = BoundCount,
        refusalCount = RefusalCount,
        boundFraction = BoundFraction,
        ownershipRefusalCount = OwnershipRefusalCount,
        otherRefusalCount = OtherRefusalCount,
        refusalTaxonomy = RefusalTaxonomy,
        refusalReasons = Refusals.Select(item => new { key = item.Key, reason = item.Value }).ToArray(),
        bindingComplete = BindingComplete,
        hasAcceptedClaims = HasAcceptedClaims,
        runtimeProcessedSafely = RuntimeProcessedSafely,
        unsafeRepairCount = UnsafeRepairCount,
        outOfScopeAcceptedCount = OutOfScopeAcceptedCount,
    };
}

public static class V5BindingQualifier
{
    public const string FinishReasonLength = "length";

    /// <summary>
    /// Qualifies one raw provider response through the exact codec and binder the runtime uses.
    /// Pure: no provider, no Gold, no mutation of <paramref name="rawResponse"/>.
    /// </summary>
    public static (V5PackBindingQualification Qualification, SemanticClaimResponseV2_1? Response, ClaimBindingResultV2_1? Binding) Qualify(
        string? rawResponse,
        string? finishReason,
        string? transportError,
        DocumentTaskContract contract,
        string requestId,
        IReadOnlyList<SemanticSourceAtom> atoms,
        ClaimBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(scope);
        if (rawResponse is null)
            return (Fatal($"transport:{transportError ?? "no-response"}"), null, null);
        if (string.Equals(finishReason, FinishReasonLength, StringComparison.Ordinal))
            return (Fatal("finish-reason-length"), null, null);

        SemanticClaimResponseV2_1 response;
        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            try
            {
                // Parse also runs SemanticClaimContractV2_1.Validate, i.e. vocabulary and arity.
                response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                return (Fatal($"schema:{ex.Message}"), null, null);
            }
        }
        catch (JsonException ex)
        {
            return (Fatal($"json:{ex.Message}"), null, null);
        }

        var binding = ExactClaimBinderV2_1.Bind(requestId, response.Claims, atoms, scope);
        return (FromBinding(response.Claims, binding, scope), response, binding);
    }

    /// <summary>Qualifies an already-decoded, usable response against the binder's result for it.</summary>
    public static V5PackBindingQualification FromBinding(
        IReadOnlyList<SemanticClaimProposalV2_1> proposals,
        ClaimBindingResultV2_1 binding,
        ClaimBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(scope);

        // Walk proposals in order, pairing each non-refused one with the next bound claim: the binder
        // emits bound claims in proposal order and keys every refusal by existingClaimId or ordinal.
        var refusals = new List<KeyValuePair<string, string>>();
        var unsafeRepairs = 0;
        var outOfScope = 0;
        var boundIndex = 0;
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            var key = proposal.ExistingClaimId ?? $"proposal-{index + 1}";
            if (binding.Refusals.TryGetValue(key, out var reason))
            {
                refusals.Add(new(key, reason));
                continue;
            }
            if (boundIndex >= binding.Bound.Count)
            {
                // A proposal that is neither refused nor bound was silently dropped.
                unsafeRepairs++;
                continue;
            }
            var claim = binding.Bound[boundIndex++].Claim;
            if (!IsUnrepaired(proposal, claim)) unsafeRepairs++;
            if (claim.Subject.Parts.Any(part => !scope.OwnedAliases.Contains(part.Alias)) ||
                (claim.Object?.Parts.Any(part => !scope.VisibleAliases.Contains(part.Alias)) ?? false))
                outOfScope++;
        }
        // A bound claim with no proposal behind it was invented.
        unsafeRepairs += binding.Bound.Count - boundIndex;

        var ownership = refusals.Count(item => IsOwnershipRefusal(item.Value));
        return new V5PackBindingQualification
        {
            ResponseUsable = true,
            ProposalCount = proposals.Count,
            BoundCount = binding.Bound.Count,
            RefusalCount = binding.Refusals.Count,
            OwnershipRefusalCount = ownership,
            OtherRefusalCount = binding.Refusals.Count - ownership,
            Refusals = refusals,
            RefusalTaxonomy = refusals
                .GroupBy(item => RefusalCategory(item.Value), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            UnsafeRepairCount = unsafeRepairs,
            OutOfScopeAcceptedCount = outOfScope,
        };
    }

    public static bool IsOwnershipRefusal(string reason) =>
        reason.StartsWith("subject-alias-not-owned", StringComparison.Ordinal) ||
        reason.StartsWith("object-alias-not-visible", StringComparison.Ordinal);

    public static string RefusalCategory(string reason)
    {
        var colon = reason.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? reason : reason[..colon];
    }

    private static V5PackBindingQualification Fatal(string reason) => new()
    {
        ResponseUsable = false,
        ResponseFatalReason = reason,
        ProposalCount = 0,
        BoundCount = 0,
        RefusalCount = 0,
        OwnershipRefusalCount = 0,
        OtherRefusalCount = 0,
        Refusals = [],
        RefusalTaxonomy = new Dictionary<string, int>(StringComparer.Ordinal),
        UnsafeRepairCount = 0,
        OutOfScopeAcceptedCount = 0,
    };

    private static bool IsUnrepaired(SemanticClaimProposalV2_1 proposal, BoundSemanticClaim claim) =>
        string.Equals(proposal.Predicate, claim.Predicate, StringComparison.Ordinal) &&
        string.Equals(proposal.Value, claim.Value, StringComparison.Ordinal) &&
        proposal.State == claim.State &&
        SameAliases(proposal.Subject?.SourceParts, claim.Subject) &&
        (proposal.Object is null ? claim.Object is null : claim.Object is not null && SameAliases(proposal.Object.SourceParts, claim.Object));

    private static bool SameAliases(IReadOnlyList<ProviderSourcePartV2_1>? proposed, BoundClaimEndpoint bound) =>
        (proposed ?? []).Select(part => part.SourceAlias).SequenceEqual(bound.Parts.Select(part => part.Alias), StringComparer.Ordinal);
}

/// <summary>
/// The frozen cohort metrics for a qualification run. Measurement only - it carries no refusal
/// threshold and no pass/fail, so a first cohort run cannot be read as a promotion decision.
/// </summary>
public sealed record V5QualificationAggregate(
    int ProviderCalls,
    int UsableResponses,
    int TransportFailures,
    int FinishReasonLength,
    int JsonInvalidResponses,
    int SchemaInvalidResponses,
    int TotalProposals,
    int TotalBound,
    int TotalRefused,
    decimal BoundFraction,
    int OwnershipRefusals,
    int OtherBindingRefusals,
    int PacksBindingComplete,
    int PacksPartialBinding,
    int PacksBindingEmpty,
    int PacksRuntimeProcessedSafely,
    int UnsafeRepairs,
    int OutOfScopeClaimsAccepted)
{
    public static V5QualificationAggregate From(IReadOnlyList<V5PackBindingQualification> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        var proposals = packs.Sum(pack => pack.ProposalCount);
        var bound = packs.Sum(pack => pack.BoundCount);
        int FatalCount(string prefix) => packs.Count(pack =>
            pack.ResponseFatalReason?.StartsWith(prefix, StringComparison.Ordinal) == true);
        return new V5QualificationAggregate(
            ProviderCalls: packs.Count,
            UsableResponses: packs.Count(pack => pack.ResponseUsable),
            TransportFailures: FatalCount("transport:"),
            FinishReasonLength: FatalCount("finish-reason-length"),
            JsonInvalidResponses: FatalCount("json:"),
            SchemaInvalidResponses: FatalCount("schema:"),
            TotalProposals: proposals,
            TotalBound: bound,
            TotalRefused: packs.Sum(pack => pack.RefusalCount),
            BoundFraction: proposals == 0 ? 0m : (decimal)bound / proposals,
            OwnershipRefusals: packs.Sum(pack => pack.OwnershipRefusalCount),
            OtherBindingRefusals: packs.Sum(pack => pack.OtherRefusalCount),
            PacksBindingComplete: packs.Count(pack => pack.Outcome == V5PackBindingOutcome.BINDING_COMPLETE),
            PacksPartialBinding: packs.Count(pack => pack.Outcome == V5PackBindingOutcome.PARTIAL_BINDING),
            PacksBindingEmpty: packs.Count(pack => pack.Outcome == V5PackBindingOutcome.BINDING_EMPTY),
            PacksRuntimeProcessedSafely: packs.Count(pack => pack.RuntimeProcessedSafely),
            UnsafeRepairs: packs.Sum(pack => pack.UnsafeRepairCount),
            OutOfScopeClaimsAccepted: packs.Sum(pack => pack.OutOfScopeAcceptedCount));
    }
}
