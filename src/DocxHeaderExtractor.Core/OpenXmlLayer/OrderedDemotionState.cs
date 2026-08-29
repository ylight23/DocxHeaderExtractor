using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.OpenXmlLayer;

/// <summary>
/// Explicit mutable state for the ordered demotion phase. Source and numbering/style facts are
/// immutable inputs; only policy state (role/score) is mutated by the demotion operations.
/// </summary>
internal sealed class OrderedDemotionState
{
    private OrderedDemotionState(IReadOnlyList<OrderedDemotionParagraph> paragraphs)
    {
        Paragraphs = paragraphs;
    }

    public IReadOnlyList<OrderedDemotionParagraph> Paragraphs { get; }

    public static OrderedDemotionState Create(
        IReadOnlyList<SlimParagraph> legacyParagraphs,
        SourceDocument source,
        NumberingStyleFeatures features)
    {
        ArgumentNullException.ThrowIfNull(legacyParagraphs);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(features);

        var sourceById = source.Paragraphs.ToDictionary(p => p.SourceId, StringComparer.Ordinal);
        var numberingById = features.Numbering.ToDictionary(p => p.SourceId, StringComparer.Ordinal);
        var styleById = features.Styles.ToDictionary(p => p.SourceId, StringComparer.Ordinal);
        var result = new List<OrderedDemotionParagraph>(legacyParagraphs.Count);

        foreach (var legacy in legacyParagraphs)
        {
            var sourceId = string.IsNullOrWhiteSpace(legacy.StableId)
                ? $"p:{legacy.Index}"
                : legacy.StableId;
            if (!sourceById.TryGetValue(sourceId, out var sourceParagraph) ||
                !numberingById.TryGetValue(sourceId, out var numbering) ||
                !styleById.TryGetValue(sourceId, out var style))
                throw new InvalidOperationException($"Missing source features for demotion paragraph '{sourceId}'.");

            result.Add(new OrderedDemotionParagraph(
                sourceParagraph,
                numbering,
                style,
                legacy.BodyFontSizePt,
                legacy.Corrupt,
                legacy.HasBuiltInHeadingStyle,
                legacy.NumberingStyleLevel,
                legacy.Role,
                legacy.Score));
        }

        return new OrderedDemotionState(result);
    }

    public void ApplyPolicyStateTo(IReadOnlyList<SlimParagraph> legacyParagraphs)
    {
        if (legacyParagraphs.Count != Paragraphs.Count)
            throw new InvalidOperationException("Ordered demotion state and legacy paragraph order diverged.");

        for (var i = 0; i < Paragraphs.Count; i++)
        {
            legacyParagraphs[i].Role = Paragraphs[i].Role;
            legacyParagraphs[i].Score = Paragraphs[i].Score;
        }
    }
}

internal sealed class OrderedDemotionParagraph
{
    public OrderedDemotionParagraph(
        SourceParagraph source,
        ParagraphNumberingFeatures numbering,
        ParagraphStyleFeatures style,
        double? bodyFontSizePt,
        bool corrupt,
        bool trustedHeadingStyle,
        int? numberingStyleHeadingLevel,
        ParagraphRole role,
        double score)
    {
        Source = source;
        Numbering = numbering;
        Style = style;
        BodyFontSizePt = bodyFontSizePt;
        Corrupt = corrupt;
        TrustedHeadingStyle = trustedHeadingStyle;
        NumberingStyleHeadingLevel = numberingStyleHeadingLevel;
        Role = role;
        Score = score;
    }

    public SourceParagraph Source { get; }
    public ParagraphNumberingFeatures Numbering { get; }
    public ParagraphStyleFeatures Style { get; }
    public double? BodyFontSizePt { get; }
    public bool Corrupt { get; }
    public bool TrustedHeadingStyle { get; }
    // Derived compatibility input; deliberately distinct from source NumberingLevel.
    public int? NumberingStyleHeadingLevel { get; }
    public ParagraphRole Role { get; set; }
    public double Score { get; set; }
    public bool IsCandidate => Role is ParagraphRole.StyledHeading or ParagraphRole.HeadingCandidate;
    public int Index => Source.SourceOrdinal;
    public string Text => Source.Text;
    public bool InTableOfContents => Source.InTableOfContents;
    public int TableDepth => Source.Layout.TableDepth;
    public int? OutlineLevel => Style.OutlineLevel;
    public string? StyleId => Style.StyleId;
    public bool Bold => Style.Bold;
    public bool Italic => Style.Italic;
    public bool HasNumbering => Numbering.NumberingId is not null ||
        !string.IsNullOrWhiteSpace(Numbering.NumberLabel);
    public bool HasStructuralNumbering => Numbering.NumberingId is not null;
}
