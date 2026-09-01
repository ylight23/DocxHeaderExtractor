using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Model-safe description of a registered schema pack; authorities never cross this boundary.</summary>
public sealed record RegisteredSchemaDescriptor(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("fields")] IReadOnlyList<string> Fields);

/// <summary>Bounded, provider-neutral input for schema selection.</summary>
public sealed record SchemaDiscoveryContext(
    [property: JsonPropertyName("documentIdentity")] DocumentIdentity DocumentIdentity,
    [property: JsonPropertyName("structuralTypes")] IReadOnlyList<string> StructuralTypes,
    [property: JsonPropertyName("sectionCount")] int SectionCount,
    [property: JsonPropertyName("boundedExcerpts")] IReadOnlyList<string> BoundedExcerpts,
    [property: JsonPropertyName("registeredSchemas")] IReadOnlyList<RegisteredSchemaDescriptor> RegisteredSchemas);

/// <summary>Untrusted schema selection proposal. It cannot register schemas or carry authorities.</summary>
public sealed record SchemaSelectionProposal(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("schemaKeys")] IReadOnlyList<string> SchemaKeys,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("confidence")] double? Confidence = null);

public interface ISchemaProposalProducer
{
    Task<SchemaSelectionProposal> ProposeAsync(
        SchemaDiscoveryContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional registry capability used only to expose model-safe schema descriptors.</summary>
public interface IRegisteredSchemaPackCatalog
{
    IReadOnlyList<IFactSchemaPack> Packs { get; }
}

/// <summary>Explicit no-match producer for applications without an auto-discovery model.</summary>
public sealed class NoSchemaMatchProposalProducer : ISchemaProposalProducer
{
    public Task<SchemaSelectionProposal> ProposeAsync(
        SchemaDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SchemaSelectionProposal(
            "no-schema-match",
            [],
            ["no-schema-proposal-producer"],
            null));
    }
}

/// <summary>Deterministic application-owned proposal producer for rules or replay fixtures.</summary>
public sealed class RuleSchemaProposalProducer : ISchemaProposalProducer
{
    private readonly Func<SchemaDiscoveryContext, SchemaSelectionProposal> _rule;

    public RuleSchemaProposalProducer(Func<SchemaDiscoveryContext, SchemaSelectionProposal> rule)
    {
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
    }

    public Task<SchemaSelectionProposal> ProposeAsync(
        SchemaDiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_rule(context) ?? throw new InvalidOperationException("schema-proposal-null"));
    }
}

public sealed record ValidatedSchemaSelection(
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("schemaKeys")] IReadOnlyList<string> SchemaKeys,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason)
{
    [JsonIgnore]
    public bool HasMatch => Status == "matched" && SchemaKeys.Count > 0;
}

public sealed record SchemaSelectionValidationResult(
    [property: JsonPropertyName("selection")] ValidatedSchemaSelection Selection,
    [property: JsonPropertyName("unknownKeys")] IReadOnlyList<string> UnknownKeys)
{
    [JsonIgnore]
    public bool Accepted => Selection.HasMatch;
}
