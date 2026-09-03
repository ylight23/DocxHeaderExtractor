using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>H: measured authority-pipeline work counts plus offline population sensitivity.</summary>
public sealed class PdfHAuthorityPipelineCostModelProbe
{
    private const int K = 160;
    private const int RoleBatchSize = 8;
    private const int SpanBatchSize = 4;
    private static readonly (string Id, string Relative)[] Documents =
    [
        ("004", @"01_phap_quy\004_Luat_Dau_tu_61-2020-QH14_EN.docx"),
        ("030", @"02_hop_dong_mua_sam\030_WB_RFP_Consulting_Services_2019.docx"),
        ("043", @"03_tai_chinh_ke_toan\043_IBRD_Financial_Statements_June_2024.docx"),
        ("058", @"04_giao_trinh\058_Machine_Learning_Lecture_Note.docx")
    ];

    [Fact]
    public void WriteCostModel()
    {
        var json = Environment.GetEnvironmentVariable("BENCH_H_COST_JSON");
        var md = Environment.GetEnvironmentVariable("BENCH_H_COST_MD");
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(md)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var artifact = Build(root);
        json = Path.IsPathRooted(json) ? json : Path.Combine(root, json);
        md = Path.IsPathRooted(md) ? md : Path.Combine(root, md);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(json))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(md))!);
        File.WriteAllText(json, JsonSerializer.Serialize(artifact, Options));
        File.WriteAllText(md, Markdown(artifact));
    }

    [Fact]
    public void CostModelIsMeasuredWithoutProvider()
    {
        var a = Build(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        Assert.Equal(K, a.Config.K);
        Assert.Equal(0, a.PROVIDER_CALLS);
        Assert.False(a.PRODUCTION_CODE_CHANGED);
        Assert.Equal("NOT_OBSERVABLE", a.ACTUAL_PROVIDER_LATENCY);
        Assert.All(a.Documents, d =>
        {
            Assert.True(d.SourceLines > 0);
            Assert.Equal(K, d.SelectedCandidates);
            Assert.True(d.CheckpointWrites > 0);
            Assert.True(d.Counterfactuals[0].RoleInputDelta > 0);
        });
    }

    private static HArtifact Build(string root)
    {
        var runPath = Path.Combine(root, "eval", "accuracy-round6", "k160-semantic-run.v1.json");
        var checkpointPath = Path.Combine(root, "eval", "accuracy-round6", "k160-role-span.jsonl");
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        var checkpointLines = File.ReadAllLines(checkpointPath);
        var docs = Documents.Select(spec => BuildDocument(root, spec, run.RootElement, checkpointLines)).ToArray();
        return new HArtifact(1, "performance_authority_pipeline_cost_model", "H-authority-pipeline-cost-model",
            "frozen k160-semantic-run.v1.json + k160-role-span.jsonl + current source snapshot",
            new Config(K, RoleBatchSize, SpanBatchSize, 90, 120, 300, 2, 0, "k160 semantic execution manifest"), docs,
            "NOT_OBSERVABLE", 0, false);
    }

    private static HDocument BuildDocument(string root, (string Id, string Relative) spec, JsonElement run, string[] checkpointLines)
    {
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", spec.Relative);
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(path);
        var row = run.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("file").GetString()!.StartsWith(spec.Id, StringComparison.Ordinal));
        var prefix = Path.GetFileNameWithoutExtension(row.GetProperty("file").GetString()!) + ".pdf:";
        var records = checkpointLines.Where(line => line.Contains($"\"identity\":\"{prefix}", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var selection = records.Where(x => x.GetProperty("lane").GetString() == "selection").ToArray();
        var role = records.Where(x => x.GetProperty("lane").GetString() == "semantic").ToArray();
        var span = records.Where(x => x.GetProperty("lane").GetString() == "span").ToArray();
        var roleInputs = row.GetProperty("lanes").GetProperty("semantic").GetProperty("scheduled").GetInt32();
        var spanInputs = span.Sum(x => x.GetProperty("payload").GetProperty("blocks").GetArrayLength());
        var writes = records.Length;
        var bytes = checkpointLines.Where(line => line.Contains($"\"identity\":\"{prefix}", StringComparison.Ordinal))
            .Sum(line => Encoding.UTF8.GetByteCount(line) + 1);
        var generated = row.GetProperty("analystCoverage").GetProperty("available").GetInt32();
        var selected = row.GetProperty("analystCoverage").GetProperty("selected").GetInt32();
        var roleBatches = role.Length;
        var spanBatches = span.Length;
        var counterfactuals = new[] { .10, .25, .50 }.Select(rate => BuildEstimate(rate, generated, roleInputs, spanInputs, writes)).ToArray();
        return new HDocument(spec.Id, spec.Relative.Replace('\\', '/'), snapshot.Lines.Count, generated, selected, roleInputs, roleBatches,
            spanInputs, spanBatches, writes, bytes, row.GetProperty("validation").GetProperty("eligible").GetInt32(),
            row.GetProperty("docxAlignment").GetProperty("aligned").GetInt32(), counterfactuals);
    }

    private static Estimate BuildEstimate(double rate, int generated, int roleInputs, int spanInputs, int writes)
    {
        var addedRole = (int)Math.Ceiling(roleInputs * rate);
        var addedSpan = (int)Math.Ceiling(spanInputs * rate);
        var roleBatchDelta = (int)Math.Ceiling((roleInputs + addedRole) / (double)RoleBatchSize) - (int)Math.Ceiling(roleInputs / (double)RoleBatchSize);
        var spanBatchDelta = (int)Math.Ceiling((spanInputs + addedSpan) / (double)SpanBatchSize) - (int)Math.Ceiling(spanInputs / (double)SpanBatchSize);
        return new Estimate($"population_plus_{rate:P0}", (int)Math.Ceiling(generated * rate), addedRole, roleBatchDelta, addedSpan, spanBatchDelta,
            roleBatchDelta + spanBatchDelta, "estimate_work_units_not_latency");
    }

    private static string Markdown(HArtifact a)
    {
        var s = new StringBuilder();
        s.AppendLine("# Authority Pipeline Cost Model"); s.AppendLine();
        s.AppendLine("Measured counts come from frozen run/checkpoint artifacts. Population sensitivity is an offline estimate of work units, not measured provider latency."); s.AppendLine();
        s.AppendLine($"- `K={a.Config.K}`, role batch `{a.Config.RoleBatchSize}`, span batch `{a.Config.SpanBatchSize}`, concurrency `{a.Config.Concurrency}`");
        s.AppendLine($"- role timeout `{a.Config.RoleTimeoutSeconds}s`, batch timeout `{a.Config.BatchTimeoutSeconds}s`, lane deadline `{a.Config.LaneDeadlineSeconds}s`");
        s.AppendLine($"- `ACTUAL_PROVIDER_LATENCY={a.ACTUAL_PROVIDER_LATENCY}`, `PROVIDER_CALLS={a.PROVIDER_CALLS}`, `PRODUCTION_CODE_CHANGED={a.PRODUCTION_CODE_CHANGED}`"); s.AppendLine();
        s.AppendLine("| document | sourceLines | generatedCandidates | selectedCandidates | roleInputs | roleBatches | spanInputs | spanBatches | checkpointWrites | checkpointBytes | validatedHeadings | emittedHeadings |");
        s.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var d in a.Documents) s.AppendLine($"| `{d.DocumentId}` | {d.SourceLines} | {d.GeneratedCandidates} | {d.SelectedCandidates} | {d.RoleInputs} | {d.RoleBatches} | {d.SpanInputs} | {d.SpanBatches} | {d.CheckpointWrites} | {d.CheckpointBytes} | {d.ValidatedHeadings} | {d.EmittedHeadings} |");
        s.AppendLine(); s.AppendLine("## Counterfactual estimates"); s.AppendLine();
        s.AppendLine("For each candidate population increase, role/span input deltas are scaled work estimates; checkpoint write delta counts additional role and span batches only."); s.AppendLine();
        foreach (var d in a.Documents)
        {
            s.AppendLine($"### {d.DocumentId}"); s.AppendLine();
            s.AppendLine("| population | role input delta | role batch delta | span input delta | span batch delta | checkpoint write delta |");
            s.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var c in d.Counterfactuals) s.AppendLine($"| `{c.Population}` | {c.RoleInputDelta} | {c.RoleBatchDelta} | {c.SpanInputDelta} | {c.SpanBatchDelta} | {c.CheckpointWriteDelta} |");
            s.AppendLine();
        }
        s.AppendLine("No timing field in the retained artifacts identifies actual provider latency; it remains `NOT_OBSERVABLE`.");
        return s.ToString();
    }

    private sealed record Config(int K, int RoleBatchSize, int SpanBatchSize, int RoleTimeoutSeconds, int BatchTimeoutSeconds, int LaneDeadlineSeconds, int Concurrency, int VisualRegions, string Source);
    private sealed record HDocument(string DocumentId, string RelativePath, int SourceLines, int GeneratedCandidates, int SelectedCandidates, int RoleInputs, int RoleBatches, int SpanInputs, int SpanBatches, int CheckpointWrites, int CheckpointBytes, int ValidatedHeadings, int EmittedHeadings, IReadOnlyList<Estimate> Counterfactuals);
    private sealed record Estimate(string Population, int CandidateInputDelta, int RoleInputDelta, int RoleBatchDelta, int SpanInputDelta, int SpanBatchDelta, int CheckpointWriteDelta, string Interpretation);
    private sealed record HArtifact(int SchemaVersion, string ArtifactKind, string Phase, string SourceAuthority, Config Config, IReadOnlyList<HDocument> Documents,
        [property: JsonPropertyName("ACTUAL_PROVIDER_LATENCY")] string ACTUAL_PROVIDER_LATENCY,
        [property: JsonPropertyName("PROVIDER_CALLS")] int PROVIDER_CALLS,
        [property: JsonPropertyName("PRODUCTION_CODE_CHANGED")] bool PRODUCTION_CODE_CHANGED);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
