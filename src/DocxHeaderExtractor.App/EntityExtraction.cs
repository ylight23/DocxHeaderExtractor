using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Problems;
using DocxHeaderExtractor.Entities;

namespace DocxHeaderExtractor.App;

/// <summary>The entity problem as a host runs it: the first problem on the shared frame (ProblemRunner), with the default types.</summary>
public static class EntityExtraction
{
    public const string Problem = "entities";

    public static Task<ProblemRun<EntityOutput>> RunAsync(string path, PipelineOptions options, IModelProvider provider, CancellationToken ct = default) =>
        ProblemRunner.RunAsync(path, options, provider, Problem, new EntityProposer(), new EntityChecker(), ct: ct);
}
