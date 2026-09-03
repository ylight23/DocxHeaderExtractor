using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Model-free recurrence inventory for candidate construction/ranking debts. It consumes only
/// source packets and already committed review packets; absent occurrence authority is reported as
/// unavailable rather than reconstructed from aggregate counts.
/// </summary>
public sealed class PdfCrossDocumentEvidenceExpansionProbe
{
    private static readonly (string Id, string Relative, string? Gold)[] Documents =
    [
        ("028", @"02_hop_dong_mua_sam\028_WB_RFB_Works_Without_Prequal_2017.docx", null),
        ("029", @"02_hop_dong_mua_sam\029_WB_RFP_Works_DesignBuild_2021.docx", null),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx", "eval/benchmark-n3/silver-labels/030-n3.2-silver-model-assisted.v1.json"),
        ("003", @"01_phap_quy\003_Luat_Doanh_nghiep_59-2020-QH14.docx", "eval/benchmark-n0/silver-labels/003-n1.2-silver-model-assisted.v1.json"),
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx", "eval/benchmark-n3/silver-labels/004-n3.2-silver-model-assisted.v1.json"),
        ("041", @"03_tai_chinh_ke_toan\041_IBRD_Financial_Statements_June_2025.docx", null),
        ("042", @"03_tai_chinh_ke_toan\042_IDA_Financial_Statements_June_2025.docx", "eval/benchmark-n0/silver-labels/042-n1.2-silver-model-assisted.v1.json"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx", "eval/benchmark-n3/silver-labels/043-n3.2-silver-model-assisted.v1.json"),
        ("056", @"04_giao_trinh\056_OpenStax_Business_Law_I_Essentials.docx", null),
        ("057", @"04_giao_trinh\057_Quantitative_Methods_in_Finance_Lecture_Notes.docx", "eval/benchmark-n0/silver-labels/057-n1.2-silver-model-assisted.v1.json"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx", "eval/benchmark-n3/silver-labels/058-n3.2-silver-model-assisted.v1.json"),
    ];

    [Fact]
    public void WriteEvidenceExpansionArtifact()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var rows = new JsonArray();
        foreach (var document in Documents)
        {
            var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", document.Relative);
            if (!File.Exists(path))
            {
                rows.Add(new JsonObject { ["documentId"] = document.Id, ["status"] = "source_missing" });
                continue;
            }

            var row = new JsonObject
            {
                ["documentId"] = document.Id,
                ["documentSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                ["sourceOnlyCandidateCount"] = null,
                ["sourceOnlyRepresentationCounts"] = null,
                ["sourceExtraction"] = "not_reexecuted_in_lightweight_inventory",
                ["occurrenceAuthority"] = document.Gold is null ? "missing" : "committed_review_packet",
                ["producerRecurrenceStatus"] = "not_proven_without_occurrence_trace"
            };

            if (document.Gold is not null)
            {
                using var gold = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, document.Gold.Replace('/', Path.DirectorySeparatorChar))));
                row["reviewedOccurrenceCount"] = gold.RootElement.GetProperty("headingOccurrences").GetArrayLength();
                row["firstLossTraceStatus"] = "not_run_in_lightweight_inventory";
                row["producerRecurrenceStatus"] = "occurrence_packet_available_but_exact_trace_deferred";
            }

            rows.Add(row);
        }

        var output = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] = "accuracy_evidence_expansion_candidate_ranking",
            ["phase"] = "post-round5-model-free-cross-document-evidence-expansion",
            ["modelCalls"] = false,
            ["productionChanges"] = false,
            ["goldUsedForRuntimeSelection"] = false,
            ["candidateIdCrossRunAuthority"] = false,
            ["scope"] = new JsonArray("028", "029", "030", "003", "004", "041", "042", "043", "056", "057", "058"),
            ["documents"] = rows,
            ["decision"] = new JsonObject
            {
                ["candidateProducerRecurrence"] = "not_yet_proven",
                ["rankingFailureRecurrence"] = "not_yet_proven",
                ["candidateRemediation"] = "not_justified",
                ["rankerRemediation"] = "unresolved",
                ["nextGate"] = "review exact lineage/first-loss distributions before any remediation or learned reranker"
            }
        };

        var directory = Path.Combine(root, "eval", "accuracy-evidence-expansion");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "candidate-ranking-cross-document-evidence.v1.json"), output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
