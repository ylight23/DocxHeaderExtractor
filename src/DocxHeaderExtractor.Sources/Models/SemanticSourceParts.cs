using MessagePack;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// One coordinate atom: a piece of source text the harness owns and the model may name.
/// <para>
/// Deliberately not a PDF type. The layout coordinates it carries - page, row, position within the
/// row - are what a locality rule needs and are as true of a two-column page as of a wrapped
/// paragraph, so the binding contract does not have to know how the atoms were reconstructed.
/// </para>
/// </summary>
[MessagePackObject]
public sealed record SemanticSourceAtom(
    [property: JsonPropertyName("alias"), Key(0)] string Alias,
    [property: JsonPropertyName("sourceId"), Key(1)] string SourceId,
    [property: JsonPropertyName("ordinal"), Key(2)] int Ordinal,
    [property: JsonPropertyName("page"), Key(3)] int Page,
    [property: JsonPropertyName("row"), Key(4)] int Row,
    [property: JsonPropertyName("segment"), Key(5)] int Segment,
    [property: JsonPropertyName("text"), Key(6)] string Text);

