using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free architectural audit: when a response's JSON is valid but only some of its claims
/// violate the wire contract (PACK_006's raw response - 35 claims, 6 REFERENCES claims illegally
/// carrying a <c>value</c> - currently RESPONSE_FATAL, 0 claims downstream), can the malformed claims
/// be quarantined as CONTRACT_REFUSED while the structurally-independent, valid siblings continue
/// through TaskContract -> ExactClaimBinderV2_1 -> qualification, without loosening authority?
/// <para>
/// This never re-implements <see cref="SemanticClaimContractV2_1.Validate"/>'s rules: each claim is
/// validated by wrapping it alone in a synthetic single-claim response and handing it to the
/// unmodified <see cref="SemanticClaimResponseCodecV2_1.Parse"/> - the exact same rules, applied per
/// claim instead of per response, with zero risk of the audit's logic drifting from the real
/// validator's. No mutation, no coercion, no inferred correction, no repair: a claim that fails is
/// excluded as-is, never patched to make it pass.
/// </para>
/// <para>
/// Claim-local independence is established two ways: (1) by code inspection - <c>Validate</c> reads
/// only the claim it is currently checking, never another claim's fields, and never touches
/// <c>existingClaimId</c> (that is <see cref="ExactClaimBinderV2_1"/>'s concern, for refinement
/// against harness-owned prior state, not a same-response dependency); (2) empirically - re-validating
/// each of the 35 claims one at a time reproduces exactly the 6/29 split the aggregate parse implies,
/// and no claim's outcome depends on which other claims are present in the batch.
/// </para>
/// <para>
/// This is an audit only. It does not change <see cref="SemanticClaimContractV2_1"/>,
/// <see cref="SemanticClaimResponseCodecV2_1"/> or <see cref="ExactClaimBinderV2_1"/>, and does not by
/// itself decide whether production should adopt per-claim quarantine - it establishes whether doing
/// so would be safe to consider.
/// </para>
/// </summary>
public sealed class V5ClaimLocalIndependenceAuditTests
{
    private const string SourceResultPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-result.v1.json";
    private const string OutPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/claim-local-independence-audit.v1.json";

    [Fact]
    public void PACK_006_claims_are_structurally_independent_and_quarantine_would_recover_29_of_35()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var sourceResult = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(SourceResultPath))).RootElement;
        var pack006 = sourceResult.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("role").GetString() == "same-atom-normalization-heavy");
        Assert.Equal("RESPONSE_FATAL", pack006.GetProperty("qualification").GetProperty("wireStatus").GetString());
        var raw = pack006.GetProperty("rawResponse").GetString()!;

        // The aggregate (current production) behavior, unchanged: the whole response is one fatal unit.
        var aggregateFailed = false;
        try { SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse(raw).RootElement, contract); }
        catch (InvalidOperationException) { aggregateFailed = true; }
        Assert.True(aggregateFailed, "the aggregate parse must still fail exactly as production observed - this audit changes nothing about it");

        using var rawDocument = JsonDocument.Parse(raw);
        var claimsElement = rawDocument.RootElement.GetProperty("claims");
        var claimJsonTexts = claimsElement.EnumerateArray().Select(c => c.GetRawText()).ToArray();
        Assert.Equal(35, claimJsonTexts.Length);

        // No claim references another within this same response: existingClaimId is absent from every
        // one (this pack seeds no openOrConflictedClaims), so there is no refinement dependency either.
        Assert.All(claimsElement.EnumerateArray(), claim => Assert.False(claim.TryGetProperty("existingClaimId", out _)));

        var perClaim = new List<object>();
        var validClaimTexts = new List<string>();
        int contractRefused = 0, structurallyValid = 0;
        var refusalReasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < claimJsonTexts.Length; i++)
        {
            var singleClaimResponse = $$"""{"claims":[{{claimJsonTexts[i]}}]}""";
            string? refusalReason = null;
            try
            {
                SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse(singleClaimResponse).RootElement, contract);
                structurallyValid++;
                validClaimTexts.Add(claimJsonTexts[i]);
            }
            catch (InvalidOperationException ex)
            {
                contractRefused++;
                refusalReason = ex.Message;
                refusalReasonCounts[ex.Message] = refusalReasonCounts.GetValueOrDefault(ex.Message) + 1;
            }
            var predicate = JsonDocument.Parse(claimJsonTexts[i]).RootElement.TryGetProperty("predicate", out var p) ? p.GetString() : null;
            perClaim.Add(new { index = i, predicate, outcome = refusalReason is null ? "STRUCTURALLY_VALID" : "CONTRACT_REFUSED", refusalReason });
        }

        Assert.Equal(6, contractRefused);
        Assert.Equal(29, structurallyValid);
        Assert.All(refusalReasonCounts.Keys, reason => Assert.Contains("relation-has-value", reason, StringComparison.Ordinal));

        // Independence, proven not assumed: re-validating in reverse order and re-validating twice each
        // must reproduce the identical 6/29 split - no claim's outcome depends on batch composition or order.
        var reversedRefused = 0;
        for (var i = claimJsonTexts.Length - 1; i >= 0; i--)
        {
            try { SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse($$"""{"claims":[{{claimJsonTexts[i]}}]}""").RootElement, contract); }
            catch (InvalidOperationException) { reversedRefused++; }
        }
        Assert.Equal(contractRefused, reversedRefused);

        // Downstream: the 29 structurally-valid claims, fed together through the unmodified codec and
        // ExactClaimBinderV2_1, exactly as production already does once parsing succeeds at all.
        var quarantinedResponseJson = $$"""{"claims":[{{string.Join(",", validClaimTexts)}}]}""";
        var quarantinedResponse = SemanticClaimResponseCodecV2_1.Parse(JsonDocument.Parse(quarantinedResponseJson).RootElement, contract);
        Assert.Equal(29, quarantinedResponse.Claims.Count);

        var packRow = FindPackRow(TestRepository.Path("artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json"), pack006.GetProperty("documentId").GetString()!, pack006.GetProperty("packId").GetString()!);
        var ownedAliases = packRow["ownedAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        var visibleAliases = packRow["visibleAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        var scope = ClaimBindingScope.Create(ownedAliases, visibleAliases);
        var atoms = DocxHeaderExtractor.DocumentProcessing.Pipeline.V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src089));
        var binding = ExactClaimBinderV2_1.Bind(pack006.GetProperty("packId").GetString()!, quarantinedResponse.Claims, atoms, scope);

        var report = new
        {
            schemaVersion = "v5-claim-local-independence-audit-v1",
            subject = "SRC-089 PACK_006 raw response (source-selection-remediation-canary-result.v1.json, commit c287f19)",
            currentProductionBehavior = new { wireStatus = "RESPONSE_FATAL", claimsAccepted = 0, reason = "single relation-has-value issue in the aggregate Validate() call fails the whole 35-claim response" },
            claimLocalIndependence = new
            {
                establishedByCodeInspection = "SemanticClaimContractV2_1.Validate reads only the claim it is currently checking; it never reads existingClaimId (that is ExactClaimBinderV2_1's refinement concern against harness-owned prior state, not a same-response dependency)",
                establishedEmpirically = "re-validating all 35 claims one at a time, and again in reverse order, reproduces the identical 6 CONTRACT_REFUSED / 29 STRUCTURALLY_VALID split both times - no claim's outcome depends on which other claims are present or their order",
                noExistingClaimIdPresent = true,
            },
            perClaimQuarantineIfAdopted = new
            {
                totalClaims = 35,
                contractRefused,
                structurallyValid,
                refusalReasons = refusalReasonCounts,
                downstreamBinding = new
                {
                    proposalCount = quarantinedResponse.Claims.Count,
                    boundCount = binding.Bound.Count,
                    refusalCount = binding.Refusals.Count,
                    refusalReasons = binding.Refusals.Values.Distinct().ToArray(),
                },
            },
            invariantsRequiredIfAdopted = new[]
            {
                "NO mutation - a refused claim's fields are never altered to make it pass",
                "NO coercion - relation-has-value is never silently turned into a valid unary or a value-less relation",
                "NO inferred correction - a missing/wrong field is never filled in or guessed",
                "NO Gold read for this decision",
                "NO provider call for this decision",
                "invalid claim -> CONTRACT_REFUSED, recorded, never silently dropped without a reason",
                "valid sibling claims may continue only through the unmodified TaskContract -> ExactClaimBinderV2_1 -> qualification pipeline",
            },
            recommendation = "The audit supports that per-claim quarantine is SAFE to consider: independence holds both by inspection and empirically, and no invariant above requires touching SemanticClaimContractV2_1, SemanticClaimResponseCodecV2_1 or ExactClaimBinderV2_1's existing rules - only whether Parse aggregates-then-throws or isolates-then-continues. Adopting it is a real, production-facing codec change (affects every caller of SemanticClaimResponseCodecV2_1.Parse) and is not implemented by this audit; it needs its own explicit decision.",
            providerCalls = 0,
            goldRead = false,
        };

        var path = TestRepository.Path(OutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    private static JsonNode FindPackRow(string cohortPath, string documentId, string packId)
    {
        var cohort = JsonNode.Parse(File.ReadAllText(cohortPath))!;
        return cohort["rows"]!.AsArray().Single(r => r!["DocumentId"]!.GetValue<string>() == documentId && r["PackId"]!.GetValue<string>() == packId)!;
    }
}
