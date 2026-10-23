using DocxHeaderExtractor.Contracts.Problems;
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
        var first = ProblemRunner.Read(path, new PipelineOptions(), default).Catalog.Units[0];
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
        public string Contract => Version;

        public Task<Proposal> ProposeAsync(ProblemSource source, ModelAccess access, CancellationToken ct)
        {
            access.EnsureActive();   // a real agent checks the lease before and after each model turn
            Calls++; Key = access.ApiKey;
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
