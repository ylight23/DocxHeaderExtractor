using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Evidence layers packed for semantic reasoning; none of these layers grants binding authority.</summary>
public sealed record SemanticContextPacket(
    IReadOnlyList<string> TargetEvidence,
    IReadOnlyList<string> LocalContext,
    IReadOnlyList<string> GlobalContext)
{
    public IReadOnlyList<string> VisibleEvidence => TargetEvidence.Concat(LocalContext).Concat(GlobalContext).ToArray();
}

public static class SemanticContextPacker
{
    public static SemanticContextPacket Pack(
        IEnumerable<string> targetEvidence,
        IEnumerable<string> localContext,
        IEnumerable<string> globalContext)
    {
        ArgumentNullException.ThrowIfNull(targetEvidence);
        ArgumentNullException.ThrowIfNull(localContext);
        ArgumentNullException.ThrowIfNull(globalContext);
        return new(
            targetEvidence.Where(item => item is not null).ToArray(),
            localContext.Where(item => item is not null).ToArray(),
            globalContext.Where(item => item is not null).ToArray());
    }
}

/// <summary>Attention-only candidate metadata. A miss never makes an owned source ineligible.</summary>
public sealed record SemanticCandidateAttentionHint(string SourceAlias, bool HeuristicMatch, string? Reason = null);

public static class SemanticCandidatePolicy
{
    public static bool CanAcceptOwnedOccurrence(string sourceAlias, IReadOnlyCollection<SemanticCandidateAttentionHint> hints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAlias);
        ArgumentNullException.ThrowIfNull(hints);
        // Hints influence routing only. Semantic discovery always retains the owned occurrence.
        return true;
    }
}

public sealed record SemanticVisualEvidence(string SourceAlias, string EvidenceId, string EvidenceKind);

/// <summary>Optional visual evidence route; implementations are explicitly outside the exact binder.</summary>
public interface ISemanticVisualEvidenceProvider
{
    ValueTask<IReadOnlyList<SemanticVisualEvidence>> ResolveAsync(
        IReadOnlyList<SemanticSourceAlias> aliases,
        CancellationToken cancellationToken = default);
}

public static class SemanticVisualEscalation
{
    public static bool IsOptional => true;
}

/// <summary>Stable graph-cache identity. User task wording is intentionally not an input.</summary>
public static class CanonicalSemanticGraphCacheKey
{
    public static string Create(
        string sourceSha256,
        string semanticSchemaVersion,
        string modelVersion,
        string extractorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractorVersion);
        var canonical = string.Join("\n", sourceSha256.Trim().ToLowerInvariant(), semanticSchemaVersion,
            modelVersion, extractorVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record SemanticContractIssue(string Code, string? SourceAlias, string Message);

/// <summary>Validates semantic proposals without using Gold or interpreting model intent.</summary>
public static class CanonicalSemanticContractValidator
{
    private static readonly HashSet<string> NumericCoordinateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "end", "offset", "startOffset", "endOffset", "page", "bbox"
    };

    public static IReadOnlyList<SemanticContractIssue> ValidateJson(JsonElement payload)
    {
        var issues = new List<SemanticContractIssue>();
        Visit(payload, issues, null);
        return issues;
    }

    public static IReadOnlyList<SemanticContractIssue> Validate(
        CanonicalSemanticProposal proposal,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlySet<string>? ownedAliases = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(aliases);
        var issues = new List<SemanticContractIssue>();
        var names = proposal.SourceAliases is { Count: > 0 } ? proposal.SourceAliases : [proposal.SourceAlias];
        foreach (var name in names)
        {
            if (!aliases.ContainsKey(name))
                issues.Add(new("UNKNOWN_ALIAS", name, "The alias is not owned by this source catalog."));
            else if (ownedAliases is not null && !ownedAliases.Contains(name))
                issues.Add(new("OUT_OF_OWNED_SEGMENT", name, "The alias is visible but outside this segment's ownership."));
        }
        if (proposal.IsHeading && string.IsNullOrEmpty(proposal.VerbatimText) && (proposal.VerbatimParts is not { Count: > 0 }))
            issues.Add(new("MISSING_VERBATIM_TEXT", proposal.SourceAlias, "A heading must identify exact source text."));
        return issues;
    }

    private static void Visit(JsonElement value, List<SemanticContractIssue> issues, string? sourceAlias)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var nextAlias = property.NameEquals("sourceAlias") && property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() : sourceAlias;
                if (NumericCoordinateNames.Contains(property.Name))
                    issues.Add(new("NUMERIC_COORDINATE_REJECTED", nextAlias, $"Field '{property.Name}' is not part of the semantic contract."));
                Visit(property.Value, issues, nextAlias);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Visit(item, issues, sourceAlias);
        }
    }
}

public sealed record CanonicalSemanticBindingValidation(
    bool IsValid,
    IReadOnlyList<string> Errors);

/// <summary>Post-binder guard: coordinates are accepted only when they reproduce source bytes.</summary>
public static class CanonicalSemanticHardBindingValidator
{
    public static CanonicalSemanticBindingValidation Validate(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IReadOnlyList<SemanticSourceAlias> aliases,
        string actualSourceSha256,
        string expectedSourceSha256)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualSourceSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceSha256);
        var errors = new List<string>();
        if (!string.Equals(actualSourceSha256, expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("SOURCE_HASH_MISMATCH");
        var byAlias = aliases.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        foreach (var heading in bound)
        {
            var parts = heading.Parts.Count == 0
                ? [new CanonicalSemanticBoundPart(heading.Alias, heading.SourceId, heading.SourceOrdinal, heading.Text, heading.Start, heading.End)]
                : heading.Parts;
            foreach (var part in parts)
            {
                if (!byAlias.TryGetValue(part.Alias, out var alias))
                {
                    errors.Add("UNKNOWN_ALIAS");
                    continue;
                }
                var localStart = part.Start - alias.SourceSpan.Start;
                var localEnd = part.End - alias.SourceSpan.Start;
                if (!alias.Contains(new StructuralSpan(localStart, localEnd)) ||
                    !string.Equals(alias.Text[localStart..localEnd], part.Text, StringComparison.Ordinal))
                    errors.Add("NON_VERBATIM_BINDING");
            }
        }
        return new(errors.Count == 0, errors);
    }
}

public sealed record SemanticIntent(string ProjectionId, bool CollapseRepeatedNodes);

/// <summary>Task intent and projection are downstream of the canonical graph.</summary>
public static class CanonicalSemanticProjection
{
    public static SemanticIntent NormalizeIntent(string? userRequest) =>
        new(string.IsNullOrWhiteSpace(userRequest) ? "main-document-outline" : "main-document-outline", true);

    public static IReadOnlyList<CanonicalSemanticGraphOccurrence> Project(
        CanonicalSemanticGraph graph,
        SemanticIntent intent)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(intent);
        if (!intent.CollapseRepeatedNodes) return graph.Occurrences.ToArray();
        return graph.Occurrences
            .GroupBy(item => item.SemanticNodeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start)
            .ToArray();
    }
}

public sealed record SemanticTransitionLedgerEntry(
    string Stage,
    string Status,
    int InputCount,
    int OutputCount,
    string? FirstLossCode = null);

public static class SemanticTransitionLedger
{
    public static SemanticTransitionLedgerEntry FirstLoss(
        string stage, int inputCount, int outputCount, string? code) =>
        new(stage, outputCount < inputCount ? "LOSS_OBSERVED" : "PRESERVED", inputCount, outputCount, code);
}
