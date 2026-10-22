using MessagePack;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>Exact source-text coordinates. End is exclusive, matching <see cref="TextOffsetSpan"/>.</summary>
[MessagePackObject]
public sealed record StructuralSpan(
    [property: JsonPropertyName("start"), Key(0)] int Start,
    [property: JsonPropertyName("end"), Key(1)] int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End > Start && End <= text.Length;
}
