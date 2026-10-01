using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Read-only Gold audit of frozen P5O responses. Counterfactual canonicalization is never runtime behavior.</summary>
public sealed class V5P5QGoldOfflineAuditTests
{
    private const string SourceRoot = "artifacts/v5-p5o-v32-semantic-cohort-manifest";
    private const string Root = "artifacts/v5-p5q-gold-offline-audit";
    private static readonly HashSet<string> OccurrencePredicates = new(StringComparer.Ordinal) { "DOCUMENT_IDENTITY", "STRUCTURAL_REGION", "NAVIGATION_REPRESENTATION" };
    private static readonly (string Role, string Doc, string Pdf, string Pack)[] Selection =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    [Fact]
    public void Score_bound_claims_and_freeze_counterfactual_representation_loss()
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{SourceRoot}/result.v1.json")));
        var sourceRows = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var packsByDoc = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);
        var gold = Gold();
        var actual = NewSets(); var counter = NewSets(); var rawSubjects = NewSets();
        var mismatchRows = new List<object>(); var wholeEchoRows = new List<object>();
        foreach (var spec in Selection)
        {
            if (!packsByDoc.TryGetValue(spec.Doc, out var packs))
            {
                packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(spec.Pdf), spec.Doc, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                packsByDoc[spec.Doc] = packs;
            }
            var pack = packs.Single(p => p.PackId == spec.Pack); var row = sourceRows.Single(r => r.GetProperty("role").GetString() == spec.Role);
            using var raw = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
            var response = V5SemanticSparseDecisionContractV3_1.Parse(raw.RootElement, contract, pack.OwnedAliases.Count, pack.Packet.ContextOnlyEvidence.Count);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(spec.Pdf)); var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
            var scope = ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases);
            var binding = V5SemanticSparseDecisionContractV3_1.Bind(row.GetProperty("semanticRequestHash").GetString()!, response, contract, pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence, atoms, scope);
            AddBound(actual[spec.Doc], binding);
            foreach (var decision in response.Decisions.Where(d => d.Claims.Count > 0))
            {
                var node = pack.Packet.SubjectEvidence[decision.OwnedIndex]; var atom = byAlias[node.SourceAlias];
                rawSubjects[spec.Doc].Add($"{atom.Alias}:0-{atom.Text.Length}");
            }
            foreach (var refusal in binding.Refusals.Where(pair => pair.Value.Contains("does not contain", StringComparison.Ordinal) || pair.Value.Contains("context given", StringComparison.Ordinal)))
                mismatchRows.Add(new { role = spec.Role, refusal.Key, refusal.Value, intentProxy = "OWNED_SUBJECT_ONLY; semantic relation/predicate intent not Gold-evaluable" });
            var transformed = new V5SemanticSparseDecisionResponseV3_1(response.Decisions.Select(d => new V5SemanticSparseSubjectDecisionV3_1(d.OwnedIndex,
                d.Claims.Select(c => CanonicalizeWholeEcho(c, d.OwnedIndex, pack, byAlias)).ToArray()) { WireOrdinal = d.WireOrdinal }).ToArray()) { ParseRefusals = response.ParseRefusals };
            var counterBinding = V5SemanticSparseDecisionContractV3_1.Bind(row.GetProperty("semanticRequestHash").GetString()!, transformed, contract, pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence, atoms, scope);
            AddBound(counter[spec.Doc], counterBinding);
            wholeEchoRows.Add(new { role = spec.Role, actualBoundClaims = binding.Bound.Count, counterfactualBoundClaims = counterBinding.Bound.Count, gainedBoundClaims = counterBinding.Bound.Count - binding.Bound.Count, runtimeChange = "NONE" });
        }
        var docs = new List<object>(); int tp = 0, fp = 0, fn = 0, counterTp = 0, counterFp = 0, counterFn = 0;
        foreach (var doc in gold.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var scored = actual[doc]; var counterScored = counter[doc]; var goldSet = gold[doc]; var t = scored.Intersect(goldSet).Count(); var f = scored.Except(goldSet).Count(); var n = goldSet.Except(scored).Count(); tp += t; fp += f; fn += n;
            var ct = counterScored.Intersect(goldSet).Count(); var cf = counterScored.Except(goldSet).Count(); var cn = goldSet.Except(counterScored).Count(); counterTp += ct; counterFp += cf; counterFn += cn;
            docs.Add(new { documentId = doc, goldOccurrences = goldSet.Count, boundOccurrencePredictions = scored.Count, truePositive = t, falsePositive = f, falseNegative = n,
                counterfactual = new { boundOccurrencePredictions = counterScored.Count, truePositive = ct, falsePositive = cf, falseNegative = cn },
                misses = goldSet.Except(scored).OrderBy(x => x, StringComparer.Ordinal).Select(identity => new { identity, disposition = counter[doc].Contains(identity) ? "REPRESENTATION_CONTRACT_LOSS_COUNTERFACTUAL" : rawSubjects[doc].Contains(identity) ? "EXACT_BINDING_LOSS_OR_SELECTION_MISMATCH" : "NO_MODEL_PROPOSAL" }).ToArray() });
        }
        var precision = tp + fp == 0 ? 1d : (double)tp / (tp + fp); var recall = tp + fn == 0 ? 1d : (double)tp / (tp + fn);
        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5q-gold-offline-audit-v1", source = $"{SourceRoot}/result.v1.json", providerCalls = 0, goldRead = true,
            scoreability = new { occurrencePredicates = OccurrencePredicates.OrderBy(x => x).ToArray(), relationClaims = "NOT_EVALUABLE: canonical Gold has no relation axis" },
            boundOccurrenceScore = new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = Math.Round(precision, 4), recall = Math.Round(recall, 4), f1 = Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4), documents = docs },
            wholeAtomEchoCounterfactualOccurrenceScore = new { truePositive = counterTp, falsePositive = counterFp, falseNegative = counterFn, precision = Math.Round(counterTp + counterFp == 0 ? 1d : (double)counterTp / (counterTp + counterFp), 4), recall = Math.Round(counterTp + counterFn == 0 ? 1d : (double)counterTp / (counterTp + counterFn), 4), f1 = Math.Round(counterTp == 0 ? 0d : 2d * counterTp / (2 * counterTp + counterFp + counterFn), 4) },
            exactBindingMismatches = new { count = mismatchRows.Count, rows = mismatchRows, limitation = "Exact occurrence Gold cannot judge relation/predicate semantic intent; no binder conclusion follows." },
            wholeAtomEchoCounterfactual = new { rows = wholeEchoRows, limitation = "Alias-only canonicalization is audit-only and is not a production binder/runtime change." },
            recallDecomposition = "Gold misses are classified only from frozen proposal/binding evidence; projection loss is NOT_EVALUABLE because projection was not run.",
            v4F1Baseline = 0.754,
        });
    }

    private static Dictionary<string, HashSet<string>> Gold()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var id in new[] { "SRC-089", "SRC-095" })
        {
            CanonicalGoldRegistry.RequireCapability(id, GoldCapability.Occurrence); using var doc = CanonicalGoldRegistry.Resolve(id);
            result[id] = doc.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(x => x.GetProperty("identity").GetString()!).ToHashSet(StringComparer.Ordinal);
        }
        return result;
    }
    private static Dictionary<string, HashSet<string>> NewSets() => new(StringComparer.Ordinal) { ["SRC-089"] = new(StringComparer.Ordinal), ["SRC-095"] = new(StringComparer.Ordinal) };
    private static void AddBound(HashSet<string> output, V5DecisionBindingResultV3 binding) { foreach (var claim in binding.Bound.Where(x => OccurrencePredicates.Contains(x.Claim.Predicate))) output.Add(claim.Claim.Subject.Identity); }
    private static V5SemanticDecisionClaimV3 CanonicalizeWholeEcho(V5SemanticDecisionClaimV3 claim, int owned, V5PackedDecisionRequestV3 pack, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        var subjectAlias = pack.Packet.SubjectEvidence[owned].SourceAlias;
        return claim with { SubjectSelection = ClearIfWhole(claim.SubjectSelection, atoms[subjectAlias]), AdditionalSubjectParts = claim.AdditionalSubjectParts?.Select(p => p with { Selection = ClearIfWhole(p.Selection, atoms[pack.Packet.SubjectEvidence[p.OwnedIndex].SourceAlias]) }).ToArray(), TargetParts = claim.TargetParts?.Select(p => { var node = p.SourceGroup == "OWNED" ? pack.Packet.SubjectEvidence[p.SourceIndex] : pack.Packet.ContextOnlyEvidence[p.SourceIndex]; return p with { Selection = ClearIfWhole(p.Selection, atoms[node.SourceAlias]) }; }).ToArray() };
    }
    private static V5DecisionTextSelectionV3? ClearIfWhole(V5DecisionTextSelectionV3? selection, SemanticSourceAtom atom) => selection?.VerbatimText is { } text && text == atom.Text ? null : selection;
}
