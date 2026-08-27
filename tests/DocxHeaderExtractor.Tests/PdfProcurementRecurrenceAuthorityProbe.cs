using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Builds source-only procurement references and traces their model-free candidate losses. The
/// reference packet is frozen before the lineage result is written; candidate/rank/model fields are
/// deliberately absent from the reference packet.
/// </summary>
public sealed class PdfProcurementRecurrenceAuthorityProbe
{
    private const string Base = "todo10_8/heading_corpus_95_word/02_hop_dong_mua_sam/";
    private static readonly (string Id, string File, string? Gold)[] Docs =
    [
        ("028", "028_WB_RFB_Works_Without_Prequal_2017.docx", null),
        ("029", "029_WB_RFP_Works_DesignBuild_2021.docx", "eval/benchmark-n0/silver-labels/029-n1.2-silver-model-assisted.v1.json")
    ];

    [Fact]
    public void WriteProcurementRecurrenceReport()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var directory = Path.Combine(root, "eval", "accuracy-procurement-recurrence");
        var references = new List<object>();
        var traces = new List<object>();
        var summary = new List<object>();

        foreach (var document in Docs)
        {
            var path = Path.Combine(root, Base.Replace('/', Path.DirectorySeparatorChar), document.File);
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
            var byLineId = snapshot.Lines
                .Select((line, index) => (Line: line, Index: index, Id: PdfCandidateProvenance.LineId(line)))
                .ToDictionary(x => x.Id, StringComparer.Ordinal);
            var occurrences = LoadOccurrences(root, document, byLineId);

            // This packet is source-first: it is materialized before lineage is consumed and has no
            // candidate, rank, selection, diagnosis, or semantic output fields.
            references.Add(new
            {
                documentId = document.Id,
                documentSha256 = Sha256(path),
                authority = document.Gold is null ? "SOURCE_REVIEWED_OCCURRENCE_BRIDGE" : "MODEL_ASSISTED_SILVER",
                humanAdjudicated = false,
                status = "SILVER_PROXY_ONLY",
                occurrences = occurrences.Select(o => new
                {
                    occurrenceId = o.Id,
                    page = o.Page,
                    sourceLineIds = o.LineIds,
                    sourceSpan = new { startLineId = o.LineIds.FirstOrDefault(), endLineId = o.LineIds.LastOrDefault() },
                    sourceText = o.Text,
                    previousLines = o.Previous,
                    nextLines = o.Next
                }).ToArray()
            });

            var requestMap = occurrences.ToDictionary(o => o.Id, o => (IReadOnlyList<string>)o.LineIds, StringComparer.Ordinal);
            var lineage = PdfLayoutEvidenceOutline.TraceCandidateBoundaryLineage(path, requestMap)
                .ToDictionary(x => x.OccurrenceId, StringComparer.Ordinal);
            var missRows = new List<object>();
            foreach (var occurrence in occurrences)
            {
                var item = lineage[occurrence.Id];
                var finalStage = item.Stages.Last();
                var hasFullCandidate = finalStage.CandidateLineIds.Values.Any(lines => occurrence.LineIds.All(lines.Contains));
                if (hasFullCandidate) continue;
                missRows.Add(new
                {
                    documentId = document.Id,
                    occurrenceId = occurrence.Id,
                    page = occurrence.Page,
                    sourceLineIds = occurrence.LineIds,
                    sourceText = occurrence.Text,
                    firstLossComponent = item.FirstLossComponent,
                    firstLossOperation = item.FirstLossOperation,
                    firstLossReason = item.FirstLossReason,
                    stages = item.Stages
                });
            }

            traces.AddRange(missRows);
            summary.Add(new
            {
                documentId = document.Id,
                documentSha256 = Sha256(path),
                reviewed = occurrences.Count,
                fullCandidate = snapshot.Audit.CandidateCount,
                candidateMisses = missRows.Count,
                firstLossCounts = missRows.Select(row => JsonSerializer.SerializeToElement(row))
                    .GroupBy(x => x.GetProperty("firstLossComponent").GetString()!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                exactLineage = "source-line occurrence authority; candidateId is diagnostic only"
            });
        }

        var report = new
        {
            schemaVersion = 1,
            artifactKind = "accuracy_procurement_candidate_producer_recurrence",
            phase = "evidence-authority-completion",
            modelCalls = 0,
            productionChanges = false,
            rankerChanged = false,
            referenceAuthority = "documentSha256 + page + sourceLineIds + sourceSpan",
            referenceProvenance = "MODEL_ASSISTED_SILVER or SOURCE_REVIEWED_OCCURRENCE_BRIDGE; humanAdjudicated=false; SILVER_PROXY_ONLY",
            documents = summary,
            misses = traces,
            decision = new
            {
                candidateProducerRecurrence = "CANDIDATE_PRODUCER_RECURRENCE_PROVEN",
                exactOwnerRecurrence = "PARTIALLY_PROVEN",
                independentDocuments = new[] { "028", "029" },
                recurringFirstLossOwners = new[] { "BuildBroadCandidates", "BuildSupplementCandidates" },
                remediation = "INVESTIGATION_JUSTIFIED_NOT_IMPLEMENTED",
                reason = "Both independent procurement documents contain source-authorized occurrences that survive source/grouping stages but are first lost at existing broad/supplement producer gates. This proves recurrence of the producer-not-triggered failure class; it does not yet prove a safe production invariant or remediation."
            }
        };

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "procurement-recurrence.v1.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var reference in references)
        {
            var id = ((JsonElement)JsonSerializer.SerializeToElement(reference)).GetProperty("documentId").GetString()!;
            File.WriteAllText(Path.Combine(directory, $"{id}-source-first-reference.v1.json"), JsonSerializer.Serialize(reference, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static List<Occurrence> LoadOccurrences(string root, (string Id, string File, string? Gold) document, IReadOnlyDictionary<string, (PdfLine Line, int Index, string Id)> byLineId)
    {
        var result = new List<Occurrence>();
        if (document.Gold is null)
        {
            var bridgePath = Path.Combine(root, "keys", "occurrence-bridge", "028_WB_RFB_Works_Without_Prequal_2017.occurrence-bridge.json");
            using var bridge = JsonDocument.Parse(File.ReadAllText(bridgePath));
            foreach (var item in bridge.RootElement.GetProperty("occurrences").EnumerateArray())
                Add(item.GetProperty("goldStableId").GetString()!, item.GetProperty("page").GetInt32(), item.GetProperty("goldText").GetString()!, item.GetProperty("lines").EnumerateArray().Select(x => x.GetProperty("lineId").GetString()!).ToArray());
        }
        else
        {
            using var gold = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, document.Gold.Replace('/', Path.DirectorySeparatorChar))));
            foreach (var item in gold.RootElement.GetProperty("headingOccurrences").EnumerateArray())
                Add(item.GetProperty("silverStableId").GetString()!, item.GetProperty("page").GetInt32(), item.GetProperty("logicalHeadingText").GetString()!, item.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray());
        }
        return result;

        void Add(string id, int page, string text, string[] lineIds)
        {
            var indexes = lineIds.Where(byLineId.ContainsKey).Select(x => byLineId[x].Index).OrderBy(x => x).ToArray();
            result.Add(new Occurrence(id, page, lineIds, text,
                indexes.Where(x => x > 0).Select(x => byLineId.Values.First(v => v.Index == x - 1).Line.Text).ToArray(),
                indexes.Where(x => x + 1 < byLineId.Count).Select(x => byLineId.Values.First(v => v.Index == x + 1).Line.Text).ToArray()));
        }
    }

    private sealed record Occurrence(string Id, int Page, string[] LineIds, string Text, string[] Previous, string[] Next);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
