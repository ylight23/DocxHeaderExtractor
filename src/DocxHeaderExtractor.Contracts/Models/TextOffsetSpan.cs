using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Contracts.Models;

/// <summary>A UTF-16 span inside one source unit: a leaf value shared by the source readers and the authority pipeline.</summary>
public sealed record TextOffsetSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End);
