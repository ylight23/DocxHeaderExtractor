using System.Numerics;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6FLocatorShardingFeasibilityTests
{
    private const string Root = "artifacts/v5-p6f-locator-sharding-feasibility";
    [Fact]
    public void Measure_full31_symbolic_lower_bound_without_provider()
    {
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300);
        var rows = new List<object>(); var maxRecord = 0;
        foreach (var (id, pdf, expected) in new[] { ("SRC-089", SourcePdfCorpus.Src089, 7), ("SRC-095", SourcePdfCorpus.Src095, 24) })
        {
            var packs = V5PdfPreflightBuilder.BuildV3(TestRepository.Path(pdf), id, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
            Assert.Equal(expected, packs.Count);
            foreach (var (pack, ordinal) in packs.Select((p, i) => (p, i + 1)))
            {
                var atoms = pack.Packet.SubjectEvidence.Select((n, i) => new SemanticSourceAtom(n.SourceAlias, n.SourceId, n.SourceOrdinal, 0, i, 0, n.Text)).ToArray();
                var r = RequestLocalLocatorRegistry.Create(atoms);
                var parts = atoms.Select((a, i) => a.Text.Length > 1
                    ? new OccurrenceLocatorPart(r.AtomHandle(i), r.BoundaryHandle(i, 0), r.BoundaryHandle(i, 1))
                    : new OccurrenceLocatorPart(r.AtomHandle(i))).ToArray();
                var occurrence = new OccurrenceLocator(parts[0], parts.Skip(1).ToArray(), ["DOCUMENT_IDENTITY", "NAVIGATION_REPRESENTATION", "STRUCTURAL_REGION"]);
                var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(occurrence)); maxRecord = Math.Max(maxRecord, bytes);
                rows.Add(new { documentId = id, parentOrdinal = ordinal, owned = atoms.Length, maxOccurrenceRecordBytes = bytes });
            }
        }
        using var p6a = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("artifacts/v5-p6a-source-domain-sharding/source-domain-sharding.v1.json")));
        var symbolic = p6a.RootElement.GetProperty("cohort").GetProperty("largestSourceDomain").GetProperty("rootedMultipartIdentityUpperBound").GetString()!;
        var universe = BigInteger.Parse(symbolic); var recordsPerResponse = Math.Max(1, 49_152 / maxRecord);
        var leaves = (universe + recordsPerResponse - 1) / recordsPerResponse; var depth = 0; for (var capacity = BigInteger.One; capacity < leaves; capacity <<= 1) depth++;
        FreezeArtifact.AssertJson(Root, "feasibility.v1.json", new { schemaVersion = "v5-p6f-feasibility-v1", providerCalls = 0, goldRead = false, goldMutation = "NONE", packs = rows.Count, maxOccurrenceRecordBytes = maxRecord, conservativeRecordsPer49152ByteResponse = recordsPerResponse, largestSymbolicLocatorUniverse = symbolic, minimumWorstCaseLeaves = leaves.ToString(), minimumBinaryTreeDepth = depth, verdict = "SOURCE_DOMAIN_EXHAUSTIVE_SHARDING_CORRECT_BUT_OPERATIONALLY_INFEASIBLE", providerManifest = "NOT_PREPARED", sharedRuntime = "UNCHANGED", note = "Lower bound uses real full-31 locator serialization and symbolic P6A universe; it is a feasibility bound, not semantic occurrence density." });
    }
}
