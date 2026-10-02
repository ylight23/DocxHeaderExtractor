using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free authority audit: observations never become task cardinality rules by implication.</summary>
public sealed class V5P5WTaskSemanticCardinalityAuditTests
{
    private const string Root = "artifacts/v5-p5w-task-semantic-cardinality";
    private static readonly DocumentTaskContract Contract =
        DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    [Fact]
    public void Freeze_task_cardinality_authority_and_initial_refinement_scope_for_all_31_packs()
    {
        var documents = new[] { ("SRC-089", SourcePdfCorpus.Src089, 7), ("SRC-095", SourcePdfCorpus.Src095, 24) };
        var rows = new List<object>();
        foreach (var document in documents)
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(document.Item2), document.Item1, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            Assert.Equal(document.Item3, packs.Count);
            foreach (var (pack, index) in packs.Select((pack, index) => (pack, index)))
            {
                var composed = V5CompactDecisionComposerV3_3.Compose(Contract, pack.Packet);
                using var prompt = JsonDocument.Parse(composed.Prompt);
                var supplied = prompt.RootElement.GetProperty("packet").GetProperty("openOrConflictedClaims");
                Assert.Equal(JsonValueKind.Array, supplied.ValueKind);
                Assert.Empty(pack.Packet.OpenOrConflictedClaims);
                Assert.Equal(0, supplied.GetArrayLength());
                rows.Add(new { documentId = document.Item1, parentOrdinal = index + 1, packId = pack.PackId,
                    openOrConflictedClaims = 0, providerVisibleExistingClaimIds = 0 });
            }
        }
        Assert.Equal(31, rows.Count);
        var contractJson = JsonSerializer.SerializeToElement(Contract, CanonicalJson.Options);
        var contractKeys = contractJson.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        FreezeArtifact.AssertJson(Root, "task-cardinality-audit.v1.json", new
        {
            schemaVersion = "v5-p5w-task-semantic-cardinality-audit-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            taskContract = new
            {
                taskId = Contract.TaskId, serializedTopLevelKeys = contractKeys,
                unaryPredicates = Contract.Predicates.Select(item => item.Name).Order(StringComparer.Ordinal).ToArray(),
                relations = Contract.Relations.Select(item => new { item.Name, item.StructuralParent, item.IdentityMerge }).OrderBy(item => item.Name).ToArray(),
                taskDefinedPerSubjectPredicateCap = "NOT_DEFINED",
                taskDefinedSubjectRelationTargetUnique = "NOT_DEFINED",
                taskDefinedUnaryPerSubjectCap = "NOT_DEFINED",
                taskDefinedTotalClaimCap = "NOT_DEFINED",
            },
            existingClaimId = new
            {
                initialCohortPacks = 31, initialCohortPacketsWithOpenOrConflictedClaims = 0,
                providerViewContainsNoExistingClaimIdAuthority = true,
                parserAuthority = "PARSER_CANNOT_VALIDATE_EXISTING_CLAIM_ID: scope is supplied only to Bind",
                binderAuthority = "ExactClaimBinderV2_1 rejects unknown-existing-claim-id and existing-claim-id-mismatch using ClaimBindingScope.KnownClaims",
                consequence = "Initial-response source-volume proof must omit existingClaimId; a future refinement proof must derive IDs from the concrete request packet.",
            },
            relationSemantics = new
            {
                parentOf = "graph rejects self-parent and cycles after binding; no per-subject cardinality rule",
                references = "no cardinality or uniqueness rule",
                sameEntity = "identity merge marker; no cardinality or uniqueness rule",
                continues = "no cardinality or uniqueness rule",
                historicalObservationIsNotAuthority = "The frozen V3.2 cohort may inform a redesign but cannot establish a V3.3 task bound.",
            },
            conclusion = "P5U_RESPONSE_VOLUME_GATE_REMAINS_BLOCKED. No task-derived finite claim/relation volume bound exists in the current DocumentTaskContract; do not lower 129 or promote V3.3 from this audit.",
            rows,
        });
    }
}
