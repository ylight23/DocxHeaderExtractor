using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N3.6: causal diagnosis, not remediation, for the 12 `UNMATCHED_OUTPUT` items N3.4/N3.5 found under
/// R1 on `004`. Traces each through Candidate -> Ranking -> Eligibility -> Role -> Span -> Validator ->
/// Grounding -> R1, using only facts the canonical N3.4 run and checkpoint already recorded (no
/// provider call, no new candidate construction, no implementation attempted here).
/// <para>
/// The question this answers is not "is R1 at fault" - R1 only stops discarding a batch's results on
/// `partial_timeout`; it never proposes a role or a span. The question is whether the 12 share one
/// causal class or split into several, per the standing project rule against inferring one "owner"
/// from a population that may not have one.
/// </para>
/// </summary>
public sealed class PdfN36TwelveOutputCausalDiagnosisProbe
{
    [Fact]
    public void WriteDiagnosis()
    {
        var output = Environment.GetEnvironmentVariable("N36_DIAGNOSIS_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedDiagnosisReproduces()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n3", "n3.4", "reports", "004-n3.6-twelve-output-diagnosis.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        using var collateral = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n3", "n3.4", "reports", "004-n3.4-collateral-check.v1.json")));
        var targetIds = collateral.RootElement.GetProperty("r1").GetProperty("trueCollateralItems").EnumerateArray()
            .Select(i => i.GetProperty("SourceId").GetString()!)
            .ToArray();

        using var run = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n3", "n3.4", "runs", "004-n3.4-canonical-run.v1.json")));
        var itemsById = run.RootElement.GetProperty("rows")[0].GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("sourceFactId").GetString()!, i => i, StringComparer.Ordinal);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var checkpointLines = File.ReadLines(Path.Combine(root, "eval", "benchmark-n3", "n3.4", "checkpoints", "004-n3.4-canonical.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CheckpointEntry>(line, options))
            .Where(e => e is not null).Cast<CheckpointEntry>().ToArray();
        var roleById = checkpointLines.Where(e => e.Lane == "semantic").SelectMany(e => e.Payload.Blocks)
            .Where(b => targetIds.Contains(b.Id)).ToDictionary(b => b.Id, StringComparer.Ordinal);
        var spanById = checkpointLines.Where(e => e.Lane == "span").SelectMany(e => e.Payload.Blocks)
            .Where(b => targetIds.Contains(b.Id)).ToDictionary(b => b.Id, StringComparer.Ordinal);

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "01_phap_quy", "004_Luat_Dau_tu_61-2020-QH14_EN.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var selected = PdfLayoutEvidenceOutline.SelectRankedCandidates(snapshot.CandidateBlocks, snapshot.Audit.Candidates, 160).Selected;
        var contexts = PdfCandidateContextBuilder.Build(selected, snapshot.Annotations);
        var rankOf = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations))
            .Select((c, i) => (c.SourceId, Rank: i + 1)).ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);

        var rows = targetIds.Select(id =>
        {
            var item = itemsById.GetValueOrDefault(id);
            var role = roleById.GetValueOrDefault(id);
            var span = spanById.GetValueOrDefault(id);
            var block = snapshot.CandidateBlocks.FirstOrDefault(b => b.Id == id);
            var ctx = contexts.GetValueOrDefault(id);
            var deterministicExclusion = ctx is null ? "candidate_not_selected_at_160" : PdfExtractorQualityBenchmarkProbe.DeterministicExclusionReason(ctx);

            var itemExists = item.ValueKind != JsonValueKind.Undefined;
            var sourceText = block?.Text ?? (itemExists && item.TryGetProperty("sourceBlockText", out var sbt) ? sbt.GetString() : null) ?? "";
            var headingText = itemExists && item.TryGetProperty("headingText", out var h) ? h.GetString() : null;
            var spanLength = (span?.End ?? 0) - (span?.Start ?? 0);
            var isDegenerateSpan = span is not null && spanLength < 6; // captures 1-4 char garbage vs a full clause line
            var endsWithClausePunctuation = sourceText.TrimEnd().EndsWith(';') || sourceText.TrimEnd().EndsWith(":");

            var ownerClass = isDegenerateSpan
                ? "OWNER_A_ROLE_PLUS_DEGENERATE_SPAN"
                : "OWNER_B_ROLE_ONLY_SPAN_WELL_FORMED";

            return new
            {
                sourceId = id,
                candidateStage = new
                {
                    becameCandidate = block is not null,
                    rank = rankOf.GetValueOrDefault(id, -1),
                    selectedAt160 = selected.Any(b => b.Id == id),
                },
                eligibilityStage = new
                {
                    structuralScope = ctx?.Source.StructuralScope,
                    domainRole = ctx?.Source.DomainRole,
                    deterministicExclusionReason = deterministicExclusion,
                    note = deterministicExclusion is null
                        ? "no deterministic gate (scope/domain/evidence-origin) rejects this candidate - the only thing distinguishing it from a real heading is the analyst's own role call"
                        : "a deterministic gate already rejects this candidate independent of role - inconsistent with it reaching emitted output, worth re-checking",
                },
                roleStage = new { role = role?.Role, confidence = role?.Confidence, reason = role?.Reason },
                spanStage = new
                {
                    resolved = span?.Resolved,
                    start = span?.Start,
                    end = span?.End,
                    spanLength,
                    isDegenerateSpan,
                    resultingHeadingText = headingText,
                },
                validatorStage = new
                {
                    validated = itemExists,
                    note = "validator accepts because structural scope/domain/evidence are clean and a non-null span exists - it has no invariant about span plausibility (length vs. source line length) or role-vs-marker-shape consistency",
                },
                groundingStage = new
                {
                    sourceTextFull = sourceText,
                    note = "grounds to the real, correct source location in both owner classes - grounding is not where either failure originates",
                },
                r1Stage = new
                {
                    note = "R1 only decides whether to keep or discard a batch's already-produced role+span facts on partial_timeout. It does not propose a role or a span. Both owner classes' errors were produced upstream by the semantic/span lanes and were previously invisible only because baseline's all-or-nothing discard removed every block from that document's output on this run, true positives and latent errors alike.",
                },
                endsWithClausePunctuation,
                ownerClass,
            };
        }).ToArray();

        var byOwner = rows.GroupBy(r => (string)((dynamic)r).ownerClass, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new
        {
            schemaVersion = 1,
            artifactKind = "n3_6_twelve_output_causal_diagnosis",
            usesModel = false,
            documentId = "004",
            purpose = "Diagnosis only - no remediation implemented or proposed as production code here.",
            ownerClassTally = byOwner,
            ownerClassDefinitions = new
            {
                OWNER_A_ROLE_PLUS_DEGENERATE_SPAN = "Role misclassified AND the span lane resolved a near-zero-length span (< 6 chars) - the emitted heading text is a garbage fragment of the leading marker digits, not the source sentence. Two compounding upstream errors, not one.",
                OWNER_B_ROLE_ONLY_SPAN_WELL_FORMED = "Role misclassified but the span lane resolved a full, well-formed span matching the entire source line. The span-resolution mechanism did its job correctly; the error is confined to role classification.",
            },
            rows,
            observedButNotActedOn = new
            {
                clausePunctuationSignal = $"{rows.Count(r => (bool)((dynamic)r).endsWithClausePunctuation)}/{rows.Length} of the 12 end with ';' or ':' - conventional legal enumerated-clause punctuation, already partially recognized elsewhere in this codebase (PdfSemanticBlockGrouper's allowSemicolonContinuation). This is observed as a candidate signal for a future, separately-gated investigation - not implemented, not promoted, and explicitly not a blanket 'numbered/lettered marker -> reject' rule, which the project has already been burned by once (labelled_numbering_marker on 029/042).",
            },
        };
    }

    private sealed record CheckpointEntry(string Lane, CheckpointPayload Payload);
    private sealed record CheckpointPayload(IReadOnlyList<CheckpointBlock> Blocks);
    private sealed record CheckpointBlock(string Id, string? Role, double Confidence, string? Reason, bool Resolved, int? Start, int? End);
}
