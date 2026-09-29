using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// How one source lane lets the model name a place in its document.
/// <para>
/// The semantic core - what a heading is, what a role means, how a claim is judged - is one thing
/// and stays one thing. Where a claim <em>points</em> is not: a DOCX paragraph is addressed by
/// alias and an exact span within it, a PDF is addressed by a run of visual-segment atoms, and an
/// HTML document would be addressed by neither. Those are properties of the source, not of the
/// reasoning, and the engine had been assuming a single global answer for all of them.
/// </para>
/// <para>
/// So the coordinate half of the model contract belongs to the lane that knows the source. Both
/// lanes still hand over the same schema today; what changes is that neither of them is reading it
/// off a shared static any more, which is what makes it possible for one to move without the other.
/// </para>
/// </summary>
public sealed record SemanticCoordinateContract(
    string CoordinateSystem,
    string ProtocolVersion,
    Func<object> SchemaFactory,
    Func<JsonElement, IReadOnlyList<SemanticContractIssue>> Validator,
    Func<JsonElement, SemanticProposalDecodeResult> Decoder,
    SemanticCoordinateBinding Binding)
{
    /// <summary>
    /// Text appended to the shared semantic-core prompt to teach this contract's coordinate shape.
    /// Null means the core prompt already covers it - true of both current contracts, since neither
    /// has changed what it asks the model to return. A contract that introduces a new coordinate
    /// shape, like structured sourceParts, supplies the fragment that teaches it.
    /// </summary>
    public string? PromptClause { get; init; }

    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The schema the model is shown, produced by the lane rather than by a static.</summary>
    public object Schema() => SchemaFactory();

    /// <summary>
    /// Identity of the schema bytes. Taken over the same canonical serialization the freeze
    /// artifacts use, so a lane's contract can be compared with a recorded hash without either
    /// side having to describe how it was produced.
    /// </summary>
    public string SchemaHash() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(Schema(), Canonical).ReplaceLineEndings("\n"))));

    /// <summary>
    /// Checks a reply against this contract. Paired with <see cref="Schema"/> deliberately: a
    /// request built from one contract and validated against another would accept coordinates the
    /// model was never offered, and the mismatch would be invisible from either side alone.
    /// </summary>
    public IReadOnlyList<SemanticContractIssue> Validate(JsonElement payload) => Validator(payload);

    /// <summary>
    /// Materializes one heading entry of a reply into proposals, under the contract that issued the
    /// schema the model answered. Paired with <see cref="Schema"/> and <see cref="Validate"/> for
    /// the same reason they are paired with each other, and learned the hard way: a reply written
    /// in this contract's shape and read by another contract's decoder does not fail, it comes back
    /// empty, and an empty measurement is indistinguishable from a model that found nothing.
    /// </summary>
    public SemanticProposalDecodeResult Decode(JsonElement heading) => Decoder(heading);

    /// <summary>
    /// Checks and resolves a decoded proposal against the source, in this contract's coordinate
    /// system. The fourth member of one agreement: schema, validator, decoder, binder. A reply
    /// decoded as an ordered tuple and then bound by rules written for one alias and one span is
    /// the same class of mismatch as decoding it with the wrong decoder, one stage later.
    /// </summary>
    public SemanticCoordinateBindingOutcome BindProposals(SemanticCoordinateBindingRequest request) =>
        Binding.Bind(request);

    /// <summary>
    /// DOCX: alias plus an exact UTF-16 span inside it. The span is what a paragraph needs and
    /// what its Gold is written in; nothing about a PDF's segmented rows applies to it.
    /// </summary>
    public static readonly SemanticCoordinateContract DocxAliasSpan = new(
        "SOURCE_ALIAS_PLUS_UTF16_SPAN",
        CanonicalSemanticContract.ProtocolVersion,
        CanonicalSemanticContract.Schema,
        CanonicalSemanticContractValidator.ValidateJson,
        SemanticProposalDecoder.DecodeAliasScalar,
        SemanticCoordinateBinding.AliasSpan);

    /// <summary>
    /// PDF: structured source parts over segment atoms, one closed semantic function per claim, with
    /// membership derived by the harness. The only PDF contract.
    /// </summary>
    public static readonly SemanticCoordinateContract PdfSemanticFunctionMembershipV1 = new(
        "STRUCTURED_SOURCE_PART_TUPLE",
        SemanticFunctionMembershipContractV1.ProtocolVersion,
        SemanticFunctionMembershipContractV1.Schema,
        SemanticFunctionMembershipContractV1.ValidateJson,
        SemanticFunctionMembershipContractV1.Decode,
        SemanticFunctionMembershipContractV1.Binding)
    {
        PromptClause = PdfSemanticFunctionMembershipPromptClause.Text,
    };
}
