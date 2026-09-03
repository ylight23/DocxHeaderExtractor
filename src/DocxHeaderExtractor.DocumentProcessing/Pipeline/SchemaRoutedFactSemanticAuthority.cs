using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Routes semantic validation by the schema on the proposal context. A missing pack is a hard
/// rejection; there is deliberately no generic/default semantic authority.
/// </summary>
public sealed class SchemaRoutedFactSemanticAuthority : IFactSemanticAuthority
{
    private readonly IFactSchemaPackRegistry _packs;

    public SchemaRoutedFactSemanticAuthority(IFactSchemaPackRegistry packs)
    {
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
    }

    public FactSemanticDecision Validate(FactSemanticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_packs.TryGet(context.Schema.Key, out var pack))
            return new FactSemanticDecision(false, "fact-schema-pack-missing", "fact-schema-pack-missing");

        return pack.SemanticAuthority.Validate(context);
    }
}
