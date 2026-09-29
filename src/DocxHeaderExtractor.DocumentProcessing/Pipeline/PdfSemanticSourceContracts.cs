using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

internal sealed record PdfStructuralContainerObservation(
    string ContainerId,
    StructuralElementType Type,
    string SourceId,
    StructuralSpan Span,
    IReadOnlyList<string> MemberSourceIds,
    string Evidence);

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

    /// <summary>
    /// Format the document declares about this occurrence, as opposed to where it sits. A PDF has
    /// no style names, so weight, slant and size are the only declarations available - and for a
    /// PDF they carry most of the heading signal, which is why they belong on the facts rather than
    /// being reconstructed downstream from geometry.
    /// </summary>
    public double BoldRatio { get; init; }

    public double ItalicRatio { get; init; }

    /// <summary>Absolute point size. Only ever reported to the model relative to the document.</summary>
    public double FontSize { get; init; }

    /// <summary>
    /// The block's typography with each fact's origin (<see cref="PdfLineTypography"/>, averaged over its
    /// lines; the font name and bold evidence source of its first line that has one). Null when the lines
    /// carry none.
    /// </summary>
    public PdfLineTypography? Typography { get; init; }

    /// <summary>Stable parser line identities retained for source/audit correlation.</summary>
    public IReadOnlyList<string> LineIds { get; init; } = [];

    /// <summary>
    /// Physical position, reported to the model in place of <see cref="StructuralScope"/>: the vertical
    /// position of the block's top line (0 = lowest text in the document, 1 = highest), and on how
    /// many pages - first to last - its normalized text recurs. For a multi-line block that is its least-recurring line, since the block
    /// recurs as a whole at most that often. Null where no line annotation was available.
    /// </summary>
    public double? VerticalPosition { get; init; }

    public int? SameNormalizedTextPageCount { get; init; }

    public int? SameNormalizedTextFirstPage { get; init; }

    public int? SameNormalizedTextLastPage { get; init; }

    /// <summary>Structured fact provenance for validator authority checks.</summary>
    public IReadOnlyList<PdfObservedEvidence> EvidenceDetails { get; init; } = [];

    public string? ScopeHostSourceId { get; init; }
    public string? ScopeTargetDocument { get; init; }
    public bool InsideQuote { get; init; }
    public string? AmendmentOperation { get; init; }

    /// <summary>Parser/layout-owned container observations; semantic labels cannot create these.</summary>
    public IReadOnlyList<PdfStructuralContainerObservation> LayoutContainers { get; init; } = [];
}

internal sealed record PdfObservedEvidence(string Kind, string Value, string Origin);

/// <summary>Small, stable context for a 9B semantic pass; no document-wide free-text prompt.</summary>
internal sealed record PdfSemanticSourceContext(
    PdfSourceFacts Source,
    IReadOnlyList<string> PreviousBlocks,
    IReadOnlyList<string> NextBlocks,
    IReadOnlyList<string> AllowedParentIds,
    string DocumentRegime)
{
}

/// <summary>
/// Parser-owned UTF-16 source boundaries for pointer spans. The model may select from this map,
/// but it never creates or materializes heading text. Boundaries are derived only from immutable
/// source text and remain independent of document-specific literals.
/// </summary>
internal static class PdfSpanBoundaryMap
{
    public static IReadOnlyList<int> For(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var boundaries = new SortedSet<int> { 0, sourceText.Length };
        for (var index = 0; index < sourceText.Length; index++)
        {
            var current = sourceText[index];
            var previous = index == 0 ? '\0' : sourceText[index - 1];

            if (char.IsWhiteSpace(current) || char.IsPunctuation(current) || char.IsSymbol(current))
            {
                boundaries.Add(index);
                boundaries.Add(index + 1);
            }

            if (index > 0 && char.IsLetterOrDigit(current) != char.IsLetterOrDigit(previous))
                boundaries.Add(index);
        }

        return boundaries.ToArray();
    }

    public static bool Contains(string sourceText, int offset) =>
        offset >= 0 && offset <= sourceText.Length && For(sourceText).Contains(offset);
}

/// <summary>Validated stage trace. It is diagnostic data, never a source of extraction facts.</summary>
public sealed record PdfSemanticSourceStageTrace(
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
    DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan HeadingSpan,
    PdfBlockRole Role,
    string StructuralScope,
    string ValidationBasis)
{
    /// <summary>
    /// The claim's complete ordered coordinate tuple, when its coordinate system has one. Null
    /// where a claim is one selection inside one occurrence - a single-part claim -
    /// because there is nothing a tuple would say that <see cref="HeadingSpan"/> does not.
    /// <para>
    /// A heading that wraps across two atoms is two parts here and stays two parts through
    /// materialization. The alternative - carrying only the first and letting the span stand for
    /// the whole - is the truncation the structured coordinate system exists to remove.
    /// </para>
    /// </summary>
    public IReadOnlyList<CanonicalSemanticBoundPart>? Parts { get; init; }
}

/// <summary>
/// Explicit property names so a camelCase or case-sensitive reader round-trips this type instead of
/// silently leaving fields at their default.
/// </summary>
public sealed record PdfValidatedStructure(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("parentId")] string? ParentId,
    [property: JsonPropertyName("parentResolution")] string ParentResolution,
    [property: JsonPropertyName("decision")] string Decision)
{
    [JsonPropertyName("structuralScope")]
    public string StructuralScope { get; init; } = "document_body";

}

internal static class PdfSemanticSourceContextBuilder
{
    public static IReadOnlyDictionary<string, PdfSemanticSourceContext> Build(
        IReadOnlyList<PdfSemanticBlock> blocks,
        IReadOnlyList<PdfLineBlockAnnotation> annotations,
        int contextWindow = 2)
    {
        var annotationByLine = annotations.ToDictionary(a => LineKey(a.Line));
        var ordered = blocks.OrderBy(b => b.Page).ThenByDescending(b => b.TopY).ThenBy(b => b.Id, StringComparer.Ordinal).ToArray();
        const string regime = "document_body";
        var result = new Dictionary<string, PdfSemanticSourceContext>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
        {
            var block = ordered[index];
            var facts = BuildFacts(block, annotationByLine, regime);
            var window = Math.Clamp(contextWindow, 0, 6);
            var previous = ordered.Take(index).TakeLast(window).Select(b => PromptExcerpt(b.DisplayText)).ToArray();
            var next = ordered.Skip(index + 1).Take(window).Select(b => PromptExcerpt(b.DisplayText)).ToArray();
            var parents = ordered.Take(index).TakeLast(8).Select(b => b.Id).ToArray();
            result[block.Id] = new PdfSemanticSourceContext(facts, previous, next, parents, regime);
        }
        return result;
    }

    private static PdfLineTypography? TypographyOf(IReadOnlyList<PdfLine> lines)
    {
        var typed = lines.Select(line => line.Typography).OfType<PdfLineTypography>().ToArray();
        if (typed.Length == 0) return null;
        var bold = typed.FirstOrDefault(t => t.BoldEvidenceSource != PdfLineTypography.None);
        return new PdfLineTypography(
            typed[0].Version,
            typed.Average(t => t.NominalFontSize),
            typed.Average(t => t.EffectivePointSize),
            typed.Average(t => t.FontBoldFlagRatio),
            typed.Average(t => t.FontNameBoldRatio),
            typed.Average(t => t.DerivedBoldRatio),
            bold?.BoldEvidenceSource ?? PdfLineTypography.None,
            typed.Select(t => t.FontName).FirstOrDefault(n => n.Length > 0) ?? "")
        {
            Glyphs = GlyphsOf(lines),
        };
    }

    /// <summary>
    /// A block's glyph statistics from its lines': the dominant size is the one whose lines carry most characters,
    /// the median the median of the lines' medians, the extremes the extremes, the ratios weighted by characters.
    /// </summary>
    private static PdfGlyphStatistics? GlyphsOf(IReadOnlyList<PdfLine> lines)
    {
        var typed = lines.Where(l => l.Typography?.Glyphs is not null).Select(l => (Line: l, Glyphs: l.Typography!.Glyphs!)).ToArray();
        if (typed.Length == 0) return null;
        if (typed.Length == 1) return typed[0].Glyphs;
        var weight = typed.Select(t => (double)Math.Max(1, t.Line.Text.Length)).ToArray();
        var total = weight.Sum();
        var medians = typed.Select(t => t.Glyphs.MedianPointSize).Order().ToArray();
        return new PdfGlyphStatistics(
            typed.Select((t, i) => (t.Glyphs.DominantPointSize, weight[i])).GroupBy(x => x.DominantPointSize)
                .OrderByDescending(g => g.Sum(x => x.Item2)).ThenBy(g => g.Key).First().Key,
            medians[medians.Length / 2],
            typed.Min(t => t.Glyphs.MinPointSize),
            typed.Max(t => t.Glyphs.MaxPointSize),
            typed.Select((t, i) => (t.Glyphs.DominantFontName, weight[i])).Where(x => x.DominantFontName.Length > 0)
                .GroupBy(x => x.DominantFontName).OrderByDescending(g => g.Sum(x => x.Item2)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault() ?? "",
            typed.Select((t, i) => t.Glyphs.BoldGlyphRatio * weight[i]).Sum() / total,
            typed.Select((t, i) => t.Glyphs.ItalicGlyphRatio * weight[i]).Sum() / total);
    }

    private static PdfLineBlockAnnotation? LeastRecurring(IReadOnlyList<PdfLineBlockAnnotation> annotations) =>
        annotations.Count == 0 ? null : annotations.MinBy(a => a.SameNormalizedTextPageCount);

    private static PdfSourceFacts BuildFacts(
        PdfSemanticBlock block,
        IReadOnlyDictionary<string, PdfLineBlockAnnotation> annotationByLine,
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
        var looseMarker = LooseLabelledMarkerParser.ParseCanonical(block.DisplayText);
        if (looseMarker is not null &&
            PdfTextUtilities.CanonicalForMatch(block.DisplayText).Length <
            PdfTextUtilities.CanonicalForMatch(looseMarker).Length + 6)
            evidence.Add("marker_only_source");
        var facts = new PdfSourceFacts(
            // The canonical projection, the same string the alias catalog and the binder use. These
            // two paths build facts for one occurrence and must agree: if the model is shown the raw
            // concatenation while the binder validates against the projection, every proposal fails
            // as non-verbatim and the failure looks like the model getting the text wrong.
            block.Id, block.VerbatimText, block.Page, block.LineCount, block.Left, block.TopY, block.Right, block.BottomY,
            "document_body", evidence)
        {
            Marker = marker,
            BoldRatio = block.Lines.Count == 0 ? 0 : block.Lines.Average(line => line.BoldRatio),
            ItalicRatio = block.Lines.Count == 0 ? 0 : block.Lines.Average(line => line.ItalicRatio),
            FontSize = block.Lines.Count == 0 ? 0 : block.Lines.Average(line => line.FontSize),
            Typography = TypographyOf(block.Lines),
            LineIds = block.Lines.Select(LineKey).ToArray(),
            VerticalPosition = sourceAnnotations.Length == 0 ? null
                : sourceAnnotations.Max(a => a.VerticalPosition),
            SameNormalizedTextPageCount = LeastRecurring(sourceAnnotations)?.SameNormalizedTextPageCount,
            SameNormalizedTextFirstPage = LeastRecurring(sourceAnnotations)?.SameNormalizedTextFirstPage,
            SameNormalizedTextLastPage = LeastRecurring(sourceAnnotations)?.SameNormalizedTextLastPage,
            EvidenceDetails = evidence.Select(item => new PdfObservedEvidence(item, "true",
                item is "standalone_line" or "multi_line_cluster"
                    ? "layout_parser"
                    : "marker_parser")).ToArray(),
        };
        return facts;
    }

    private static string LineKey(PdfLine line) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{line.Page}|{line.Y:R}|{line.Left:R}|{line.Right:R}|{line.Text}");

    private static string PromptExcerpt(string text) => text.Length <= 180 ? text : text[..180];
}

/// <summary>
/// Turns model proposals into validated headings. It answers one question only: can this proposal
/// be anchored in the source exactly as the model stated it — a real pointer span, on a real token
/// boundary, from a parser lineage this pipeline owns.
/// <para>
/// It deliberately does not answer whether the proposal <em>means</em> a heading. It validates
/// only source identity and exact pointer spans; semantic interpretation belongs to the model.
/// </para>
/// </summary>
internal static class PdfProposalValidator
{
    public static IReadOnlyList<PdfValidatedHeading> Validate(
        IReadOnlyDictionary<string, PdfSemanticSourceContext> contexts,
        IReadOnlyList<PdfBlockDecision> decisions) => decisions
        .Where(decision => contexts.TryGetValue(decision.Id, out var context) && IsEligibleHeading(decision, context))
        .Select(decision =>
        {
            var context = contexts[decision.Id];
            return new PdfValidatedHeading(
                decision.Id, decision.HeadingSpan!, decision.Role, context.Source.StructuralScope,
                "source-grounded-pointer-span")
            {
                Parts = decision.Parts,
            };
        })
        .ToArray();

    public static IReadOnlyList<PdfSemanticSourceStageTrace> Trace(
        IReadOnlyDictionary<string, PdfSemanticSourceContext> contexts,
        IReadOnlyList<PdfBlockDecision> decisions)
    {
        var byId = decisions.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return contexts.Values.Select(context =>
        {
            if (!byId.TryGetValue(context.Source.SourceId, out var decision))
                return new PdfSemanticSourceStageTrace(context.Source.SourceId, context.Source.StructuralScope, "unknown", "not-proposed", "unresolved", "missing-model-proposal");

            string? spanReason = null;
            var spanStatus = decision.Role == PdfBlockRole.HeadingTopic
                ? ValidateSpan(decision, context.Source.RawText, out spanReason)
                : "not-applicable";
            var validation = decision.Role != PdfBlockRole.HeadingTopic
                ? "not-heading"
                : spanStatus == "valid" ? "eligible" : "unresolved";
            return new PdfSemanticSourceStageTrace(
                context.Source.SourceId, context.Source.StructuralScope, decision.SemanticRole.ToString(), spanStatus,
                validation, spanReason);
        }).ToArray();
    }

    /// <summary>
    /// Source validity only. The model said this is a heading; the three conditions below ask
    /// whether the harness can point at it — not whether it agrees.
    /// </summary>
    public static bool IsEligibleHeading(PdfBlockDecision decision, PdfSemanticSourceContext context) =>
        decision.Role == PdfBlockRole.HeadingTopic &&
        HasTrustedEvidenceOrigins(context.Source) &&
        ValidateSpan(decision, context.Source.RawText, out _) == "valid";

    private static bool HasTrustedEvidenceOrigins(PdfSourceFacts source) =>
        source.EvidenceDetails.All(evidence => evidence.Origin is "layout_parser" or "marker_parser" or
            "scope_detector" or "docx_parser" or "ooxml_parser");

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

        if (!PdfSpanBoundaryMap.Contains(sourceText, span.Start) ||
            !PdfSpanBoundaryMap.Contains(sourceText, span.End))
        {
            reason = "invalid-pointer-boundary";
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
