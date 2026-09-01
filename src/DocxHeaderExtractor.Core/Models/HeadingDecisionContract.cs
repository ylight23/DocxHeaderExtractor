using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Immutable parser/render facts for one candidate. Model calls may read these facts but must not
/// create or alter them; downstream output is built only after a separate validation pass.
/// </summary>
public sealed record SourceFacts
{
    public required string SourceId { get; init; }
    public required string RawText { get; init; }
    public required SourceAnchor Source { get; init; }
    public required SourceTextSpan RawSpan { get; init; }
    public MarkerFacts? Marker { get; init; }
    public IReadOnlyList<ObservedEvidence> ObservedEvidence { get; init; } = [];
}

public sealed record SourceTextSpan(int Start, int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End > Start && End <= text.Length;
}

/// <summary>Stable source/writeback coordinates. PDF render coordinates are optional for DOCX-only input.</summary>
public sealed record SourceAnchor
{
    public required string SourceType { get; init; }
    public string? ParagraphId { get; init; }
    public int? ParagraphIndex { get; init; }
    public IReadOnlyList<SourceSegment> SourceSegments { get; init; } = [];
    public int? Page { get; init; }
    public string? RenderBlockId { get; init; }
    public IReadOnlyList<string> RenderLineIds { get; init; } = [];
    public PdfBoundingBox? BoundingBox { get; init; }
}

public sealed record PdfBoundingBox(double Left, double Bottom, double Right, double Top);

public enum MarkerKind
{
    Decimal,
    DecimalDotted,
    RomanUpper,
    RomanLower,
    AlphaUpper,
    AlphaLower,
    DocxNumbering,
}

public sealed record MarkerFacts
{
    public required MarkerKind Kind { get; init; }
    public required string Raw { get; init; }
    public string? Normalized { get; init; }
    public int? Depth { get; init; }
    public IReadOnlyList<int> Components { get; init; } = [];
    public int? NumId { get; init; }
    public int? Ilvl { get; init; }
}

public enum ObservedEvidenceKind
{
    NumberingMarker,
    DocxNumbering,
    BuiltInHeadingStyle,
    OutlineLevel,
    FontWeight,
    FontSize,
    Alignment,
    TableMembership,
    ContentControl,
    LineBreak,
    PageBreakBefore,
    KeepNext,
}

public enum EvidenceOrigin { DocxParser, PdfParser, MarkerParser, LayoutEngine, Renderer }

public sealed record ObservedEvidence(
    ObservedEvidenceKind Kind,
    string Value,
    EvidenceOrigin Origin);

public enum ProposedRole
{
    HeadingTopic,
    LocalSubheading,
    ListItemTopic,
    DocumentTitle,
    CoverTitle,
    TableHeader,
    SignatureLabel,
    RunningHeader,
    RunningFooter,
    Caption,
    FigureTitle,
    StructuralContainer,
    BodyText,
    Metadata,
    Unknown,
}

public enum SemanticEvidenceTag { OpensContent, TopicPhrase, SiblingSymmetry, SignatureBlock, ListContinuation }

public enum VisualEvidenceTag
{
    StandaloneLine,
    CenterAligned,
    LeftIndentLevel1,
    LeftIndentLevel2,
    FontLargerThanBody,
    BoldDominant,
    WhitespaceBefore,
    WhitespaceAfter,
    TableGridContext,
    SignatureRegion,
    RepeatedRunningHeader,
}

/// <summary>Untrusted model output. It contains no raw text, anchors, marker facts, or final tree authority.</summary>
public sealed record ModelProposal
{
    public required string SourceId { get; init; }
    public required ProposedRole Role { get; init; }
    public SourceTextSpan? HeadingSpan { get; init; }
    public IReadOnlyList<SemanticEvidenceTag> SemanticEvidence { get; init; } = [];
    public IReadOnlyList<VisualEvidenceTag> VisualEvidence { get; init; } = [];
    public int? ProposedLevel { get; init; }
    public string? ProposedParentId { get; init; }
    [JsonIgnore] public double? ModelScore { get; init; }
}

public sealed record HeadingValidation(
    bool SourceGrounded,
    bool SpanValid,
    bool EvidenceValid,
    bool MarkerValid,
    bool MarkerSequenceValid,
    bool HierarchyValid,
    bool ParentValid,
    string? ParentResolution = null);

/// <summary>The only heading contract that downstream writeback/output may consume.</summary>
public sealed record ValidatedHeading
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required ProposedRole Role { get; init; }
    public required SourceTextSpan HeadingSpan { get; init; }
    public required int Level { get; init; }
    public string? ParentId { get; init; }
    public required HeadingValidation Validation { get; init; }
}

public sealed record HeadingPolicy(
    bool IncludeDocumentTitle = true,
    bool IncludeLocalSubheading = false,
    bool IncludeListItemTopic = false)
{
    public bool Includes(ProposedRole role) => role switch
    {
        ProposedRole.HeadingTopic => true,
        ProposedRole.DocumentTitle => IncludeDocumentTitle,
        ProposedRole.LocalSubheading => IncludeLocalSubheading,
        ProposedRole.ListItemTopic => IncludeListItemTopic,
        _ => false,
    };
}
