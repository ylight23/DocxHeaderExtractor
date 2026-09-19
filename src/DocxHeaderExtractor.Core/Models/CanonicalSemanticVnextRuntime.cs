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

    public static bool CanAcceptVisualOccurrence(string visualAlias, IReadOnlyCollection<SemanticCandidateAttentionHint> hints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visualAlias);
        ArgumentNullException.ThrowIfNull(hints);
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

public sealed record SemanticProposalValidationSummary(
    IReadOnlyList<CanonicalSemanticProposal> ValidProposals,
    IReadOnlyList<SemanticContractIssue> Issues);

/// <summary>
/// Validates the model/harness semantic boundary without using Gold or deciding semantic truth.
/// This stage may reject source-invalid output, but it must never turn formatting or style
/// evidence into a heading decision.
/// </summary>
public static class CanonicalSemanticContractValidator
{
    private static readonly HashSet<string> NumericCoordinateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "end", "offset", "startOffset", "endOffset", "page", "pageNumber", "bbox",
        "boundingBox", "imageSha256", "regionSha256", "transcriptHash"
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
        // Ordered on purpose, and the order is part of the contract.
        //
        // Source address validity and contract shape are checked for every proposal, and only then
        // does isHeading short-circuit. A proposal saying isHeading=false must still not be allowed
        // to name an alias that does not exist, claim one this segment does not own, or carry an
        // invalid occurrence: returning early on !IsHeading would let a "no" carry a fabricated
        // source address through as contract-valid.

        // 1. SOURCE ADDRESS VALIDITY
        var names = proposal.SourceAliases is { Count: > 0 }
            ? proposal.SourceAliases
            : [proposal.SourceAlias];

        if (string.IsNullOrWhiteSpace(proposal.SourceAlias))
            issues.Add(new("MISSING_SOURCE_ALIAS", null, "A proposal must identify a source alias."));

        foreach (var name in names)
        {
            if (!aliases.ContainsKey(name))
                issues.Add(new("UNKNOWN_ALIAS", name, "The alias does not exist in this source catalog."));
            else if (ownedAliases is not null && !ownedAliases.Contains(name))
                issues.Add(new("OUT_OF_OWNED_SEGMENT", name,
                    "The alias may be visible as context but is outside this segment's ownership."));
        }

        if (proposal.SourceAliases is { Count: > 0 } &&
            !proposal.SourceAliases.Contains(proposal.SourceAlias, StringComparer.Ordinal))
            issues.Add(new("PRIMARY_ALIAS_NOT_IN_COMPOSITE", proposal.SourceAlias,
                "sourceAlias must be one of sourceAliases."));

        // 2. CONTRACT SHAPE
        var wholeAlias = string.Equals(
            proposal.SelectionMode, CanonicalSemanticSelectionMode.WholeAlias, StringComparison.Ordinal);
        if (proposal.SelectionMode is not null && !wholeAlias &&
            !string.Equals(proposal.SelectionMode, CanonicalSemanticSelectionMode.VerbatimText, StringComparison.Ordinal))
        {
            issues.Add(new("INVALID_SELECTION_MODE", proposal.SourceAlias,
                "The semantic proposal uses an unsupported source-selection mode."));
            return issues;
        }

        if (proposal.Occurrence is <= 0)
            issues.Add(new("INVALID_OCCURRENCE", proposal.SourceAlias,
                "occurrence must be a positive ordinal when supplied."));

        // 3. Everything above applies to any proposal. What follows is about a heading's text.
        if (!proposal.IsHeading) return issues;

        // 4. WHOLE_ALIAS addresses one parser-owned occurrence and carries no text of its own.
        if (wholeAlias)
        {
            if (names.Count != 1)
                issues.Add(new("WHOLE_ALIAS_REQUIRES_ONE_ALIAS", proposal.SourceAlias,
                    "WHOLE_ALIAS may identify exactly one parser-owned source occurrence."));
            return issues;
        }

        // 5. VERBATIM / COMPOSITE.
        // The parts list is resolved BEFORE the cardinality check so the check also covers the
        // shape a model actually produced: several sourceAliases with a single verbatimText and no
        // verbatimParts. Guarding only the populated-verbatimParts case left that shape to index a
        // one-element list with the alias ordinal, and the validator threw instead of rejecting.
        var parts = proposal.VerbatimParts is { Count: > 0 }
            ? proposal.VerbatimParts
            : proposal.VerbatimText is null ? [] : (IReadOnlyList<string>)[proposal.VerbatimText];
        if (parts.Count == 0)
        {
            issues.Add(new("MISSING_VERBATIM_TEXT", proposal.SourceAlias,
                "A heading must identify exact source text unless WHOLE_ALIAS is explicitly selected."));
            return issues;
        }

        if (names.Count != parts.Count)
            issues.Add(new("COMPOSITE_MAPPING_MISMATCH", proposal.SourceAlias,
                "sourceAliases and verbatimParts must have the same cardinality."));

        // Text checks only once the addressing is sound: reporting a substring miss against an
        // alias that does not exist, or against the wrong part, describes a defect that is not there.
        if (issues.Count > 0) return issues;

        for (var index = 0; index < names.Count; index++)
        {
            var alias = aliases[names[index]];
            var text = parts[index];
            var first = alias.Text.IndexOf(text, StringComparison.Ordinal);
            if (first < 0)
            {
                issues.Add(new("NON_VERBATIM_TEXT", names[index],
                    "The proposed verbatim text does not occur exactly inside the addressed source alias."));
                continue;
            }

            // A short heading routinely repeats inside its own occurrence - "Africa" occurs twice
            // in "Africa Gregoire ... African Development Bank". The contract refuses to guess
            // which was meant, so a duplicate must be disambiguated by occurrence ordinal or by
            // exact neighbouring text. Neither is a coordinate: both are source text the harness
            // resolves itself.
            var second = alias.Text.IndexOf(text, first + Math.Max(1, text.Length), StringComparison.Ordinal);
            if (second >= 0 && proposal.Occurrence is null &&
                proposal.LeftExactContext is null && proposal.RightExactContext is null)
                issues.Add(new("AMBIGUOUS_BINDING", names[index],
                    "duplicate source text requires occurrence or exact context."));
        }

        return issues;
    }

    public static SemanticProposalValidationSummary ValidateProposals(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlySet<string>? ownedAliases = null)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(aliases);
        var valid = new List<CanonicalSemanticProposal>();
        var issues = new List<SemanticContractIssue>();
        foreach (var proposal in proposals)
        {
            var proposalIssues = Validate(proposal, aliases, ownedAliases);
            if (proposalIssues.Count == 0) valid.Add(proposal);
            else issues.AddRange(proposalIssues);
        }
        return new(valid, issues);
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
                    issues.Add(new("NUMERIC_COORDINATE_REJECTED", nextAlias,
                        $"Field '{property.Name}' is not part of the semantic contract."));
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
        new(stage, outputCount < inputCount ? "LOSS_OBSERVED" : "COMPLETED", inputCount, outputCount, code);
}
