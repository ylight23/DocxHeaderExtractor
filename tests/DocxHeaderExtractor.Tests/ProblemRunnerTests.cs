using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Problems;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Problems;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The shared problem frame, exercised by a small span-finding problem that lives only here: its agent is a script and its checker keeps
/// the spans the source supports. Nothing in the pipeline was written for it, which is the point.
/// </summary>
public sealed class ProblemRunnerTests : IDisposable
{
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dhx-problem-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public async Task A_new_problem_runs_with_only_a_proposer_and_a_checker_and_keeps_what_the_source_supports()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "sample.docx"));
        var first = ProblemRunner.ReadText(path, ProblemRunner.SourceKind.Docx, new PipelineOptions(), default).Catalog.Units[0];
        var proposer = new ScriptedSpans(source =>
        [
            new Span(first.SourceId, 0, first.Text.Length, "title"),
            new Span(first.SourceId, 0, first.Text.Length + 5, "title"),   // runs past the unit's text
            new Span("no-such-unit", 0, 1, "title"),
        ]);

        var run = await ProblemRunner.RunAsync(path, new PipelineOptions(), new Provider("k"), "spans", proposer, new SpanChecker());

        Assert.Equal("k", proposer.Key);
        Assert.Equal(1, proposer.Calls);
        Assert.Equal(first.Text, Assert.Single(run.Output!.Kept).Text);
        Assert.Equal(new[] { "SPAN_OUTSIDE_TEXT", "UNKNOWN_SOURCE" }, run.Output.Rejected.Select(r => r.Reason));
        Assert.Equal(("spans", ScriptedSpans.Version, "test-model", true), (run.Envelope.Problem, run.Envelope.Contract, run.Envelope.Model, run.Envelope.SentDataExternally));
        Assert.Equal("sample.docx", run.Envelope.Identity.FileName);
    }

    [Fact]
    public async Task With_the_model_disabled_no_route_is_asked_and_no_proposal_is_made()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "sample.docx"));
        var proposer = new ScriptedSpans(_ => []);
        var run = await ProblemRunner.RunAsync(path, new PipelineOptions { DisableLlm = true }, new NoRoute(), "spans", proposer, new SpanChecker());
        Assert.Equal(0, proposer.Calls);
        Assert.Null(run.Output);
        Assert.Null(run.Envelope.Model);
        Assert.False(run.Envelope.SentDataExternally);
    }

    [Fact]
    public async Task A_route_without_a_key_is_refused_before_the_agent_runs()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "sample.docx"));
        var proposer = new ScriptedSpans(_ => []);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProblemRunner.RunAsync(path, new PipelineOptions(), new Provider(null), "spans", proposer, new SpanChecker()));
        Assert.StartsWith("PROBLEM_KEY_MISSING", refused.Message);
        Assert.Equal(0, proposer.Calls);
    }

    [Fact]
    public async Task A_file_no_source_reader_owns_is_refused_with_its_reason()
    {
        var path = Path.Combine(dir, "notes.txt");
        File.WriteAllText(path, "plain text");
        var refused = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ProblemRunner.RunAsync(path, new PipelineOptions(), new Provider("k"), "spans", new ScriptedSpans(_ => []), new SpanChecker()));
        Assert.StartsWith("PROBLEM_FORMAT_UNSUPPORTED", refused.Message);
    }

    static string ScanPath => TestRepository.Path("todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx");

    [Fact]
    public async Task A_scan_is_read_through_the_scan_reader_inside_the_run_and_its_unread_lines_are_reported()
    {
        var reader = new FixedReader(ScanPath, unreadable: ["P001-L003"]);
        var proposer = new ScriptedSpans(source => [new Span(source.Catalog.Units[1].SourceId, 0, 6, "title")]);

        var run = await ProblemRunner.RunAsync(ScanPath, new PipelineOptions(), new Provider("k"), "spans", proposer, new SpanChecker(), reader);

        Assert.Equal("k", reader.Key);
        Assert.Equal(new[] { "Chương I", "Điều 2. Thẩm quyền" }, proposer.Seen!.Catalog.Units.Select(u => u.Text));
        Assert.Equal("Điều 2", Assert.Single(run.Output!.Kept).Text);
        Assert.Equal("scan", run.Envelope.Identity.SourceKind);
        Assert.Equal(["unreadable-line:P001-L003"], run.Envelope.SourceGaps);
    }

    [Fact]
    public async Task A_scan_without_a_reader_is_refused_and_with_the_model_disabled_it_is_not_read()
    {
        var refused = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ProblemRunner.RunAsync(ScanPath, new PipelineOptions(), new Provider("k"), "spans", new ScriptedSpans(_ => []), new SpanChecker()));
        Assert.StartsWith("PROBLEM_SCAN_READER_REQUIRED", refused.Message);

        var reader = new FixedReader(ScanPath);
        var run = await ProblemRunner.RunAsync(ScanPath, new PipelineOptions { DisableLlm = true }, new NoRoute(), "spans",
            new ScriptedSpans(_ => []), new SpanChecker(), reader);
        Assert.Equal(0, reader.Calls);
        Assert.Null(run.Output);
        Assert.Equal("scan", run.Envelope.Identity.SourceKind);
    }

    [Fact]
    public async Task A_scan_reading_of_another_file_is_refused_before_the_agent_runs()
    {
        var proposer = new ScriptedSpans(_ => []);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ProblemRunner.RunAsync(ScanPath, new PipelineOptions(),
            new Provider("k"), "spans", proposer, new SpanChecker(), new FixedReader(ScanPath, sha: new string('0', 64))));
        Assert.StartsWith("PROBLEM_SCAN_SOURCE_MISMATCH", refused.Message);
        Assert.Equal(0, proposer.Calls);
    }

    /// <summary>A scan reader that returns two transcribed lines of the given file, with no image work and no provider call.</summary>
    sealed class FixedReader(string path, IReadOnlyList<string>? unreadable = null, string? sha = null) : IScanReader
    {
        public int Calls { get; private set; }
        public string? Key { get; private set; }

        public Task<ScanReading> ReadAsync(string scanPath, string? apiKey, CancellationToken ct)
        {
            Calls++; Key = apiKey;
            ScanSourceLine Line(int n, string text) => new($"P001-L{n:000}", $"P001-ocr-original-L{n:000}", 1, new PixelBox(10, 40 * n, 300, 30),
                1700, 2200, text, "", "transcription", new Dictionary<string, string> { ["transcription"] = text }, "accepted", "{}", text);
            return Task.FromResult(new ScanReading(sha ?? Sources.Detect.SourceHash.Compute(path), [Line(1, "Chương I"), Line(4, "Điều 2. Thẩm quyền")],
                new ScanRunDiagnostics(1, 0, 0, 0, 2, 2, new Dictionary<string, int>(), [], [], [], new Dictionary<string, double>())
                { UnreadableLines = unreadable ?? [] }));
        }
    }

    // ---- the sample problem: an agent proposes typed spans, C# keeps the ones the source supports ----

    sealed record Span(string SourceId, int Start, int End, string Type);
    sealed record Proposal(IReadOnlyList<Span> Spans);
    sealed record Found(string SourceId, string Text, string Type);
    sealed record Refused(Span Span, string Reason);
    sealed record Output(IReadOnlyList<Found> Kept, IReadOnlyList<Refused> Rejected);

    sealed class ScriptedSpans(Func<ProblemSource, IReadOnlyList<Span>> script) : IProposer<Proposal>
    {
        public const string Version = "sample-spans-v1";
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public ProblemSource? Seen { get; private set; }
        public string Contract => Version;

        public Task<Proposal> ProposeAsync(ProblemSource source, ModelAccess access, CancellationToken ct)
        {
            access.EnsureActive();   // a real agent checks the lease before and after each model turn
            Calls++; Key = access.ApiKey; Seen = source;
            return Task.FromResult(new Proposal(script(source)));
        }
    }

    sealed class SpanChecker : IChecker<Proposal, Output>
    {
        public Output Check(ProblemSource source, Proposal proposal)
        {
            var units = source.Catalog.Units.ToDictionary(u => u.SourceId, StringComparer.Ordinal);
            var kept = new List<Found>();
            var rejected = new List<Refused>();
            foreach (var span in proposal.Spans)
            {
                if (!units.TryGetValue(span.SourceId, out var unit)) rejected.Add(new(span, "UNKNOWN_SOURCE"));
                else if (!new Contracts.Models.StructuralSpan(span.Start, span.End).IsValidFor(unit.Text)) rejected.Add(new(span, "SPAN_OUTSIDE_TEXT"));
                else kept.Add(new(span.SourceId, unit.Text[span.Start..span.End], span.Type));
            }
            return new(kept, rejected);
        }
    }

    sealed class Provider(string? key) : IModelProvider
    {
        public ModelRoute CreateRoute() => new("test-model", key);
    }

    sealed class NoRoute : IModelProvider;
}
