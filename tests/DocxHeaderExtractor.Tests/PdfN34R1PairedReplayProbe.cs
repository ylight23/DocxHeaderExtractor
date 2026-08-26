using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// N3.4 consumes one frozen provider artifact and its append-only checkpoint. It never constructs a
/// classifier: baseline and R1 are paired policy replays over identical role/span evidence.
/// </summary>
public sealed class PdfN34R1PairedReplayProbe
{
    private const string Stem = "004";

    [Fact]
    public void FrozenN34ContractHasOneEligibleLiveDocumentAndR1Only()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "benchmark-n3", "n3.4", "manifest.v1.json")));
        var execution = manifest.RootElement.GetProperty("execution");
        Assert.Equal(1, execution.GetProperty("providerExecutions").GetInt32());
        Assert.Equal([Stem], execution.GetProperty("liveDocuments").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(["030", "043", "058"], execution.GetProperty("noLiveCallDocuments").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(["R1"], manifest.RootElement.GetProperty("frozenInputs").GetProperty("remediationSet").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("qwen/qwen3.5-9b", manifest.RootElement.GetProperty("frozenProfile").GetProperty("model").GetString());
    }

    [Fact]
    public void CanonicalRunReplaysFromOneCheckpointWhenArtifactsExist()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var run = RunPath(root);
        var checkpoint = CheckpointPath(root);
        if (!File.Exists(run) || !File.Exists(checkpoint)) return;

        var paired = BuildPairedReplay(root);
        Assert.Equal(55, paired.R1.Denominators.DecisionRelevant);
        Assert.Equal(paired.R1.LaneStatus.Span, paired.Baseline.LaneStatus.Span);
        if (paired.R1.LaneStatus.Span == "complete")
            Assert.Equal(JsonSerializer.Serialize(paired.Baseline.Stages), JsonSerializer.Serialize(paired.R1.Stages));
    }

    [Fact]
    public void WritePairedReplayArtifacts()
    {
        var output = Environment.GetEnvironmentVariable("BENCH_N3_N34_REPORT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        if (!File.Exists(RunPath(root)) || !File.Exists(CheckpointPath(root)))
            throw new InvalidOperationException("N3.4 canonical run/checkpoint missing; replay is forbidden before its one live trace.");

        var paired = BuildPairedReplay(root);
        Directory.CreateDirectory(output);
        Write(Path.Combine(output, "004-n3.4-baseline-replay.v1.json"), paired.Baseline);
        Write(Path.Combine(output, "004-n3.4-r1-replay.v1.json"), paired.R1);
        Write(Path.Combine(output, "004-n3.4-decision-input.v1.json"), new
        {
            schemaVersion = 1,
            artifactKind = "n3_4_paired_replay_input",
            paired.DocumentId,
            paired.Inputs,
            baselineLaneStatus = paired.Baseline.LaneStatus,
            r1LaneStatus = paired.R1.LaneStatus,
            baselineStages = paired.Baseline.Stages,
            r1Stages = paired.R1.Stages,
            delta = StageCounts.Delta(paired.Baseline.Stages, paired.R1.Stages),
            providerCalls = 0,
            note = "N3.5 owns the promotion decision; this artifact intentionally contains no promotion result.",
        });
    }

    private static PairedReplay BuildPairedReplay(string root)
    {
        var runPath = RunPath(root);
        var checkpointPath = CheckpointPath(root);
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        using var census = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "benchmark-n3", "census", "004-n3.3-census.v1.json")));
        using var silver = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "benchmark-n3", "silver-labels", "004-n3.2-silver-model-assisted.v1.json")));

        var row = run.RootElement.GetProperty("rows").EnumerateArray().Single();
        var documentSha = row.GetProperty("sourceDocumentSha256").GetString()!;
        var occurrences = ReadDecisionRelevantOccurrences(silver.RootElement, census.RootElement);
        var roles = ReadCheckpoint(checkpointPath, "semantic", requireResolved: false, requireHeadingRole: true);
        var spans = ReadCheckpoint(checkpointPath, "span", requireResolved: true, requireHeadingRole: false);
        var items = ReadArtifacts(row.GetProperty("items"));
        var itemBySourceFact = items.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var grounded = row.GetProperty("canonicalGroundings").EnumerateArray()
            .Select(value => value.GetProperty("sourceFactId").GetString()!)
            .Where(itemBySourceFact.ContainsKey).Select(id => itemBySourceFact[id]).ToArray();
        var emittedIds = ReadEmittedSourceFactIds(run.RootElement, row.GetProperty("file").GetString()!);
        var emitted = items.Where(item => emittedIds.Contains(item.SourceId)).ToArray();
        var spanStatus = row.GetProperty("spanLaneStatus").GetString() ?? "unknown";
        var laneStatus = new LaneStatus(row.GetProperty("semanticLaneStatus").GetString() ?? "unknown", spanStatus);

        var r1 = Replay("r1_partial_span_preservation", occurrences, roles, spans, items, grounded, emitted, laneStatus, documentSha, runPath, checkpointPath);
        var baselineUsesNoSpans = spanStatus == "partial_timeout";
        var baseline = Replay("baseline_all_or_nothing_span_discard", occurrences, roles, spans,
            baselineUsesNoSpans ? [] : items,
            baselineUsesNoSpans ? [] : grounded,
            baselineUsesNoSpans ? [] : emitted,
            laneStatus, documentSha, runPath, checkpointPath);
        return new PairedReplay(Stem, new ReplayInputs(
            Relative(root, runPath), Sha256(runPath), Relative(root, checkpointPath), Sha256(checkpointPath),
            Relative(root, Path.Combine(root, "eval", "benchmark-n3", "census", "004-n3.3-census.v1.json")),
            Relative(root, Path.Combine(root, "eval", "benchmark-n3", "silver-labels", "004-n3.2-silver-model-assisted.v1.json"))), baseline, r1);
    }

    private static ReplayReport Replay(string policy, IReadOnlyList<Occurrence> occurrences, IReadOnlyList<Evidence> roles,
        IReadOnlyList<Evidence> spans, IReadOnlyList<Evidence> validated, IReadOnlyList<Evidence> grounded,
        IReadOnlyList<Evidence> emitted, LaneStatus lane, string documentSha, string runPath, string checkpointPath)
    {
        var rows = occurrences.Select(occurrence => new OccurrenceReplay(
            occurrence.StableId,
            occurrence.Page,
            occurrence.SourceLineIds,
            Join(occurrence, roles), Join(occurrence, spans), Join(occurrence, validated),
            Join(occurrence, grounded), Join(occurrence, emitted))).ToArray();
        return new ReplayReport(
            SchemaVersion: 1,
            ArtifactKind: "n3_4_paired_replay",
            DocumentId: Stem,
            Policy: policy,
            Identity: "documentSha256 + page + sourceLineIds/sourceSpan; candidateId is diagnostics-only",
            DocumentSha256: documentSha,
            LaneStatus: lane,
            Inputs: new { canonicalRun = Path.GetFileName(runPath), checkpoint = Path.GetFileName(checkpointPath), providerCalls = 0 },
            Denominators: new Denominators(93, 83, 55, occurrences.Count),
            Stages: new StageCounts(occurrences.Count, Count(rows, x => x.RoleSurvival), Count(rows, x => x.SpanResolved),
                Count(rows, x => x.Validated), Count(rows, x => x.Grounded), Count(rows, x => x.Emitted)),
            Occurrences: rows);
    }

    private static int Count(IEnumerable<OccurrenceReplay> rows, Func<OccurrenceReplay, OccurrenceJoin> stage) =>
        rows.Count(row => stage(row).Status == "EXACT_OCCURRENCE_MATCH");

    private static OccurrenceJoin Join(Occurrence occurrence, IEnumerable<Evidence> evidence)
    {
        var samePage = evidence.Where(item => item.Page == occurrence.Page).ToArray();
        var exact = samePage.FirstOrDefault(item => occurrence.SourceLineIds.All(item.LineIds.Contains));
        if (exact is not null) return new OccurrenceJoin("EXACT_OCCURRENCE_MATCH", exact.SourceId);
        var partial = samePage.FirstOrDefault(item => item.LineIds.Overlaps(occurrence.SourceLineIds));
        return partial is null ? new OccurrenceJoin("UNMATCHED_OUTPUT", null) : new OccurrenceJoin("PARTIAL_SAME_OCCURRENCE", partial.SourceId);
    }

    private static IReadOnlyList<Occurrence> ReadDecisionRelevantOccurrences(JsonElement silver, JsonElement census)
    {
        var wanted = census.GetProperty("occurrences").GetProperty("decisionRelevant").EnumerateArray()
            .Select(value => value.GetProperty("stableId").GetString()!).ToHashSet(StringComparer.Ordinal);
        return silver.GetProperty("headingOccurrences").EnumerateArray()
            .Where(value => wanted.Contains(value.GetProperty("goldStableId").GetString()!))
            .Select(value => new Occurrence(value.GetProperty("goldStableId").GetString()!, value.GetProperty("page").GetInt32(),
                value.GetProperty("sourceLineIds").EnumerateArray().Select(line => line.GetString()!).ToHashSet(StringComparer.Ordinal)))
            .OrderBy(value => value.StableId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<Evidence> ReadCheckpoint(string path, string lane, bool requireResolved, bool requireHeadingRole)
    {
        var evidence = new List<Evidence>();
        foreach (var line in File.ReadLines(path))
        {
            using var json = JsonDocument.Parse(line);
            if (!string.Equals(json.RootElement.GetProperty("lane").GetString(), lane, StringComparison.Ordinal)) continue;
            foreach (var block in json.RootElement.GetProperty("payload").GetProperty("blocks").EnumerateArray())
            {
                if (requireResolved && (!block.TryGetProperty("resolved", out var resolved) || !resolved.GetBoolean())) continue;
                if (requireHeadingRole && (!block.TryGetProperty("role", out var role) ||
                    !string.Equals(role.GetString(), "HeadingTopic", StringComparison.OrdinalIgnoreCase))) continue;
                evidence.Add(ReadEvidence(block));
            }
        }
        return evidence;
    }

    private static IReadOnlyList<Evidence> ReadArtifacts(JsonElement items) => items.EnumerateArray().Select(item =>
        new Evidence(item.GetProperty("sourceFactId").GetString()!, item.GetProperty("page").GetInt32(),
            item.GetProperty("lineIds").EnumerateArray().Select(line => line.GetString()!).ToHashSet(StringComparer.Ordinal))).ToArray();

    private static Evidence ReadEvidence(JsonElement block) => new(
        block.GetProperty("id").GetString()!, block.GetProperty("page").GetInt32(),
        block.GetProperty("lineIds").EnumerateArray().Select(line => line.GetString()!).ToHashSet(StringComparer.Ordinal));

    private static HashSet<string> ReadEmittedSourceFactIds(JsonElement run, string file) =>
        !run.TryGetProperty("legacyProductHeadings", out var products) || !products.TryGetProperty(file, out var headings)
            ? []
            : headings.EnumerateArray().Where(value => value.TryGetProperty("sourceId", out _))
                .Select(value => value.GetProperty("sourceId").GetString()!).ToHashSet(StringComparer.Ordinal);

    private static string RunPath(string root) => Path.Combine(root, "eval", "benchmark-n3", "n3.4", "runs", "004-n3.4-canonical-run.v1.json");
    private static string CheckpointPath(string root) => Path.Combine(root, "eval", "benchmark-n3", "n3.4", "checkpoints", "004-n3.4-canonical.jsonl");
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private sealed record Occurrence(string StableId, int Page, HashSet<string> SourceLineIds);
    private sealed record Evidence(string SourceId, int Page, HashSet<string> LineIds);
    private sealed record OccurrenceJoin(string Status, string? EvidenceSourceId);
    private sealed record OccurrenceReplay(string StableId, int Page, HashSet<string> SourceLineIds, OccurrenceJoin RoleSurvival,
        OccurrenceJoin SpanResolved, OccurrenceJoin Validated, OccurrenceJoin Grounded, OccurrenceJoin Emitted);
    private sealed record LaneStatus(string Semantic, string Span);
    private sealed record Denominators(int Silver, int FullCandidate, int SelectedAt160, int DecisionRelevant);
    private sealed record StageCounts(int DecisionRelevant, int RoleSurvival, int SpanResolved, int Validated, int Grounded, int Emitted)
    {
        public static object Delta(StageCounts baseline, StageCounts r1) => new
        {
            decisionRelevant = r1.DecisionRelevant - baseline.DecisionRelevant,
            roleSurvival = r1.RoleSurvival - baseline.RoleSurvival,
            spanResolved = r1.SpanResolved - baseline.SpanResolved,
            validated = r1.Validated - baseline.Validated,
            grounded = r1.Grounded - baseline.Grounded,
            emitted = r1.Emitted - baseline.Emitted,
        };
    }
    private sealed record ReplayInputs(string CanonicalRun, string CanonicalRunRawSha256, string Checkpoint, string CheckpointRawSha256,
        string Census, string Silver);
    private sealed record ReplayReport(int SchemaVersion, string ArtifactKind, string DocumentId, string Policy, string Identity,
        string DocumentSha256, LaneStatus LaneStatus, object Inputs, Denominators Denominators, StageCounts Stages,
        IReadOnlyList<OccurrenceReplay> Occurrences);
    private sealed record PairedReplay(string DocumentId, ReplayInputs Inputs, ReplayReport Baseline, ReplayReport R1);
}
