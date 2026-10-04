using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free matched P6T-C preparation: P6T-A plus read-only text correspondence evidence only.</summary>
public sealed class V5P6TCCorrespondenceEvidenceContractTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence";

    [Fact]
    public void P6TC_changes_only_read_only_correspondence_evidence_and_remains_total_role_compatible()
    {
        var rows = new List<object>();
        foreach (var (documentId, source) in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var baseline = PdfTotalOccurrenceRoleQualificationAdapter.Prepare(plan, pack);
            var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
            var correspondences = BuildCorrespondences(pack);
            var treatment = PdfTotalOccurrenceRoleQualificationAdapter.PrepareWithReadOnlyCorrespondences(plan, pack, correspondences);
            Assert.Equal(baseline.Request.SystemPrompt, treatment.Request.SystemPrompt);
            Assert.Equal(baseline.Request.Occurrences.Select(value => value.Id), treatment.Request.Occurrences.Select(value => value.Id));
            Assert.Equal(96, treatment.Request.Occurrences.Count);
            using var request = JsonDocument.Parse(treatment.Request.UserMessage);
            Assert.Equal(96, request.RootElement.GetProperty("occurrences").GetArrayLength());
            Assert.All(request.RootElement.GetProperty("occurrences").EnumerateArray(), occurrence =>
            {
                Assert.True(occurrence.TryGetProperty("correspondences", out var values));
                Assert.Equal(JsonValueKind.Array, values.ValueKind);
                Assert.False(values.EnumerateArray().Any(value => value.TryGetProperty("id", out _) || value.TryGetProperty("candidate", out _) || value.TryGetProperty("locator", out _) || value.TryGetProperty("sourceParts", out _)));
            });
            var valid = JsonSerializer.Serialize(new { decisions = treatment.Request.Occurrences.Select(value => new { occurrence = value.Id, role = "OTHER" }).ToArray() });
            var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(treatment, valid);
            Assert.Equal(96, parsed.Decisions.Count);
            rows.Add(new
            {
                documentId, packId = pack.PackId, issuedOccurrences = treatment.Request.Occurrences.Count,
                baselineRequestHash = baseline.Request.UserMessageSha256, treatmentRequestHash = treatment.Request.UserMessageSha256,
                baselineProviderRequestHash = baseline.ProviderRequestHash, treatmentProviderRequestHash = treatment.ProviderRequestHash,
                correspondenceAnchors = correspondences.Count, correspondenceEdges = correspondences.Values.Sum(value => value.Count),
                responseContract = "UNCHANGED_TOTAL_ANCHOR_ROLE", responseParser = "ACCEPTED_SYNTHETIC_96_OTHER",
            });
        }
        FreezeArtifact.AssertJson(OutputRoot, "two-pack-correspondence-evidence-manifest.v1.json", new
        {
            schemaVersion = "v5-p6tc-read-only-correspondence-evidence-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            matchedVariable = "ONLY_OCCURRENCE_READ_ONLY_CORRESPONDENCES", locatorOutput = "FORBIDDEN", relationIds = "FORBIDDEN",
            roles = new[] { "HEADING_START", "REPRESENTATION_START", "OTHER" }, expectedDecisions = 96, maximumFutureProviderCalls = 2,
            retry = 0, repair = false, fallback = false, rows,
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
            var list = result.TryGetValue(candidate.Endpoint.Parts[0].Alias, out var existing) ? existing.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[candidate.Endpoint.Parts[0].Alias] = list;
        }
        return result;
    }
}
