namespace DocxHeaderExtractor.Contracts.Models;

public sealed record CanonicalSemanticBoundPart(
    string Alias,
    string SourceId,
    int SourceOrdinal,
    string Text,
    int Start,
    int End);
