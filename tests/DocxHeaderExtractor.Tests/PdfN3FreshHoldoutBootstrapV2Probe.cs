using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Correction to <see cref="PdfN3FreshHoldoutBootstrapProbe"/>: its selection rule picked the lowest
/// remaining document id per stratum but never enforced the A3 usability predicate
/// (<c>PdfA3PopulationScreeningProbe</c>'s selected@160 &gt;= 20 AND decisionRelevant &gt;= 15), so it
/// selected 002 and 026 - both already known, before N3 existed, to have zero extractable candidates
/// (0/0/0/0%, confirmed in <c>.verify-build/a3-screening.txt</c>). Their blind packets came back
/// genuinely empty; this is a selection-rule implementation error, not an N3 finding.
/// <para>
/// v1 is left in place, unmodified - not deleted, not silently rewritten. This file adds the missing
/// usability check and re-runs the same frozen tie-break (lowest remaining numeric id) over the
/// corrected eligible set. 043 and 058 were already usable and are unchanged; only the legal and
/// procurement slots move, to 004 and 030 respectively - the next lowest ids that clear the usability
/// bar, using facts (A3 screening) that predate N3 entirely: no N3 label, no R1 output, no model output,
/// no accuracy outcome was consulted to pick the replacements.
/// </para>
/// </summary>
public sealed class PdfN3FreshHoldoutBootstrapV2Probe
{
    private const int SelectedBudget = 160;
    private const int MinimumSelected = 20;
    private const int MinimumDecisionRelevant = 15;
    private const int ContextRadius = 2;
    private static readonly string[] Strata = ["01_phap_quy", "02_hop_dong_mua_sam", "03_tai_chinh_ke_toan", "04_giao_trinh"];
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "001", "002", "003", "026", "028", "029", "032", "041", "042", "054", "056", "057", "091", "092",
    };

    [Fact]
    public void WriteCorrectedBootstrapAndPackets()
    {
        var outputRoot = Environment.GetEnvironmentVariable("BENCH_N3_V2_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(outputRoot)) return;
        WriteAll(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), outputRoot);
    }

    [Fact]
    public void CommittedV2BootstrapAndReplacementPacketsReproduce()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var manifestPath = Path.Combine(root, "keys", "benchmark-n3", "manifest.v2.json");
        var packet004 = Path.Combine(root, "eval", "benchmark-n3", "source-packets", "004-blind-source-review.v1.json");
        var packet030 = Path.Combine(root, "eval", "benchmark-n3", "source-packets", "030-blind-source-review.v1.json");
        if (!File.Exists(manifestPath) || !File.Exists(packet004) || !File.Exists(packet030)) return;

        var temp = Path.Combine(Path.GetTempPath(), "dhx-n3v2-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteAll(root, temp);
            Assert.Equal(Normalize(File.ReadAllText(manifestPath)), Normalize(File.ReadAllText(Path.Combine(temp, "keys", "benchmark-n3", "manifest.v2.json"))));
            Assert.Equal(Normalize(File.ReadAllText(packet004)), Normalize(File.ReadAllText(Path.Combine(temp, "eval", "benchmark-n3", "source-packets", "004-blind-source-review.v1.json"))));
            Assert.Equal(Normalize(File.ReadAllText(packet030)), Normalize(File.ReadAllText(Path.Combine(temp, "eval", "benchmark-n3", "source-packets", "030-blind-source-review.v1.json"))));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static void WriteAll(string root, string outputRoot)
    {
        var corpus = Path.Combine(root, "todo10_8", "heading_corpus_95_word");
        var replacedLegal = SelectFirstUsable(corpus, "01_phap_quy");
        var replacedProcurement = SelectFirstUsable(corpus, "02_hop_dong_mua_sam");
        var kept043 = new Document("03_tai_chinh_ke_toan", "043", Path.Combine(corpus, "03_tai_chinh_ke_toan", "043_IBRD_Financial_Statements_June_2024.docx"));
        var kept058 = new Document("04_giao_trinh", "058", Path.Combine(corpus, "04_giao_trinh", "058_Machine_Learning_Lecture_Note.docx"));
        var documents = new[] { replacedLegal, replacedProcurement, kept043, kept058 };

        var manifest = new
        {
            schemaVersion = 1,
            artifactKind = "n3_fresh_holdout_population",
            supersedes = new
            {
                artifact = "keys/benchmark-n3/manifest.v1.json",
                reason = "SELECTION_RULE_IMPLEMENTATION_ERROR",
                affectedDocuments = new[] { "002", "026" },
                evidence = "pre-existing A3 usability facts (.verify-build/a3-screening.txt) - both already 0/0/0/0% before N3 existed",
                accuracyOutcomeUsed = false,
                modelOutputUsed = false,
                replacementRuleChanged = false,
                detail = "v1's selector picked the lowest remaining non-excluded document id per stratum but never enforced the A3 usability predicate (selected@160 >= 20 AND decisionRelevant >= 15). 002 and 026 have zero extractable candidates and produced genuinely empty blind packets - a selection-rule bug, not an N3 accuracy finding.",
            },
            notASilentReplacement = "keys/benchmark-n3/manifest.v1.json and its 002/026 packets are left in place, unmodified - not deleted or rewritten.",
            purpose = "Fresh source-first holdout, independent from N0/N2 diagnosis. It is not selected for a historical failure shape or candidate outcome.",
            selectionRule = new
            {
                targetStrata = Strata,
                excludedDocumentStems = Excluded.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                usabilityPredicate = $"selected@160 >= {MinimumSelected} AND decisionRelevant >= {MinimumDecisionRelevant} (same threshold as PdfA3PopulationScreeningProbe)",
                tieBreak = "lowest remaining numeric document id within each stratum that also clears the usability predicate",
                prohibitedInputs = new[] { "candidate counts", "candidate rank", "selected status", "gold/silver label", "model output", "historical failure outcome" },
            },
            documents = documents.Select(document => new
            {
                stem = document.Stem,
                domain = document.Domain,
                file = Path.GetRelativePath(root, document.Path).Replace('\\', '/'),
                sourceDocumentSha256 = Sha256(document.Path),
                changedFromV1 = document.Stem is "004" or "030",
            }),
            reviewContract = new
            {
                identity = "documentSha256 + page + sourceLineIds + sourceSpan; candidateId is diagnostics-only and never cross-run identity",
                sourceFirst = true,
                modelCalls = "prohibited until a later separately frozen phase",
            },
        };
        var manifestPath = Path.Combine(outputRoot, "keys", "benchmark-n3", "manifest.v2.json");
        Write(manifestPath, manifest);
        var manifestSha = Sha256(manifestPath);

        foreach (var document in new[] { replacedLegal, replacedProcurement })
        {
            var lines = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(document.Path).Lines;
            var packet = new
            {
                schemaVersion = 1,
                artifactKind = "n3_blind_source_review_packet",
                parentManifestSha256 = manifestSha,
                document.Stem,
                document.Domain,
                documentSha256 = Sha256(document.Path),
                sourceLineExtractionFingerprint = SourceFingerprint(lines),
                reviewInstructions = new
                {
                    question = "Which source occurrences are structural document-outline headings?",
                    allowedLabels = new[] { "REVIEWED_HEADING", "REVIEWED_NON_HEADING", "UNCERTAIN" },
                    prohibitedEvidence = new[] { "candidate id", "candidate score", "rank", "selected status", "scope", "model output", "validated output" },
                },
                items = lines.Select((line, index) => new
                {
                    reviewItemId = $"{document.Stem}/line/{index:D6}",
                    page = line.Page,
                    lineId = PdfCandidateProvenance.LineId(line),
                    sourceSpan = new { startLineId = PdfCandidateProvenance.LineId(line), endLineId = PdfCandidateProvenance.LineId(line) },
                    text = line.Text,
                    previousLines = Neighbors(lines, index, -1),
                    nextLines = Neighbors(lines, index, 1),
                }),
            };
            Write(Path.Combine(outputRoot, "eval", "benchmark-n3", "source-packets", $"{document.Stem}-blind-source-review.v1.json"), packet);
        }
    }

    /// <summary>Same usability check as PdfA3PopulationScreeningProbe, over the candidate pool only -
    /// no gold, no model call - applied here as a selection-eligibility gate instead of a report.</summary>
    private static Document SelectFirstUsable(string corpus, string domain)
    {
        var candidates = Directory.EnumerateFiles(Path.Combine(corpus, domain), "*.docx")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new Document(domain, Path.GetFileNameWithoutExtension(path).Split('_', 2)[0], path))
            .Where(document => !Excluded.Contains(document.Stem));

        foreach (var document in candidates)
        {
            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(document.Path);
            var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
            var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
            var selected = ranked.Take(SelectedBudget).ToArray();
            var decisionRelevant = selected.Count(candidate =>
                contexts.TryGetValue(candidate.SourceId, out var ctx) &&
                PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(ctx) is null);

            if (selected.Length >= MinimumSelected && decisionRelevant >= MinimumDecisionRelevant)
                return document;
        }
        throw new InvalidOperationException($"No usable document found in stratum {domain} after exclusions.");
    }

    private static object[] Neighbors(IReadOnlyList<PdfLine> lines, int index, int direction)
    {
        var result = new List<object>();
        for (var offset = 1; offset <= ContextRadius; offset++)
        {
            var neighborIndex = index + direction * offset;
            if (neighborIndex < 0 || neighborIndex >= lines.Count) break;
            var neighbor = lines[neighborIndex];
            result.Add(new { page = neighbor.Page, lineId = PdfCandidateProvenance.LineId(neighbor), text = neighbor.Text });
        }
        return result.ToArray();
    }

    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string value) => value.Replace("\r\n", "\n");
    private static string SourceFingerprint(IReadOnlyList<PdfLine> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines.Select((line, index) => $"{index}|{PdfCandidateProvenance.LineId(line)}"))))).ToLowerInvariant();
    private sealed record Document(string Domain, string Stem, string Path);
}
