using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Problems;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Problems;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.App;

/// <summary>
/// Where a host runs a problem other than headings. It hands <see cref="ProblemRunner"/> what the pipeline cannot build itself: the scan
/// reader, which lives with the agents. The problem brings its proposer and checker.
/// </summary>
public static class ProblemSetup
{
    public static Task<ProblemRun<TOutput>> RunAsync<TProposal, TOutput>(string path, PipelineOptions options, IModelProvider provider,
        string problem, IProposer<TProposal> proposer, IChecker<TProposal, TOutput> checker, CancellationToken ct = default)
        where TProposal : class
        where TOutput : class =>
        ProblemRunner.RunAsync(path, options, provider, problem, proposer, checker, ScanReader(options, provider), ct);

    /// <summary>The host's own scan reader, or the production one: the scan pipeline the heading lanes use (same evidence store, same
    /// transcription), stopped before any heading is chosen.</summary>
    public static IScanReader ScanReader(PipelineOptions options, IModelProvider provider) =>
        provider.Create<IScanReader>(AgentRoles.ScanReader) ?? new ScanSelector(options.ScanEvidenceRoot, parallelism: options.ScanJobs,
            transcriptionOptions: options.ScanJobs is { } jobs ? new ScanTranscriptionPreflight.Options(Parallelism: jobs) : null);
}
