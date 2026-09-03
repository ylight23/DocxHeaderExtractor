namespace DocxHeaderExtractor.Application.Semantics;

public enum SemanticDefinitionKind
{
    Concept,
    Schema,
}

public enum SemanticDefinitionLifecycle
{
    Draft,
    Active,
    Deprecated,
    Retired,
}

/// <summary>
/// Runtime semantic metadata. The registry stores definitions supplied by configuration or a
/// trusted catalog; it never infers or auto-promotes a definition from model output.
/// </summary>
public sealed record SemanticDefinition(
    string Key,
    int Version,
    SemanticDefinitionKind Kind,
    SemanticDefinitionLifecycle Lifecycle,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record SemanticResolution(
    SemanticDefinition? Definition,
    string? FailureReason)
{
    public bool IsResolved => Definition is not null && FailureReason is null;

    public static SemanticResolution Resolved(SemanticDefinition definition) => new(definition, null);

    public static SemanticResolution Failed(string reason) => new(null, reason);
}

/// <summary>
/// Trusted baseline definitions for the generic document task surface. Hosts may register additional
/// definitions from trusted configuration; model output is never a registration source.
/// </summary>
public static class SemanticRegistryDefaults
{
    public static SemanticRegistry Create()
    {
        var registry = new SemanticRegistry();
        registry.Register(new SemanticDefinition(
            "document.structure", 1, SemanticDefinitionKind.Concept,
            SemanticDefinitionLifecycle.Active, ["document-structure"]));
        registry.Register(new SemanticDefinition(
            "document.outline", 1, SemanticDefinitionKind.Schema,
            SemanticDefinitionLifecycle.Active, ["outline", "document-outline"]));
        return registry;
    }
}

/// <summary>
/// Exact, version-aware catalog shared by concepts and schemas. Ambiguous or retired entries never
/// resolve silently.
/// </summary>
public sealed class SemanticRegistry
{
    private readonly Dictionary<(string Key, int Version), SemanticDefinition> _definitions = [];

    public IReadOnlyCollection<SemanticDefinition> Definitions => _definitions.Values;

    public void Register(SemanticDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Key))
            throw new ArgumentException("Semantic key không được rỗng.", nameof(definition));
        if (definition.Version < 1)
            throw new ArgumentOutOfRangeException(nameof(definition), "Semantic version phải >= 1.");

        var key = (definition.Key, definition.Version);
        if (_definitions.ContainsKey(key))
            throw new InvalidOperationException($"Semantic definition đã tồn tại: {definition.Key}@{definition.Version}.");

        var identifiers = new[] { definition.Key }.Concat(definition.Aliases ?? []).ToArray();
        if (identifiers.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Semantic alias không được rỗng.", nameof(definition));
        var collision = _definitions.Values
            .Where(existing => existing.Lifecycle != SemanticDefinitionLifecycle.Retired)
            .SelectMany(existing => new[] { existing.Key }.Concat(existing.Aliases ?? []),
                (existing, identifier) => (existing, identifier))
            .FirstOrDefault(item => identifiers.Contains(item.identifier, StringComparer.Ordinal));
        if (collision.existing is not null)
            throw new InvalidOperationException(
                $"Semantic identifier đã được đăng ký: {collision.identifier}.");

        _definitions.Add(key, definition with { Aliases = definition.Aliases ?? [] });
    }

    public SemanticResolution Resolve(
        string identifier,
        SemanticDefinitionKind? expectedKind = null,
        int? version = null)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return SemanticResolution.Failed("semantic-identifier-missing");

        var matches = _definitions.Values
            .Where(definition => definition.Lifecycle is not SemanticDefinitionLifecycle.Draft
                and not SemanticDefinitionLifecycle.Retired)
            .Where(definition => expectedKind is null || definition.Kind == expectedKind)
            .Where(definition => version is null || definition.Version == version)
            .Where(definition => string.Equals(definition.Key, identifier, StringComparison.Ordinal) ||
                definition.Aliases.Contains(identifier, StringComparer.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => SemanticResolution.Resolved(matches[0]),
            0 => SemanticResolution.Failed("semantic-not-found-or-inactive"),
            _ => SemanticResolution.Failed("semantic-ambiguous-version"),
        };
    }
}
