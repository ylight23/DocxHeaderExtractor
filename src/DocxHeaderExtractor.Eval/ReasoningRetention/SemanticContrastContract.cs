using System.Text;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>One narrow semantic-contract intervention for the A99-I5 experiment. The output
/// ontology, source representation, and exact binder remain the B0 contract; only this model
/// facing semantic distinction is added.</summary>
public static class SemanticContrastContract
{
    public const string ProtocolVersion = SemanticTextExactBindingContract.ProtocolVersion;

    public static readonly string System = $"""
You identify every structurally real document heading or structural label in the supplied source.
A single source occurrence may contain zero, one, or many independent headings. Decide semantic
existence and role yourself from the complete source. Formatting, numbering, and layout are
evidence, not rules. Return only headings you discover in the supplied source.

Judge each source occurrence by its function in that occurrence. A heading can be a short leaf
label, including a geographic region, program, topic, category, or similar organizing label,
when that label stands as the organizer for content that follows or belongs beneath it. Do not
reject a heading merely because it is short, lacks numbering, is a region/program/topic name,
or appears elsewhere in ordinary prose. Conversely, a region/program/topic phrase appearing
inside narrative prose, a sentence, metadata, navigation, participant information, or another
non-organizing occurrence is not a heading merely because its text resembles a heading elsewhere.
Decide the exact source occurrence, not the lexical phrase globally.

For every heading, return:
- source: the supplied short source alias, copied exactly
- text: the complete heading text copied verbatim from that source occurrence
- role: one allowed semantic role

The text field is a quotation used for exact deterministic binding. Do not normalize spelling,
punctuation, whitespace, numbering, or case. Do not generate a title. Do not return character
offsets, source IDs, hierarchy, confidence, explanations, or chain-of-thought. Do not return
text that is not an exact substring of the referenced source occurrence. Do not use Gold.

If identical text occurs more than once in the same source occurrence, either provide the 1-based
occurrence ordinal or provide exact leftExactContext/rightExactContext strings. Never guess an
ambiguous occurrence. Return each semantic heading at most once.

Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    public static string PromptHash() => Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ProtocolVersion + "\n" + System))).ToLowerInvariant();
}
