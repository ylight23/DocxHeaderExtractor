using DocxHeaderExtractor.Core.Semantics.Binding;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Decoding and binding for v2 replies, where the coordinate mode is the harness's.</summary>
public static class SemanticSourcePartsV2
{
    /// <summary>
    /// Reads one heading entry of a v2 reply. Parts arrive without a selection mode and carry
    /// <see cref="SemanticSourcePartCanonicalizer.PendingSelectionMode"/> until the canonicalizer
    /// derives the real one - which is why nothing between here and there may hand a part to the
    /// binder.
    /// </summary>
    public static SemanticProposalDecodeResult Decode(JsonElement heading)
    {
        if (heading.ValueKind != JsonValueKind.Object)
            return Failed("STRUCTURED_V2_ENTRY_NOT_AN_OBJECT", "A heading entry was not a JSON object.");

        if (!heading.TryGetProperty("isHeading", out var isHeading) ||
            isHeading.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failed("STRUCTURED_V2_ENTRY_MISSING_IS_HEADING",
                "A heading entry carried no isHeading boolean.");

        if (!heading.TryGetProperty("sourceParts", out var partsElement) ||
            partsElement.ValueKind != JsonValueKind.Array)
            return Failed("STRUCTURED_V2_ENTRY_MISSING_SOURCE_PARTS",
                "This contract addresses a claim by sourceParts; the entry carried none.");

        var parts = new List<SemanticSourcePart>();
        foreach (var element in partsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return Failed("STRUCTURED_V2_PART_NOT_AN_OBJECT", "A source part was not a JSON object.");
            if (Text(element, "sourceAlias") is not { Length: > 0 } alias)
                return Failed("STRUCTURED_V2_PART_MISSING_ALIAS", "A source part named no alias.");
            if (!TryOptionalText(element, "verbatimText", out var verbatimText) ||
                !TryOptionalText(element, "leftExactContext", out var left) ||
                !TryOptionalText(element, "rightExactContext", out var right))
                return Failed("STRUCTURED_V2_PART_FIELD_MISTYPED",
                    $"The part naming '{alias}' carried a non-text value where text belongs.");
            if (!TryOptionalOrdinal(element, "occurrence", out var occurrence))
                return Failed("STRUCTURED_V2_PART_OCCURRENCE_MISTYPED",
                    $"The part naming '{alias}' carried a non-integer occurrence.");

            parts.Add(new SemanticSourcePart(
                alias, SemanticSourcePartCanonicalizer.PendingSelectionMode,
                verbatimText, occurrence, left, right));
        }

        if (parts.Count == 0)
            return Failed("STRUCTURED_V2_ENTRY_EMPTY_SOURCE_PARTS", "A claim must name at least one source part.");

        if (!TryOptionalTextArray(heading, "relationHints", out var relationHints))
            return Failed("STRUCTURED_V2_ENTRY_RELATION_HINTS_MISTYPED",
                "relationHints was present but was not an array of text.");
        if (!TryOptionalText(heading, "semanticRole", out var semanticRole))
            return Failed("STRUCTURED_V2_ENTRY_FIELD_MISTYPED",
                "A heading entry carried a non-text value where text belongs.");

        return new SemanticProposalDecodeResult(
            [new CanonicalSemanticProposal(
                parts[0].SourceAlias,
                isHeading.GetBoolean(),
                VerbatimText: null,
                VerbatimParts: null,
                SemanticRole: semanticRole,
                StructuralType: null,
                Scope: null,
                RelationHints: relationHints,
                SourceAliases: parts.Select(part => part.SourceAlias).ToArray(),
                Occurrence: null,
                LeftExactContext: null,
                RightExactContext: null,
                SelectionMode: null,
                SourceParts: parts)],
            []);
    }

    /// <summary>
    /// The v2 binding: canonicalize, then bind. The binder itself is untouched and still strict -
    /// what changed is that it now receives parts whose coordinate form the harness decided.
    /// </summary>
    public static readonly SemanticCoordinateBinding Binding = new(
        "STRUCTURED_SOURCE_PARTS_V2",
        ValidateProposal,
        BindCanonicalized);

    private static IReadOnlyList<SemanticContractIssue> ValidateProposal(
        CanonicalSemanticProposal proposal,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlySet<string>? ownedAliases)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(aliases);

        var issues = new List<SemanticContractIssue>();
        if (proposal.SourceParts is not { Count: > 0 })
        {
            issues.Add(new("MISSING_SOURCE_PARTS", proposal.SourceAlias,
                "This contract addresses a claim as an ordered list of source parts."));
            return issues;
        }

        // Alias existence and segment ownership only. What the quote means is the canonicalizer's,
        // and a selection mode is no longer anything the reply can get wrong.
        foreach (var part in proposal.SourceParts)
        {
            if (!aliases.ContainsKey(part.SourceAlias))
            {
                issues.Add(new("UNKNOWN_ALIAS", part.SourceAlias,
                    "The alias does not exist in this source catalog."));
                continue;
            }
            if (ownedAliases is not null && !ownedAliases.Contains(part.SourceAlias))
                issues.Add(new("OUT_OF_OWNED_SEGMENT", part.SourceAlias,
                    "The alias may be visible as context but is outside this segment's ownership."));
        }

        return issues;
    }

    private static SemanticCoordinateBindingOutcome BindCanonicalized(SemanticCoordinateBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Atoms is null)
            throw new InvalidOperationException("STRUCTURED_BINDING_REQUIRES_SOURCE_ATOMS");

        var canonicalized = new List<CanonicalSemanticProposal>(request.Proposals.Count);
        var observations = new List<CanonicalSemanticBindingObservation>();

        for (var index = 0; index < request.Proposals.Count; index++)
        {
            var proposal = request.Proposals[index];
            if (proposal.SourceParts is not { Count: > 0 })
            {
                canonicalized.Add(proposal);
                continue;
            }

            var canonical = SemanticSourcePartCanonicalizer.Canonicalize(request.Atoms, proposal.SourceParts);
            if (!canonical.IsCanonical)
            {
                // A refusal here is the claim's, not the encoding's: the words quoted are not in the
                // source it named, or they are in it twice and nothing says which.
                observations.Add(new(index, proposal, StatusOf(canonical.Status), null, null, null,
                    canonical.Status.ToString()));
                continue;
            }

            canonicalized.Add(proposal with { SourceParts = canonical.Parts });
        }

        var bound = SemanticCoordinateBinding.SourceParts.Bind(
            request with { Proposals = canonicalized });

        return new SemanticCoordinateBindingOutcome(
            bound.Bound, [.. observations, .. bound.Observations]);
    }

    private static CanonicalSemanticBindingStatus StatusOf(SemanticSourcePartsStatus status) => status switch
    {
        SemanticSourcePartsStatus.UnknownAlias => CanonicalSemanticBindingStatus.UnknownAlias,
        SemanticSourcePartsStatus.TextNotInAtom => CanonicalSemanticBindingStatus.NonVerbatimText,
        SemanticSourcePartsStatus.AmbiguousSelection => CanonicalSemanticBindingStatus.AmbiguousBinding,
        _ => CanonicalSemanticBindingStatus.NonVerbatimText,
    };

    private static SemanticProposalDecodeResult Failed(string code, string detail) =>
        new([], [new SemanticProposalDecodeFailure(code, detail)]);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryOptionalText(JsonElement element, string name, out string? text)
    {
        text = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        return true;
    }

    private static bool TryOptionalOrdinal(JsonElement element, string name, out int? ordinal)
    {
        ordinal = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed)) return false;
        ordinal = parsed;
        return true;
    }

    private static bool TryOptionalTextArray(JsonElement element, string name, out string[]? items)
    {
        items = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;

        var collected = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            collected.Add(item.GetString()!);
        }
        items = collected.ToArray();
        return true;
    }
}
