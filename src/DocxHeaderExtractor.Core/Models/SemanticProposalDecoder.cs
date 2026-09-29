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
    /// mode inside it. Used by the DOCX contract.
    /// </summary>
    public static SemanticProposalDecodeResult DecodeAliasScalar(JsonElement heading)
    {
        if (CanonicalSemanticProposalParser.TryParse(heading) is { } proposal)
            return new SemanticProposalDecodeResult([proposal], []);

        return new SemanticProposalDecodeResult([], [new SemanticProposalDecodeFailure(
            "ALIAS_SCALAR_ENTRY_UNREADABLE",
            "A heading entry omitted a field this contract requires, or typed one wrongly.")]);
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
