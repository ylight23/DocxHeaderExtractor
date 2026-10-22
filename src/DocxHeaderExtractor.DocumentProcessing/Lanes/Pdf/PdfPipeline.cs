using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Routing;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.DocumentProcessing.Lanes.Pdf;

/// <summary>
/// Turns an uploaded PDF into a canonical document, the same shape the DOCX lane produces.
/// <para>
/// The projections after the semantic stage - source catalog, sections, chunks - are the shared
/// ones, so a caller receives the same contract whichever format it uploaded and can project either
/// with the same intent. What must not be shared is the content: this reads the PDF and only the
/// PDF.
/// </para>
/// </summary>
public static class PdfPipeline
{
    /// <summary>
    /// The canonical document plus the outline shape the harness has always spoken.
    /// <para>
    /// Both are produced here, from one run, for the same reason the source catalog is: a host that
    /// needed an outline used to have no PDF path at all, and giving it a second entry point that
    /// re-derived one would be a second answer about the same document.
    /// </para>
    /// </summary>
    /// <param name="route">The model route; null when the model is disabled, and then the run reads the source only.</param>
    /// <param name="agents">The PDF selector, which is the PDF heading authority, and the placer. Without them a run that needs the model
    /// refuses (PDF_HEADING_SELECTOR_REQUIRED, HEADING_PLACER_REQUIRED).</param>
    internal static async Task<RunResult> RunAsync(
        UploadedFile file,
        PipelineOptions options,
        ModelRoute? route,
        HeadingAgents agents,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        if (file.DetectedType != SourceType.Pdf)
            throw new UnsupportedSourceException(file);

        var started = Environment.TickCount64;
        var used = options.DisableLlm ? null : route;

        LaneResult authority;
        // PDF heading authority is the V6 selected-lines authority. It has no semantic fallback:
        // a unit whose answer fails the contract leaves its lines undecided rather than delegating
        // them to a second membership authority.
        var sourceBuild = options.SourceCacheRoot is { } cacheRoot
            ? PdfSourceCache.BuildOrLoad(file.LocalPath, cacheRoot)
            : PdfSourceAdapter.BuildWithDetails(file.LocalPath);
        authority = await PdfLane.RunAsync(
            sourceBuild.Snapshot, sourceBuild.Details, file.LocalPath, used, LaneOptions.Default, agents.Pdf, agents.Placer, ct);

        // The catalog the lane parsed, not a second one derived from the audit. The audit's block
        // text is a readable rendering meant for a person to read; the model was shown, and the
        // binder bound against, the declared projection. Reconstructing from the audit meant a
        // consumer could be handed different text for exactly the occurrences where those two
        // disagree - which is the divergence this lane exists to rule out.
        var catalog = authority.SourceCatalog ?? new DocumentSourceCatalog([]);
        // The outline's File is the checked-out name, as the DOCX lane reports it. OriginalFileName is richer, but a field that means the
        // upload's name on one lane and the temp file's on the other is exactly the kind of quiet divergence this work is removing.
        // ParagraphCount is the source occurrence count, which is what the field means on the DOCX side too.
        return ExtractionOutput.Build(
            new ExtractionOutput.Lane(
                file.LocalPath,
                new DocumentIdentity(Path.GetFileNameWithoutExtension(file.LocalPath), file.OriginalFileName, "pdf", file.LocalPath),
                new DocumentExtractionProvenance(
                    Route,
                    "pdf-source-document",
                    authority.Audit?.HeadingProviderCalls ?? authority.Audit?.RawModelResponses.Count ?? 0)
                {
                    ExecutionContract = ExecutionContracts.ExplicitUploadedPdfCanonical,
                },
                ProductSha256: file.Sha256,
                ParagraphCount: catalog.Units.Count,
                SourceCount: authority.Audit?.SourceBlocksSelected ?? 0,
                Route: Route,
                Model: used?.Model,
                SentDataExternally: used is not null),
            authority,
            authority.Structure,
            authority.EmittedElementIds ?? authority.Structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal),
            catalog,
            options,
            started);
    }

    private const string Route = "pdf-v6-selected-lines";
}
