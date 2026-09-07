using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Materializes the already validated PDF final structure into the generic structural authority.
/// It does not select candidates, resolve hierarchy, or derive product fields. Output emission is
/// still controlled by <see cref="PdfOutputDecisionPolicy"/> and is represented separately so the
/// full validated structure remains available to audit consumers.
/// </summary>
public static class StructuralAuthorityMaterializer
{
    private const string BoundarySource = "pdf-final-structure-v1";
    private const string ConfidenceBasis = "pdf-final-structure-validated";

    public static StructuralMaterializationResult Materialize(
        PdfFinalStructure finalStructure,
        IReadOnlyList<PdfOutputDecision> decisions,
        DocumentSourceCatalog? sourceCatalog = null,
        StructuralMaterializationSourceAuthority? sourceAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(finalStructure);
        ArgumentNullException.ThrowIfNull(decisions);

        // The source-bearing production route supplies this explicitly. The inferred value is
        // retained only for existing catalog-free compatibility/shadow callers.
        var authority = sourceAuthority ?? (sourceCatalog is null
            ? StructuralMaterializationSourceAuthority.CanonicalDocumentSource
            : StructuralMaterializationSourceAuthority.PdfParserSource);
        if (authority == StructuralMaterializationSourceAuthority.PdfParserSource && sourceCatalog is null)
            throw new InvalidOperationException("pdf-source-catalog-missing");

        var decisionById = decisions
            .GroupBy(decision => decision.HeadingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sourceUnjoined = 0;
        var elements = new List<ValidatedStructuralElement>(finalStructure.Headings.Count);
        var elementIdByHeadingId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var heading in finalStructure.Headings)
        {
            if (authority == StructuralMaterializationSourceAuthority.CanonicalDocumentSource &&
                heading.SourceAnchor is null)
            {
                sourceUnjoined++;
                continue;
            }

            var elementId = ElementId(heading.Id);
            elementIdByHeadingId[heading.Id] = elementId;
            var decision = decisionById.GetValueOrDefault(heading.Id);
            var decisionStatus = decision?.RequiresReview == false
                ? nameof(HeadingDecisionStatus.AutoAcceptedEvidence)
                : nameof(HeadingDecisionStatus.RequiresReview);
            var reasons = decision?.Reasons ?? [];
            var source = ResolveSource(heading, authority, sourceCatalog);
            if (!source.ProposedSpan.IsValidFor(source.Text) ||
                source.ProposedSpan.Start < source.RawSpan.Start ||
                source.ProposedSpan.End > source.RawSpan.End)
            {
                throw new StructuralMaterializationException(
                    heading.Id,
                    authority,
                    authority == StructuralMaterializationSourceAuthority.PdfParserSource
                        ? "pdf-source-span-invalid"
                        : "canonical-source-span-invalid",
                    source.SourceId,
                    source.ProposedSpan,
                    source.Text.Length,
                    source.SourceUnitFound);
            }

            var sourceFacts = new SourceFacts
            {
                SourceId = source.SourceId,
                RawText = source.Text,
                Source = source.Anchor,
                RawSpan = new SourceTextSpan(source.RawSpan.Start, source.RawSpan.End),
            };
            var candidate = new StructuralCandidate
            {
                CandidateId = heading.Id,
                ObservedSourceFacts = [sourceFacts],
            };
            var proposal = new StructuralProposal
            {
                CandidateId = heading.Id,
                Type = ElementType(heading.Role),
                Role = ElementRole(heading.Role),
                ProposedSources =
                [
                    new ProposedSourceReference(source.SourceId, source.ProposedSpan),
                ],
                ProposedParentId = heading.ParentId is { } parent && elementIdByHeadingId.ContainsKey(parent)
                    ? elementIdByHeadingId[parent]
                    : null,
                ProposedLevel = heading.Level,
            };
            var validation = StructuralProposalValidator.Validate(
                candidate, proposal, elementIdByHeadingId.Values.ToHashSet(StringComparer.Ordinal));
            if (!validation.Accepted)
            {
                throw new StructuralMaterializationException(
                    heading.Id,
                    authority,
                    validation.RejectionReason ?? "structural-proposal-rejected",
                    source.SourceId,
                    source.ProposedSpan,
                    source.Text.Length,
                    source.SourceUnitFound);
            }

            var element = StructuralProposalValidator.Materialize(
                candidate, proposal, elementId,
                new StructuralDecision("model", decisionStatus, 1.0, ConfidenceBasis),
                elementIdByHeadingId.Values.ToHashSet(StringComparer.Ordinal),
                new StructuralProjectionMetadata
                {
                    CompatibilitySourceId = heading.Id,
                    CompatibilitySourceOrdinal = heading.SourceAnchor?.ParagraphIndex,
                    CompatibilityStableId = heading.SourceAnchor?.StableId,
                    CompatibilityHeadingSpan = heading.SourceAnchor is { } compatibilityAnchor
                        ? new StructuralSpan(compatibilityAnchor.Span.Start, compatibilityAnchor.Span.End)
                        : null,
                    CompatibilityText = heading.Text,
                    CompatibilityLevel = heading.Level,
                    CompatibilityLevelIsSet = true,
                    OriginalText = heading.SourceText,
                    BoundarySource = BoundarySource,
                    AcceptanceSignature = reasons.Count > 0 ? string.Join(",", reasons) : null,
                });
            if (element is null)
                throw new StructuralMaterializationException(
                    heading.Id,
                    authority,
                    "structural-proposal-materialization-failed",
                    source.SourceId,
                    source.ProposedSpan,
                    source.Text.Length,
                    source.SourceUnitFound);

            elements.Add(element with
            {
                Sources = element.Sources.Select(item => item with
                {
                    StableId = source.CompatibilityStableId,
                }).ToArray(),
            });
        }

        var relationProposals = elements
            .Where(element => element.ParentId is not null)
            .Select(element => new StructuralRelationProposal(
                element.ParentId!, element.Id, StructuralRelationType.ParentChild));
        var structure = ValidatedStructure.FromElements(elements, relationProposals);
        var emittedElementIds = finalStructure.Headings
            .Where(heading => decisionById.TryGetValue(heading.Id, out var decision) &&
                decision.Emit && elementIdByHeadingId.ContainsKey(heading.Id))
            .Select(heading => elementIdByHeadingId[heading.Id])
            .ToHashSet(StringComparer.Ordinal);

        return new StructuralMaterializationResult(
            structure,
            emittedElementIds,
            sourceUnjoined,
            finalStructure.Headings.Count(heading => heading.ParentId is not null &&
                (!elementIdByHeadingId.ContainsKey(heading.Id) ||
                 !elementIdByHeadingId.ContainsKey(heading.ParentId!))));
    }

    private static string ElementId(string headingId) => $"structural:pdf:{headingId}";

    private static MaterializationSource ResolveSource(
        PdfFinalHeading heading,
        StructuralMaterializationSourceAuthority authority,
        DocumentSourceCatalog? sourceCatalog)
    {
        if (authority == StructuralMaterializationSourceAuthority.PdfParserSource)
        {
            var evidence = heading.PdfEvidence ?? throw new StructuralMaterializationException(
                heading.Id, authority, "pdf-source-evidence-missing", null, null, null, false);
            var pdfSourceUnit = sourceCatalog!.Units.FirstOrDefault(unit =>
                string.Equals(unit.SourceId, evidence.BlockId, StringComparison.Ordinal));
            if (pdfSourceUnit is null)
                throw new StructuralMaterializationException(
                    heading.Id, authority, "pdf-source-not-in-catalog", evidence.BlockId,
                    new StructuralSpan(evidence.Span.Start, evidence.Span.End), null, false);

            return new MaterializationSource(
                pdfSourceUnit.SourceId,
                pdfSourceUnit.Text,
                pdfSourceUnit.SourceSpan,
                new StructuralSpan(evidence.Span.Start, evidence.Span.End),
                pdfSourceUnit.SourceAnchor,
                pdfSourceUnit.SourceAnchor.ParagraphId,
                true);
        }

        var anchor = heading.SourceAnchor ?? throw new StructuralMaterializationException(
            heading.Id, authority, "canonical-source-anchor-missing", null, null, null, false);
        var sourceId = anchor.StableId ?? heading.Id;
        var canonicalUnit = sourceCatalog?.Units.FirstOrDefault(unit =>
            string.Equals(unit.SourceId, sourceId, StringComparison.Ordinal));
        if (sourceCatalog is not null && canonicalUnit is null)
            throw new StructuralMaterializationException(
                heading.Id, authority, "canonical-source-not-in-catalog", sourceId,
                new StructuralSpan(anchor.Span.Start, anchor.Span.End), null, false);

        var text = canonicalUnit?.Text ?? heading.SourceText;
        var sourceSpan = canonicalUnit?.SourceSpan ?? new StructuralSpan(0, text.Length);
        var sourceAnchor = canonicalUnit?.SourceAnchor ?? new SourceAnchor
        {
            SourceType = "docx",
            ParagraphId = anchor.StableId,
            ParagraphIndex = anchor.ParagraphIndex,
        };
        return new MaterializationSource(
            sourceId,
            text,
            sourceSpan,
            new StructuralSpan(anchor.Span.Start, anchor.Span.End),
            sourceAnchor,
            anchor.StableId,
            canonicalUnit is not null);
    }

    private sealed record MaterializationSource(
        string SourceId,
        string Text,
        StructuralSpan RawSpan,
        StructuralSpan ProposedSpan,
        SourceAnchor Anchor,
        string? CompatibilityStableId,
        bool SourceUnitFound);

    private static StructuralElementType ElementType(string role) => role switch
    {
        "Title" or "DocumentTitle" => StructuralElementType.Title,
        "Subtitle" or "DocumentSubtitle" => StructuralElementType.Subtitle,
        _ => StructuralElementType.Heading,
    };

    private static ProposedRole ElementRole(string role) => role switch
    {
        "Title" or "DocumentTitle" => ProposedRole.DocumentTitle,
        "Subtitle" or "DocumentSubtitle" => ProposedRole.CoverTitle,
        _ => ProposedRole.HeadingTopic,
    };
}

public sealed record StructuralMaterializationResult(
    ValidatedStructure Structure,
    IReadOnlySet<string> EmittedElementIds,
    int UnjoinedSourceCount,
    int UnjoinedParentCount);

public enum StructuralMaterializationSourceAuthority
{
    CanonicalDocumentSource,
    PdfParserSource,
}

public sealed class StructuralMaterializationException : InvalidOperationException
{
    public StructuralMaterializationException(
        string headingId,
        StructuralMaterializationSourceAuthority sourceAuthority,
        string reason,
        string? sourceId,
        StructuralSpan? proposedSpan,
        int? sourceTextLength,
        bool sourceUnitFound)
        : base(BuildMessage(headingId, sourceAuthority, reason, sourceId, proposedSpan, sourceTextLength,
            sourceUnitFound))
    {
        HeadingId = headingId;
        SourceAuthority = sourceAuthority;
        Reason = reason;
        SourceId = sourceId;
        ProposedSpan = proposedSpan;
        SourceTextLength = sourceTextLength;
        SourceUnitFound = sourceUnitFound;
    }

    public string HeadingId { get; }
    public StructuralMaterializationSourceAuthority SourceAuthority { get; }
    public string Reason { get; }
    public string? SourceId { get; }
    public StructuralSpan? ProposedSpan { get; }
    public int? SourceTextLength { get; }
    public bool SourceUnitFound { get; }

    private static string BuildMessage(
        string headingId,
        StructuralMaterializationSourceAuthority sourceAuthority,
        string reason,
        string? sourceId,
        StructuralSpan? proposedSpan,
        int? sourceTextLength,
        bool sourceUnitFound) =>
        $"Structural materialization failed for heading '{headingId}' using {sourceAuthority}: {reason}; " +
        $"sourceId='{sourceId ?? "<none>"}'; " +
        $"span={(proposedSpan is null ? "<none>" : $"{proposedSpan.Start}..{proposedSpan.End}")}; " +
        $"sourceTextLength={(sourceTextLength?.ToString() ?? "<none>")}; " +
        $"sourceUnitFound={sourceUnitFound}.";
}
