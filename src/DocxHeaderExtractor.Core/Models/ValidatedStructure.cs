using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The heading authority graph. It retains exact source selections and validated parent relations.
/// </summary>
public sealed class ValidatedStructure
{
    internal ValidatedStructure(
        IReadOnlyList<ValidatedStructuralElement> elements,
        IReadOnlyList<StructuralRelation> relations)
    {
        Elements = elements;
        Relations = relations;
    }

    [JsonPropertyName("elements")]
    public IReadOnlyList<ValidatedStructuralElement> Elements { get; }

    [JsonPropertyName("relations")]
    public IReadOnlyList<StructuralRelation> Relations { get; }

    /// <summary>Compatibility view: every admitted element is a heading. Preserves the wire property.</summary>
    public IReadOnlyList<ValidatedStructuralElement> OutlineElements => Elements;

}
