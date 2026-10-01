using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free evidence audit: binds frozen P5O responses and never judges semantics or reads Gold.</summary>
public sealed class V5P5PBoundClaimRefusalAuditTests
{
    private const string SourceRoot = "artifacts/v5-p5o-v32-semantic-cohort-manifest";
    private const string Root = "artifacts/v5-p5p-bound-claim-refusal-audit";
    private static readonly (string Role, string Doc, string Pdf, string Pack)[] Selection =
    [
        ("MAX_OWNED_AND_MULTIPART", "SRC-089", SourcePdfCorpus.Src089, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001"),
        ("L1472_OWNER_OMISSION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017"),
        ("L1710_RETYPING", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020"),
        ("MULTIPART_RELATION", "SRC-095", SourcePdfCorpus.Src095, "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011"),
    ];

    [Fact]
    public void Freeze_all_bound_and_refused_claim_evidence_without_semantic_judgment()
    {
        using var resultDocument = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{SourceRoot}/result.v1.json")));
        var sourceRows = resultDocument.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var cache = new Dictionary<string, IReadOnlyList<V5PackedDecisionRequestV3>>(StringComparer.Ordinal);
        var auditRows = new List<object>();
        foreach (var selected in Selection)
        {
            if (!cache.TryGetValue(selected.Doc, out var packs))
            {
                packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(selected.Pdf), selected.Doc, contract,
                    V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                cache[selected.Doc] = packs;
            }
            var pack = packs.Single(item => item.PackId == selected.Pack);
            var source = sourceRows.Single(row => row.GetProperty("role").GetString() == selected.Role);
            using var responseDocument = JsonDocument.Parse(source.GetProperty("rawResponse").GetString()!);
            var parsed = V5SemanticSparseDecisionContractV3_1.Parse(responseDocument.RootElement, contract, pack.OwnedAliases.Count, pack.Packet.ContextOnlyEvidence.Count);
            var binding = V5SemanticSparseDecisionContractV3_1.Bind(source.GetProperty("semanticRequestHash").GetString()!, parsed, contract,
                pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence, V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(selected.Pdf)),
                ClaimBindingScope.Create(pack.OwnedAliases, pack.VisibleAliases));
            auditRows.Add(new
            {
                role = selected.Role,
                rawClaims = parsed.Decisions.Sum(decision => decision.Claims.Count),
                parsedSparseDecisions = parsed.Decisions.Count,
                boundClaims = binding.Bound.Select(claim => new { claim.Claim.ClaimId, subject = claim.Claim.Subject.Identity, claim.Claim.Predicate, claim.Claim.Value, @object = claim.Claim.Object?.Identity, claim.Claim.State }).ToArray(),
                refusals = binding.Refusals.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, pair.Value, mechanicalClass = MechanicalClass(pair.Value), semanticDisposition = SemanticDisposition(pair.Value) }).ToArray(),
            });
        }
        Assert.Equal(4, auditRows.Count);
        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p5p-bound-claim-refusal-audit-v1",
            status = "PROVIDER_FREE_COMPLETE_SEMANTICS_UNJUDGED",
            source = $"{SourceRoot}/result.v1.json",
            providerCalls = 0,
            goldRead = false,
            semanticScore = "NOT_RUN",
            classificationBoundary = new
            {
                modelError = "Only mechanical model-output contract/addressing violations are classified here.",
                binderTooStrict = "NOT_ASSERTED without semantic evidence; no binder change is licensed by this audit.",
                contractDesignError = "NOT_ASSERTED without a repeated measured pattern.",
                expectedRefusal = "NOT_ASSERTED without Gold semantic judgment.",
            },
            rows = auditRows,
        });
    }

    private static string MechanicalClass(string reason) => reason switch
    {
        "whole-atom-must-omit-verbatim-text" => "MODEL_REPRESENTATION_CONTRACT_VIOLATION",
        "duplicate-owned-index" or "additional-owned-index-out-of-range-or-order" or "target-visible-index-out-of-range" or "target-source-group-invalid" => "MODEL_ADDRESSING_OR_MULTIPART_ERROR",
        _ when reason.Contains("claim-", StringComparison.Ordinal) || reason.Contains("state", StringComparison.Ordinal) => "MODEL_TASK_CONTRACT_VIOLATION",
        _ => "EXACT_BINDING_MISMATCH_SEMANTIC_UNJUDGED",
    };

    private static string SemanticDisposition(string reason) => reason switch
    {
        "whole-atom-must-omit-verbatim-text" or "duplicate-owned-index" => "MODEL_OUTPUT_NONCOMPLIANT_NOT_BINDER_TOO_STRICT",
        _ => "REQUIRES_GOLD_OR_HUMAN_SEMANTIC_REVIEW",
    };
}
