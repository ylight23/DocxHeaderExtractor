using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free P6T-D contract: P6T-C plus explicit total abstention only.</summary>
public sealed class V5P6TDExplicitAbstentionContractTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";

    [Fact]
    public void P6TD_accepts_unresolved_without_reopening_locator_or_pass_two()
    {
        var rows = new List<object>();
        foreach (var (documentId, source) in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var correspondences = BuildCorrespondences(pack);
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareWithExplicitAbstentionAndReadOnlyCorrespondences(plan, pack, correspondences);
            Assert.Equal("v5-total-occurrence-anchor-role-abstention-1", prepared.Request.ProtocolVersion);
            Assert.Equal(96, prepared.Request.Occurrences.Count);
            Assert.DoesNotContain("sourceParts", prepared.Request.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("UNRESOLVED", prepared.Request.SystemPrompt, StringComparison.Ordinal);
            using var request = JsonDocument.Parse(prepared.Request.UserMessage);
            Assert.Equal(96, request.RootElement.GetProperty("occurrences").GetArrayLength());
            Assert.All(request.RootElement.GetProperty("occurrences").EnumerateArray(), value => Assert.True(value.TryGetProperty("correspondences", out _)));

            var response = JsonSerializer.Serialize(new
            {
                decisions = prepared.Request.Occurrences.Select((value, index) => new
                {
                    occurrence = value.Id,
                    role = index == 0 ? "UNRESOLVED" : "OTHER",
                }).ToArray(),
            });
            var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(prepared, response);
            Assert.Equal(96, parsed.Decisions.Count);
            Assert.Equal(1, parsed.Decisions.Count(value => value.Role == V5OccurrenceRoleD.UNRESOLVED));
            Assert.Throws<InvalidOperationException>(() => PdfTotalOccurrenceRoleQualificationAdapter.Parse(prepared,
                JsonSerializer.Serialize(new { decisions = prepared.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "HEADING" }).ToArray() })));
            Assert.Throws<InvalidOperationException>(() => PdfTotalOccurrenceRoleQualificationAdapter.Parse(prepared,
                JsonSerializer.Serialize(new { decisions = prepared.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "UNRESOLVED", extra = true }).ToArray() })));
            rows.Add(new { documentId, packId = pack.PackId, issued = 96, accepted = parsed.Decisions.Count, unresolved = 1, correspondenceAnchors = correspondences.Count, correspondenceEdges = correspondences.Values.Sum(value => value.Count) });
        }

        FreezeArtifact.AssertJson("artifacts/v5-p6t-total-occurrence-role/p6td-explicit-abstention", "two-pack-abstention-manifest.v1.json", new
        {
            schemaVersion = "v5-p6td-explicit-abstention-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            matchedVariable = "ONLY_EXPLICIT_UNRESOLVED_ROLE_FROM_P6TC", roles = new[] { "HEADING_START", "REPRESENTATION_START", "OTHER", "UNRESOLVED" }, expectedDecisions = 96, maximumFutureProviderCalls = 2,
            unresolvedPromotedToPass2 = false, retry = 0, repair = false, fallback = false, rows,
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var list = result.TryGetValue(key, out var existing) ? existing.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }
}
