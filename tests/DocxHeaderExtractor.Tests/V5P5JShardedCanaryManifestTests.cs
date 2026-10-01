using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Freezes the smallest post-P5I execution manifest. It deliberately has no provider client:
/// P5J proves the four selected shards and exact wire bodies before a separately authorized P5K.
/// </summary>
public sealed class V5P5JShardedCanaryManifestTests
{
    private const string ArtifactRoot = "artifacts/v5-p5j-v3-sharded-canary-manifest";
    private const int MaxOwnedPerShard = 32;
    private static readonly DocumentTaskContract Contract =
        DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };

    private sealed record RoleSpec(
        string Role,
        string DocumentId,
        string PackId,
        string SubjectAlias,
        IReadOnlyList<string> AdditionalSubjectAliases,
        string? RelationTargetAlias,
        string Focus);

    private sealed record ResolvedRole(
        RoleSpec Spec,
        V5PackedDecisionRequestV3 Original,
        V5ShardedDecisionRequestV3 Shard,
        V5OriginalOwnedDecisionCoordinateV3 Coordinate,
        IReadOnlyList<V5ShardedDecisionRequestV3> RebuiltShards);

    private sealed record MultipartProof(bool Representable, string[] ResolvedSourceAliases);
    private sealed record WholeAtomProof(bool WholeAtomWithoutVerbatimAccepted, bool WholeAtomEchoRefused);
    private sealed record RelationProof(bool CrossShardContextOnly, bool TargetSourceResolved, object ManifestTarget);

    private static readonly RoleSpec[] Roles =
    [
        new("MAX_OWNED_AND_MULTIPART", "SRC-089", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_001",
            "L0002:S0", ["L0002:S1"], null,
            "Historical 96-decision/multipart role; select its primary multipart subject, not an arbitrary shard."),
        new("L1472_OWNER_OMISSION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_017",
            "L1472:S0", [], null,
            "Historical owner-omission occurrence must remain a shard subject."),
        new("L1710_RETYPING", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_020",
            "L1710:S0", [], null,
            "Historical whole-atom retyping occurrence must remain a shard subject."),
        new("MULTIPART_RELATION", "SRC-095", "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_011",
            "L0973:S0", ["L0974:S0"], "L0918:S4",
            "Historical multipart relation with an intentionally cross-shard target."),
    ];

    [Fact]
    public void Freeze_exact_four_targeted_32_decision_shards_without_provider_or_gold()
    {
        Assert.Equal(4, Roles.Length);
        Assert.Equal(4, Roles.Select(role => role.Role).Distinct(StringComparer.Ordinal).Count());
        var originals = BuildOriginals();
        var resolved = Roles.Select(role => Resolve(role, originals)).ToArray();
        Assert.Equal(4, resolved.Length);
        Assert.All(resolved, item =>
        {
            Assert.Equal(MaxOwnedPerShard, item.Shard.CandidateMaxOwnedPerShard);
            Assert.Equal(MaxOwnedPerShard, item.Shard.Request.OwnedAliases.Count);
            Assert.Equal(MaxOwnedPerShard, item.Shard.Request.Request.ResponseBounds.MaxDecisions);
            Assert.Contains(item.Spec.SubjectAlias, item.Shard.Request.OwnedAliases, StringComparer.Ordinal);
            Assert.DoesNotContain(item.Spec.SubjectAlias,
                item.Shard.Request.Packet.ContextOnlyEvidence.Select(node => node.SourceAlias), StringComparer.Ordinal);
            Assert.Equal(item.Spec.SubjectAlias, item.Coordinate.SourceAlias);
            Assert.Equal(item.Shard.Coordinates[item.Coordinate.ShardLocalDecisionOrdinal], item.Coordinate);
        });

        var multipart = resolved.Single(item => item.Spec.Role == "MAX_OWNED_AND_MULTIPART");
        var multipartProof = ProveMultipartSubject(multipart);
        var retyping = resolved.Single(item => item.Spec.Role == "L1710_RETYPING");
        var retypingProof = ProveWholeAtomPolicy(retyping);
        var relation = resolved.Single(item => item.Spec.Role == "MULTIPART_RELATION");
        var relationProof = ProveCrossShardRelation(relation);

        var ownershipDrift = 0;
        var visibilityDrift = 0;
        var sourceEvidenceDrift = 0;
        var identityDrift = 0;
        var relationTargetResolutionDrift = relationProof.TargetSourceResolved ? 0 : 1;
        var rows = new List<object>();

        foreach (var item in resolved)
        {
            var replay = Assert.Single(item.RebuiltShards,
                shard => shard.ShardOrdinal == item.Shard.ShardOrdinal);
            if (replay.Request.Request.RequestHash != item.Shard.Request.Request.RequestHash ||
                replay.Request.ProviderRequestHash != item.Shard.Request.ProviderRequestHash)
                identityDrift++;

            V5DecisionShardingV3.ValidatePartition(item.Original, item.RebuiltShards);
            var originalVisible = item.Original.Packet.SubjectEvidence.Concat(item.Original.Packet.ContextOnlyEvidence)
                .Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
            var shardVisible = item.Shard.Request.Packet.SubjectEvidence.Concat(item.Shard.Request.Packet.ContextOnlyEvidence)
                .Select(node => node.SourceAlias).ToHashSet(StringComparer.Ordinal);
            if (!originalVisible.SetEquals(shardVisible)) visibilityDrift++;
            if (item.RebuiltShards.SelectMany(shard => shard.Request.OwnedAliases).Count(alias => alias == item.Spec.SubjectAlias) != 1)
                ownershipDrift++;

            foreach (var alias in new[] { item.Spec.SubjectAlias }
                .Concat(item.Spec.AdditionalSubjectAliases)
                .Append(item.Spec.RelationTargetAlias)
                .Where(alias => alias is not null).Cast<string>())
            {
                var originalNode = item.Original.Packet.SubjectEvidence.Concat(item.Original.Packet.ContextOnlyEvidence)
                    .Single(node => node.SourceAlias == alias);
                var shardNode = item.Shard.Request.Packet.SubjectEvidence.Concat(item.Shard.Request.Packet.ContextOnlyEvidence)
                    .Single(node => node.SourceAlias == alias);
                if (!string.Equals(System.Text.Json.JsonSerializer.Serialize(originalNode, CanonicalJson.Options),
                        System.Text.Json.JsonSerializer.Serialize(shardNode, CanonicalJson.Options), StringComparison.Ordinal))
                    sourceEvidenceDrift++;
            }

            var bodyFile = $"wire-bodies/{item.Spec.Role}.json";
            FreezeArtifact.AssertText(ArtifactRoot, bodyFile, Encoding.UTF8.GetString(item.Shard.Request.ProviderBody));
            var frozenBody = File.ReadAllBytes(TestRepository.Path($"{ArtifactRoot}/{bodyFile}"));
            Assert.True(frozenBody.AsSpan().SequenceEqual(item.Shard.Request.ProviderBody));
            Assert.Equal(item.Shard.Request.ProviderRequestHash, Sha256(frozenBody));
            Assert.Equal(item.Shard.Request.Request.RequestHash, replay.Request.Request.RequestHash);
            Assert.Equal(item.Shard.Request.ProviderRequestHash, replay.Request.ProviderRequestHash);

            rows.Add(new
            {
                role = item.Spec.Role,
                focus = item.Spec.Focus,
                documentId = item.Spec.DocumentId,
                originalPackId = item.Original.PackId,
                originalSemanticRequestHash = item.Original.Request.RequestHash,
                originalOwnedOrdinal = item.Coordinate.OriginalOwnedOrdinal,
                sourceAlias = item.Spec.SubjectAlias,
                selectedShardOrdinal = item.Shard.ShardOrdinal,
                shardLocalDecisionOrdinal = item.Coordinate.ShardLocalDecisionOrdinal,
                packId = item.Shard.Request.PackId,
                semanticRequestHash = item.Shard.Request.Request.RequestHash,
                providerRequestHash = item.Shard.Request.ProviderRequestHash,
                semanticRequestBytes = item.Shard.Request.Request.Utf8Bytes,
                providerRequestBytes = item.Shard.Request.ProviderRequestBytes,
                ownedCount = item.Shard.Request.OwnedAliases.Count,
                visibleCount = item.Shard.Request.VisibleAliases.Count,
                maxResponseUtf8Bytes = item.Shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes,
                maxCompletionTokens = item.Shard.Request.MaxCompletionTokens,
                coordinates = item.Shard.Coordinates,
                additionalSubjectAliases = item.Spec.AdditionalSubjectAliases,
                relationTarget = item.Spec.RelationTargetAlias is null ? null : relationProof.ManifestTarget,
                providerBodyFile = bodyFile,
                providerBodySha256 = Sha256(frozenBody),
                futureSuccessCriteria = new
                {
                    transportSuccess = true,
                    jsonComplete = true,
                    finishReasonNotLength = true,
                    exactDecisionCount = item.Shard.Request.OwnedAliases.Count,
                    responseBytesAtMostFrozenBound = item.Shard.Request.Request.ResponseBounds.MaxResponseUtf8Bytes,
                    parserAccepted = true,
                    noRepair = true,
                    noSemanticRetry = true,
                    noFallback = true,
                },
            });
        }

        Assert.Equal(0, ownershipDrift);
        Assert.Equal(0, visibilityDrift);
        Assert.Equal(0, sourceEvidenceDrift);
        Assert.Equal(0, identityDrift);
        Assert.Equal(0, relationTargetResolutionDrift);
        Assert.True(multipartProof.Representable);
        Assert.True(retypingProof.WholeAtomWithoutVerbatimAccepted);
        Assert.True(retypingProof.WholeAtomEchoRefused);
        Assert.True(relationProof.CrossShardContextOnly);
        Assert.True(relationProof.TargetSourceResolved);
        Assert.True(RepeatGuardStopsBeforeNetwork());

        FreezeArtifact.AssertJson(ArtifactRoot, "selection.v1.json", new
        {
            schemaVersion = "v5-p5j-v3-sharded-canary-selection-v1",
            providerCalls = 0,
            goldRead = false,
            selectedRequests = resolved.Length,
            selectedMaxOwnedDecisionsPerCall = MaxOwnedPerShard,
            roles = rows.Select(row => row),
            proofs = new
            {
                maxOwnedAndMultipart = multipartProof,
                l1472OwnerOmission = new { subjectEvidence = true, contextOnlyEvidence = false },
                l1710Retyping = retypingProof,
                multipartRelation = relationProof,
            },
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p5j-v3-sharded-provider-execution-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            policyId = V5DecisionShardingV3.PolicyId,
            selectedMaxOwnedDecisionsPerCall = MaxOwnedPerShard,
            route = new
            {
                gateway = "openrouter",
                api = "chat-completions",
                model = Envelope.Model,
                providerPin = Envelope.Provider,
                temperature = 0,
                reasoning = Envelope.Reasoning,
                streaming = Envelope.Streaming,
                responseFormat = Envelope.ResponseFormat,
            },
            executionAuthorized = false,
            providerCalls = 0,
            goldRead = false,
            maximumFutureCalls = 4,
            full91CallCohortAuthorized = false,
            selectedRequests = resolved.Length,
            resultArtifactGuard = new
            {
                resultPath = $"{ArtifactRoot}/result.v1.json",
                condition = "result.v1.json already exists",
                action = "STOP_BEFORE_NETWORK",
                providerFreeTested = true,
            },
            contractQualification = new
            {
                decisionCount = "exactly ownedCount; no partial acceptance",
                semanticScore = "NOT_RUN",
                goldRead = false,
                repair = "FORBIDDEN",
                semanticRetry = "FORBIDDEN",
                fallback = "FORBIDDEN",
            },
            rows,
        });
        FreezeArtifact.AssertJson(ArtifactRoot, "audit.v1.json", new
        {
            schemaVersion = "v5-p5j-v3-sharded-canary-audit-v1",
            status = "PROVIDER_FREE_COMPLETE",
            providerCalls = 0,
            goldRead = false,
            selectedRequests = resolved.Length,
            maximumFutureCalls = 4,
            executionAuthorized = false,
            ownershipDrift,
            visibilityDrift,
            sourceEvidenceDrift,
            identityDrift,
            relationTargetResolutionDrift,
            wireBodiesDeterministic = true,
            repeatGuardStopsBeforeNetwork = true,
            p5f2Single96DecisionLane = "CLOSED_NOT_PROMOTED",
            nextProviderAction = "NONE without a new explicit authorization for exactly these four P5J bodies",
        });
    }

    private static ResolvedRole Resolve(RoleSpec spec, IReadOnlyList<(string DocumentId, V5PackedDecisionRequestV3 Pack)> originals)
    {
        var original = Assert.Single(originals, item => item.DocumentId == spec.DocumentId && item.Pack.PackId == spec.PackId).Pack;
        var shards = V5DecisionShardingV3.Shard(original, Contract, Envelope, MaxOwnedPerShard);
        var shard = Assert.Single(shards, candidate => candidate.Coordinates.Any(coordinate => coordinate.SourceAlias == spec.SubjectAlias));
        var coordinate = Assert.Single(shard.Coordinates, candidate => candidate.SourceAlias == spec.SubjectAlias);
        Assert.Contains(spec.SubjectAlias, original.OwnedAliases, StringComparer.Ordinal);
        Assert.All(spec.AdditionalSubjectAliases, alias => Assert.Contains(alias, shard.Request.OwnedAliases, StringComparer.Ordinal));
        Assert.All(spec.AdditionalSubjectAliases, alias =>
            Assert.True(IndexOf(shard.Request.OwnedAliases, alias) > coordinate.ShardLocalDecisionOrdinal));
        if (spec.RelationTargetAlias is not null)
        {
            Assert.DoesNotContain(spec.RelationTargetAlias, shard.Request.OwnedAliases, StringComparer.Ordinal);
            Assert.Contains(spec.RelationTargetAlias, shard.Request.Packet.ContextOnlyEvidence.Select(node => node.SourceAlias), StringComparer.Ordinal);
        }
        return new ResolvedRole(spec, original, shard, coordinate, shards);
    }

    private static MultipartProof ProveMultipartSubject(ResolvedRole role)
    {
        var atoms = Atoms(role.Spec.DocumentId);
        var additionalLocal = role.Spec.AdditionalSubjectAliases.Select(alias => IndexOf(role.Shard.Request.OwnedAliases, alias)).ToArray();
        var response = SingleClaim(role.Shard, role.Coordinate.ShardLocalDecisionOrdinal,
            new V5SemanticDecisionClaimV3("DOCUMENT_IDENTITY", "P5J multipart proof",
                AdditionalSubjectParts: additionalLocal.Select(index => new V5AdditionalOwnedSubjectPartV3(index)).ToArray(), EvidenceNeeds: []));
        var bound = Assert.Single(V5DecisionShardingV3.Bind(role.Shard, response, Contract, atoms).Bound).Claim;
        Assert.Equal(new[] { role.Spec.SubjectAlias }.Concat(role.Spec.AdditionalSubjectAliases), bound.Subject.Parts.Select(part => part.Alias));
        return new MultipartProof(true, bound.Subject.Parts.Select(part => part.Alias).ToArray());
    }

    private static WholeAtomProof ProveWholeAtomPolicy(ResolvedRole role)
    {
        var atoms = Atoms(role.Spec.DocumentId);
        var atom = atoms.Single(candidate => candidate.Alias == role.Spec.SubjectAlias);
        var wholeAtom = SingleClaim(role.Shard, role.Coordinate.ShardLocalDecisionOrdinal,
            new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "P5J whole atom", EvidenceNeeds: []));
        var accepted = V5DecisionShardingV3.Bind(role.Shard, wholeAtom, Contract, atoms);
        Assert.Single(accepted.Bound);
        var echoed = SingleClaim(role.Shard, role.Coordinate.ShardLocalDecisionOrdinal,
            new V5SemanticDecisionClaimV3("STRUCTURAL_REGION", "P5J whole atom",
                SubjectSelection: new V5DecisionTextSelectionV3(VerbatimText: atom.Text), EvidenceNeeds: []));
        var refused = V5DecisionShardingV3.Bind(role.Shard, echoed, Contract, atoms);
        Assert.Contains("whole-atom-must-omit-verbatim-text", refused.Refusals.Values.Single(), StringComparison.Ordinal);
        return new WholeAtomProof(accepted.Bound.Count == 1, true);
    }

    private static RelationProof ProveCrossShardRelation(ResolvedRole role)
    {
        var targetAlias = Assert.IsType<string>(role.Spec.RelationTargetAlias);
        var targetIndex = role.Shard.Request.Packet.ContextOnlyEvidence.ToList()
            .FindIndex(node => node.SourceAlias == targetAlias);
        Assert.True(targetIndex >= 0);
        var additionalIndex = IndexOf(role.Shard.Request.OwnedAliases, role.Spec.AdditionalSubjectAliases.Single());
        var response = SingleClaim(role.Shard, role.Coordinate.ShardLocalDecisionOrdinal,
            new V5SemanticDecisionClaimV3("REFERENCES",
                AdditionalSubjectParts: [new V5AdditionalOwnedSubjectPartV3(additionalIndex)],
                TargetParts: [new V5VisibleTargetPartV3("CONTEXT_ONLY", targetIndex)], EvidenceNeeds: []));
        var claim = Assert.Single(V5DecisionShardingV3.Bind(role.Shard, response, Contract, Atoms(role.Spec.DocumentId)).Bound).Claim;
        Assert.Equal(targetAlias, Assert.Single(claim.Object!.Parts).Alias);
        return new RelationProof(true, true, new
        {
            sourceGroup = "CONTEXT_ONLY",
            sourceIndex = targetIndex,
            targetAlias,
            resolvedAlias = Assert.Single(claim.Object.Parts).Alias,
        });
    }

    private static V5SemanticDecisionResponseV3 SingleClaim(V5ShardedDecisionRequestV3 shard, int localDecision,
        V5SemanticDecisionClaimV3 claim) =>
        new(shard.Request.OwnedAliases.Select((_, index) => new V5SemanticSubjectDecisionV3(index == localDecision ? [claim] : [])).ToArray());

    private static bool RepeatGuardStopsBeforeNetwork()
    {
        var absentPath = Path.Combine(Path.GetTempPath(), $"p5j-repeat-guard-{Guid.NewGuid():N}.json");
        try
        {
            Assert.False(File.Exists(absentPath));
            Assert.False(ResultArtifactExistsStopsBeforeNetwork(absentPath));
            File.WriteAllText(absentPath, "{}", new UTF8Encoding(false));
            return ResultArtifactExistsStopsBeforeNetwork(absentPath);
        }
        finally
        {
            if (File.Exists(absentPath)) File.Delete(absentPath);
        }
    }

    private static bool ResultArtifactExistsStopsBeforeNetwork(string resultPath) => File.Exists(resultPath);

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        throw new InvalidOperationException($"missing-alias:{value}");
    }

    private static IReadOnlyList<SemanticSourceAtom> Atoms(string documentId) =>
        V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095));

    private static IReadOnlyList<(string DocumentId, V5PackedDecisionRequestV3 Pack)> BuildOriginals()
    {
        var documents = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) };
        return documents.SelectMany(document => V5PdfPreflightBuilder.BuildV3(TestRepository.Path(document.Item2), document.Item1,
            Contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope).Select(pack => (document.Item1, pack))).ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
