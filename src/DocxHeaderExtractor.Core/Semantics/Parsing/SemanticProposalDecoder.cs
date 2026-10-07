using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Semantics.Parsing;

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

}
