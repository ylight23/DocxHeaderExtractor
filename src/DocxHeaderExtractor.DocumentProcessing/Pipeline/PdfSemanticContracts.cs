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
    TextOffsetSpan? ProposedSourceSpan = null);

/// <summary>Independent execution budget for the semantic lane.</summary>
public sealed record SemanticLaneOptions(
    TimeSpan RequestTimeout,
    TimeSpan BatchTimeout,
    TimeSpan LaneDeadline,
    int MaxConcurrency = 1,
    DateTimeOffset? DeadlineUtc = null,
    int MaxBatchSize = 0)
{
    public static readonly SemanticLaneOptions Default = new(
        TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(5));

    public TimeSpan RemainingOr(TimeSpan requested)
    {
        if (DeadlineUtc is not { } deadline) return requested;
        return TimeSpan.FromTicks(Math.Max(0, Math.Min(requested.Ticks, (deadline - DateTimeOffset.UtcNow).Ticks)));
    }
}
