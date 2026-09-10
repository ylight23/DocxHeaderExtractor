using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Generic Pass B contract for the semantic-text omission intervention. The source
/// packet is unchanged and the inventory is advisory only; every emitted item still uses the
/// frozen alias/verbatim-text/role contract and the unchanged exact binder.</summary>
public static class SemanticTextOmissionReviewContract
{
    public const string ProtocolVersion = "a99-semantic-text-omission-review-v1";

    public static readonly string System = $"""
Review the complete supplied source and the current semantic inventory. Identify structural
headings or structural labels that are genuinely present in the source but absent from the
inventory. The inventory is informational only and must never limit your review. A source
occurrence may contain zero, one, or many independent headings. Decide semantic existence and
role from the source itself; formatting, numbering, and layout are evidence, not rules.

Return only additional discoveries. For every item return:
- source: one supplied short source alias, copied exactly
- text: the complete heading text copied verbatim from that source occurrence
- role: one allowed semantic role

The text field is used for exact deterministic binding. Do not normalize spelling, punctuation,
whitespace, numbering, or case. Do not generate a title. Do not return numeric offsets, source
IDs, hierarchy, confidence, explanations, or chain-of-thought. Do not return text that is not an
exact substring of the referenced source occurrence. If identical text occurs more than once in
one source occurrence, provide the 1-based occurrence ordinal or exact left/right context. Never
guess an ambiguous occurrence. Return each semantic heading at most once.

Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    public static string BuildUser(string packetJson, string inventoryJson, string route) =>
        $"TASK={ProtocolVersion}\nroute={route}\nSOURCE_PACKET={packetJson}\nCURRENT_INVENTORY={inventoryJson}";

    /// <summary>The exact frozen semantic-text schema is reused; no second serializer or binder
    /// contract is introduced for the review pass.</summary>
    public static object Schema() => SemanticTextExactBindingContract.Schema();

    public static string Hash()
    {
        var material = ProtocolVersion + "\n" + System + "\n" + JsonSerializer.Serialize(Schema());
        return Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
