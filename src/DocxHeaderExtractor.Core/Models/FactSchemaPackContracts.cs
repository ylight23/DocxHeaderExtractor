using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Schema plus the semantic policy that is allowed to validate that schema.</summary>
public interface IFactSchemaPack
{
    string Key { get; }

    string Version { get; }

    FactSchemaDefinition Schema { get; }

    IFactSemanticAuthority SemanticAuthority { get; }
}

/// <summary>Application registry for schema packs. Missing packs are never defaulted.</summary>
public interface IFactSchemaPackRegistry
{
    bool TryGet(string key, out IFactSchemaPack pack);
}

/// <summary>Small in-memory pack implementation for application composition and tests.</summary>
public sealed class FactSchemaPack : IFactSchemaPack
{
    public FactSchemaPack(
        string key,
        string version,
        FactSchemaDefinition schema,
        IFactSemanticAuthority semanticAuthority)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Schema pack key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Schema pack version is required.", nameof(version));
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(semanticAuthority);
        if (!string.Equals(key, schema.Key, StringComparison.Ordinal))
            throw new InvalidOperationException("fact-schema-pack-key-mismatch");

        Key = key;
        Version = version;
        Schema = schema;
        SemanticAuthority = semanticAuthority;
    }

    public string Key { get; }

    public string Version { get; }

    public FactSchemaDefinition Schema { get; }

    [JsonIgnore]
    public IFactSemanticAuthority SemanticAuthority { get; }
}

public sealed class InMemoryFactSchemaPackRegistry : IFactSchemaPackRegistry
{
    private readonly IReadOnlyDictionary<string, IFactSchemaPack> _packs;

    public InMemoryFactSchemaPackRegistry(IEnumerable<IFactSchemaPack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        var materialized = packs.ToArray();
        if (materialized.Any(pack => string.IsNullOrWhiteSpace(pack.Key)))
            throw new InvalidOperationException("empty-fact-schema-pack-key");
        if (materialized.Select(pack => pack.Key).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidOperationException("duplicate-fact-schema-pack-key");
        _packs = materialized.ToDictionary(pack => pack.Key, StringComparer.Ordinal);
    }

    public bool TryGet(string key, out IFactSchemaPack pack) =>
        _packs.TryGetValue(key, out pack!);
}
