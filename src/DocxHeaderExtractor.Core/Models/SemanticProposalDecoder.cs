using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Why one heading entry in a reply could not be turned into a proposal.
/// <para>
/// This exists because the alternative was a silent drop. A structured reply that validated
/// cleanly, named real atoms and quoted real text produced zero proposals and zero visible
/// failures, because the decoder it reached was the one belonging to a different coordinate
/// contract and simply returned null for every entry. A run whose measurement collapsed looked
/// exactly like a run whose model found nothing.
/// </para>
/// </summary>
public sealed record SemanticProposalDecodeFailure(string Code, string Detail);

/// <summary>
/// One reply's headings array, decoded under the contract that issued its schema.
/// </summary>
public sealed record SemanticProposalDecodeResult(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    IReadOnlyList<SemanticProposalDecodeFailure> Failures);

/// <summary>
/// Materializes model replies into proposals, one decoder per coordinate contract.
/// <para>
/// Schema, validator and decoder are three views of one agreement, and they have to move together.
/// The structured PDF contract asked the model for an ordered <c>sourceParts</c> tuple, the model
/// answered in exactly that shape, and the reply was then handed to a decoder that required a
/// top-level <c>sourceAlias</c> - a field the structured schema never offered. Every heading was
/// dropped. Binding the decoder to the contract, rather than to the engine, is what stops the
/// mismatch from being expressible.
/// </para>
/// </summary>
public static class SemanticProposalDecoder
{
    /// <summary>
    /// The shape that addresses one alias per claim, optionally with an exact span or selection
    /// mode inside it. Used by the DOCX contract and by the legacy PDF occurrence contract.
    /// </summary>
    public static SemanticProposalDecodeResult DecodeAliasScalar(JsonElement heading)
    {
        if (CanonicalSemanticProposalParser.TryParse(heading) is { } proposal)
            return new SemanticProposalDecodeResult([proposal], []);

        return new SemanticProposalDecodeResult([], [new SemanticProposalDecodeFailure(
            "ALIAS_SCALAR_ENTRY_UNREADABLE",
            "A heading entry omitted a field this contract requires, or typed one wrongly.")]);
    }

    /// <summary>
    /// The shape that addresses a claim as an ordered list of exact selections over coordinate
    /// atoms. The tuple is carried through whole: a two-part heading stays two parts, because
    /// collapsing it to its first part would reintroduce, one level down, the assumption that a
    /// semantic boundary coincides with a parser boundary.
    /// </summary>
    public static SemanticProposalDecodeResult DecodeSourceParts(JsonElement heading)
    {
        if (heading.ValueKind != JsonValueKind.Object)
            return Failed("STRUCTURED_ENTRY_NOT_AN_OBJECT", "A heading entry was not a JSON object.");

        if (!heading.TryGetProperty("isHeading", out var isHeadingValue) ||
            isHeadingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Failed("STRUCTURED_ENTRY_MISSING_IS_HEADING",
                "A heading entry carried no isHeading boolean.");

        if (!heading.TryGetProperty("sourceParts", out var partsElement) ||
            partsElement.ValueKind != JsonValueKind.Array)
            return Failed("STRUCTURED_ENTRY_MISSING_SOURCE_PARTS",
                "This contract addresses a claim by sourceParts; the entry carried none.");

        var parts = new List<SemanticSourcePart>();
        foreach (var element in partsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                return Failed("STRUCTURED_PART_NOT_AN_OBJECT", "A source part was not a JSON object.");
            if (Text(element, "sourceAlias") is not { Length: > 0 } alias)
                return Failed("STRUCTURED_PART_MISSING_ALIAS", "A source part named no alias.");
            if (Text(element, "selectionMode") is not { Length: > 0 } selectionMode)
                return Failed("STRUCTURED_PART_MISSING_SELECTION_MODE",
                    $"The part naming '{alias}' did not say how much of it is selected.");
            if (!TryOptionalText(element, "verbatimText", out var verbatimText) ||
                !TryOptionalText(element, "leftExactContext", out var left) ||
                !TryOptionalText(element, "rightExactContext", out var right))
                return Failed("STRUCTURED_PART_FIELD_MISTYPED",
                    $"The part naming '{alias}' carried a non-text value where text belongs.");
            if (!TryOptionalOrdinal(element, "occurrence", out var occurrence))
                return Failed("STRUCTURED_PART_OCCURRENCE_MISTYPED",
                    $"The part naming '{alias}' carried a non-integer occurrence.");

            parts.Add(new SemanticSourcePart(alias, selectionMode, verbatimText, occurrence, left, right));
        }

        if (parts.Count == 0)
            return Failed("STRUCTURED_ENTRY_EMPTY_SOURCE_PARTS", "A claim must name at least one source part.");

        if (!TryOptionalTextArray(heading, "relationHints", out var relationHints))
            return Failed("STRUCTURED_ENTRY_RELATION_HINTS_MISTYPED",
                "relationHints was present but was not an array of text.");
        if (!TryOptionalText(heading, "semanticRole", out var semanticRole) ||
            !TryOptionalText(heading, "structuralType", out var structuralType) ||
            !TryOptionalText(heading, "scope", out var scope))
            return Failed("STRUCTURED_ENTRY_FIELD_MISTYPED",
                "A heading entry carried a non-text value where text belongs.");

        // SourceParts is the claim's identity. SourceAlias and SourceAliases are populated from it
        // so the surrounding pipeline - segment ownership, conflict detection - can route the claim
        // without each of those places learning a second addressing scheme. They are derived views,
        // never the identity: the tuple above is what the binder resolves and what Gold records.
        return new SemanticProposalDecodeResult(
            [new CanonicalSemanticProposal(
                parts[0].SourceAlias,
                isHeadingValue.GetBoolean(),
                VerbatimText: null,
                VerbatimParts: null,
                SemanticRole: semanticRole,
                StructuralType: structuralType,
                Scope: scope,
                RelationHints: relationHints,
                SourceAliases: parts.Select(part => part.SourceAlias).ToArray(),
                Occurrence: null,
                LeftExactContext: null,
                RightExactContext: null,
                SelectionMode: null,
                SourceParts: parts)],
            []);
    }

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
