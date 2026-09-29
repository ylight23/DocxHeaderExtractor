using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Canonical PDF block-role vocabulary shared by source validation and route audit.</summary>
internal enum PdfBlockRole
{
    DocumentTitle,
    HeadingTopic,
    ListItem,
    BodySentence,
    TableOrChartLabel,
    DecorativeNoise,
    Uncertain,
}

/// <summary>Closed semantic role vocabulary carried through the canonical PDF decision contract.</summary>
internal enum PdfSemanticRole
{
    DocumentTitle, SectionHeading, TopicHeading, LocalSubheading,
    LegalChapter, LegalSection, LegalArticle, LegalClause, LegalPoint, AppendixHeading,
    MeetingSection, AgendaItem, NoteHeading,
    TableTitle, TableHeader, FigureTitle, FigureCaption, ListItemTopic, RunningHeader, RunningFooter, FormLabel,
    SignatureLabel, TranslationNotice, BodyText, Unknown,
}

internal sealed record PdfBlockDecision(
    string Id,
    PdfBlockRole Role,
    double Confidence,
    string Reason,
    TextOffsetSpan? HeadingSpan = null,
    string? ProposedParentId = null,
    PdfSemanticRole SemanticRole = PdfSemanticRole.Unknown,
    TextOffsetSpan? ProposedSourceSpan = null,
    // The bound claim's ordered parts, for a coordinate system whose claims can span several
    // source occurrences. Null everywhere else, which is every lane that had one span per claim.
    IReadOnlyList<CanonicalSemanticBoundPart>? Parts = null);

/// <summary>
/// Runaway guard for one document's semantic lane. It is not a work budget: every provider call is
/// already bounded by its own transport deadline and bounded retries. The former 5-minute default
/// cut real documents short - SRC-095's 24 leaves took about 327 s of provider time in the production
/// re-baseline, so the default lane would have thrown before finishing - and the per-request/batch
/// timeouts that sat beside it were never read.
/// </summary>
public sealed record SemanticLaneOptions(TimeSpan LaneDeadline)
{
    public static readonly SemanticLaneOptions Default = new(TimeSpan.FromMinutes(60));
}
