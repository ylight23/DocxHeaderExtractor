using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free proof that v3 subject ownership can be partitioned without hiding evidence.</summary>
public sealed class V5P5IDecisionShardingTests
{
    private const string ArtifactRoot = "artifacts/v5-p5i-v3-decision-sharding";
    private static readonly int[] CandidateSizes = [48, 32, 24];
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    [Fact]
    public void Freeze_subject_ownership_sharding_candidates_and_select_conservative_32()
    {
        var originals = BuildOriginals();
        Assert.Equal(31, originals.Count);
        var pack007 = Assert.Single(originals, item => item.DocumentId == "SRC-089"
            && item.Pack.PackId.EndsWith("PACK_007", StringComparison.Ordinal));
        Assert.Equal(6, pack007.Pack.OwnedAliases.Count);
        var maxOwned = originals.Max(item => item.Pack.OwnedAliases.Count);
        Assert.Equal(96, maxOwned);

        var candidateRows = new List<object>();
        var proofRows = new List<object>();
        var candidateSummaries = new List<object>();
        IReadOnlyList<V5ShardedDecisionRequestV3>? selectedShards = null;
        var ownershipGapCount = 0;
        var ownershipDuplicateCount = 0;
        var visibilityDriftCount = 0;
        var sourceEvidenceDriftCount = 0;

        foreach (var size in CandidateSizes)
        {
            var candidateCalls = 0;
            var candidateMaxRequestBytes = 0;
            var candidateMaxProviderBytes = 0;
            var candidateMaxResponseBytes = 0;
            var candidateMaxTokens = 0;
            foreach (var original in originals)
            {
                var shards = V5DecisionShardingV3.Shard(original.Pack, Contract, Envelope, size);
                V5DecisionShardingV3.ValidatePartition(original.Pack, shards);
                if (original.Pack.OwnedAliases.Count == 96)
                    Assert.Equal(size == 48 ? 2 : size == 32 ? 3 : 4, shards.Count);
                if (original.Pack.PackId == pack007.Pack.PackId && original.DocumentId == pack007.DocumentId)
                    Assert.Single(shards);

                var originalOwned = original.Pack.Packet.SubjectEvidence.Select(node => node.SourceAlias).ToArray();
                var originalVisible = original.Pack.Packet.SubjectEvidence.Concat(original.Pack.Packet.ContextOnlyEvidence)
                    .Select(node => node.SourceAlias).ToArray();
                var shardOwned = shards.SelectMany(shard => shard.Request.OwnedAliases).ToArray();
                var gaps = originalOwned.Except(shardOwned, StringComparer.Ordinal).Count();
                var duplicates = shardOwned.Length - shardOwned.Distinct(StringComparer.Ordinal).Count();
                ownershipGapCount += gaps;
                ownershipDuplicateCount += duplicates;
                foreach (var shard in shards)
                {
                    var shardVisible = shard.Request.Packet.SubjectEvidence.Concat(shard.Request.Packet.ContextOnlyEvidence).ToArray();
                    if (!shardVisible.Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(originalVisible)) visibilityDriftCount++;
                    foreach (var node in original.Pack.Packet.SubjectEvidence.Concat(original.Pack.Packet.ContextOnlyEvidence))
                    {
                        var equivalent = shardVisible.Single(candidate => candidate.SourceAlias == node.SourceAlias);
                        if (!string.Equals(JsonSerializer.Serialize(node, CanonicalJson.Options),
                                JsonSerializer.Serialize(equivalent, CanonicalJson.Options), StringComparison.Ordinal))
                            sourceEvidenceDriftCount++;
                    }
                    Assert.Equal(shard.Request.OwnedAliases.Count, shard.Request.Request.ResponseBounds.MaxDecisions);
                    Assert.Equal(Math.Min(129, shard.Request.OwnedAliases.Count * 10), shard.Request.Request.ResponseBounds.MaxClaimsTotal);
                    candidateRows.Add(new
                    {
                        candidateShardSize = size,
                        documentId = original.DocumentId,
                        originalPackId = original.Pack.PackId,
                        originalOwnedCount = original.Pack.OwnedAliases.Count,
                        shardCount = shards.Count,
                        shardOrdinal = shard.ShardOrdinal,
                        ownedPerShard = shard.Request.OwnedAliases.Count,
                        visiblePerShard = shard.Request.VisibleAliases.Count,
                        requestUtf8Bytes = shard.Request.Request.Utf8Bytes,
                        responseBounds = shard.Request.Request.ResponseBounds,
                        maxResponseUtf8Bytes = shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes,
                        configuredMaxCompletionTokens = shard.Request.MaxCompletionTokens,
                        providerRequestBytes = shard.Request.ProviderRequestBytes,
                        semanticRequestHash = shard.Request.Request.RequestHash,
                        providerRequestHash = shard.Request.ProviderRequestHash,
                    });
                    candidateMaxRequestBytes = Math.Max(candidateMaxRequestBytes, shard.Request.Request.Utf8Bytes);
                    candidateMaxProviderBytes = Math.Max(candidateMaxProviderBytes, shard.Request.ProviderRequestBytes);
                    candidateMaxResponseBytes = Math.Max(candidateMaxResponseBytes, shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes);
                    candidateMaxTokens = Math.Max(candidateMaxTokens, shard.Request.MaxCompletionTokens);
                }
                candidateCalls += shards.Count;
                proofRows.Add(new
                {
                    candidateShardSize = size,
                    documentId = original.DocumentId,
                    originalPackId = original.Pack.PackId,
                    originalOwnedCount = original.Pack.OwnedAliases.Count,
                    shardCount = shards.Count,
                    ownershipGapCount = gaps,
                    ownershipDuplicateCount = duplicates,
                    fullVisibilityPreserved = true,
                    sourceEvidenceDriftCount = 0,
                    coordinates = shards.SelectMany(shard => shard.Coordinates).Select(coordinate => new
                    {
                        coordinate.OriginalOwnedOrdinal,
                        coordinate.ShardOrdinal,
                        coordinate.ShardLocalDecisionOrdinal,
                        coordinate.SourceAlias,
                    }),
                });
                if (size == 32)
                    selectedShards = (selectedShards ?? []).Concat(shards).ToArray();
            }
            candidateSummaries.Add(new
            {
                candidateShardSize = size,
                totalProviderCallsFor31PackCohort = candidateCalls,
                originalProviderCalls = originals.Count,
                callMultiplication = candidateCalls / (double)originals.Count,
                maxOwnedPerCall = size,
                maxRequestBytes = candidateMaxRequestBytes,
                maxProviderBodyBytes = candidateMaxProviderBytes,
                maxResponseByteBound = candidateMaxResponseBytes,
                maxCompletionTokens = candidateMaxTokens,
                fullVisibilityPreserved = true,
                ownershipPartitionExact = true,
            });
        }

        Assert.Equal(0, ownershipGapCount);
        Assert.Equal(0, ownershipDuplicateCount);
        Assert.Equal(0, visibilityDriftCount);
        Assert.Equal(0, sourceEvidenceDriftCount);
        Assert.NotNull(selectedShards);
        Assert.Equal(candidateRows.Count(row => (int)row.GetType().GetProperty("candidateShardSize")!.GetValue(row)! == 32), selectedShards.Count);

        var relationProof = ProveCrossShardRelationAndStableIdentity(originals);
        Assert.True(relationProof.CrossShardTargetResolved);
        Assert.True(relationProof.DurableIdentityStable);

        FreezeArtifact.AssertJson(ArtifactRoot, "historical-observation.v1.json", new
        {
            schemaVersion = "v5-p5i-historical-observation-v1",
            providerCalls = 0,
            goldRead = false,
            p5f = Observe("artifacts/v5-p5f-v3-canary/result.v1.json"),
            p5f2 = Observe("artifacts/v5-p5f2-v3-canary/result.v1.json"),
            interpretation = "Empirical bytes/claims/tokens per returned decision are diagnostics only; no response bound is derived from an average.",
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "candidate-sizing.v1.json", new
        {
            schemaVersion = "v5-p5i-candidate-sizing-v1",
            providerCalls = 0,
            goldRead = false,
            originalPackCount = originals.Count,
            candidates = candidateSummaries,
            shards = candidateRows,
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "ownership-visibility-proof.v1.json", new
        {
            schemaVersion = "v5-p5i-ownership-visibility-proof-v1",
            providerCalls = 0,
            goldRead = false,
            ownershipGapCount,
            ownershipDuplicateCount,
            visibilityDriftCount,
            sourceEvidenceDriftCount,
            unrepresentableRelationTargetCount = relationProof.CrossShardTargetResolved ? 0 : 1,
            durableIdentityDriftCount = relationProof.DurableIdentityStable ? 0 : 1,
            packs = proofRows,
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "selected-policy.v1.json", new
        {
            schemaVersion = "v5-p5i-selected-policy-v1",
            policyId = V5DecisionShardingV3.PolicyId,
            providerCalls = 0,
            goldRead = false,
            selectedMaxOwnedDecisionsPerCall = 32,
            selection = "32 is the largest conservative candidate: 48 is only 13 decisions below the observed 61-decision stop, while 32 leaves materially more headroom without choosing the unnecessary 24-decision multiplication.",
            single96DecisionQualification = "CLOSED_NOT_PROMOTED",
            semanticScore = "NOT_RUN",
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "execution-manifest-preview.v1.json", new
        {
            schemaVersion = "v5-p5i-sharded-execution-manifest-preview-v1",
            status = "PREVIEW_NOT_AUTHORIZED",
            providerCalls = 0,
            goldRead = false,
            executionAuthorized = false,
            full31PackCohortAuthorized = false,
            policyId = V5DecisionShardingV3.PolicyId,
            selectedMaxOwnedDecisionsPerCall = 32,
            requestCount = selectedShards.Count,
            rows = selectedShards.Select(shard => new
            {
                originalPackId = shard.OriginalPackId,
                shard.Request.PackId,
                shard.ShardOrdinal,
                originalOwnedCount = shard.OriginalOwnedCount,
                ownedCount = shard.Request.OwnedAliases.Count,
                visibleCount = shard.Request.VisibleAliases.Count,
                originalSemanticRequestHash = shard.OriginalSemanticRequestHash,
                semanticRequestHash = shard.Request.Request.RequestHash,
                providerRequestHash = shard.Request.ProviderRequestHash,
                requestUtf8Bytes = shard.Request.Request.Utf8Bytes,
                providerRequestBytes = shard.Request.ProviderRequestBytes,
                maxResponseUtf8Bytes = shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes,
                maxCompletionTokens = shard.Request.MaxCompletionTokens,
                coordinates = shard.Coordinates,
            }),
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "audit.v1.json", new
        {
            schemaVersion = "v5-p5i-v3-decision-sharding-audit-v1",
            status = "PROVIDER_FREE_COMPLETE",
            providerCalls = 0,
            goldRead = false,
            single96DecisionLane = "CLOSED_NOT_PROMOTED",
            selectedMaxOwnedDecisionsPerCall = 32,
            ownershipGapCount,
            ownershipDuplicateCount,
            visibilityDriftCount,
            sourceEvidenceDriftCount,
            unrepresentableRelationTargetCount = relationProof.CrossShardTargetResolved ? 0 : 1,
            durableIdentityDriftCount = relationProof.DurableIdentityStable ? 0 : 1,
            nextProviderAction = "NONE without a newly frozen execution manifest and explicit authorization",
        });
    }

    private static (bool CrossShardTargetResolved, bool DurableIdentityStable) ProveCrossShardRelationAndStableIdentity(
        IReadOnlyList<(string DocumentId, V5PackedDecisionRequestV3 Pack)> originals)
    {
        var original = Assert.Single(originals, item => item.DocumentId == "SRC-095" && item.Pack.PackId.EndsWith("PACK_020", StringComparison.Ordinal)).Pack;
        var relationContract = Contract with { Relations = [new SemanticRelationDefinition("RELATES_TO", "P5I cross-shard target proof.")] };
        var shards32 = V5DecisionShardingV3.Shard(original, relationContract, Envelope, 32);
        var subjectShard = shards32[0];
        var targetAlias = original.OwnedAliases[32];
        var contextIndex = subjectShard.Request.Packet.ContextOnlyEvidence.ToList().FindIndex(node => node.SourceAlias == targetAlias);
        Assert.True(contextIndex >= 0);
        var relationResponse = new V5SemanticDecisionResponseV3(subjectShard.Request.OwnedAliases.Select((_, index) =>
            new V5SemanticSubjectDecisionV3(index == 0
                ? [new V5SemanticDecisionClaimV3("RELATES_TO", TargetParts: [new V5VisibleTargetPartV3("CONTEXT_ONLY", contextIndex)], EvidenceNeeds: [])]
                : [])).ToArray());
        var atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(SourcePdfCorpus.Src095));
        var relationBinding = V5DecisionShardingV3.Bind(subjectShard, relationResponse, relationContract, atoms);
        var relationClaim = Assert.Single(relationBinding.Bound).Claim;
        var crossShardTargetResolved = relationClaim.Object?.Parts.Single().Alias == targetAlias;

        var stableOriginalOrdinal = 64;
        var shards48 = V5DecisionShardingV3.Shard(original, Contract, Envelope, 48);
        var shard32 = Assert.Single(shards32, shard => shard.Coordinates.Any(coordinate => coordinate.OriginalOwnedOrdinal == stableOriginalOrdinal));
        var shard48 = Assert.Single(shards48, shard => shard.Coordinates.Any(coordinate => coordinate.OriginalOwnedOrdinal == stableOriginalOrdinal));
        var bind32 = BindUnaryForOriginalOrdinal(shard32, stableOriginalOrdinal, atoms);
        var bind48 = BindUnaryForOriginalOrdinal(shard48, stableOriginalOrdinal, atoms);
        var durableIdentityStable = bind32.ClaimId == bind48.ClaimId && bind32.Subject.Identity == bind48.Subject.Identity;
        return (crossShardTargetResolved, durableIdentityStable);
    }

    private static BoundSemanticClaim BindUnaryForOriginalOrdinal(V5ShardedDecisionRequestV3 shard, int originalOrdinal,
        IReadOnlyList<SemanticSourceAtom> atoms)
    {
        var local = shard.Coordinates.Single(coordinate => coordinate.OriginalOwnedOrdinal == originalOrdinal).ShardLocalDecisionOrdinal;
        var response = new V5SemanticDecisionResponseV3(shard.Request.OwnedAliases.Select((_, index) =>
            new V5SemanticSubjectDecisionV3(index == local
                ? [new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "x", EvidenceNeeds: [])]
                : [])).ToArray());
        return Assert.Single(V5DecisionShardingV3.Bind(shard, response, Contract, atoms).Bound).Claim;
    }

    private static IReadOnlyList<object> Observe(string relativePath)
    {
        using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(relativePath)));
        return result.RootElement.GetProperty("results").EnumerateArray().Select(row =>
        {
            var raw = row.GetProperty("rawResponse").ValueKind == JsonValueKind.String ? row.GetProperty("rawResponse").GetString() : null;
            int? decisions = null;
            int? claims = null;
            if (raw is not null)
            {
                try
                {
                    using var response = JsonDocument.Parse(raw);
                    if (response.RootElement.TryGetProperty("decisions", out var values) && values.ValueKind == JsonValueKind.Array)
                    {
                        decisions = values.GetArrayLength();
                        claims = values.EnumerateArray().Sum(decision => decision.TryGetProperty("claims", out var items) && items.ValueKind == JsonValueKind.Array
                            ? items.GetArrayLength() : 0);
                    }
                }
                catch (JsonException) { }
            }
            var completionTokens = row.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("completion_tokens", out var tokens) ? tokens.GetInt32() : (int?)null;
            return (object)new
            {
                role = row.GetProperty("role").GetString(),
                finishReason = row.GetProperty("finishReason").GetString(),
                rawResponseBytes = row.GetProperty("rawResponseBytes").GetInt32(),
                returnedDecisions = decisions,
                returnedClaims = claims,
                completionTokens,
                responseBytesPerReturnedDecision = decisions is > 0 ? row.GetProperty("rawResponseBytes").GetInt32() / (double)decisions : (double?)null,
                claimsPerReturnedDecision = decisions is > 0 && claims is not null ? claims / (double)decisions : (double?)null,
                completionTokensPerReturnedDecision = decisions is > 0 && completionTokens is not null ? completionTokens / (double)decisions : (double?)null,
            };
        }).ToArray();
    }

    private static IReadOnlyList<(string DocumentId, V5PackedDecisionRequestV3 Pack)> BuildOriginals()
    {
        var documents = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) };
        return documents.SelectMany(document => V5PdfPreflightBuilder.BuildV3(TestRepository.Path(document.Item2), document.Item1,
            Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope)
            .Select(pack => (document.Item1, pack))).ToArray();
    }
}
