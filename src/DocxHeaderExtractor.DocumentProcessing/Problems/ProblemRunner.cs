using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Problems;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.Sources.Scan;
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
    /// <param name="scans">Reads a scanned DOCX or PDF into text lines (App wires the production reader). Without it a scan is refused.</param>
    public static async Task<ProblemRun<TOutput>> RunAsync<TProposal, TOutput>(
        string path,
        PipelineOptions options,
        IModelProvider provider,
        string problem,
        IProposer<TProposal> proposer,
        IChecker<TProposal, TOutput> checker,
        IScanReader? scans = null,
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
        var kind = Classify(path, ct);
        if (kind == SourceKind.Scan && scans is null)
            throw new NotSupportedException("PROBLEM_SCAN_READER_REQUIRED: a scanned document is read through an IScanReader, and none was given.");
        // With the model disabled there is no proposal, so nothing to check. A scan is not read either: without the model nothing is
        // transcribed, so it would have no text.
        if (options.DisableLlm)
        {
            var identity = kind == SourceKind.Scan ? ScanIdentity(path) : ReadText(path, kind, options, ct).Identity;
            return new(new RunEnvelope(identity, problem, proposer.Contract, null, false, Environment.TickCount64 - started), null);
        }

        var route = provider.CreateRoute();
        if (string.IsNullOrWhiteSpace(route.ApiKey))
            throw new InvalidOperationException($"PROBLEM_KEY_MISSING: the model route of '{problem}' carries no provider key.");
        var proposed = await LaneRun.CompleteAsync(
            async (lease, laneCt) =>
            {
                void EnsureActive() { if (!lease.IsActive) throw new LaneLeaseLostException(); }
                // A scan's text comes from the model (each line is transcribed), so it is read inside the lease like the proposal.
                var source = kind == SourceKind.Scan
                    ? await ReadScanAsync(path, scans!, route.ApiKey, laneCt).ConfigureAwait(false)
                    : ReadText(path, kind, options, laneCt);
                EnsureActive();
                return new Proposed<TProposal>(source,
                    await proposer.ProposeAsync(source, new ModelAccess(route.ApiKey, EnsureActive), laneCt).ConfigureAwait(false));
            },
            LaneOptions.Default.LaneDeadline,
            new LaneRun.Messages($"{problem}: the run exceeded its lane deadline.", $"{problem}: the run failed.",
                $"{problem}: the result lost its execution lease."),
            ct).ConfigureAwait(false);
        var output = checker.Check(proposed.Source, proposed.Proposal);
        return new(new RunEnvelope(proposed.Source.Identity, problem, proposer.Contract, route.Model, true, Environment.TickCount64 - started)
        { SourceGaps = proposed.Source.Gaps }, output);
    }

    sealed record Proposed<TProposal>(ProblemSource Source, TProposal Proposal);

    internal enum SourceKind { Docx, Pdf, Scan }

    /// <summary>Which reader owns the file: a DOCX or a PDF with a text layer, or a scan (a DOCX of page images, or a PDF with scanned pages).</summary>
    internal static SourceKind Classify(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return SourceDetector.Detect(path) switch
        {
            SourceType.Docx => DocxSourceIntegrity.Read(path).Kind == DocxSourceKind.ScannedImageOnly ? SourceKind.Scan : SourceKind.Docx,
            SourceType.Pdf => PdfSourceIntegrity.Read(path, ct).RequiresOcr ? SourceKind.Scan : SourceKind.Pdf,
            _ => throw new NotSupportedException("PROBLEM_FORMAT_UNSUPPORTED: a problem reads a DOCX or a PDF; convert other formats first."),
        };
    }

    /// <summary>The source of a DOCX or a PDF with a text layer, read the way the heading lanes read it (and from the same cache).</summary>
    internal static ProblemSource ReadText(string path, SourceKind kind, PipelineOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (kind == SourceKind.Docx)
        {
            var docx = DocxSourceCache.ReadOrLoad(path, options.SourceCacheRoot, options.Extraction);
            return new(new DocumentIdentity(docx.DocumentId, Path.GetFileName(path), docx.SourceKind, path), CatalogBuilder.FromSourceDocument(docx));
        }
        if (kind == SourceKind.Pdf)
        {
            var pdf = options.SourceCacheRoot is { } cacheRoot ? PdfSourceCache.BuildOrLoad(path, cacheRoot) : PdfSourceAdapter.BuildWithDetails(path);
            return new(new DocumentIdentity(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), "pdf", path), pdf.Snapshot.Catalog);
        }
        throw new InvalidOperationException("A scan is read through ReadScanAsync.");
    }

    /// <summary>A scan's source: its transcribed lines, checked against the file it was read from, with what could not be read.</summary>
    internal static async Task<ProblemSource> ReadScanAsync(string path, IScanReader scans, string apiKey, CancellationToken ct)
    {
        var sha = SourceHash.Compute(path);
        var reading = await scans.ReadAsync(path, apiKey, ct).ConfigureAwait(false);
        if (reading.SourceSha256 != sha || sha != SourceHash.Compute(path))
            throw new InvalidOperationException("PROBLEM_SCAN_SOURCE_MISMATCH: the scan reading is not of this file.");
        var snapshot = ScanSourceAdapter.Build(reading.SourceSha256, reading.Lines);
        var d = reading.Diagnostics;
        IReadOnlyList<string> gaps = [.. d.UnreadableLines.Select(a => "unreadable-line:" + a),
            .. d.TranscriptionFailures.Select(f => "transcription-failed:" + f),
            .. d.OcrGaps.Select(g => "view-missing:" + g.ViewId + ":" + g.Reason)];
        return new(ScanIdentity(path) with { DocumentId = reading.SourceSha256[..16] }, snapshot.Catalog) { Gaps = gaps };
    }

    static DocumentIdentity ScanIdentity(string path) =>
        new(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), "scan", path);
}
