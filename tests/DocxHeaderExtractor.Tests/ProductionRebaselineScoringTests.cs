using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline score of the PDF production re-baseline. Runs only after the provider execution finished:
/// it replays the recorded responses, in leaf order, through the production adapter and scores what
/// production emits against Gold with the exact scorer. Gold is opened here and nowhere earlier.
/// <para>
/// A new baseline: the request bytes differ from T3/T4, so the score is reported on its own and
/// never as a causal delta against earlier runs.
/// </para>
/// </summary>
public sealed class ProductionRebaselineScoringTests
{
    private const string Root = "eval/a99-closed-loop/production-rebaseline-v1";
    private static readonly (string Id, string Pdf)[] Documents =
        [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];

    [Fact]
    public async Task Freeze_production_rebaseline_score()
    {
        var runDirs = Directory.GetDirectories(TestRepository.Path(Root), "run-*");
        if (runDirs.Length == 0) return; // Nothing executed yet: the score is only defined after the run.
        Assert.Single(runDirs);
        var runDir = runDirs[0];
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "run-manifest.json")));
        var m = manifest.RootElement;
        Assert.False(m.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, m.GetProperty("semanticRecoveryCalls").GetInt32());
        Assert.Equal(0, m.GetProperty("mediumFallbackCalls").GetInt32());
        var rows = m.GetProperty("rows").EnumerateArray().OrderBy(r => r.GetProperty("Ordinal").GetInt32()).ToArray();

        var documents = new List<object>();
        int totalGold = 0, totalTp = 0, totalFp = 0, totalFn = 0;
        foreach (var (id, pdf) in Documents)
        {
            var leafRows = rows.Where(r => r.GetProperty("DocumentId").GetString() == id).ToArray();
            var replies = leafRows.Select(r =>
            {
                var content = Path.Combine(runDir, $"leaf-{r.GetProperty("Ordinal").GetInt32():000}.content.json");
                return r.GetProperty("ContractValidAfterReassembly").GetBoolean() && File.Exists(content)
                    ? File.ReadAllText(content)
                    : "{}";
            }).ToArray();

            using var replay = new FrozenReplyClassifier(replies);
            var authority = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                TestRepository.Path(pdf), replay, CancellationToken.None, runPlacement: false);
            Assert.Equal(0, replay.CallsBeyondRecording);
            Assert.Equal(leafRows.Length, replay.Requests.Count);

            var aliasBySourceId = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms
                .ToDictionary(atom => atom.SourceId, atom => atom.Alias, StringComparer.Ordinal);
            var emitted = authority.EmittedElementIds;
            var produced = authority.Structure.Elements
                .Where(element => emitted is null || emitted.Contains(element.Id))
                .Select(element =>
                {
                    var spans = element.Sources
                        .Select(source => new ExactScorer.Span(aliasBySourceId[source.SourceId], source.Span.Start, source.Span.End))
                        .ToArray();
                    return (Identity: ExactScorer.Identity(spans), element.Text, Spans: spans);
                })
                .GroupBy(x => x.Identity, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToArray();

            var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(pdf));
            var gold = ExactScorer.ReadGold(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json"), universe);
            var hypotheses = produced.Select((p, i) => new ExactScorer.Hypothesis(
                i, p.Text, "TRUE", [], null, null, [], "TITLE", null, [], p.Identity, null, p.Spans)).ToArray();
            var score = ExactScorer.Compute(gold, hypotheses);
            var goldIds = gold.Select(c => c.Identity).ToHashSet(StringComparer.Ordinal);
            var tp = produced.Count(p => goldIds.Contains(p.Identity));
            var fp = produced.Length - tp;
            var fn = gold.Count - tp;
            totalGold += gold.Count; totalTp += tp; totalFp += fp; totalFn += fn;
            documents.Add(new
            {
                documentId = id,
                leaves = leafRows.Length,
                contractValidLeaves = leafRows.Count(r => r.GetProperty("ContractValidAfterReassembly").GetBoolean()),
                gold = gold.Count,
                produced = produced.Length,
                tp,
                fp,
                fn,
                precision = Ratio(tp, tp + fp),
                recall = Ratio(tp, tp + fn),
                f1 = F1(tp, fp, fn),
                exactScorerBuckets = score.Rows.GroupBy(r => r.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
            });
        }

        long Percentile(IEnumerable<long> values, double p)
        {
            var ordered = values.OrderBy(v => v).ToArray();
            return ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling(p * ordered.Length) - 1];
        }
        var summary = m.GetProperty("summary");
        FreezeArtifact.AssertJson(Root, "production-rebaseline-score.v1.json", new
        {
            artifactKind = "a99_pdf_production_rebaseline_score",
            baseline = "NEW_PRODUCTION_QUALITY_BASELINE",
            comparability = "Request bytes and model-visible evidence differ from T3/T4; reported on its own, not as a causal delta.",
            scoredOutput = "production adapter output (primary semantic pass, placement off) replayed from the recorded responses",
            scorer = ExactScorer.ScorerId,
            run = new { runId = m.GetProperty("runId").GetString(), frozenPreflight = m.GetProperty("frozenPreflight") },
            goldOpenedAfterExecution = true,
            execution = new
            {
                primaryProviderCalls = m.GetProperty("primaryProviderCalls").GetInt32(),
                transportRetryCalls = m.GetProperty("transportRetryCalls").GetInt32(),
                semanticRecoveryCalls = 0,
                mediumFallbackCalls = 0,
                firstAttemptContractValid = rows.Count(r => r.GetProperty("ContractValidAfterReassembly").GetBoolean()),
                finishLength = summary.GetProperty("finishLength").GetInt32(),
                totalPromptTokens = summary.GetProperty("totalPromptTokens").GetInt32(),
                totalCompletionTokens = summary.GetProperty("totalCompletionTokens").GetInt32(),
                totalReasoningTokens = summary.GetProperty("totalReasoningTokens").GetInt32(),
                p50WallClockMs = Percentile(rows.Select(r => r.GetProperty("TotalWallClockMs").GetInt64()), 0.50),
                p95WallClockMs = Percentile(rows.Select(r => r.GetProperty("TotalWallClockMs").GetInt64()), 0.95),
            },
            total = new
            {
                gold = totalGold,
                tp = totalTp,
                fp = totalFp,
                fn = totalFn,
                precision = Ratio(totalTp, totalTp + totalFp),
                recall = Ratio(totalTp, totalTp + totalFn),
                f1 = F1(totalTp, totalFp, totalFn),
            },
            documents,
        });
    }

    private static double Ratio(int a, int b) => b == 0 ? 0 : Math.Round((double)a / b, 4);

    private static double F1(int tp, int fp, int fn) =>
        tp == 0 ? 0 : Math.Round(2.0 * tp / (2.0 * tp + fp + fn), 4);
}
