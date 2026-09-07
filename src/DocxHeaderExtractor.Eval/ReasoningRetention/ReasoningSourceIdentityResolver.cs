namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public enum ReasoningSourceIdentityMatch
{
    CanonicalSourceId,
    SourceOccurrenceId,
    ProviderSourceAlias,
    Unknown,
}

public sealed record ReasoningSourceIdentityResolution(
    string ReturnedId,
    string? CanonicalSourceId,
    ReasoningSourceIdentityMatch Match,
    bool IsOwned)
{
    public bool IsVisible => Match is not ReasoningSourceIdentityMatch.Unknown;
    public bool IsOutsideOwnedRange => IsVisible && !IsOwned;
}

/// <summary>
/// Resolves only identities explicitly present in the current context. No text, ordinal,
/// fuzzy, nearest, or candidate lookup is performed here.
/// </summary>
public static class ReasoningSourceIdentityResolver
{
    public static ReasoningSourceIdentityResolution Resolve(
        string returnedId,
        IReadOnlyList<ReasoningSourceOccurrence> visible,
        IReadOnlySet<string> ownedSourceIds,
        IReadOnlyList<ReasoningSourceIdentity>? identityMap = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnedId);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(ownedSourceIds);

        var canonical = visible.FirstOrDefault(item => item.SourceId == returnedId);
        if (canonical is not null)
            return new(returnedId, canonical.SourceId, ReasoningSourceIdentityMatch.CanonicalSourceId,
                ownedSourceIds.Contains(canonical.SourceId));

        var occurrence = visible.FirstOrDefault(item => item.SourceOccurrenceId == returnedId);
        if (occurrence is not null)
            return new(returnedId, occurrence.SourceId, ReasoningSourceIdentityMatch.SourceOccurrenceId,
                ownedSourceIds.Contains(occurrence.SourceId));

        var alias = identityMap?.FirstOrDefault(item => item.ProviderSourceAlias == returnedId);
        if (alias is not null && visible.Any(item => item.SourceId == alias.CanonicalSourceId))
            return new(returnedId, alias.CanonicalSourceId, ReasoningSourceIdentityMatch.ProviderSourceAlias,
                ownedSourceIds.Contains(alias.CanonicalSourceId));

        return new(returnedId, null, ReasoningSourceIdentityMatch.Unknown, false);
    }
}
