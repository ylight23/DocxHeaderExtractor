using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Parses the model's semantic shape without consulting a source catalog. This is the freeze
/// boundary: the returned proposals are still model output and have not been source-validated,
/// bound, deduplicated, or assigned coordinates.
/// </summary>
public static class CanonicalSemanticProposalParser
{
    public static IReadOnlyList<CanonicalSemanticProposal> Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("headings", out var headings) ||
            headings.ValueKind != JsonValueKind.Array)
            return [];

        var proposals = new List<CanonicalSemanticProposal>();
        foreach (var element in headings.EnumerateArray())
            if (TryParse(element) is { } proposal)
                proposals.Add(proposal);
        return proposals;
    }

    public static CanonicalSemanticProposal? TryParse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (Text(element, "sourceAlias") is not { Length: > 0 } sourceAlias) return null;
        if (!element.TryGetProperty("isHeading", out var isHeadingValue) ||
            isHeadingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        if (!TryTextArray(element, "sourceAliases", out var sourceAliases)) return null;
        if (!TryTextArray(element, "relationHints", out var relationHints)) return null;
        if (!TryOrdinal(element, "occurrence", out var occurrence)) return null;
        if (!TryText(element, "verbatimText", out var verbatimText)) return null;
        if (!TryTextArray(element, "verbatimParts", out var verbatimParts)) return null;
        if (!TryText(element, "semanticRole", out var semanticRole)) return null;
        if (!TryText(element, "structuralType", out var structuralType)) return null;
        if (!TryText(element, "scope", out var scope)) return null;
        if (!TryText(element, "leftExactContext", out var left)) return null;
        if (!TryText(element, "rightExactContext", out var right)) return null;
        if (!TryText(element, "selectionMode", out var mode)) return null;

        return new CanonicalSemanticProposal(
            sourceAlias,
            isHeadingValue.GetBoolean(),
            verbatimText,
            verbatimParts,
            semanticRole,
            structuralType,
            scope,
            relationHints,
            sourceAliases,
            occurrence,
            left,
            right,
            mode);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryText(JsonElement element, string name, out string? text)
    {
        text = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        return true;
    }

    private static bool TryTextArray(JsonElement element, string name, out string[]? items)
    {
        items = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            result.Add(item.GetString()!);
        }
        items = [.. result];
        return true;
    }

    private static bool TryOrdinal(JsonElement element, string name, out int? ordinal)
    {
        ordinal = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return false;
        ordinal = number;
        return true;
    }
}
