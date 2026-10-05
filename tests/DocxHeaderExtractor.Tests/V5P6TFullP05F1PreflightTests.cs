using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Freezes the first missing authority layer for a full-P05 qualification cohort.
/// This is deliberately F1-only: G2A and H2-C request universes depend on the raw, accepted
/// F1 ledger for each pack and therefore cannot be honestly precomputed before F1 execution.
/// </summary>
public sealed class V5P6TFullP05F1PreflightTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tf1-full-p05-preflight";

    private static readonly Source[] Sources =
    [
        new("SRC-089", SourcePdfCorpus.Src089, true, 7),
        new("SRC-041", SourcePdfCorpus.Src041, false, 109),
        new("SRC-095", SourcePdfCorpus.Src095, true, 24),
        new("DOC-0252", SourcePdfCorpus.Doc0252, false, 7),
        new("DOC-0256", SourcePdfCorpus.Doc0256, false, 6),
    ];

    [Fact]
    public void P6TF1_freezes_full_P05_request_universe_without_provider_or_Gold()
    {
        var documents = new List<object>();
        var requests = new List<object>();
        var ordinal = 0;

        foreach (var source in Sources)
        {
            var sourcePath = TestRepository.Path(source.Pdf);
            var sourceSha256 = CanonicalSemanticSourceHash.Compute(sourcePath);
            var snapshotPath = TestRepository.Path($"{SnapshotRoot}/{sourceSha256}.json");
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, source.DocumentId);

            Assert.Equal(source.ExpectedPackCount, plan.Packs.Count);
            Assert.Equal(plan.SourceOccurrenceTotal, plan.Packs.SelectMany(pack => pack.OwnedAliases).Distinct(StringComparer.Ordinal).Count());
            Assert.All(plan.Packs, pack => Assert.NotEmpty(pack.OwnedAliases));

            var packRows = new List<object>();
            foreach (var pack in plan.Packs)
            {
                var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(
                    plan, pack, source.UseReadOnlyCorrespondences ? Correspondences(pack) : EmptyCorrespondences);
                ordinal++;
                Assert.Equal(pack.OwnedAliases.Count, prepared.Request.Occurrences.Count);
                Assert.Equal(pack.MaxCompletionTokens, prepared.SourcePack.MaxCompletionTokens);
                Assert.Contains("ESTABLISHES_STRUCTURE", prepared.Request.SystemPrompt, StringComparison.Ordinal);
                Assert.Contains("REPRESENTS_STRUCTURE", prepared.Request.SystemPrompt, StringComparison.Ordinal);

                var row = new
                {
                    providerCallOrdinal = ordinal,
                    documentId = source.DocumentId,
                    packId = pack.PackId,
                    packOrdinal = pack.PackOrdinal,
                    sourceSha256 = plan.SourceSha256,
                    sourceUniverseSha256 = plan.SourceUniverseSha256,
                    ownedOccurrenceCount = pack.OwnedAliases.Count,
                    visibleOccurrenceCount = pack.VisibleAliases.Count,
                    issuedOccurrenceCount = prepared.Request.Occurrences.Count,
                    issuedOccurrencesSha256 = Hash(string.Join("\n", prepared.Request.Occurrences.Select(item => item.Id)) + "\n"),
                    ownedAliasesSha256 = Hash(string.Join("\n", pack.OwnedAliases) + "\n"),
                    semanticRequestSha256 = prepared.Request.UserMessageSha256,
                    systemPromptSha256 = Hash(prepared.Request.SystemPrompt),
                    providerBodySha256 = prepared.ProviderRequestHash,
                    providerBodyBytes = prepared.ProviderRequestBytes,
                    maxCompletionTokens = prepared.SourcePack.MaxCompletionTokens,
                    readOnlyCorrespondenceEvidence = source.UseReadOnlyCorrespondences,
                };
                requests.Add(row);
                packRows.Add(row);
            }

            documents.Add(new
            {
                documentId = source.DocumentId,
                sourceSha256 = plan.SourceSha256,
                sourceUniverseSha256 = plan.SourceUniverseSha256,
                sourceOccurrenceTotal = plan.SourceOccurrenceTotal,
                p05PackCount = plan.Packs.Count,
                readOnlyCorrespondenceEvidence = source.UseReadOnlyCorrespondences,
                packs = packRows,
            });
        }

        Assert.Equal(153, ordinal);
        Assert.Equal(153, requests.Count);
        Assert.Equal(5, documents.Count);

        FreezeArtifact.AssertJson(OutputRoot, "f1-full-p05-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tf1-full-p05-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            authority = new
            {
                source = "CANONICAL_PDF_SOURCE_SNAPSHOT_V1",
                packing = "P05_EXACT_ONCE_OWNERSHIP",
                protocol = "V5_TOTAL_OCCURRENCE_FUNCTION_F1",
                transport = "OPENROUTER_QWEN37_JSON_OBJECT_CARRIER_V2_1_REASONING_ENABLED_EFFORT_OMITTED",
            },
            execution = new
            {
                providerCalls = 0,
                maximumPrimaryCalls = 153,
                retry = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            downstream = new
            {
                g2a = "BLOCKED_PENDING_RAW_ACCEPTED_F1_LEDGER_PER_PACK",
                h2cV2 = "BLOCKED_PENDING_RAW_ACCEPTED_G2A_LEDGER_PER_PACK",
                productionPromotion = "BLOCKED_PENDING_FULL_CHAIN_QUALIFICATION",
            },
            documents,
            requests,
        });
    }

    private sealed record Source(string DocumentId, string Pdf, bool UseReadOnlyCorrespondences, int ExpectedPackCount);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> EmptyCorrespondences =
        new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var alias = candidate.Endpoint.Parts[0].Alias;
            var values = result.TryGetValue(alias, out var existing) ? existing.ToList() : [];
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                values.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[alias] = values;
        }
        return result;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
