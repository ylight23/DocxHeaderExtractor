using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.DocumentProcessing.Authority;

/// <summary>
/// Materializes an evaluation-safe occurrence trace from explicit route telemetry. This builder
/// never joins by text: a source occurrence is connected to a candidate only through the route's
/// declared source representation map.
/// </summary>
public static class RouteOccurrenceTraceBuilder
{
    public static IReadOnlyList<RouteOccurrenceTrace> Build(
        string documentId,
        string sourceSha256,
        DocumentSourceCatalog sourceCatalog,
        ValidatedStructure structure,
        IReadOnlySet<string> emittedElementIds,
        RouteExecutionAudit audit,
        string? documentGroupId = null,
        string routeOwner = "UNKNOWN_ROUTE")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(sourceCatalog);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(emittedElementIds);
        ArgumentNullException.ThrowIfNull(audit);
        documentGroupId ??= documentId;

        var representations = audit.SourceRepresentations
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var candidates = audit.CandidateBlocks
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var selected = audit.SelectedCandidateBlocks
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var requestsByCandidate = audit.ModelRequests
            .SelectMany(request => request.CandidateIds.Select(candidateId => (candidateId, request.RequestId)))
            .GroupBy(item => item.candidateId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var decisions = audit.BlockDecisions
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var validations = audit.CandidateStageTraces
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var markerFacts = audit.HierarchyFacts
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var structuralFacts = audit.ValidatedStructures
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var elementsBySource = structure.Elements
            .SelectMany(element => element.Sources.Select(source => (source.SourceId, Element: element)))
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Element, StringComparer.Ordinal);
        var elementsById = structure.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);

        return sourceCatalog.Units
            .OrderBy(unit => unit.SourceOrdinal)
            .ThenBy(unit => unit.SourceId, StringComparer.Ordinal)
            .Select(unit =>
            {
                representations.TryGetValue(unit.SourceId, out var representation);
                var candidateId = representation?.CandidateId;
                var requestIds = candidateId is not null && requestsByCandidate.TryGetValue(candidateId, out var ids)
                    ? ids
                    : [];
                decisions.TryGetValue(candidateId ?? string.Empty, out var decision);
                validations.TryGetValue(candidateId ?? string.Empty, out var validation);
                markerFacts.TryGetValue(unit.SourceId, out var marker);
                structuralFacts.TryGetValue(unit.SourceId, out var structural);
                elementsBySource.TryGetValue(unit.SourceId, out var element);
                var finalParent = element?.ParentId is { } parentId && elementsById.TryGetValue(parentId, out var parent)
                    ? parent.Sources.FirstOrDefault()?.SourceId ?? parent.Id
                    : null;
                var candidateConstructed = candidateId is not null && candidates.Contains(candidateId);
                var candidateSelected = candidateId is not null && selected.Contains(candidateId);

                return new RouteOccurrenceTrace
                {
                    DocumentId = documentId,
                    DocumentGroupId = documentGroupId,
                    SourceSha256 = sourceSha256,
                    SourceId = unit.SourceId,
                    StableId = unit.SourceAnchor.ParagraphId ?? unit.SourceId,
                    SourceOrdinal = unit.SourceOrdinal,
                    SourceSpan = ToTextSpan(unit.SourceSpan),
                    RepresentationId = representation?.RepresentationId,
                    RepresentationKind = representation?.RepresentationKind,
                    CandidateId = candidateId,
                    RouteOwner = requestIds.Length > 0 ? "SEMANTIC_MODEL_ROUTE" : routeOwner,
                    CandidateConstructed = representation is null ? null : candidateConstructed,
                    CandidateSelected = representation is null ? null : candidateSelected,
                    ModelRequestIds = requestIds,
                    ModelRequestMembership = representation is null ? "UNKNOWN" :
                        requestIds.Length > 0 ? "EXACT_CANDIDATE_ID" : "NOT_REQUESTED",
                    ModelProposalPresent = requestIds.Length == 0 ? null : decision is not null,
                    ModelRole = requestIds.Length == 0 ? null : decision?.SemanticRole,
                    ModelParent = requestIds.Length == 0 ? null : decision?.ProposedParentId,
                    ModelSpan = requestIds.Length == 0 ? null : decision?.ProposedSourceSpan,
                    ValidationStatus = validation?.ValidationStatus,
                    ValidationIssues = validation?.Reason is { Length: > 0 } reason ? [reason] : [],
                    MarkerBefore = marker?.MarkerPath ?? marker?.MarkerFamily,
                    MarkerAfter = marker?.ResolvedLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MarkerReason = marker?.ParentResolution,
                    StructuralBefore = structural?.ParentResolution,
                    StructuralAfter = structural?.ParentId,
                    StructuralReason = structural?.Decision,
                    FinalIncluded = element is not null && emittedElementIds.Contains(element.Id),
                    FinalRole = element?.Role.ToString() ?? element?.Type.ToString(),
                    FinalLevel = element?.Level,
                    FinalParent = finalParent,
                    FinalSpan = element?.Sources.FirstOrDefault() is { } source
                        ? ToTextSpan(source.Span)
                        : null,
                };
            })
            .ToArray();
    }

    private static TextOffsetSpan ToTextSpan(StructuralSpan span) => new(span.Start, span.End);
}
