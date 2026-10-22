using MessagePack;
namespace DocxHeaderExtractor.Sources.Common;

/// <summary>Format-neutral source occurrence identity with no parser-specific geometry.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record DocumentOccurrence(
    [property: Key(0)] string Id,
    [property: Key(1)] string Alias,
    [property: Key(2)] int Ordinal,
    [property: Key(3)] string Text,
    [property: Key(4)] string SourceKind,
    [property: Key(5)] string? StyleId = null);
