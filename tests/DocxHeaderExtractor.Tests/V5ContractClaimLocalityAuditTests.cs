using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using SemanticSourceAtom = DocxHeaderExtractor.Core.Models.SemanticSourceAtom;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free AUDIT (not a behaviour change) of one architectural question: when a response is
/// valid JSON but only SOME claims break the TaskContract arity rules (e.g. <c>relation-has-value</c>),
/// is the response-fatal discard the only safe answer, or can exactly the offending claims be refused
/// while their independent siblings continue? The subject is the one frozen real response that hit it:
/// SRC-089 PACK_006, 35 claims, 6 relation-has-value, currently RESPONSE_FATAL with 0 claims downstream.
/// Invariants: no mutation, no coercion, no inferred correction, no Gold, no provider call. The audit
/// only reads the frozen raw response and never feeds anything into a runtime path.
/// </summary>
public sealed class V5ContractClaimLocalityAuditTests
{
    private const string ResultPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-result.v1.json";
    private const string CohortPath = "artifacts/v5-provider-cohort-31-windows/preflight/cohort.v1.json";
    private const string OutPath = "artifacts/v5-contract-claim-locality-audit/audit.v1.json";
    private const string RawSha256 = "7e04be99b2540ccfc0910ec8cf7d212bdff986ea2995694481c249cf3fe8a22f";

    private static readonly DocumentTaskContract Contract = DocumentStructureTaskContract.Create();

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string LoadRaw()
    {
        var results = JsonNode.Parse(File.ReadAllText(TestRepository.Path(ResultPath)))!["results"]!.AsArray();
        var raw = results.Select(item => item!["rawResponse"]!.GetValue<string>()).Single(text => Sha(text) == RawSha256);
        Assert.Equal(RawSha256, Sha(raw));
        return raw;
    }

    /// <summary>Audit-only lenient decode: the codec's own options, minus the contract check that made it fatal.</summary>
    private static SemanticClaimResponseV2_1 LenientDecode(string json)
    {
        var options = new JsonSerializerOptions(CanonicalJson.Options) { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<SemanticClaimResponseV2_1>(json, options)!;
    }

    [Fact]
    public void Contract_validation_is_claim_local_so_only_the_offending_claims_are_implicated()
    {
        var raw = LoadRaw();

        // 1. The current behaviour: strict parse fails the WHOLE response on exactly six issues.
        using var document = JsonDocument.Parse(raw);
        var fatal = Assert.Throws<InvalidOperationException>(() => SemanticClaimResponseCodecV2_1.Parse(document.RootElement, Contract));
        Assert.Equal("relation-has-value,relation-has-value,relation-has-value,relation-has-value,relation-has-value,relation-has-value", fatal.Message);

        // 2. Same claims, decoded without the contract gate.
        var response = LenientDecode(raw);
        Assert.Equal(35, response.Claims.Count);

        // 3. Claim-local independence: validating each claim ALONE yields, concatenated in order,
        //    exactly the issues of validating the whole response. No issue depends on a sibling.
        var perClaim = response.Claims.Select(claim => SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([claim]), Contract)).ToArray();
        var whole = SemanticClaimContractV2_1.Validate(response, Contract);
        Assert.Equal(whole, perClaim.SelectMany(issues => issues).ToArray());

        // 4. Reversing the claim order changes nothing either (no positional dependence).
        var reversed = SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1(response.Claims.Reverse().ToArray()), Contract);
        Assert.Equal(whole.Order(StringComparer.Ordinal), reversed.Order(StringComparer.Ordinal));

        var refusedIndexes = Enumerable.Range(0, perClaim.Length).Where(i => perClaim[i].Count > 0).ToArray();
        Assert.Equal(6, refusedIndexes.Length);
        Assert.All(refusedIndexes, i => Assert.Equal("REFERENCES", response.Claims[i].Predicate));
        Assert.All(refusedIndexes, i => Assert.Equal(["relation-has-value"], perClaim[i]));
        // All six REFERENCES claims of the response carry it: a per-predicate wire habit, not random noise.
        Assert.Equal(6, response.Claims.Count(claim => claim.Predicate == "REFERENCES"));
    }

    [Fact]
    public void Excluding_the_refused_claims_leaves_every_sibling_byte_identical_and_wire_valid()
    {
        var raw = LoadRaw();
        var response = LenientDecode(raw);
        var refused = Enumerable.Range(0, response.Claims.Count)
            .Where(i => SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([response.Claims[i]]), Contract).Count > 0)
            .ToHashSet();

        var root = JsonNode.Parse(raw)!.AsObject();
        var claims = root["claims"]!.AsArray();
        var survivorsRaw = claims.Where((_, i) => !refused.Contains(i)).Select(node => node!.ToJsonString()).ToArray();
        var originalRaw = claims.Select(node => node!.ToJsonString()).ToArray();

        // Exclusion only: every surviving claim's JSON is identical to what the provider sent.
        Assert.Equal(originalRaw.Where((_, i) => !refused.Contains(i)), survivorsRaw);

        // A response consisting solely of those unmodified survivors passes the STRICT codec unchanged,
        // so no response-level invariant (shape, unknown fields, endpoints, states) was violated by them.
        var survivorsPayload = new JsonObject { ["claims"] = new JsonArray(survivorsRaw.Select(text => JsonNode.Parse(text)).ToArray()) };
        using var parsed = JsonDocument.Parse(survivorsPayload.ToJsonString());
        var strict = SemanticClaimResponseCodecV2_1.Parse(parsed.RootElement, Contract);
        Assert.Equal(29, strict.Claims.Count);
    }

    [Fact]
    public void Survivors_must_keep_their_original_ordinals_or_durable_claim_ids_silently_change()
    {
        var raw = LoadRaw();
        var response = LenientDecode(raw);
        var cohortRow = JsonNode.Parse(File.ReadAllText(TestRepository.Path(CohortPath)))!["rows"]!.AsArray()
            .Single(row => row!["DocumentId"]!.GetValue<string>() == "SRC-089" && row["PackId"]!.GetValue<string>().EndsWith("PACK_006", StringComparison.Ordinal))!;
        const string packId = "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_006";
        var scope = ClaimBindingScope.Create(
            cohortRow["ownedAliases"]!.AsArray().Select(item => item!.GetValue<string>()),
            cohortRow["visibleAliases"]!.AsArray().Select(item => item!.GetValue<string>()));
        var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src089));

        var contractRefused = Enumerable.Range(0, response.Claims.Count)
            .Where(i => SemanticClaimContractV2_1.Validate(new SemanticClaimResponseV2_1([response.Claims[i]]), Contract).Count > 0)
            .ToHashSet();

        // What the binder would do with ALL 35 if the contract gate did not exist: it does not look at
        // `value` on a relation, so the six malformed claims would bind. The contract gate must run first.
        var fullBinding = ExactClaimBinderV2_1.Bind(packId, response.Claims, atoms, scope);
        var fullQualification = V5BindingQualifier.FromBinding(response.Claims, fullBinding, scope);
        var boundIndexes = BoundProposalIndexes(response.Claims, fullBinding);
        var malformedThatWouldBind = boundIndexes.Count(index => contractRefused.Contains(index));

        // Correct shape: bind with original ordinals, then drop the contract-refused positions.
        var preservedIds = boundIndexes.Where(index => !contractRefused.Contains(index))
            .Select(index => fullBinding.Bound[boundIndexes.IndexOf(index)].Claim.ClaimId).ToArray();

        // Naive shape: filter first, then bind. Ordinals shift, so every survivor after the first
        // removed claim would get a different durable id.
        var filtered = response.Claims.Where((_, i) => !contractRefused.Contains(i)).ToArray();
        var naiveBinding = ExactClaimBinderV2_1.Bind(packId, filtered, atoms, scope);
        var naiveIds = naiveBinding.Bound.Select(item => item.Claim.ClaimId).ToArray();
        var idsChangedByNaiveFilter = naiveIds.Except(preservedIds, StringComparer.Ordinal).Count();

        Assert.Equal(6, malformedThatWouldBind); // the contract gate is the ONLY thing stopping them
        Assert.True(idsChangedByNaiveFilter > 0, "filtering before binding must be shown to renumber durable ids");

        var survivorQualification = V5BindingQualifier.FromBinding(filtered, naiveBinding, scope);
        var audit = new
        {
            schemaVersion = "v5-contract-claim-locality-audit-v1",
            question = "Must a valid-JSON response with SOME contract-violating claims be RESPONSE_FATAL, or can exactly those claims be quarantined?",
            subject = new { documentId = "SRC-089", packId, rawResponseSha256 = RawSha256, source = ResultPath },
            providerCalls = 0,
            goldRead = false,
            invariants = new { mutation = false, coercion = false, inferredCorrection = false, valueDeletedToRepair = false },
            current = new { outcome = "RESPONSE_FATAL", rawClaims = response.Claims.Count, claimsDownstream = 0 },
            finding = new
            {
                contractValidationIsClaimLocal = true,
                perClaimIssuesConcatenatedEqualWholeResponseIssues = true,
                orderIndependent = true,
                contractRefusedClaims = contractRefused.Count,
                contractRefusedIssue = "relation-has-value",
                contractRefusedPredicate = "REFERENCES (6 of 6 REFERENCES claims)",
                structurallyValidSiblings = response.Claims.Count - contractRefused.Count,
                siblingsByteIdenticalToProviderOutput = true,
                siblingsOnlyResponsePassesStrictCodec = true,
                malformedClaimsThatWouldBindIfContractGateWereSkipped = malformedThatWouldBind,
                naiveFilterBeforeBindRenumbersDurableIds = idsChangedByNaiveFilter,
            },
            counterfactualIfQuarantined = new
            {
                note = "COUNTERFACTUAL ONLY. Nothing here is fed to a runtime path and no protocol semantics were changed.",
                proposals = response.Claims.Count,
                contractRefused = contractRefused.Count,
                survivorsBound = survivorQualification.BoundCount,
                survivorsBinderRefused = survivorQualification.RefusalCount,
                survivorBinderRefusals = survivorQualification.Refusals.Select(item => new { item.Key, item.Value }).ToArray(),
                unsafeRepairs = survivorQualification.UnsafeRepairCount,
                outOfScopeAccepted = survivorQualification.OutOfScopeAcceptedCount,
            },
            wouldStillBeResponseFatal = new[]
            {
                "payload is not a JSON object / claims is not an array",
                "unknown field at response, claim, endpoint or part level (wire-contract drift)",
                "empty sourceParts / empty verbatimText / missing subject shape",
                "model-originated EXHAUSTED or a non-provider-facing state",
                "transport incomplete, finish_reason=length, invalid JSON",
            },
            requiredIfAdopted = new[]
            {
                "run the TaskContract gate BEFORE the binder, per claim, at the claim's ORIGINAL ordinal",
                "record every refused claim as an explicit issue (own code, e.g. CLAIM_CONTRACT with the claim ordinal); never drop silently",
                "bind the full ordered list and exclude contract-refused positions, or pass original ordinals to the id function; never filter then bind",
                "never delete or rewrite `value` on a relation to make a claim pass",
                "qualification must expose the layer: contractRefusedCount separate from binderRefusedCount",
            },
            decisionNeeded = "Adopting claim-local contract quarantine changes runtime semantics (today: RESPONSE_FATAL). This audit does not make that change.",
        };
        var path = TestRepository.Path(OutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
        _ = fullQualification;
    }

    /// <summary>Which proposal index each bound claim came from (the binder emits bound claims in proposal order).</summary>
    private static List<int> BoundProposalIndexes(IReadOnlyList<SemanticClaimProposalV2_1> proposals, ClaimBindingResultV2_1 binding)
    {
        var indexes = new List<int>();
        for (var i = 0; i < proposals.Count; i++)
        {
            var key = proposals[i].ExistingClaimId ?? $"proposal-{i + 1}";
            if (!binding.Refusals.ContainsKey(key)) indexes.Add(i);
        }
        Assert.Equal(binding.Bound.Count, indexes.Count);
        return indexes;
    }
}
