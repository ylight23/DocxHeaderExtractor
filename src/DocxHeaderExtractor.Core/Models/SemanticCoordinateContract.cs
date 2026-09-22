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
    Func<JsonElement, IReadOnlyList<SemanticContractIssue>> Validator)
{
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
    /// DOCX: alias plus an exact UTF-16 span inside it. The span is what a paragraph needs and
    /// what its Gold is written in; nothing about a PDF's segmented rows applies to it.
    /// </summary>
    public static readonly SemanticCoordinateContract DocxAliasSpan = new(
        "SOURCE_ALIAS_PLUS_UTF16_SPAN",
        CanonicalSemanticContract.ProtocolVersion,
        CanonicalSemanticContract.Schema,
        CanonicalSemanticContractValidator.ValidateJson);

    /// <summary>
    /// PDF: alias plus a selection mode over the occurrence it names. Still the shared schema -
    /// this lane has a structured multi-part successor validated in shadow, and adopting it is a
    /// separate, explicit act.
    /// </summary>
    public static readonly SemanticCoordinateContract PdfAliasSelection = new(
        "SOURCE_ALIAS_PLUS_SELECTION_MODE",
        CanonicalSemanticContract.ProtocolVersion,
        CanonicalSemanticContract.Schema,
        CanonicalSemanticContractValidator.ValidateJson);
}
