using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// P6T-D keeps the total anchor ledger but makes abstention explicit.  This is a
/// qualification-only protocol; it does not alter the live document runtime.
/// </summary>
public enum V5OccurrenceRoleD { HEADING_START, REPRESENTATION_START, OTHER, UNRESOLVED }

public sealed record V5TotalRoleRequestD(
    string ProtocolVersion,
    string SystemPrompt,
    string UserMessage,
    string UserMessageSha256,
    int SystemPromptUtf8Bytes,
    int UserMessageUtf8Bytes,
    IReadOnlyList<V5IssuedOccurrenceV1> Occurrences);

public sealed record V5TotalRoleDecisionD(string OccurrenceId, V5OccurrenceRoleD Role);

public sealed record V5TotalRoleDecisionResultD(IReadOnlyList<V5TotalRoleDecisionD> Decisions);

public static class V5TotalOccurrenceRoleProtocolD
{
    public const string Version = "v5-total-occurrence-anchor-role-abstention-1";

    public const string SystemPrompt = """
        You are reading source occurrences in document order. The harness has issued one occurrence id (O#) for every owned source occurrence in this request. Classify every issued occurrence exactly once as the start of a semantic unit, not as membership in a unit that may have started earlier.

        HEADING_START means this occurrence starts a local structural heading; that heading may continue through later occurrences. REPRESENTATION_START means this occurrence starts an entry that only lists, points to, summarizes, or repeats structure located elsewhere; that entry may continue through later occurrences. OTHER means you determine that this occurrence starts neither kind of unit. OTHER may be a continuation of a heading or representation that began earlier; it does not mean the occurrence is unrelated to that unit. UNRESOLVED means there is not enough evidence to commit to HEADING_START, REPRESENTATION_START, or OTHER. UNRESOLVED is distinct from OTHER and must never be promoted to extent resolution.

        Return exactly one JSON object with exactly this shape: {"decisions":[{"occurrence":"O1","role":"UNRESOLVED"}]}. Emit exactly one decision for every issued O#; do not omit any decision. occurrence must be an issued id and role must be exactly HEADING_START, REPRESENTATION_START, OTHER, or UNRESOLVED. This pass classifies only the unit start role: do not return candidate ids, source parts, text, spans, locators, relations, reasons, confidence, hierarchy, or any other property. contextOnlyEvidence and read-only correspondences are reasoning-only and are never selectable output.
        """;

    private static readonly JsonSerializerOptions Json = CanonicalJson.Options;
    private static readonly HashSet<string> RootKeys = new(["decisions"], StringComparer.Ordinal);
    private static readonly HashSet<string> DecisionKeys = new(["occurrence", "role"], StringComparer.Ordinal);

    public static V5TotalRoleRequestD ComposeWithReadOnlyCorrespondences(
        IReadOnlyList<SemanticSourceAtom> ownedAtoms,
        IReadOnlyList<(int Page, string Text)> contextOnlyEvidence,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(ownedAtoms);
        ArgumentNullException.ThrowIfNull(contextOnlyEvidence);
        ArgumentNullException.ThrowIfNull(correspondences);
        var ordered = ownedAtoms.OrderBy(value => value.Ordinal).ThenBy(value => value.Alias, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(value => value.Alias).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidOperationException("total-role-d-owned-occurrence-universe-invalid");
        var occurrences = ordered.Select((atom, index) => new V5IssuedOccurrenceV1($"O{index + 1}", atom)).ToArray();
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            occurrences = occurrences.Select(value => new
            {
                id = value.Id, page = value.Atom.Page, text = value.Atom.Text,
                correspondences = correspondences.TryGetValue(value.Atom.Alias, out var targets)
                    ? targets.Select(target => new { targetPage = target.TargetPage, targetText = target.TargetText }).ToArray()
                    : Array.Empty<object>(),
            }).ToArray(),
            contextOnlyEvidence = contextOnlyEvidence.Select(value => new { page = value.Page, text = value.Text }).ToArray(),
        }, Json);
        return new V5TotalRoleRequestD(Version, SystemPrompt, user, Hashing.Sha256(user),
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user), occurrences);
    }

    public static V5TotalRoleDecisionResultD Parse(JsonElement payload, int rawUtf8Bytes, int responseCap,
        IReadOnlyList<V5IssuedOccurrenceV1> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (responseCap < 1 || rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap)
            throw new InvalidOperationException("total-role-d-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(value => !RootKeys.Contains(value.Name)) ||
            payload.EnumerateObject().Count() != 1 || !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("total-role-d-root-invalid");
        if (decisions.GetArrayLength() != occurrences.Count)
            throw new InvalidOperationException("total-role-d-decision-cardinality-invalid");
        var issued = occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var accepted = new Dictionary<string, V5TotalRoleDecisionD>(StringComparer.Ordinal);
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Any(value => !DecisionKeys.Contains(value.Name)) ||
                decision.EnumerateObject().Count() != 2 || !decision.TryGetProperty("occurrence", out var occurrence) || occurrence.ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("total-role-d-decision-schema-invalid");
            var id = occurrence.GetString()!;
            if (!issued.ContainsKey(id)) throw new InvalidOperationException("total-role-d-occurrence-not-issued");
            var roleText = role.GetString()!;
            if (!Enum.TryParse<V5OccurrenceRoleD>(roleText, false, out var parsed) || !Enum.IsDefined(parsed) || roleText != parsed.ToString())
                throw new InvalidOperationException("total-role-d-not-in-enum");
            if (!accepted.TryAdd(id, new V5TotalRoleDecisionD(id, parsed)))
                throw new InvalidOperationException("total-role-d-occurrence-duplicate");
        }
        if (accepted.Count != issued.Count || issued.Keys.Any(id => !accepted.ContainsKey(id)))
            throw new InvalidOperationException("total-role-d-occurrence-omitted");
        return new V5TotalRoleDecisionResultD(occurrences.Select(value => accepted[value.Id]).ToArray());
    }
}
