using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free preflight of the F1 layer after the PDF source universe became font independent
/// (<c>a99-pdf-glyph-geometry-v2-font-independent</c>). Only SRC-089 (of the five-document qualification cohort)
/// changed universe; SRC-004 and SRC-029 are not in the cohort. Two statements are frozen here:
/// <list type="bullet">
/// <item>SRC-089's new snapshot (<c>pdf-canonical-source-v2</c>, written next to, never over, the v1 history) and the F1
/// request set it yields - the exact call count and sizes a requalification would spend.</item>
/// <item>the other four cohort documents still produce byte-identical F1 requests from a live parse.</item>
/// </list>
/// G2A and H2-C request universes depend on raw accepted F1 / G2A ledgers and cannot be precomputed; they are not frozen here.
/// </summary>
public sealed class V5P6TFullP05F1PdfUniverseV2PreflightTests
{
    private const string SnapshotV1Root = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string SnapshotV2Root = "eval/a99-closed-loop/pdf-canonical-source-v2";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6tf1-src089-pdf-universe-v2-preflight";
    private const string V1Preflight = "artifacts/v5-p6t-function-membership/p6tf1-full-p05-preflight/f1-full-p05-preflight.v1.json";

    private static readonly (string Id, string Pdf, bool Correspondences)[] UnchangedCohort =
    [
        ("SRC-041", SourcePdfCorpus.Src041, false),
        ("SRC-095", SourcePdfCorpus.Src095, true),
        ("DOC-0252", SourcePdfCorpus.Doc0252, false),
        ("DOC-0256", SourcePdfCorpus.Doc0256, false),
    ];

    [Fact]
    public void SRC089_new_universe_snapshot_and_F1_request_set_are_frozen_without_provider()
    {
        var path = TestRepository.Path(SourcePdfCorpus.Src089);
        var sourceSha256 = CanonicalSemanticSourceHash.Compute(path);
        var live = PdfSourceAdapter.BuildWithDetails(path);
        if (FreezeArtifact.UpdateRequested)
            FreezeArtifact.AssertJson(SnapshotV2Root, $"{sourceSha256}.json", PdfCanonicalSourceSnapshotV1.From(live));

        var snapshotPath = Path.Combine(TestRepository.Root(), SnapshotV2Root.Replace('/', Path.DirectorySeparatorChar), $"{sourceSha256}.json");
        var replay = JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(File.ReadAllText(snapshotPath), FreezeArtifact.Json)!.Rehydrate();
        Assert.Equal(live.Snapshot.SourceAliasUniverseHash, replay.SourceAliasUniverseSha256);
        Assert.Equal(live.Snapshot.ModelVisibleEvidenceHash, replay.ModelVisibleEvidenceSha256);

        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, "SRC-089");
        var rows = Rows(plan, readOnlyCorrespondences: true);

        // The v1 snapshot is history: its universe differs, which is the reason this preflight exists.
        using var v1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V1Preflight)));
        var v1Doc = v1.RootElement.GetProperty("documents").EnumerateArray().Single(item => item.GetProperty("documentId").GetString() == "SRC-089");
        Assert.NotEqual(v1Doc.GetProperty("sourceUniverseSha256").GetString(), plan.SourceUniverseSha256);

        FreezeArtifact.AssertJson(OutputRoot, "f1-src089-pdf-universe-v2-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tf1-src089-pdf-universe-v2-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            geometryPolicy = PdfGeometryPolicy.Version,
            authority = new
            {
                source = "CANONICAL_PDF_SOURCE_SNAPSHOT_V2",
                snapshot = $"{SnapshotV2Root}/{sourceSha256}.json",
                packing = "P05_EXACT_ONCE_OWNERSHIP",
                protocol = "V5_TOTAL_OCCURRENCE_FUNCTION_F1",
                transport = "OPENROUTER_QWEN37_JSON_OBJECT_CARRIER_V2_1_REASONING_ENABLED_EFFORT_OMITTED",
            },
            execution = new { providerCalls = 0, retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE", runtimeChanged = false },
            comparisonWithV1 = new
            {
                v1SourceUniverseSha256 = v1Doc.GetProperty("sourceUniverseSha256").GetString(),
                v1OccurrenceTotal = v1Doc.GetProperty("sourceOccurrenceTotal").GetInt32(),
                v1PackCount = v1Doc.GetProperty("p05PackCount").GetInt32(),
            },
            document = new
            {
                documentId = "SRC-089",
                sourceSha256 = plan.SourceSha256,
                sourceUniverseSha256 = plan.SourceUniverseSha256,
                sourceOccurrenceTotal = plan.SourceOccurrenceTotal,
                p05PackCount = plan.Packs.Count,
            },
            cost = new
            {
                maximumF1PrimaryCalls = rows.Count,
                providerBodyBytes = rows.Sum(row => row.Bytes),
                maxCompletionTokens = rows.Sum(row => row.MaxCompletionTokens),
                estimatedInputTokensAtFourBytesPerToken = rows.Sum(row => row.Bytes) / 4,
                note = "estimate only; G2A and H2-C calls depend on the accepted F1 and G2A ledgers and are bounded in the report, not here",
            },
            requests = rows.Select(row => new
            {
                providerCallOrdinal = row.Ordinal,
                packId = row.PackId,
                ownedOccurrenceCount = row.Owned,
                providerBodySha256 = row.BodySha256,
                semanticRequestSha256 = row.SemanticSha256,
                providerBodyBytes = row.Bytes,
                maxCompletionTokens = row.MaxCompletionTokens,
            }).ToArray(),
        });
    }

    [Fact]
    public void The_four_unchanged_cohort_documents_keep_byte_identical_F1_requests_from_a_live_parse()
    {
        using var frozen = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V1Preflight)));
        var requests = frozen.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var temp = Path.Combine(Path.GetTempPath(), "a99-f1-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var (id, pdf, correspondences) in UnchangedCohort)
            {
                var path = TestRepository.Path(pdf);
                var snapshot = Path.Combine(temp, id + ".json");
                File.WriteAllText(snapshot, JsonSerializer.Serialize(
                    PdfCanonicalSourceSnapshotV1.From(PdfSourceAdapter.BuildWithDetails(path)), FreezeArtifact.Json));
                var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshot, id);
                var rows = Rows(plan, correspondences);
                var expected = requests.Where(item => item.GetProperty("documentId").GetString() == id).ToArray();

                Assert.Equal(expected.Length, rows.Count);
                Assert.Equal(
                    expected.Select(item => (item.GetProperty("packId").GetString(), item.GetProperty("providerBodySha256").GetString(), item.GetProperty("semanticRequestSha256").GetString(), item.GetProperty("providerBodyBytes").GetInt32())),
                    rows.Select(row => (row.PackId, row.BodySha256, row.SemanticSha256, row.Bytes))!);
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private sealed record Row(int Ordinal, string PackId, int Owned, string BodySha256, string SemanticSha256, int Bytes, int MaxCompletionTokens);

    private static List<Row> Rows(PdfCandidateAuthorityDocumentPlan plan, bool readOnlyCorrespondences)
    {
        var rows = new List<Row>();
        foreach (var pack in plan.Packs)
        {
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(
                plan, pack, readOnlyCorrespondences ? Correspondences(pack) : EmptyCorrespondences);
            rows.Add(new Row(rows.Count + 1, pack.PackId, pack.OwnedAliases.Count, prepared.ProviderRequestHash,
                prepared.Request.UserMessageSha256, prepared.ProviderRequestBytes, prepared.SourcePack.MaxCompletionTokens));
        }
        return rows;
    }

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
}
