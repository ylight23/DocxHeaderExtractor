using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Immutable facts observed by the PDF parser and layout filter. Model output is deliberately
/// represented separately so it cannot overwrite text, geometry, or source identity.
/// </summary>
internal sealed record PdfSourceFacts(
    string SourceId,
    string RawText,
    int Page,
    int LineCount,
    double Left,
    double TopY,
    double Right,
    double BottomY,
    string StructuralScope,
    IReadOnlyList<string> ObservedEvidence)
{
    /// <summary>Parser-derived only; model proposals cannot alter this marker fact.</summary>
    public PdfMarkerFact? Marker { get; init; }

    /// <summary>Stable parser line identities retained for source/audit correlation.</summary>
    public IReadOnlyList<string> LineIds { get; init; } = [];

    /// <summary>Structured fact provenance for validator authority checks.</summary>
    public IReadOnlyList<PdfObservedEvidence> EvidenceDetails { get; init; } = [];

    /// <summary>Document-family role inferred only from source scope, marker, and text shape.</summary>
    public PdfDomainRole DomainRole { get; init; } = PdfDomainRole.Unknown;

    public string? ScopeHostSourceId { get; init; }
    public string? ScopeTargetDocument { get; init; }
    public bool InsideQuote { get; init; }
    public string? AmendmentOperation { get; init; }
}

internal sealed record PdfObservedEvidence(string Kind, string Value, string Origin);

/// <summary>Small, stable context for a 9B semantic pass; no document-wide free-text prompt.</summary>
internal sealed record PdfCandidateContext(
    PdfSourceFacts Source,
    IReadOnlyList<string> PreviousBlocks,
    IReadOnlyList<string> NextBlocks,
    IReadOnlyList<string> AllowedParentIds,
    string DocumentRegime,
    IReadOnlyList<string> ActiveHeadingStack)
{
    /// <summary>
    /// Nearby source-only blocks that independently passed a generic structural-looking shape.
    /// Used only by the bounded semantic-recovery context experiment; never an asserted heading.
    /// </summary>
    public IReadOnlyList<string> SiblingStructuralBlocks { get; init; } = [];
}

/// <summary>Validated stage trace. It is diagnostic data, never a source of extraction facts.</summary>
public sealed record PdfCandidateStageTrace(
    string Id,
    string Scope,
    string SemanticRole,
    string SpanStatus,
    string ValidationStatus,
    string? Reason);

/// <summary>
/// Separate authority layer: only this source-grounded value may enter grounding/output. It is not
/// a model record and deliberately carries no mutable raw text or geometry.
/// </summary>
internal sealed record PdfValidatedHeading(
    string SourceId,
    DocxHeaderExtractor.Core.Models.TextOffsetSpan HeadingSpan,
    PdfBlockRole Role,
    string StructuralScope,
    string ValidationBasis);

/// <summary>
/// Embedded verbatim in the frozen <c>pdf_hierarchy_facts</c> artifact
/// (<see cref="PdfHierarchyFactsRow.ValidatedStructures"/>), which the CLI writes under a camelCase
/// naming policy - explicit property names here so an offline reader (M9.4's shadow comparator among
/// them) round-trips this type correctly instead of a case-sensitive reader silently leaving
/// <see cref="SourceId"/>/<see cref="DomainRole"/>/etc. at their default.
/// </summary>
public sealed record PdfValidatedStructure(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("parentResolution")] string ParentResolution,
    [property: JsonPropertyName("decision")] string Decision)
{
    [JsonPropertyName("domainRole")]
    public PdfDomainRole DomainRole { get; init; } = PdfDomainRole.Unknown;

    [JsonPropertyName("structuralScope")]
    public string StructuralScope { get; init; } = "document_body";
}

internal static class PdfHierarchyResolver
{
    public static IReadOnlyList<PdfValidatedStructure> Resolve(
        IReadOnlyList<PdfValidatedHeading> headings,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts)
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = headings.Select(heading =>
        {
            var source = contexts[heading.SourceId].Source;
            // Reparse the immutable raw source for hierarchy. Source.Marker is retained for
            // provenance/context, but hierarchy must stay identical when a caller supplied a
            // narrower parser marker fact.
            var marker = PdfMarkerFactsParser.Parse(source.RawText) ?? source.Marker;
            var signature = marker?.Signature;
            if (signature is not null && !signatures.ContainsKey(signature)) signatures[signature] = signatures.Count + 1;
            return (Heading: heading, Marker: marker, Signature: signature, Role: source.DomainRole);
        }).ToArray();

        var result = new List<PdfValidatedStructure>();
        foreach (var item in items)
        {
            var tier = DocumentDomainPolicy.HierarchyTier(item.Role);
            var parent = FindParent(item, items, result, tier);
            var level = tier ?? (item.Marker is { IsPath: true, Depth: > 1 }
                ? item.Marker.Value.Depth
                : item.Signature is not null && signatures.Count >= 2 ? signatures[item.Signature] : 1);
            result.Add(new PdfValidatedStructure(item.Heading.SourceId, Math.Clamp(level, 1, 9), parent,
                parent is null ? "unresolved" : "marker-resolved", "requires_review")
            {
                DomainRole = item.Role,
                StructuralScope = contexts[item.Heading.SourceId].Source.StructuralScope,
            });
        }
        return result;
    }

    private static string? FindParent((PdfValidatedHeading Heading, PdfMarkerFact? Marker, string? Signature, PdfDomainRole Role) item,
        IReadOnlyList<(PdfValidatedHeading Heading, PdfMarkerFact? Marker, string? Signature, PdfDomainRole Role)> all,
        IReadOnlyList<PdfValidatedStructure> resolved, int? tier)
    {
        if (item.Marker is not { } current) return null;
        for (var index = resolved.Count - 1; index >= 0; index--)
        {
            var previous = all[index];
            if (previous.Marker is not { } marker) continue;
            var previousTier = DocumentDomainPolicy.HierarchyTier(previous.Role);
            if (tier is not null && previousTier is not null && previousTier < tier)
                return previous.Heading.SourceId;
            if (current.IsPath && marker.IsPath && marker.Depth == current.Depth - 1)
                return previous.Heading.SourceId;
            if (!current.IsPath && !marker.IsPath && item.Signature is not null && previous.Signature is not null &&
                !StringComparer.Ordinal.Equals(item.Signature, previous.Signature)) return previous.Heading.SourceId;
        }
        return null;
    }
}

internal static class PdfCandidateContextBuilder
{
    public static IReadOnlyDictionary<string, PdfCandidateContext> Build(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        int contextWindow = 2,
        List<StructuralScopeTransition>? scopeTrace = null,
        IReadOnlySet<string>? withheldAppendixEntries = null,
        IReadOnlySet<string>? withheldQuoteEntries = null)
    {
        var annotationByLine = annotations.ToDictionary(a => LineKey(a.Line));
        var ordered = blocks.OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray();
        var fallbackRegime = annotations.Count == 0 ? "document_body" :
            annotations.Count(a => a.TableLike) / (double)annotations.Count > 0.55 ? "table_dominant" : "document_body";
        var regime = DocumentDomainPolicy.InferRegime(ordered.Select(block => block.DisplayText), fallbackRegime);
        var result = new Dictionary<string, PdfCandidateContext>(StringComparer.Ordinal);
        var tocBlockIds = PdfStructuralScopeDetector.DetectTocBlockIds(ordered);
        var scopeTracker = new StructuralScopeTracker(scopeTrace, withheldAppendixEntries, withheldQuoteEntries);
        var stack = new List<string>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var block = ordered[index];
            var facts = scopeTracker.Apply(BuildFacts(block, annotationByLine, tocBlockIds.Contains(block.Id), regime));
            var window = Math.Clamp(contextWindow, 0, 6);
            var previous = ordered.Take(index).TakeLast(window).Select(b => PromptExcerpt(b.DisplayText)).ToArray();
            var next = ordered.Skip(index + 1).Take(window).Select(b => PromptExcerpt(b.DisplayText)).ToArray();
            var parents = ordered.Take(index).TakeLast(8).Select(b => b.Id).ToArray();
            result[block.Id] = new PdfCandidateContext(facts, previous, next, parents, regime, stack.TakeLast(4).ToArray());
            if (facts.StructuralScope == "document_body" && PdfMarkerFactsParser.Parse(block.DisplayText) is not null)
                stack.Add($"{block.Id}: {PromptExcerpt(block.DisplayText)}");
        }
        return result;
    }

    private static PdfSourceFacts BuildFacts(
        PdfSemanticBlock block,
        IReadOnlyDictionary<string, PdfLineBlockAnnotation> annotationByLine,
        bool isTocBlock,
        string regime)
    {
        var sourceAnnotations = block.Lines
            .Select(line => annotationByLine.TryGetValue(LineKey(line), out var annotation) ? annotation : null)
            .Where(annotation => annotation is not null)
            .Cast<PdfLineBlockAnnotation>()
            .ToArray();
        var marker = PdfMarkerFactsParser.Parse(block.DisplayText);
        var evidence = new List<string>
        {
            block.LineCount == 1 ? "standalone_line" : "multi_line_cluster",
            marker is null ? "no_marker" : $"marker:{marker.Value.Family}",
        };
        var looseMarker = PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(block.DisplayText);
        if (looseMarker is not null &&
            PdfTextUtilities.CanonicalForMatch(block.DisplayText).Length <
            PdfTextUtilities.CanonicalForMatch(looseMarker).Length + 6)
            evidence.Add("marker_only_source");
        if (sourceAnnotations.Any(a => a.Repeated)) evidence.Add("repeated_region");
        if (sourceAnnotations.Any(a => a.HeaderFooterZone)) evidence.Add("header_footer_zone");
        if (sourceAnnotations.Any(a => a.TableLike)) evidence.Add("table_like");

        var scope = sourceAnnotations.Length > 0 && sourceAnnotations.All(a => a.TableLike)
            ? "table"
            : sourceAnnotations.Length > 0 && sourceAnnotations.All(a => a.PageNumber || (a.Repeated && a.HeaderFooterZone))
                ? "running_page_artifact"
                : isTocBlock
                    ? "table_of_contents"
                : PdfStructuralScopeDetector.IsFormalSyntax(block.DisplayText)
                    ? "code_or_grammar"
                : "document_body";
        if (scope == "code_or_grammar") evidence.Add("formal_syntax_shape");
        if (scope == "table_of_contents") evidence.Add("toc_entry_cluster");
        var facts = new PdfSourceFacts(
            block.Id, block.Text, block.Page, block.LineCount, block.Left, block.TopY, block.Right, block.BottomY,
            scope, evidence)
        {
            Marker = marker,
            LineIds = block.Lines.Select(LineKey).ToArray(),
            EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                item is "standalone_line" or "multi_line_cluster" or "table_like" or "header_footer_zone" or "repeated_region"
                    ? "layout_parser"
                    : item.StartsWith("marker:", StringComparison.Ordinal) ? "marker_parser" : "scope_detector")).ToArray(),
        };
        return facts with { DomainRole = DocumentDomainPolicy.Classify(facts, regime) };
    }

    private static string LineKey(PdfLine line) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    private static string PromptExcerpt(string text) => text.Length <= 180 ? text : text[..180];
}

internal static class PdfProposalValidator
{
    public static IReadOnlyList<PdfValidatedHeading> Validate(
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlyList<PdfBlockDecision> decisions) => decisions
        .Where(decision => contexts.TryGetValue(decision.Id, out var context) && IsEligibleHeading(decision, context))
        .Select(decision =>
        {
            var context = contexts[decision.Id];
            return new PdfValidatedHeading(
                decision.Id, decision.HeadingSpan!, decision.Role, context.Source.StructuralScope,
                "source-grounded-pointer-span");
        })
        .ToArray();

    public static IReadOnlyList<PdfCandidateStageTrace> Trace(
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        var byId = decisions.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return contexts.Values.Select(context =>
        {
            if (!byId.TryGetValue(context.Source.SourceId, out var decision))
                return new PdfCandidateStageTrace(context.Source.SourceId, context.Source.StructuralScope, "unknown", "not-proposed", "unresolved", "missing-model-proposal");

            string? spanReason = null;
            var spanStatus = decision.Role == PdfBlockRole.HeadingTopic
                ? ValidateSpan(decision, context.Source.RawText, out spanReason)
                : "not-applicable";
            var structuralScopeRejected = context.Source.StructuralScope is "table" or "running_page_artifact" or "table_of_contents" or "code_or_grammar" or "reference_list" or "index_terms";
            var scopeRejected = structuralScopeRejected || DocumentDomainPolicy.IsExcludedFromOutline(context.Source.DomainRole);
            var validation = decision.Role != PdfBlockRole.HeadingTopic
                ? "not-heading"
                : scopeRejected
                    ? "unresolved"
                    : spanStatus == "valid" ? "eligible" : "unresolved";
            return new PdfCandidateStageTrace(
                context.Source.SourceId, context.Source.StructuralScope, decision.SemanticRole.ToString(), spanStatus,
                validation, scopeRejected
                    ? structuralScopeRejected ? "scope-conflict" : $"domain-role-conflict:{context.Source.DomainRole}"
                    : spanReason);
        }).ToArray();
    }

    public static bool IsEligibleHeading(PdfBlockDecision decision, PdfCandidateContext context) =>
        decision.Role == PdfBlockRole.HeadingTopic &&
        context.Source.StructuralScope is not ("table" or "running_page_artifact" or "table_of_contents" or "code_or_grammar" or "reference_list" or "index_terms") &&
        !DocumentDomainPolicy.IsExcludedFromOutline(context.Source.DomainRole) &&
        HasTrustedEvidenceOrigins(context.Source) &&
        ValidateSpan(decision, context.Source.RawText, out _) == "valid";

    private static bool HasTrustedEvidenceOrigins(PdfSourceFacts source) =>
        source.EvidenceDetails.All(evidence => evidence.Origin is "layout_parser" or "marker_parser" or "scope_detector");

    private static string ValidateSpan(PdfBlockDecision decision, string sourceText, out string? reason)
    {
        if (decision.HeadingSpan is null)
        {
            reason = "missing-pointer-span";
            return "invalid";
        }

        var span = decision.HeadingSpan;
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length)
        {
            reason = "invalid-pointer-span";
            return "invalid";
        }

        reason = null;
        return "valid";
    }
}

/// <summary>
/// Parser-side marker facts are intentionally broader than <see cref="NumberingAudit"/>. They
/// improve PDF retrieval/context only; final sequence auditing remains strict and independent.
/// </summary>
internal readonly record struct PdfMarkerFact(string Signature, int Depth, string Family, bool IsPath)
{
    /// <summary>
    /// M8.1d-2 representation only. The parser already knows every component of a numeric path;
    /// previously it kept only the count, which forced downstream code to re-derive components with
    /// a stricter grammar that cannot read a dot-stripped source. Carrying them here removes that
    /// second parse as a source of truth. It grants no hierarchy authority on its own.
    /// </summary>
    public ImmutableArray<int> Components { get; init; } = ImmutableArray<int>.Empty;
}

internal static class PdfMarkerFactsParser
{
    private static readonly Regex SpacedArabicPathRx = new(
        @"^\s*((?:\d{1,3}\s+){1,4}\d{1,3})(?:[.)\-:]?\s+)(?=\p{L})",
        RegexOptions.Compiled);

    public static PdfMarkerFact? Parse(string text)
    {
        // PDF extraction often turns `4.2.2` into `4 2 2`. Check this repairable source shape
        // before the strict parser mistakes only its first component for a level-one marker.
        var spacedPath = SpacedArabicPathRx.Match(text);
        if (spacedPath.Success)
        {
            var parts = Regex.Matches(spacedPath.Groups[1].Value, @"\d{1,3}")
                .Select(match => int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            if (parts.Length > 0)
                return new PdfMarkerFact($"Arabic:{parts.Length}", parts.Length, "spaced_arabic", true)
                {
                    Components = [.. parts],
                };
        }

        if (NumberingAudit.Parse(text) is { } strict)
            return new PdfMarkerFact(strict.Signature, strict.Depth, strict.Kind.ToString().ToLowerInvariant(),
                strict.Kind == NumberKind.Arabic)
            {
                // Only an arabic path has components. Roman/letter/labelled markers stay empty
                // rather than being flattened into a one-element path they never had.
                Components = strict.Kind == NumberKind.Arabic && NumberingAudit.ParseArabicPath(text) is { } strictPath
                    ? [.. strictPath]
                    : ImmutableArray<int>.Empty,
            };

        var looseLabel = PdfLayoutEvidenceOutline.ParseLooseLabelledMarkerForAudit(text);
        if (looseLabel is not null)
        {
            var separator = looseLabel.IndexOf(':');
            var label = separator > 0 ? looseLabel[..separator] : looseLabel;
            return new PdfMarkerFact($"label:{label}", 1, "loose_labelled", false);
        }

        return null;
    }
}

internal static class PdfStructuralScopeDetector
{
    // Formal syntax has a stable operator-plus-symbol shape. This does not depend on the
    // vocabulary of a particular standard or document family.
    private static readonly Regex FormalSyntaxRx = new(
        @"^\s*[A-Za-z][A-Za-z0-9_-]{0,80}\s*(?:::?=|=)\s*(?:[^.]{1,240})$",
        RegexOptions.Compiled);
    private static readonly Regex TocEntryRx = new(
        @"(?:\.{2,}|\u2026)\s*\d{1,4}\s*$",
        RegexOptions.Compiled);

    public static bool IsFormalSyntax(string text)
    {
        if (!FormalSyntaxRx.IsMatch(text)) return false;
        return text.Contains("::=", StringComparison.Ordinal) ||
               text.Count(character => character == '=') == 1;
    }

    public static IReadOnlySet<string> DetectTocBlockIds(IReadOnlyList<PdfSemanticBlock> ordered)
    {
        if (ordered.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var firstPage = ordered[0].Page;
        var lastPage = ordered[^1].Page;
        var earlyPageLimit = Math.Min(firstPage + 5, firstPage + Math.Max(1, (lastPage - firstPage + 1) / 4));
        var entries = ordered.Where(block => block.Page <= earlyPageLimit && TocEntryRx.IsMatch(block.DisplayText)).ToArray();
        var tocPages = entries.GroupBy(block => block.Page)
            .Where(group => group.Count() >= 3)
            .Select(group => group.Key)
            .ToHashSet();
        return entries.Where(block => tocPages.Contains(block.Page))
            .Select(block => block.Id)
            .ToHashSet(StringComparer.Ordinal);
    }
}
