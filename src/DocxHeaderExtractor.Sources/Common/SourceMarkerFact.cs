using MessagePack;
using System.Collections.Immutable;

namespace DocxHeaderExtractor.Sources.Common;

/// <summary>
/// Shared parser-side marker shape for DOCX and PDF source context and audit.
/// A marker is source evidence, never a heading decision; sequence auditing remains independent.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
internal readonly record struct SourceMarkerFact([property: Key(0)] string Signature, [property: Key(1)] int Depth, [property: Key(2)] string Family, [property: Key(3)] bool IsPath)
{
    /// <summary>
    /// M8.1d-2 representation only. The parser already knows every component of a numeric path;
    /// previously it kept only the count, which forced downstream code to re-derive components with
    /// a stricter grammar that cannot read a dot-stripped source. Carrying them here removes that
    /// second parse as a source of truth. It grants no hierarchy authority on its own.
    /// </summary>
    [Key(4)]
    public ImmutableArray<int> Components { get; init; } = ImmutableArray<int>.Empty;
}
