using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Problems;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.DocumentProcessing.Problems;

/// <summary>
/// Runs any document problem the same way: read the source into the shared shape, let the problem's agent propose under the lane lease and
/// deadline, let its C# checker keep what the source supports, and report the run in the shared envelope. A new problem supplies a
/// proposer and a checker and changes nothing here. The heading lanes predate this runner and keep their own route.
/// </summary>
public static class ProblemRunner
{
    /// <param name="problem">The problem's name, recorded in the envelope and in the lane's refusals (for example "entities").</param>
    public static async Task<ProblemRun<TOutput>> RunAsync<TProposal, TOutput>(
        string path,
        PipelineOptions options,
        IModelProvider provider,
        string problem,
        IProposer<TProposal> proposer,
        IChecker<TProposal, TOutput> checker,
        CancellationToken ct = default)
        where TProposal : class
        where TOutput : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(checker);
        var started = Environment.TickCount64;
        var source = Read(path, options, ct);
        // With the model disabled there is no proposal, so nothing to check: the run reports the source it read and no output.
        if (options.DisableLlm)
            return new(new RunEnvelope(source.Identity, problem, proposer.Contract, null, false, Environment.TickCount64 - started), null);

        var route = provider.CreateRoute();
        if (string.IsNullOrWhiteSpace(route.ApiKey))
            throw new InvalidOperationException($"PROBLEM_KEY_MISSING: the model route of '{problem}' carries no provider key.");
        var proposal = await LaneRun.CompleteAsync(
            (lease, laneCt) =>
            {
                void EnsureActive() { if (!lease.IsActive) throw new LaneLeaseLostException(); }
                return proposer.ProposeAsync(source, new ModelAccess(route.ApiKey, EnsureActive), laneCt);
            },
            LaneOptions.Default.LaneDeadline,
            new LaneRun.Messages($"{problem}: the run exceeded its lane deadline.", $"{problem}: the run failed.",
                $"{problem}: the result lost its execution lease."),
            ct).ConfigureAwait(false);
        var output = checker.Check(source, proposal);
        return new(new RunEnvelope(source.Identity, problem, proposer.Contract, route.Model, true, Environment.TickCount64 - started), output);
    }

    /// <summary>
    /// The source of a DOCX or a PDF with a text layer, read the way the heading lanes read it (and from the same cache). A scanned page has
    /// no text until the scan pipeline transcribes it, which today happens inside the heading selection, so a scan is refused here.
    /// </summary>
    internal static ProblemSource Read(string path, PipelineOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (SourceDetector.Detect(path))
        {
            case SourceType.Docx:
                if (DocxSourceIntegrity.Read(path).Kind == DocxSourceKind.ScannedImageOnly)
                    throw new NotSupportedException("PROBLEM_SCAN_UNSUPPORTED: a scanned DOCX has no text layer for a problem to read yet.");
                var docx = DocxSourceCache.ReadOrLoad(path, options.SourceCacheRoot, options.Extraction);
                return new(new DocumentIdentity(docx.DocumentId, Path.GetFileName(path), docx.SourceKind, path), CatalogBuilder.FromSourceDocument(docx));
            case SourceType.Pdf:
                if (PdfSourceIntegrity.Read(path, ct).RequiresOcr)
                    throw new NotSupportedException("PROBLEM_SCAN_UNSUPPORTED: a PDF with scanned pages has no text layer for a problem to read yet.");
                var pdf = options.SourceCacheRoot is { } cacheRoot ? PdfSourceCache.BuildOrLoad(path, cacheRoot) : PdfSourceAdapter.BuildWithDetails(path);
                return new(new DocumentIdentity(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), "pdf", path), pdf.Snapshot.Catalog);
            default:
                throw new NotSupportedException("PROBLEM_FORMAT_UNSUPPORTED: a problem reads a DOCX or a PDF; convert other formats first.");
        }
    }
}
