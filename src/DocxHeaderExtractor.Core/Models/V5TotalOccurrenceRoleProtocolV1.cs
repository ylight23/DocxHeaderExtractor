using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Qualification-only P6T-A pass one.  The harness issues one O# identity for every owned atom;
/// the model must classify every issued occurrence but cannot select an extent in this pass.
/// </summary>
public enum V5OccurrenceRoleV1 { HEADING, REPRESENTATION, OTHER }

public sealed record V5IssuedOccurrenceV1(string Id, SemanticSourceAtom Atom);

public sealed record V5TotalRoleRequestV1(
    string ProtocolVersion,
    string SystemPrompt,
    string UserMessage,
    string UserMessageSha256,
    int SystemPromptUtf8Bytes,
    int UserMessageUtf8Bytes,
    IReadOnlyList<V5IssuedOccurrenceV1> Occurrences);

public sealed record V5TotalRoleDecisionV1(string OccurrenceId, V5OccurrenceRoleV1 Role);

public sealed record V5TotalRoleDecisionResultV1(IReadOnlyList<V5TotalRoleDecisionV1> Decisions)
{
    public IReadOnlyList<string> HeadingOccurrenceIds => Decisions.Where(value => value.Role == V5OccurrenceRoleV1.HEADING)
        .Select(value => value.OccurrenceId).ToArray();
}

public static class V5TotalOccurrenceRoleProtocolV1
{
    public const string Version = "v5-total-occurrence-role-1";

    public const string SystemPrompt = """
        You are reading source occurrences in document order. The harness has issued one occurrence id (O#) for every owned source occurrence in this request. Classify every issued occurrence exactly once.

        HEADING establishes or names document structure at its own source location. REPRESENTATION only lists, points to, summarizes, or repeats structure located elsewhere. OTHER is neither. Do not use any external answer key.

        Return exactly one JSON object with exactly this shape: {"decisions":[{"occurrence":"O1","role":"OTHER"}]}. Emit exactly one decision for every issued O#; do not omit OTHER. occurrence must be an issued id and role must be exactly HEADING, REPRESENTATION, or OTHER. This pass classifies role only: do not return candidate ids, source parts, text, spans, locators, relations, reasons, confidence, hierarchy, or any other property. contextOnlyEvidence is reasoning-only and is never selectable.
        """;

    private static readonly JsonSerializerOptions Json = CanonicalJson.Options;
    private static readonly HashSet<string> RootKeys = new(["decisions"], StringComparer.Ordinal);
    private static readonly HashSet<string> DecisionKeys = new(["occurrence", "role"], StringComparer.Ordinal);

    public static V5TotalRoleRequestV1 Compose(IReadOnlyList<SemanticSourceAtom> ownedAtoms,
        IReadOnlyList<(int Page, string Text)> contextOnlyEvidence)
    {
        ArgumentNullException.ThrowIfNull(ownedAtoms);
        ArgumentNullException.ThrowIfNull(contextOnlyEvidence);
        var ordered = ownedAtoms.OrderBy(value => value.Ordinal).ThenBy(value => value.Alias, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(value => value.Alias).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidOperationException("total-role-owned-occurrence-universe-invalid");
        var occurrences = ordered.Select((atom, index) => new V5IssuedOccurrenceV1($"O{index + 1}", atom)).ToArray();
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            occurrences = occurrences.Select(value => new { id = value.Id, page = value.Atom.Page, text = value.Atom.Text }).ToArray(),
            contextOnlyEvidence = contextOnlyEvidence.Select(value => new { page = value.Page, text = value.Text }).ToArray(),
        }, Json);
        return new V5TotalRoleRequestV1(Version, SystemPrompt, user, Hashing.Sha256(user),
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user), occurrences);
    }

    /// <summary>
    /// Totality is a contract invariant, not a repair opportunity: malformed, duplicate, unknown,
    /// or omitted occurrence decisions make the role response invalid as a whole.
    /// </summary>
    public static V5TotalRoleDecisionResultV1 Parse(JsonElement payload, int rawUtf8Bytes, int responseCap,
        IReadOnlyList<V5IssuedOccurrenceV1> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (responseCap < 1 || rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap)
            throw new InvalidOperationException("total-role-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(value => !RootKeys.Contains(value.Name)) ||
            payload.EnumerateObject().Count() != 1 || !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("total-role-root-invalid");
        if (decisions.GetArrayLength() != occurrences.Count)
            throw new InvalidOperationException("total-role-decision-cardinality-invalid");

        var issued = occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var accepted = new Dictionary<string, V5TotalRoleDecisionV1>(StringComparer.Ordinal);
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Any(value => !DecisionKeys.Contains(value.Name)) ||
                decision.EnumerateObject().Count() != 2 || !decision.TryGetProperty("occurrence", out var occurrence) || occurrence.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("total-role-decision-schema-invalid");
            var id = occurrence.GetString()!;
            if (!issued.ContainsKey(id)) throw new InvalidOperationException("total-role-occurrence-not-issued");
            if (!Enum.TryParse<V5OccurrenceRoleV1>(role.GetString(), false, out var parsed) || !Enum.IsDefined(parsed) || role.GetString() != parsed.ToString())
                throw new InvalidOperationException("total-role-not-in-enum");
            if (!accepted.TryAdd(id, new V5TotalRoleDecisionV1(id, parsed)))
                throw new InvalidOperationException("total-role-occurrence-duplicate");
        }
        if (accepted.Count != issued.Count || issued.Keys.Any(id => !accepted.ContainsKey(id)))
            throw new InvalidOperationException("total-role-occurrence-omitted");
        return new V5TotalRoleDecisionResultV1(occurrences.Select(value => accepted[value.Id]).ToArray());
    }
}
