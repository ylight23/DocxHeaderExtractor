using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Runs schema discovery over bounded context and fails closed on no match.</summary>
public sealed class SchemaDiscoveryRuntime
{
    private readonly IFactSchemaPackRegistry _registry;
    private readonly ISchemaProposalProducer _producer;

    public SchemaDiscoveryRuntime(
        IFactSchemaPackRegistry registry,
        ISchemaProposalProducer? producer = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _producer = producer ?? new NoSchemaMatchProposalProducer();
    }

    public async Task<SchemaSelectionValidationResult> DiscoverAsync(
        DocumentExtractionResult extraction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        var context = BuildContext(extraction, _registry);
        var proposal = await _producer.ProposeAsync(context, cancellationToken).ConfigureAwait(false);
        return SchemaSelectionValidator.Validate(proposal, _registry);
    }

    public static SchemaDiscoveryContext BuildContext(
        DocumentExtractionResult extraction,
        IFactSchemaPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(registry);

        var structuralTypes = extraction.Structure.Elements
            .Select(element => element.Type.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();
        var excerpts = extraction.SourceCatalog.Units
            .OrderBy(unit => unit.SourceOrdinal)
            .Take(24)
            .Select(unit => unit.Text.Length <= 240 ? unit.Text : unit.Text[..240])
            .ToArray();
        var descriptors = extractionSchemaDescriptors(registry);
        return new SchemaDiscoveryContext(
            extraction.DocumentIdentity,
            structuralTypes,
            extraction.Sections.Count,
            excerpts,
            descriptors);

        static IReadOnlyList<RegisteredSchemaDescriptor> extractionSchemaDescriptors(
            IFactSchemaPackRegistry packs)
        {
            // The registry interface intentionally exposes no enumeration. Discovery therefore
            // receives descriptors through a registry capability when one is available.
            if (packs is not IRegisteredSchemaPackCatalog catalog)
                return [];
            return catalog.Packs
                .OrderBy(pack => pack.Key, StringComparer.Ordinal)
                .Select(pack => new RegisteredSchemaDescriptor(
                    pack.Key,
                    pack.Version,
                    $"Registered schema pack {pack.Key}",
                    pack.Schema.Fields.Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray()))
                .ToArray();
        }
    }
}
