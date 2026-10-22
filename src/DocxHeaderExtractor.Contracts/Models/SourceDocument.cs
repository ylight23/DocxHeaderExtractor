using System.Collections.ObjectModel;
using MessagePack;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>
/// Immutable source-only view of a document. It deliberately contains no routing policy,
/// proposal, or validated hierarchy state.
/// </summary>
[MessagePackObject]
public sealed record SourceDocument
{
    [Key(0)] public required string DocumentId { get; init; }
    [Key(1)] public required string FileName { get; init; }
    [Key(2)] public required string SourcePath { get; init; }
    [Key(3)] public required string SourceKind { get; init; }
    [Key(4)] public required IReadOnlyList<SourceParagraph> Paragraphs { get; init; }
}

/// <summary>One source paragraph and its source-backed normalized representation.</summary>
[MessagePackObject]
public sealed record SourceParagraph
{
    [Key(0)] public required string SourceId { get; init; }
    [Key(1)] public required int SourceOrdinal { get; init; }
    [Key(2)] public required string Text { get; init; }
    [Key(3)] public IReadOnlyList<SourceTextRunSpan> TextSpans { get; init; } = new ReadOnlyCollection<SourceTextRunSpan>([]);
    [Key(4)] public IReadOnlyList<int> LineBreakOffsets { get; init; } = new ReadOnlyCollection<int>([]);
    [Key(5)] public IReadOnlyList<SourceSegment> SourceSegments { get; init; } = new ReadOnlyCollection<SourceSegment>([]);
    [Key(6)] public required SourceStyleFacts Style { get; init; }
    [Key(7)] public required SourceNumberingFacts Numbering { get; init; }

    /// <summary>
    /// The bookmark name of every internal hyperlink in the paragraph, as written in the OOXML.
    /// </summary>
    [Key(8)] public IReadOnlyList<string> HyperlinkAnchors { get; init; } = [];
}

/// <summary>Formatting span over normalized source text, retaining run-level provenance.</summary>
[MessagePackObject]
public sealed record SourceTextRunSpan(
    [property: Key(0)] int Start,
    [property: Key(1)] int End,
    [property: Key(2)] bool Bold,
    [property: Key(3)] bool Italic,
    [property: Key(4)] bool Underline,
    [property: Key(5)] double? FontSizePt);

/// <summary>Maps a normalized source range back to a raw OOXML run and offset.</summary>
[MessagePackObject]
public sealed record SourceSegment(
    [property: Key(0)] int Start,
    [property: Key(1)] int End,
    [property: Key(2)] int RunIndex,
    [property: Key(3)] int RawStart);

/// <summary>Source style and direct formatting facts. No trust or selection decision is included.</summary>
[MessagePackObject]
public sealed record SourceStyleFacts
{
    [Key(0)] public string? StyleId { get; init; }
    [Key(1)] public string? StyleName { get; init; }
    [Key(2)] public int? OutlineLevel { get; init; }
    [Key(3)] public bool Bold { get; init; }
    [Key(4)] public bool Italic { get; init; }
    [Key(5)] public bool Underline { get; init; }
    [Key(6)] public bool AllCaps { get; init; }
    [Key(7)] public double? FontSizePt { get; init; }
    [Key(8)] public string? Alignment { get; init; }
}

/// <summary>Source numbering facts and normalized rendered label.</summary>
[MessagePackObject]
public sealed record SourceNumberingFacts
{
    [Key(0)] public int? NumberingId { get; init; }
    [Key(1)] public int? NumberingLevel { get; init; }
    [Key(2)] public string? NumberLabel { get; init; }
    [Key(3)] public string? NumberingFormat { get; init; }
}
