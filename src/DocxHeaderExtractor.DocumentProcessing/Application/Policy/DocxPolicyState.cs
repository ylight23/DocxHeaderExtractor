using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Application.Features;

namespace DocxHeaderExtractor.Core.Application.Policy;

/// <summary>
/// Deterministic policy state derived from source facts. It owns candidate role/score decisions,
/// while SourceDocument remains the only observed-document authority.
/// </summary>
public sealed class DocxPolicyState
{
    public DocxPolicyState(
        SourceDocument source,
        NumberingStyleFeatures structuralFeatures,
        DerivedDocumentFeatures derivedFeatures,
        IReadOnlyList<DocxPolicyParagraph> paragraphs,
        StyleTrust? styleTrust = null,
        DocumentModeReport? mode = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        StructuralFeatures = structuralFeatures ?? throw new ArgumentNullException(nameof(structuralFeatures));
        DerivedFeatures = derivedFeatures ?? throw new ArgumentNullException(nameof(derivedFeatures));
        Paragraphs = paragraphs ?? throw new ArgumentNullException(nameof(paragraphs));
        StyleTrust = styleTrust;
        Mode = mode;
    }

    public SourceDocument Source { get; }
    public NumberingStyleFeatures StructuralFeatures { get; }
    public DerivedDocumentFeatures DerivedFeatures { get; }
    public IReadOnlyList<DocxPolicyParagraph> Paragraphs { get; }
    public StyleTrust? StyleTrust { get; }
    public DocumentModeReport? Mode { get; }
    public IEnumerable<DocxPolicyParagraph> Candidates => Paragraphs.Where(p => p.IsCandidate);
}

/// <summary>Mutable deterministic role/score state paired with one source paragraph.</summary>
public sealed class DocxPolicyParagraph : IPolicyParagraph
{
    public required SourceParagraph Source { get; init; }
    public required ParagraphNumberingFeatures Numbering { get; init; }
    public required ParagraphStyleFeatures Style { get; init; }
    public double? BodyFontSizePt { get; set; }
    public bool Corrupt { get; set; }
    public bool TrustedHeadingStyle { get; set; }
    public int? NumberingStyleHeadingLevel { get; set; }
    public int? NumberingStyleLevel
    {
        get => NumberingStyleHeadingLevel;
        set => NumberingStyleHeadingLevel = value;
    }
    public bool InTableOfContents { get; set; }
    public bool PrecedesTableOfContents { get; set; }
    public bool PrecedesTable { get; set; }
    public TableRole TableRole { get; set; }
    public ParagraphRole Role { get; set; } = ParagraphRole.Normal;
    public int? GuessedLevel { get; set; }
    public double Score { get; set; }

    public int Index => Source.SourceOrdinal;
    public string StableId => Source.SourceId;
    public string Text => Source.Text;
    public bool InContentControl => Source.Layout.InContentControl;
    public bool IsCandidate => Role is ParagraphRole.StyledHeading or ParagraphRole.HeadingCandidate;
    public int TableDepth => Source.Layout.TableDepth;
    public int? OutlineLevel => Style.OutlineLevel;
    public string? StyleId => Style.StyleId;
    public string? StyleName => Style.StyleName;
    public bool Bold => Style.Bold;
    public bool Italic => Style.Italic;
    public bool Underline => Style.Underline;
    public bool AllCaps => Style.AllCaps;
    public double? FontSizePt => Style.FontSizePt;
    public string? Alignment => Style.Alignment;
    public bool KeepNext => Source.Layout.KeepNext;
    public bool PageBreakBefore => Source.Layout.PageBreakBefore;
    public int? NumberingId => Numbering.NumberingId;
    public string? NumberingFormat => Numbering.NumberingFormat;
    public string? NumberLabel => Numbering.NumberLabel;
    public bool HasBuiltInHeadingStyle
    {
        get => TrustedHeadingStyle;
        set => TrustedHeadingStyle = value;
    }
    public bool HasNumbering => Numbering.NumberingId is not null || !string.IsNullOrWhiteSpace(Numbering.NumberLabel);
    public bool HasStructuralNumbering => Numbering.NumberingId is not null;
    public int? NumberingLevel => Numbering.NumberingLevel;
    public int? NumberingDepth => Numbering.NumberingLevel is { } level ? level + 1 : null;
}
