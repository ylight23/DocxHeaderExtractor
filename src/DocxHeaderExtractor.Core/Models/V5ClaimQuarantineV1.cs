using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Per-pack qualification for the claim-quarantine runtime path: a claim-CONTAINED contract defect
/// (<c>relation-has-value</c>, an unknown predicate, a malformed source part, ...) is isolated to that
/// one claim - CONTRACT_REFUSED - while every structurally-independent sibling proceeds through the
/// unmodified <see cref="ExactClaimBinderV2_1"/>, which may separately refuse it on entirely different
/// (ownership, exact-text-binding, duplicate identity, ...) grounds - BINDING_REFUSED. The two layers
/// are counted and reported separately so neither ever hides the other, and a response is never
/// reported as if every surviving claim's binding refusal were somehow the same kind of failure as a
/// sibling's contract refusal.
/// <para>
/// Only a root-level malformation remains RESPONSE_FATAL - non-JSON, a non-object payload, an unknown
/// top-level field, a missing or non-array <c>claims</c> - exactly the same fatal set
/// <see cref="V5BindingQualifier"/> already uses for those cases. A claim-contained defect can never
/// reach this layer: <see cref="SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine"/> isolates it
/// before qualification ever sees it.
/// </para>
/// </summary>
public sealed record V5QuarantineQualification
{
    public required bool ResponseUsable { get; init; }

    /// <summary>Why the whole response was discarded (root-level only), or null when <see cref="ResponseUsable"/>.</summary>
    public string? ResponseFatalReason { get; init; }

    public required int RawClaims { get; init; }
    public required int ContractRefused { get; init; }
    public required int BinderEligible { get; init; }
    public required int BoundCount { get; init; }
    public required int BindingRefusedCount { get; init; }

    /// <summary>Original raw ordinal to the exact per-claim reason <see cref="SemanticClaimResponseCodecV2_1.Parse"/> would have thrown for that claim alone.</summary>
    public required IReadOnlyDictionary<int, string> ContractRefusals { get; init; }

    /// <summary>Harness refusal key to the binder's exact reason - identical shape to <see cref="V5PackBindingQualification.Refusals"/>.</summary>
    public required IReadOnlyDictionary<string, string> BindingRefusals { get; init; }

    public int TotalRefused => ContractRefused + BindingRefusedCount;

    /// <summary><c>boundCount / rawClaims</c> - the denominator is every raw claim, not just the ones eligible for binding, so quarantine can never inflate its own success rate by shrinking its own denominator.</summary>
    public decimal BoundFraction => RawClaims == 0 ? (ResponseUsable ? 1m : 0m) : (decimal)BoundCount / RawClaims;

    public string WireStatus => ResponseUsable ? V5PackBindingQualification.WirePass : nameof(V5PackBindingOutcome.RESPONSE_FATAL);

    /// <summary>
    /// The response was usable; every raw claim is accounted for exactly once
    /// (<c>contractRefused + binderEligible == rawClaims</c>, <c>bound + bindingRefused == binderEligible</c>);
    /// every contract refusal and every binding refusal is preserved explicitly, none silently dropped.
    /// Deliberately does NOT require zero refusals of either kind - excluding a bad claim while keeping
    /// its valid siblings, at either layer, is exactly the safe behavior.
    /// </summary>
    public bool RuntimeProcessedSafely =>
        ResponseUsable &&
        ContractRefused + BinderEligible == RawClaims &&
        BoundCount + BindingRefusedCount == BinderEligible &&
        ContractRefusals.Count == ContractRefused &&
        BindingRefusals.Count == BindingRefusedCount;

    public object ToReport() => new
    {
        wireStatus = WireStatus,
        responseUsable = ResponseUsable,
        responseFatalReason = ResponseFatalReason,
        rawClaims = RawClaims,
        contractRefused = ContractRefused,
        binderEligible = BinderEligible,
        boundCount = BoundCount,
        bindingRefusedCount = BindingRefusedCount,
        totalRefused = TotalRefused,
        boundFraction = BoundFraction,
        runtimeProcessedSafely = RuntimeProcessedSafely,
        contractRefusals = ContractRefusals.OrderBy(pair => pair.Key)
            .Select(pair => new { originalOrdinal = pair.Key, reason = pair.Value }).ToArray(),
        bindingRefusals = BindingRefusals.Select(pair => new { key = pair.Key, reason = pair.Value }).ToArray(),
    };
}

/// <summary>
/// Qualifies one raw provider response through <see cref="SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine"/>
/// and the indexed <see cref="ExactClaimBinderV2_1.Bind"/> overload - the claim-quarantine failure-
/// containment path, kept entirely separate from <see cref="V5BindingQualifier"/> (the existing
/// all-or-nothing path, unchanged, still the runtime default). Pure: no provider, no Gold, no mutation
/// of <paramref name="rawResponse"/> or of any claim within it.
/// </summary>
public static class V5ClaimQuarantineQualifier
{
    public static (V5QuarantineQualification Qualification, ClaimQuarantineResultV2_1? Quarantine, ClaimBindingResultV2_1? Binding) Qualify(
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
        if (string.Equals(finishReason, V5BindingQualifier.FinishReasonLength, StringComparison.Ordinal))
            return (Fatal("finish-reason-length"), null, null);

        ClaimQuarantineResultV2_1 quarantine;
        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            try
            {
                // Throws only for a root-level malformation - a claim-contained defect is isolated
                // inside this call and never surfaces as an exception here.
                quarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(document.RootElement, contract);
            }
            catch (InvalidOperationException ex)
            {
                return (Fatal($"schema:{ex.Message}"), null, null);
            }
        }
        catch (JsonException ex)
        {
            return (Fatal($"json:{ex.Message}"), null, null);
        }

        var binding = ExactClaimBinderV2_1.Bind(requestId, quarantine.Eligible, atoms, scope);
        var qualification = new V5QuarantineQualification
        {
            ResponseUsable = true,
            RawClaims = quarantine.RawClaimCount,
            ContractRefused = quarantine.ContractRefusals.Count,
            BinderEligible = quarantine.Eligible.Count,
            BoundCount = binding.Bound.Count,
            BindingRefusedCount = binding.Refusals.Count,
            ContractRefusals = quarantine.ContractRefusals,
            BindingRefusals = binding.Refusals,
        };
        return (qualification, quarantine, binding);
    }

    private static V5QuarantineQualification Fatal(string reason) => new()
    {
        ResponseUsable = false,
        ResponseFatalReason = reason,
        RawClaims = 0,
        ContractRefused = 0,
        BinderEligible = 0,
        BoundCount = 0,
        BindingRefusedCount = 0,
        ContractRefusals = new Dictionary<int, string>(),
        BindingRefusals = new Dictionary<string, string>(StringComparer.Ordinal),
    };
}
