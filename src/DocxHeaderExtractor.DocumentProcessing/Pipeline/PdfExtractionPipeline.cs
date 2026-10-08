using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Projection;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Turns an uploaded PDF into a canonical document, the same shape the DOCX lane produces.
/// <para>
/// The projections after the semantic stage - source catalog, sections, chunks - are the shared
/// ones, so a caller receives the same contract whichever format it uploaded and can project either
/// with the same intent. What must not be shared is the content: this reads the PDF and only the
/// PDF.
/// </para>
/// </summary>
public static class PdfExtractionPipeline
{
    public static async Task<DocumentExtractionResult> RunAsync(
        UploadedFile file,
        PipelineOptions options,
        IInferenceTransport? analyst = null,
        CancellationToken ct = default) =>
        (await RunExecutionAsync(file, options, analyst, ct: ct)).Result;

    /// <summary>
    /// The canonical document plus the outline shape the harness has always spoken.
    /// <para>
    /// Both are produced here, from one run, for the same reason the source catalog is: a host that
    /// needed an outline used to have no PDF path at all, and giving it a second entry point that
    /// re-derived one would be a second answer about the same document.
    /// </para>
    /// </summary>
    public static Task<DocumentExtractionExecutionResult> RunExecutionAsync(
        UploadedFile file,
        PipelineOptions options,
        IInferenceTransport? analyst = null,
        IReadOnlySet<int>? quarantinedIndexes = null,
        bool analystSendsDataExternally = false,
        CancellationToken ct = default) =>
        RunExecutionCoreAsync(
            file, options, analyst, quarantinedIndexes, analystSendsDataExternally, ct,
            SemanticLaneOptions.Default);

    /// <summary>
    /// Internal lifecycle seam for deterministic lane-boundary tests. The public execution
    /// signature above remains source- and binary-compatible with the pre-P5c route.
    /// </summary>
    internal static Task<DocumentExtractionExecutionResult> RunExecutionAsync(
        UploadedFile file,
        PipelineOptions options,
        IInferenceTransport? analyst,
        SemanticLaneOptions semanticLaneOptions,
        IReadOnlySet<int>? quarantinedIndexes = null,
        bool analystSendsDataExternally = false,
        CancellationToken ct = default) =>
        RunExecutionCoreAsync(
            file, options, analyst, quarantinedIndexes, analystSendsDataExternally, ct,
            semanticLaneOptions);

    private static async Task<DocumentExtractionExecutionResult> RunExecutionCoreAsync(
        UploadedFile file,
        PipelineOptions options,
        IInferenceTransport? transport,
        IReadOnlySet<int>? quarantinedIndexes,
        bool transportSendsDataExternally,
        CancellationToken ct,
        SemanticLaneOptions semanticLaneOptions)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);
        if (file.DetectedType != SourceType.Pdf)
            throw new UnsupportedSourceException(file);

        var started = Environment.TickCount64;
        var used = options.DisableLlm ? null : transport;
        var productionAuthorized = used is IPdfProductionAuthorizedInferenceTransport;
        if (used is not null && !productionAuthorized)
        {
            throw new InvalidOperationException(
                "PDF_PRODUCTION_AUTHORIZATION_REQUIRED: requires the production-authorized PDF inference transport.");
        }

        HeadingPipelineResult authority;
        // PDF heading authority is the promoted function-conditioned chain. Unlike the
        // historical canonical engine it has no semantic fallback: invalid stage output is
        // withheld by that adapter rather than delegated to a second membership authority.
        var sourceBuild = PdfSourceAdapter.BuildWithDetails(file.LocalPath);
        authority = await PdfHeadingPipeline.RunAsync(
            sourceBuild.Snapshot, sourceBuild.Details, file.LocalPath, used, semanticLaneOptions, ct);
        // The same repair step the DOCX lane applies, through the same implementation. A quarantine
        // that silently did nothing on one format would make the harness's repair loop mean two
        // different things depending on what was uploaded.
        authority = DocxExtractionPipeline.ApplyStructuralQuarantine(authority, quarantinedIndexes);

        // The catalog the lane parsed, not a second one derived from the audit. The audit's block
        // text is a readable rendering meant for a person to read; the model was shown, and the
        // binder bound against, the declared projection. Reconstructing from the audit meant a
        // consumer could be handed different text for exactly the occurrences where those two
        // disagree - which is the divergence this lane exists to rule out.
        var catalog = authority.SourceCatalog ?? new DocumentSourceCatalog([]);
        var sections = StructuralSectionProjection.Project(authority.Structure, catalog);
        var chunks = SectionChunkProjection.Project(
            sections, catalog, authority.Structure,
            new DocumentChunkingPolicy(Math.Max(1, options.Chunking.TokenBudget)));

        var result = new DocumentExtractionResult(
            new DocumentIdentity(
                Path.GetFileNameWithoutExtension(file.LocalPath),
                file.OriginalFileName,
                "pdf",
                file.LocalPath),
            catalog,
            authority.Structure,
            sections,
            chunks,
            new DocumentExtractionProvenance(
                "pdf-canonical-vnext",
                "pdf-source-document",
                authority.Audit?.RawAnalystResponses.Count ?? 0)
            {
                ExecutionContract = ExecutionContracts.ExplicitUploadedPdfCanonical,
            });

        return new DocumentExtractionExecutionResult(
            result,
            Outline(file, options, authority, catalog, used, transportSendsDataExternally, started))
        { HeadingPipeline = authority };
    }

    /// <summary>
    /// The outline output for a PDF run.
    /// <para>
    /// <c>ParagraphCount</c> is the source occurrence count, which is the same thing this field
    /// means on the DOCX side - how many source units the document was found to have.
    /// </para>
    /// </summary>
    private static DocumentOutline Outline(
        UploadedFile file,
        PipelineOptions options,
        HeadingPipelineResult authority,
        DocumentSourceCatalog catalog,
        IInferenceTransport? transport,
        bool transportSendsDataExternally,
        long started)
    {
        var audit = authority.Audit;
        var product = new DocumentProductOutput(file.Sha256, []);
        if (audit is not null)
        {
            var final = DocxExtractionPipeline.BuildFinalStructure(
                file.LocalPath, audit, authority.Structure, authority.ProjectionContext);
            product = DocumentProductOutputProjector.Serialize(final, OutputDecisionPolicy.Decide(final));
        }

        return new DocumentOutline
        {
            // The checked-out name, as the DOCX lane reports it. OriginalFileName is richer, but a
            // field that means the upload's name on one lane and the temp file's on the other is
            // exactly the kind of quiet divergence this work is removing.
            File = Path.GetFileName(file.LocalPath),
            ParagraphCount = catalog.Units.Count,
            SourceCount = audit?.SourceBlocksSelected ?? 0,
            Headings = HeadingOutlineProjection.Project(
                authority.Structure,
                authority.EmittedElementIds ?? authority.Structure.Elements
                    .Select(element => element.Id).ToHashSet(StringComparer.Ordinal), authority.ProjectionContext, catalog),
            ProductOutput = product,
            ElapsedMs = Environment.TickCount64 - started,
            Model = transport?.ModelName,
            DeterministicRoute = "pdf-canonical-vnext",
            RouteAudit = audit,
            Provenance = DocxExtractionPipeline.BuildProvenance(
                audit, !options.DisableLlm && transportSendsDataExternally),
        };
    }
}
