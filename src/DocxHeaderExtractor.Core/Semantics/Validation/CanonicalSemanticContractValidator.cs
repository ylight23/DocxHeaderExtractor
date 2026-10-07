using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Validation;

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
