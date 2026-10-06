namespace DocxHeaderExtractor.DocumentProcessing.Source.Common;

/// <summary>Format-neutral source context used to validate a heading against neighboring text.</summary>
internal sealed record HeadingSourceContext(
    string SourceId,
    string RawText,
    string StructuralScope,
    IReadOnlyList<string> EvidenceOrigins,
    IReadOnlyList<string> PreviousOccurrences,
    IReadOnlyList<string> NextOccurrences);
