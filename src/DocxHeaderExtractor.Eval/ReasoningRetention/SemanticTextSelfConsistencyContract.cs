using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Generic verifier contract for the self-consistency intervention. It receives only
/// source text and model-generated union proposals; it never receives Gold or an expected count.</summary>
public static class SemanticTextSelfConsistencyContract
{
    public const string ProtocolVersion = "a99-semantic-text-self-consistency-v1";

    public static readonly string System = $"""
You are a semantic heading verifier. Review the complete supplied source and the union of
independent model proposals. Decide which proposals are genuinely structural headings or labels
present in the source. Preserve a correct proposal exactly. Reject proposals that are not
structural headings. If a proposal is semantically real but its quoted text is too broad or too
narrow, return CORRECT_SPAN with the complete verbatim heading text from the same source alias.
Do not invent headings. Do not use Gold, expected counts, document-specific rules, candidate
heuristics, or numeric offsets. The deterministic binder remains authoritative for source spans.

For every union proposal return one decision with its candidateId and one action: KEEP, REJECT,
or CORRECT_SPAN. KEEP must copy the candidate source/text/role exactly. CORRECT_SPAN must copy
the corrected source alias and complete verbatim text exactly. Return no explanations or
chain-of-thought. Allowed roles: {string.Join(", ", CeilingSemanticRole.AllowedRoles)}.
Return only the JSON object described by the schema.
""";

    public static string BuildUser(string packetJson, string candidatesJson, string route) =>
        $"TASK={ProtocolVersion}\nroute={route}\nSOURCE_PACKET={packetJson}\nUNION_PROPOSALS={candidatesJson}";

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            decisions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        candidateId = new { type = "integer", minimum = 0 },
                        action = new { type = "string", @enum = new[] { "KEEP", "REJECT", "CORRECT_SPAN" } },
                        source = new { type = "string" },
                        text = new { type = "string" },
                        role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "candidateId", "action" },
                },
            },
        },
        required = new[] { "decisions" },
    };

    public static IReadOnlyList<SemanticTextVerificationDecision> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("self-consistency-verifier-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("self-consistency-verifier-json-incomplete");
        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
        if (!doc.RootElement.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new FormatException("self-consistency-verifier-decisions-missing");
        var result = new List<SemanticTextVerificationDecision>();
        foreach (var item in decisions.EnumerateArray())
        {
            if (!item.TryGetProperty("candidateId", out var candidateId) || !candidateId.TryGetInt32(out var id) || id < 0 ||
                !item.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                action.GetString() is not ("KEEP" or "REJECT" or "CORRECT_SPAN"))
                throw new FormatException("self-consistency-verifier-decision-invalid");
            string? Get(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            int? occurrence = item.TryGetProperty("occurrence", out var ordinal) && ordinal.ValueKind != JsonValueKind.Null && ordinal.TryGetInt32(out var parsed) ? parsed : null;
            result.Add(new(id, action.GetString()!, Get("source"), Get("text"), Get("role"), occurrence, Get("leftExactContext"), Get("rightExactContext")));
        }
        return result;
    }
}

public sealed record SemanticTextVerificationDecision(
    int CandidateId,
    string Action,
    string? Source,
    string? Text,
    string? Role,
    int? Occurrence,
    string? LeftExactContext,
    string? RightExactContext);
