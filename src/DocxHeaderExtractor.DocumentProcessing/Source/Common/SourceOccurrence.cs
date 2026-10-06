namespace DocxHeaderExtractor.DocumentProcessing.Source.Common;

/// <summary>Format-neutral source occurrence identity with no parser-specific geometry.</summary>
internal sealed record SourceOccurrence(
    string Id,
    string Alias,
    int Ordinal,
    string Text,
    string SourceKind,
    string? StyleId = null);
