using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free P6T-E1 freeze: topology only, no document-function vocabulary.</summary>
public sealed class V5P6TE1UnitTopologyContractTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6te1-unit-topology";

    [Fact]
    public void P6TE1_freezes_total_topology_ledger_without_function_labels()
    {
        var rows = new List<object>();
        foreach (var (documentId, source) in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareUnitTopologyE1(plan, pack, BuildCorrespondences(pack));
            var request = prepared.Request;
            Assert.Equal(96, request.Occurrences.Count);
            Assert.DoesNotContain("HEADING_START", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("REPRESENTATION_START", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("HEADING", request.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REPRESENTATION", request.UserMessage, StringComparison.OrdinalIgnoreCase);
            var synthetic = JsonSerializer.Serialize(new { decisions = request.Occurrences.Select((value, index) => new { occurrence = value.Id, topology = index == 0 ? "STARTS_UNIT" : index == 1 ? "CONTINUES_UNIT" : "NO_UNIT_BOUNDARY" }).ToArray() });
            using var payload = JsonDocument.Parse(synthetic);
            var parsed = V5UnitTopologyProtocolE1.Parse(payload.RootElement, System.Text.Encoding.UTF8.GetByteCount(synthetic), PdfCandidateAuthorityQualificationAdapter.ResponseUtf8ByteCap, request.Occurrences);
            Assert.Equal(96, parsed.Decisions.Count);
            Assert.Equal(1, parsed.Decisions.Count(value => value.Role == V5UnitTopologyRoleE1.STARTS_UNIT));
            Assert.Equal(1, parsed.Decisions.Count(value => value.Role == V5UnitTopologyRoleE1.CONTINUES_UNIT));
            Assert.Equal(94, parsed.Decisions.Count(value => value.Role == V5UnitTopologyRoleE1.NO_UNIT_BOUNDARY));
            rows.Add(new { documentId, packId = pack.PackId, issued = request.Occurrences.Count, requestHash = request.UserMessageSha256, systemPromptSha256 = Hashing.Sha256(request.SystemPrompt), providerRequestHash = prepared.ProviderRequestHash, providerRequestBytes = prepared.ProviderRequestBytes, responseContract = "TOTAL_TOPOLOGY_ONLY", roles = new[] { "STARTS_UNIT", "CONTINUES_UNIT", "NO_UNIT_BOUNDARY" } });
        }
        FreezeArtifact.AssertJson(OutputRoot, "two-pack-unit-topology-manifest.v1.json", new
        {
            schemaVersion = "v5-p6te1-unit-topology-manifest-v1", status = "PREPARED_NOT_AUTHORIZED", providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            matchedVariable = "TOPOLOGY_ONLY_FROM_P6TD", functionLabels = "FORBIDDEN", extentPass = "BLOCKED", expectedDecisions = 96, maximumFutureProviderCalls = 2, retry = 0, repair = false, fallback = false, rows,
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal); var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias; var list = result.TryGetValue(key, out var existing) ? existing.ToList() : new List<V5ReadOnlyCorrespondenceV1>();
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }
}
