using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6S-B audits the deterministic correspondence candidate cap as pair coverage, not the old
/// misleading union-of-targets measure.  Heading Gold contains no representation/relation truth,
/// therefore this test deliberately does not invent a "correct target" verdict.
/// </summary>
public sealed class V5P6SRelationAuthorityAuditTests
{
    private const string Root = "artifacts/v5-p6s-candidate-authority";
    private static readonly (string Id, string Pdf)[] Documents =
    [ ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) ];

    [Fact]
    public void P6SB_reports_relation_pair_coverage_and_freezes_absence_of_relation_truth()
    {
        var packRows = new List<object>();
        var totalPotential = 0; var totalIssued = 0; var totalTruncated = 0;
        foreach (var spec in Documents)
        {
            var authority = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(spec.Pdf));
            var byAlias = authority.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var packs = SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, authority.LayoutBlockByAtom);
            foreach (var pack in packs)
            {
                var owned = pack.Owned.Select(item => byAlias[item.SourceAlias]).ToArray();
                var capped = V5CandidateUniverseV1.Build(owned, authority.Atoms, V5CandidatePolicyV1.Default);
                var all = V5CandidateUniverseV1.Build(owned, authority.Atoms,
                    V5CandidatePolicyV1.Default with { MaxRelationsPerCandidate = int.MaxValue });
                var potential = all.Relations.Select(Pair).ToHashSet(StringComparer.Ordinal);
                var issued = capped.Relations.Select(Pair).ToHashSet(StringComparer.Ordinal);
                Assert.True(issued.IsSubsetOf(potential));
                Assert.Equal(potential.Count - issued.Count, capped.RelationsTruncated);
                Assert.Equal(issued.Count, capped.Relations.Count);

                var ordinalByAlias = authority.Atoms.ToDictionary(atom => atom.Alias, atom => atom.Ordinal, StringComparer.Ordinal);
                var candidateById = all.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
                var ranks = all.Relations.GroupBy(item => item.CandidateId, StringComparer.Ordinal)
                    .SelectMany(group => group.OrderBy(relation => Math.Abs(
                            ordinalByAlias[TargetPrimaryAlias(relation.TargetSpanIdentity)] -
                            ordinalByAlias[candidateById[relation.CandidateId].Endpoint.Parts[0].Alias]))
                        .ThenBy(relation => relation.TargetSpanIdentity, StringComparer.Ordinal)
                        .Select((relation, index) => new { relation, rank = index + 1, issued = issued.Contains(Pair(relation)) }))
                    .ToArray();
                Assert.All(ranks.Where(item => item.issued), item => Assert.InRange(item.rank, 1, V5CandidatePolicyV1.Default.MaxRelationsPerCandidate));
                Assert.All(ranks.Where(item => !item.issued), item => Assert.True(item.rank > V5CandidatePolicyV1.Default.MaxRelationsPerCandidate));
                totalPotential += potential.Count; totalIssued += issued.Count; totalTruncated += capped.RelationsTruncated;
                packRows.Add(new
                {
                    spec.Id, pack = pack.PackId, representationOccurrencesReviewed = capped.Candidates.Count,
                    correspondencePairsBeforeTruncation = potential.Count, issuedPairs = issued.Count,
                    pairsTruncated = capped.RelationsTruncated,
                    issuedRankMax = ranks.Where(item => item.issued).Select(item => item.rank).DefaultIfEmpty(0).Max(),
                    omittedRankMin = ranks.Where(item => !item.issued).Select(item => item.rank).DefaultIfEmpty(0).Min(),
                });
            }
        }
        Assert.Equal(2_015, totalIssued); Assert.Equal(2_820, totalTruncated);
        Assert.Equal(totalPotential, totalIssued + totalTruncated);
        FreezeArtifact.AssertJson(Root, "relation-truncation-coverage.v1.json", new
        {
            schemaVersion = "v5-p6s-relation-pair-coverage-v1",
            relationAuthority = "READ_ONLY_DIAGNOSTIC_ONLY; model response grammar cannot emit R#",
            representationOccurrencesReviewed = packRows.Sum(row => (int)row.GetType().GetProperty("representationOccurrencesReviewed")!.GetValue(row)!),
            correspondencePairsBeforeTruncation = totalPotential,
            issuedPairs = totalIssued, pairsTruncated = totalTruncated,
            targetTruth = "ABSENT: canonical 139-occurrence Gold has heading membership only",
            correctTargetAvailableBeforeTruncation = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
            correctTargetSurvivedMaxRelationsPerCandidate8 = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
            relationRecallAfterTruncation = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
            correctTargetDistanceRank = "NOT_EVALUABLE_WITHOUT_RELATION_GOLD",
            capMechanics = "issued pair ranks are exactly 1..8; omitted pairs are rank > 8 by deterministic source-distance order",
            providerCalls = 0, goldRead = false, sharedRuntime = "UNCHANGED", packs = packRows,
        });
    }

    private static string Pair(V5IssuedRelationV1 relation) => $"{relation.CandidateId}\u001f{relation.TargetSpanIdentity}";
    private static string TargetPrimaryAlias(string identity)
    {
        var firstPart = identity.Split('|')[0];
        return firstPart[..firstPart.LastIndexOf(':')];
    }
}
