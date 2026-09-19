using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The PDF lane. Same semantic stage as the DOCX lane, different source parser.
/// <para>
/// A PDF upload is extracted from that PDF and nothing else. It never consults a DOCX, and its
/// result is not reconciled with one: two uploads are two documents, and merging them is something
/// a user has to ask for, not something a lane decides.
/// </para>
/// <para>
/// Everything after source occurrences is <see cref="CanonicalSemanticEngine"/>, shared verbatim
/// with the DOCX lane - the prompt, segmentation, contract, binder, hierarchy resolver and
/// placement pass. What differs is only how the source is read: OOXML gives paragraphs, a PDF
/// gives text blocks grouped from positioned lines.
/// </para>
/// </summary>
internal static class CanonicalSemanticPdfAuthorityAdapter
{
    public static async Task<StructuralAuthorityResult> RunAsync(
        string pdfPath,
        IHeaderClassifier? transport,
        CancellationToken cancellationToken,
        CanonicalSemanticExperiment? experiment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        IReadOnlyList<PdfLine> lines;
        using (var document = PdfDocument.Open(pdfPath))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        if (lines.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-text-layer");

        var annotations = PdfLineBlockFilter.Analyze(lines);
        // includeRiskLines: the recall ceiling is the source occurrence universe, not a candidate
        // set. A repeated line, a table-like line or a header-zone line is still an occurrence the
        // model must be allowed to judge; the annotation travels with it as evidence. Excluding
        // them here would rebuild, in the PDF lane, exactly the hidden gate the DOCX lane removed.
        var blocks = PdfSemanticBlockGrouper.Build(annotations, includeRiskLines: true);
        if (blocks.Count == 0)
            return new StructuralAuthorityResult(new ValidatedStructure([]), null, "pdf-no-source-blocks");

        var contexts = PdfCandidateContextBuilder.Build(blocks, annotations);
        var catalog = DocumentSourceCatalogBuilder.FromPdfParserBlocks(blocks, lines);
        var aliasesBySourceId = SemanticSourceAliasCatalog.FromCatalog(catalog)
            .ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        var sourceHash = CanonicalSemanticSourceHash.Compute(pdfPath);

        var ordinalBySourceId = catalog.Units.ToDictionary(
            unit => unit.SourceId, unit => unit.SourceOrdinal, StringComparer.Ordinal);
        // The document's own body size, so font evidence can be stated relative to this document
        // rather than as a point size that means nothing on its own. Median, not mean: a title page
        // of large text should not move what counts as body.
        var bodyFontSize = Median(contexts.Values.Select(context => context.Source.FontSize));
        var evidence = contexts.Values
            .Where(context => aliasesBySourceId.ContainsKey(context.Source.SourceId))
            .OrderBy(context => ordinalBySourceId.GetValueOrDefault(context.Source.SourceId, int.MaxValue))
            .Select(context => EvidenceOf(
                context,
                aliasesBySourceId[context.Source.SourceId].Alias,
                ordinalBySourceId.GetValueOrDefault(context.Source.SourceId),
                bodyFontSize))
            .ToArray();

        var input = new CanonicalSemanticProductionInput(
            catalog,
            null,
            sourceHash,
            [new CanonicalSemanticPageEvidence("PDF", true, 0, "pdf-source")],
            evidence.Select(item => item.CandidateAttention).ToArray(),
            evidence.Select(item => $"[{item.SourceAlias}] {item.ExactSourceText}").ToArray(),
            [],
            evidence.SelectMany(item => item.LocalBefore.Concat(item.LocalAfter)).ToArray(),
            DocumentId: Path.GetFileNameWithoutExtension(pdfPath),
            SourceEvidence: evidence)
        {
            ExpectedSourceSha256 = sourceHash,
            OwnedAliases = null,
        };

        CanonicalSemanticProductionResult result;
        CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel? canonicalModel = null;
        if (transport is null)
        {
            result = CanonicalSemanticProductionEntryPoint.Run(input with { SemanticProposals = [] });
        }
        else
        {
            canonicalModel = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(
                transport, experiment ?? CanonicalSemanticExperiment.Baseline);
            result = await CanonicalSemanticProductionEntryPoint.RunAsync(
                input, canonicalModel,
                requestId: $"pdf:{Path.GetFileNameWithoutExtension(pdfPath)}",
                cancellationToken: cancellationToken);
        }

        var decisions = result.TextPipeline.BoundHeadings.Select(item => new PdfBlockDecision(
            item.SourceId,
            PdfBlockRole.HeadingTopic,
            1,
            "canonical-vnext-semantic-contract",
            new TextOffsetSpan(item.Start, item.End),
            SemanticRole: CanonicalSemanticEngine.ParseSemanticRole(item.SemanticRole))).ToArray();
        var validated = PdfProposalValidator.Validate(contexts, decisions);

        var boundHeadings = transport is null
            ? result.TextPipeline.BoundHeadings
            : await CanonicalSemanticEngine.PlaceUnresolvedHeadingsAsync(
                result.TextPipeline.BoundHeadings, transport, cancellationToken);

        var derived = ModelRelationHierarchyResolver
            .DeriveHierarchyFromModelRelations(boundHeadings)
            .Where(item => contexts.ContainsKey(item.SourceId))
            .ToArray();
        var structures = derived.ToDictionary(item => item.SourceId, item =>
        {
            var facts = contexts[item.SourceId].Source;
            return new PdfValidatedStructure(
                item.SourceId, item.Level, item.ParentSourceId, item.Resolution, "requires_review")
            {
                DomainRole = facts.DomainRole,
                StructuralScope = facts.StructuralScope,
                DomainExclusionProposed = facts.DomainEvidence.ProposesOutlineExclusion,
            };
        }, StringComparer.Ordinal);

        var primarySourceIds = derived
            .Where(item => item.IsPrimaryOccurrence)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var occurrences = contexts.ToDictionary(
            pair => pair.Key,
            pair => new CanonicalSourceOccurrence(
                pair.Value.Source.SourceId,
                ordinalBySourceId.GetValueOrDefault(pair.Key),
                pair.Value.Source.RawText,
                null),
            StringComparer.Ordinal);

        var structure = CanonicalStructureMaterializer.Materialize(
            validated.Where(item => primarySourceIds.Contains(item.SourceId)).ToArray(),
            structures, occurrences, "pdf");

        var audit = new RouteExecutionAudit(
            "pdf-canonical-vnext",
            blocks.Count,
            blocks.Count,
            0,
            0,
            blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
            blocks.Select(block => new RouteBlockAudit(block.Id, block.Page, block.DisplayText)).ToArray(),
            [],
            decisions.Select(decision => new RouteBlockDecisionAudit(
                decision.Id, decision.Role.ToString(), decision.Confidence)
            {
                SemanticRole = decision.SemanticRole.ToString(),
                ProposedSourceSpan = decision.ProposedSourceSpan,
            }).ToArray(),
            validated.Select(item => item.SourceId).ToArray(),
            [],
            validated.Select(item => item.SourceId).ToArray())
        {
            Route = "pdf-canonical-vnext",
            RawAnalystResponses = canonicalModel?.RawResponses ?? [],
            ModelInputContracts = canonicalModel is null ? [] : [CanonicalSemanticContract.ProtocolVersion],
            ValidatedStructures = structures.Values.ToArray(),
            HierarchyFacts = PdfHierarchyFactsInventory.Inspect(validated, contexts),
            ConflictCensus = SemanticConflictCensus.Take(
                result.ConflictNormalization,
                CanonicalSemanticGlobalConflictDetector.Detect(
                    result.NormalizedModelProposals,
                    SemanticSourceAliasCatalog.FromCatalog(catalog),
                    result.SemanticConflicts),
                result.SemanticAdjudicationCalls,
                result.GlobalReopenCalls,
                result.TextPipeline.BoundHeadings.Select(item => item.SourceId)
                    .ToHashSet(StringComparer.Ordinal)),
            SemanticLane = new RouteLaneExecutionAudit("complete", blocks.Count, validated.Count, 0, 0),
            SpanLane = new RouteLaneExecutionAudit("canonical-binder", result.TextPipeline.BoundHeadings.Count,
                result.TextPipeline.BoundHeadings.Count, 0, result.TextPipeline.BindingFailureCount),
        };

        return new StructuralAuthorityResult(structure, audit, "pdf-canonical-vnext")
        {
            EmittedElementIds = structure.Elements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal),
            // The catalog the model was shown and the binder bound against, handed out rather than
            // left to be reconstructed. The audit beside it records readable text for a person;
            // these are the occurrences themselves.
            SourceCatalog = catalog,
        };
    }

    /// <summary>
    /// The same evidence contract the DOCX lane fills, from what a PDF actually knows. Fields a PDF
    /// has no equivalent for are reported as absent rather than invented: there is no numbering
    /// definition and no content control in a PDF, and saying so is not the same as saying zero.
    /// </summary>
    private static CanonicalSemanticSourceEvidence EvidenceOf(
        PdfCandidateContext context,
        string alias,
        int ordinal,
        double bodyFontSize)
    {
        var source = context.Source;
        var bold = source.BoldRatio >= 0.5;
        var italic = source.ItalicRatio >= 0.5;
        // Categorical, not absolute: "larger than the body" is evidence a reader could state, while
        // a point size means nothing without the rest of the document to compare it against.
        var relativeFontSize = RelativeSize(source.FontSize, bodyFontSize);
        var attention = !source.ObservedEvidence.Contains("page_number") &&
            source.StructuralScope is not ("running_page_artifact" or "table_of_contents");
        return new CanonicalSemanticSourceEvidence(
            alias,
            source.SourceId,
            ordinal,
            source.RawText,
            source.StructuralScope,
            // A PDF has no nested tables to have a depth in, so the field is absent rather than
            // zero, and the request omits it entirely. The scope already carries whatever table
            // evidence this format actually has.
            TableDepth: null,
            SectionIndex: source.Page,
            InContentControl: false,
            InTableOfContents: source.StructuralScope == "table_of_contents",
            ["pdf-parser-source", $"scope:{source.StructuralScope}", $"page:{source.Page}"],
            // Format evidence only. The style slot means "what the document declares about this
            // occurrence"; in the DOCX lane that is StyleId and OutlineLevel. Raw coordinates are a
            // measurement, not a declaration, and putting them here would both mislabel them and
            // invite positional reasoning in the one place this architecture works hardest to
            // exclude it. Geometry stays on PdfSourceFacts for the harness and the audit.
            new { Bold = bold, Italic = italic, RelativeFontSize = relativeFontSize, LineCount = source.LineCount },
            // A PDF has no numbering definition. Reporting three nulls states a fact about a
            // concept that does not exist here; omitting the notion is the honest shape.
            new { },
            [],
            CanonicalSemanticEngine.MarkerFactsOf(source),
            source.ObservedEvidence,
            context.PreviousBlocks,
            context.NextBlocks,
            new SemanticCandidateAttentionHint(
                alias, attention, attention ? "pdf-layout-candidate" : "pdf-layout-non-candidate"))
        {
            ActiveStructuralAncestors = context.ActiveHeadingStack,
        };
    }

    private static string RelativeSize(double size, double body)
    {
        if (size <= 0 || body <= 0) return "unknown";
        var ratio = size / body;
        return ratio >= 1.25 ? "much-larger-than-body"
            : ratio >= 1.08 ? "larger-than-body"
            : ratio <= 0.85 ? "smaller-than-body"
            : "body";
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }
}
