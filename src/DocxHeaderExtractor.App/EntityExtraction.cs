using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Problems;
using DocxHeaderExtractor.Entities;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.App;

/// <summary>The entity problem as a host runs it: the first problem on the shared frame (ProblemRunner), with the default types. A scanned
/// document is read through the scan reader the heading route uses, so both routes read a scan the same way.</summary>
public static class EntityExtraction
{
    public const string Problem = "entities";

    public static Task<ProblemRun<EntityOutput>> RunAsync(string path, PipelineOptions options, IModelProvider provider, CancellationToken ct = default) =>
        ProblemRunner.RunAsync(path, options, provider, Problem, new EntityProposer(), new EntityChecker(),
            ScanReader(options, provider), ct);

    /// <summary>The host's own scan reader when the provider supplies one, else the production scan reader (the same one the heading route uses).</summary>
    public static IScanReader ScanReader(PipelineOptions options, IModelProvider provider) =>
        provider.Create<IScanReader>(AgentRoles.ScanReader) ?? new ScanSelector(options.ScanEvidenceRoot,
            parallelism: options.ScanJobs,
            transcriptionOptions: options.ScanJobs is { } jobs ? new ScanTranscriptionPreflight.Options(Parallelism: jobs) : null);
}
