using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Production tests for the claim-quarantine failure-containment path added on top of
/// <see cref="V5ClaimLocalIndependenceAuditTests"/>'s findings: <see cref="IndexedSemanticClaimProposalV2_1"/>,
/// the indexed <see cref="ExactClaimBinderV2_1.Bind"/> overload, <see cref="SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine"/>
/// and <see cref="V5ClaimQuarantineQualifier"/>. Never calls a provider, never reads Gold, never
/// changes <see cref="SemanticClaimContractV2_1"/>, <see cref="SemanticClaimResponseCodecV2_1.Parse"/>
/// or <see cref="V5BindingQualifier"/> - the existing all-or-nothing path stays the runtime default.
/// </summary>
public sealed class V5ClaimQuarantineV1Tests
{
    private static readonly DocumentTaskContract Contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();

    // ---- Mandated ordinal-identity regression test -------------------------------------------

    /// <summary>
    /// The general invariant, proven directly against the real production API (not a JSON
    /// per-claim workaround): raw #0 valid, #1 invalid, #2 valid. After quarantine, claim #2's
    /// durable identity must be computed from its ORIGINAL raw ordinal (2), and that identity must
    /// stay byte-identical in a second scenario that also excludes #0 - simulating an unrelated
    /// future change to which siblings survive. A naive implementation that fed the compacted
    /// eligible array into the OLD, unindexed <see cref="ExactClaimBinderV2_1.Bind"/> overload would
    /// get both of these wrong, exactly as <see cref="V5ClaimLocalIndependenceAuditTests"/> found.
    /// </summary>
    [Fact]
    public void Claim_2_keeps_its_original_ordinal_identity_across_an_unrelated_upstream_exclusion()
    {
        var atoms = new[] { new SemanticSourceAtom("A1", "S1", 0, 1, 1, 1, "Hello World") };
        var scope = ClaimBindingScope.Create(["A1"], ["A1"]);
        const string requestId = "regression-ordinal-v1";

        string Claim(string predicate, string? value, bool withObject) =>
            $$"""
            {"subject":{"sourceParts":[{"sourceAlias":"A1"}]},"predicate":"{{predicate}}","value":{{(value is null ? "null" : $"\"{value}\"")}},"object":{{(withObject ? """{"sourceParts":[{"sourceAlias":"A1"}]}""" : "null")}},"state":"RESOLVED","evidenceNeeds":[]}
            """;

        var rawJson = $$"""
        {"claims":[
            {{Claim("DOCUMENT_IDENTITY", "v0", withObject: false)}},
            {{Claim("REFERENCES", "bad-relation-value", withObject: true)}},
            {{Claim("DOCUMENT_IDENTITY", "v2", withObject: false)}}
        ]}
        """;

        var quarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(JsonDocument.Parse(rawJson).RootElement, Contract);
        Assert.Equal(3, quarantine.RawClaimCount);
        Assert.Equal([1], quarantine.ContractRefusals.Keys.Order());
        Assert.Contains("relation-has-value", quarantine.ContractRefusals[1], StringComparison.Ordinal);
        Assert.Equal([0, 2], quarantine.Eligible.Select(e => e.OriginalOrdinal).Order());

        // Scenario 1: quarantine excludes only #1.
        var scenario1Binding = ExactClaimBinderV2_1.Bind(requestId, quarantine.Eligible, atoms, scope);
        Assert.Equal(2, scenario1Binding.Bound.Count);
        var scenario1Claim2 = scenario1Binding.Bound.Single(b => b.Claim.Predicate == "DOCUMENT_IDENTITY" && b.Claim.Value == "v2");
        var expectedClaim2Id = HarnessClaimIdentityV2_1.Create(requestId, 2, scenario1Claim2.Claim.Subject, "DOCUMENT_IDENTITY");
        Assert.Equal(expectedClaim2Id, scenario1Claim2.Claim.ClaimId);

        // Scenario 2: an unrelated upstream change also excludes #0 (still valid on its own -
        // simulating, say, a later contract tightening that would have quarantined it too).
        // #2's original ordinal is untouched by this, so its identity must not move.
        var scenario2Eligible = quarantine.Eligible.Where(e => e.OriginalOrdinal != 0).ToArray();
        Assert.Equal([2], scenario2Eligible.Select(e => e.OriginalOrdinal));
        var scenario2Binding = ExactClaimBinderV2_1.Bind(requestId, scenario2Eligible, atoms, scope);
        Assert.Equal(1, scenario2Binding.Bound.Count);
        var scenario2Claim2 = scenario2Binding.Bound.Single();
        Assert.Equal(scenario1Claim2.Claim.ClaimId, scenario2Claim2.Claim.ClaimId);
        Assert.Equal(expectedClaim2Id, scenario2Claim2.Claim.ClaimId);

        // The naive/compacted alternative this fixes: feeding the SAME two eligible batches, minus
        // their ordinal provenance, into the OLD unindexed overload derives ordinal from array
        // position - unstable across exactly this kind of unrelated upstream exclusion.
        var naiveScenario1 = ExactClaimBinderV2_1.Bind(requestId, quarantine.Eligible.Select(e => e.Proposal).ToArray(), atoms, scope);
        var naiveScenario2 = ExactClaimBinderV2_1.Bind(requestId, scenario2Eligible.Select(e => e.Proposal).ToArray(), atoms, scope);
        var naiveClaim2Scenario1 = naiveScenario1.Bound.Single(b => b.Claim.Value == "v2").Claim.ClaimId; // naive ordinal 1 (array position)
        var naiveClaim2Scenario2 = naiveScenario2.Bound.Single(b => b.Claim.Value == "v2").Claim.ClaimId; // naive ordinal 0 (array position)
        Assert.NotEqual(naiveClaim2Scenario1, naiveClaim2Scenario2); // UNSTABLE under the naive/compacted approach
        Assert.NotEqual(expectedClaim2Id, naiveClaim2Scenario1);
        Assert.NotEqual(expectedClaim2Id, naiveClaim2Scenario2);
    }

    // ---- PACK_006-specific report test --------------------------------------------------------

    private const string RemediationCanaryPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-result.v1.json";
    private const string CohortPath = "artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json";
    private const string PromotionGateOutPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/claim-quarantine-promotion-gate.v1.json";

    [Fact]
    public void PACK_006_reproduces_exactly_35_raw_6_contract_refused_29_eligible_29_bound_0_binding_refused()
    {
        var (packId, atoms, scope, raw) = LoadRemediationPack("same-atom-normalization-heavy");
        var (qualification, quarantine, binding) = V5ClaimQuarantineQualifier.Qualify(raw, "stop", null, Contract, packId, atoms, scope);

        Assert.Equal(35, qualification.RawClaims);
        Assert.Equal(6, qualification.ContractRefused);
        Assert.Equal(29, qualification.BinderEligible);
        Assert.Equal(29, qualification.BoundCount);
        Assert.Equal(0, qualification.BindingRefusedCount);
        Assert.Equal(6, qualification.TotalRefused);
        Assert.Equal(29m / 35m, qualification.BoundFraction);
        Assert.True(qualification.RuntimeProcessedSafely);
        Assert.True(qualification.ResponseUsable);
        Assert.Equal(V5PackBindingQualification.WirePass, qualification.WireStatus);

        Assert.Equal(35, quarantine!.RawClaimCount);
        Assert.Equal(29, quarantine.Eligible.Count);
        Assert.Equal(6, quarantine.ContractRefusals.Count);
        Assert.All(quarantine.ContractRefusals.Values, reason => Assert.Contains("relation-has-value", reason, StringComparison.Ordinal));
        Assert.Equal(29, binding!.Bound.Count);
        Assert.Empty(binding.Refusals);
    }

    // ---- Full promotion-gate replay -----------------------------------------------------------

    /// <summary>
    /// Replays every frozen V2.1 artifact provider-free through both qualifiers and proves the
    /// promotion gate: for every response that was previously WIRE_PASS under the existing
    /// all-or-nothing path, quarantine's accepted claim IDs, binder refusals and bound-fraction
    /// denominator are byte-identical (IdentityDriftCount=0) - it changes nothing about an
    /// already-usable response, it only recovers claims from a previously RESPONSE_FATAL one
    /// (PACK_006). UnsafeRepairCount, OutOfScopeAccepted and SilentDropCount are all zero
    /// throughout. Never calls a provider, never reads Gold, never mutates a frozen artifact.
    /// </summary>
    [Fact]
    public void Promotion_gate_replay_proves_zero_identity_drift_and_previously_wire_pass_equivalence()
    {
        var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CohortPath))).RootElement;
        var packRows = cohort.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => (row.GetProperty("DocumentId").GetString()!, row.GetProperty("PackId").GetString()!), row => row);
        var atomsByDoc = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        IReadOnlyList<SemanticSourceAtom> AtomsFor(string documentId)
        {
            if (!atomsByDoc.TryGetValue(documentId, out var atoms))
            {
                var pdf = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                atomsByDoc[documentId] = atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf));
            }
            return atoms;
        }
        ClaimBindingScope ScopeFor(string documentId, string packId)
        {
            var row = packRows[(documentId, packId)];
            var owned = row.GetProperty("ownedAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var visible = row.GetProperty("visibleAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            return ClaimBindingScope.Create(owned, visible);
        }

        var perResponse = new List<object>();
        var identityDriftCount = 0;
        var unsafeRepairCount = 0;
        var outOfScopeAcceptedCount = 0;
        var silentDropCount = 0;
        var recoveredCount = 0;

        void Replay(string documentId, string packId, string rawResponse, string? finishReason, bool wasPreviouslyWirePass)
        {
            var atoms = AtomsFor(documentId);
            var scope = ScopeFor(documentId, packId);

            var (oldQualification, _, oldBinding) = V5BindingQualifier.Qualify(rawResponse, finishReason, null, Contract, packId, atoms, scope);
            Assert.Equal(wasPreviouslyWirePass, oldQualification.ResponseUsable);

            var (newQualification, quarantine, newBinding) = V5ClaimQuarantineQualifier.Qualify(rawResponse, finishReason, null, Contract, packId, atoms, scope);
            Assert.True(newQualification.ResponseUsable, $"{documentId}:{packId} must remain a usable response under quarantine");
            Assert.True(newQualification.RuntimeProcessedSafely, $"{documentId}:{packId} silent-drop check failed");
            if (!newQualification.RuntimeProcessedSafely) silentDropCount++;

            // Reusable exactly as-is: for every response here, either ContractRefused==0 (so each
            // eligible claim's original ordinal equals its compacted position, matching FromBinding's
            // own index-based refusal-key convention), or BindingRefusedCount==0 (PACK_006 - so no
            // refusal key is ever looked up at all). Either way FromBinding's proposal-order pairing
            // against the eligible-only proposal list is exactly correct here.
            var eligibleProposals = quarantine!.Eligible.Select(e => e.Proposal).ToArray();
            var newOverEligible = V5BindingQualifier.FromBinding(eligibleProposals, newBinding!, scope);
            unsafeRepairCount += newOverEligible.UnsafeRepairCount;
            outOfScopeAcceptedCount += newOverEligible.OutOfScopeAcceptedCount;

            bool drifted;
            if (wasPreviouslyWirePass)
            {
                Assert.Equal(0, newQualification.ContractRefused);
                Assert.Equal(oldQualification.ProposalCount, newQualification.BinderEligible);
                var oldIds = oldBinding!.Bound.Select(b => b.Claim.ClaimId).ToArray();
                var newIds = newBinding!.Bound.Select(b => b.Claim.ClaimId).ToArray();
                var idsMatch = oldIds.SequenceEqual(newIds, StringComparer.Ordinal);
                var refusalsMatch = JsonSerializer.Serialize(oldBinding.Refusals) == JsonSerializer.Serialize(newBinding.Refusals);
                drifted = !idsMatch || !refusalsMatch;
                Assert.True(idsMatch, $"{documentId}:{packId} bound claim IDs drifted under quarantine");
                Assert.True(refusalsMatch, $"{documentId}:{packId} binder refusals drifted under quarantine");
            }
            else
            {
                drifted = false; // no prior WIRE_PASS state to preserve - this is the recovery case
                recoveredCount++;
            }
            if (drifted) identityDriftCount++;

            perResponse.Add(new
            {
                documentId,
                packId,
                previouslyWirePass = wasPreviouslyWirePass,
                oldWireStatus = oldQualification.WireStatus,
                newReport = newQualification.ToReport(),
                identityDrifted = drifted,
            });
        }

        // The original 31-pack cohort - every one previously WIRE_PASS (proven by
        // V5ExactTextBindingAuditTests, which calls the unmodified aggregate Parse on all 31 raw
        // responses unconditionally and passes).
        var callDirs = Directory.GetDirectories(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/provider/calls"))
            .OrderBy(d => d, StringComparer.Ordinal).ToArray();
        Assert.Equal(31, callDirs.Length);
        foreach (var callDir in callDirs)
        {
            var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDir, "call.v1.json"))).RootElement;
            var documentId = call.GetProperty("documentId").GetString()!;
            var packId = call.GetProperty("packId").GetString()!;
            var finishReason = call.GetProperty("finishReason").GetString();
            var raw = File.ReadAllText(Path.Combine(callDir, "content.txt"));
            Replay(documentId, packId, raw, finishReason, wasPreviouslyWirePass: true);
        }

        // The 3-pack source-selection remediation canary: PACK_001 and PACK_022 were previously
        // WIRE_PASS; PACK_006 was previously RESPONSE_FATAL (the exact case quarantine recovers).
        var remediation = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RemediationCanaryPath))).RootElement;
        foreach (var result in remediation.GetProperty("results").EnumerateArray())
        {
            var documentId = result.GetProperty("documentId").GetString()!;
            var packId = result.GetProperty("packId").GetString()!;
            var wasWirePass = result.GetProperty("qualification").GetProperty("wireStatus").GetString() == V5PackBindingQualification.WirePass;
            Replay(documentId, packId, result.GetProperty("rawResponse").GetString()!, "stop", wasWirePass);
        }

        Assert.Equal(34, perResponse.Count);
        Assert.Equal(0, identityDriftCount);
        Assert.Equal(0, unsafeRepairCount);
        Assert.Equal(0, outOfScopeAcceptedCount);
        Assert.Equal(0, silentDropCount);
        Assert.Equal(1, recoveredCount); // PACK_006 alone

        var path = TestRepository.Path(PromotionGateOutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "v5-claim-quarantine-promotion-gate-v1",
                subject = "all 31 original 31-pack cohort responses (v5-provider-cohort-31-windows) plus the 3-pack source-selection remediation canary",
                gate = new
                {
                    responsesReplayed = perResponse.Count,
                    identityDriftCount,
                    unsafeRepairCount,
                    outOfScopeAcceptedCount,
                    silentDropCount,
                    recoveredFromPreviouslyResponseFatal = recoveredCount,
                    promotionCriteriaMet = identityDriftCount == 0 && unsafeRepairCount == 0 && outOfScopeAcceptedCount == 0 && silentDropCount == 0,
                },
                responses = perResponse,
                providerCalls = 0,
                goldRead = false,
            }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static (string PackId, IReadOnlyList<SemanticSourceAtom> Atoms, ClaimBindingScope Scope, string RawResponse) LoadRemediationPack(string role)
    {
        var sourceResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(RemediationCanaryPath))).RootElement;
        var pack = sourceResult.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("role").GetString() == role);
        var documentId = pack.GetProperty("documentId").GetString()!;
        var packId = pack.GetProperty("packId").GetString()!;

        var cohort = JsonNode.Parse(File.ReadAllText(TestRepository.Path(CohortPath)))!;
        var row = cohort["rows"]!.AsArray().Single(r => r!["DocumentId"]!.GetValue<string>() == documentId && r["PackId"]!.GetValue<string>() == packId)!;
        var owned = row["ownedAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        var visible = row["visibleAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        var scope = ClaimBindingScope.Create(owned, visible);

        var pdf = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
        var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf));
        return (packId, atoms, scope, pack.GetProperty("rawResponse").GetString()!);
    }
}
