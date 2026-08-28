using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Immutable source-only view of a document. It deliberately contains no candidate, policy,
/// proposal, or validated hierarchy state.
/// </summary>
public sealed record SourceDocument
{
    public required string DocumentId { get; init; }
    public required string FileName { get; init; }
    public required string SourcePath { get; init; }
    public required string SourceKind { get; init; }
    public required IReadOnlyList<SourceParagraph> Paragraphs { get; init; }
    public IReadOnlyList<string> PageHeaders { get; init; } = new ReadOnlyCollection<string>([]);
    public IReadOnlyList<string> PageFooters { get; init; } = new ReadOnlyCollection<string>([]);
}

/// <summary>One source paragraph and its source-backed normalized representation.</summary>
public sealed record SourceParagraph
{
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<SourceTextRunSpan> TextSpans { get; init; } = new ReadOnlyCollection<SourceTextRunSpan>([]);
    public IReadOnlyList<int> LineBreakOffsets { get; init; } = new ReadOnlyCollection<int>([]);
    public IReadOnlyList<SourceSegment> SourceSegments { get; init; } = new ReadOnlyCollection<SourceSegment>([]);
    public required SourceStyleFacts Style { get; init; }
    public required SourceNumberingFacts Numbering { get; init; }
    public required SourceLayoutFacts Layout { get; init; }
}

/// <summary>Formatting span over normalized source text, retaining run-level provenance.</summary>
public sealed record SourceTextRunSpan(
    int Start,
    int End,
    bool Bold,
    bool Italic,
    bool Underline,
    double? FontSizePt);

/// <summary>Maps a normalized source range back to a raw OOXML run and offset.</summary>
public sealed record SourceSegment(int Start, int End, int RunIndex, int RawStart);

/// <summary>Source style and direct formatting facts. No trust or selection decision is included.</summary>
public sealed record SourceStyleFacts
{
    public string? StyleId { get; init; }
    public string? StyleName { get; init; }
    /// <summary>Built-in Word heading level derived only from resolved style identity; not a heading decision.</summary>
    public int? BuiltInHeadingStyleLevel { get; init; }
    public int? OutlineLevel { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool AllCaps { get; init; }
    public double? FontSizePt { get; init; }
    public string? Alignment { get; init; }
}

/// <summary>Source numbering facts and normalized rendered label.</summary>
public sealed record SourceNumberingFacts
{
    public int? NumberingId { get; init; }
    public int? NumberingLevel { get; init; }
    public string? NumberLabel { get; init; }
    public string? NumberingFormat { get; init; }
}

/// <summary>Source layout/containment facts. It contains no table or heading policy result.</summary>
public sealed record SourceLayoutFacts
{
    public bool InContentControl { get; init; }
    public bool KeepNext { get; init; }
    public bool PageBreakBefore { get; init; }
    public int TableDepth { get; init; }
    public int SectionIndex { get; init; }
}
