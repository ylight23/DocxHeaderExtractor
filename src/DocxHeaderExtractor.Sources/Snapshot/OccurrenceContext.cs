using MessagePack;

namespace DocxHeaderExtractor.Sources.Snapshot;

/// <summary>Format-neutral source context used to validate a heading against neighboring text.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record OccurrenceContext(
    [property: Key(0)] string SourceId,
    [property: Key(1)] string RawText,
    [property: Key(2)] string StructuralScope,
    [property: Key(3)] IReadOnlyList<string> EvidenceOrigins,
    [property: Key(4)] IReadOnlyList<string> PreviousOccurrences,
    [property: Key(5)] IReadOnlyList<string> NextOccurrences);
