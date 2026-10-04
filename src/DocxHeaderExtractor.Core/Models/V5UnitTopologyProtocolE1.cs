using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Qualification-only P6T-E1 topology pass. It intentionally knows nothing about document function.</summary>
public enum V5UnitTopologyRoleE1 { STARTS_UNIT, CONTINUES_UNIT, NO_UNIT_BOUNDARY }

public sealed record V5UnitTopologyRequestE1(
    string ProtocolVersion,
    string SystemPrompt,
    string UserMessage,
    string UserMessageSha256,
    int SystemPromptUtf8Bytes,
    int UserMessageUtf8Bytes,
    IReadOnlyList<V5IssuedOccurrenceV1> Occurrences);

public sealed record V5UnitTopologyDecisionE1(string OccurrenceId, V5UnitTopologyRoleE1 Role);

public sealed record V5UnitTopologyResultE1(IReadOnlyList<V5UnitTopologyDecisionE1> Decisions);

public static class V5UnitTopologyProtocolE1
{
    public const string Version = "v5-total-occurrence-unit-topology-1";

    public const string SystemPrompt = """
        You are classifying local unit topology in document order. The harness has issued one occurrence id (O#) for every owned source occurrence in this request. Classify every issued occurrence exactly once by its local relationship to a unit, without deciding what document function that unit serves.

        STARTS_UNIT means this occurrence begins a new local unit. CONTINUES_UNIT means this occurrence continues a unit that began at an earlier occurrence. NO_UNIT_BOUNDARY means this occurrence is neither a unit start nor a continuation. These labels describe topology only; do not infer or emit document-function, extent, candidate, source part, locator, relation, reason, confidence, or any other semantic label.

        Return exactly one JSON object with exactly this shape: {"decisions":[{"occurrence":"O1","topology":"NO_UNIT_BOUNDARY"}]}. Emit exactly one decision for every issued O#; do not omit any decision. occurrence must be an issued id and topology must be exactly STARTS_UNIT, CONTINUES_UNIT, or NO_UNIT_BOUNDARY. contextOnlyEvidence and read-only correspondences are reasoning-only and are never selectable output.
        """;

    private static readonly JsonSerializerOptions Json = CanonicalJson.Options;
    private static readonly HashSet<string> RootKeys = new(["decisions"], StringComparer.Ordinal);
    private static readonly HashSet<string> DecisionKeys = new(["occurrence", "topology"], StringComparer.Ordinal);

    public static V5UnitTopologyRequestE1 ComposeWithReadOnlyCorrespondences(
        IReadOnlyList<SemanticSourceAtom> ownedAtoms,
        IReadOnlyList<(int Page, string Text)> contextOnlyEvidence,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(ownedAtoms);
        ArgumentNullException.ThrowIfNull(contextOnlyEvidence);
        ArgumentNullException.ThrowIfNull(correspondences);
        var ordered = ownedAtoms.OrderBy(value => value.Ordinal).ThenBy(value => value.Alias, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(value => value.Alias).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidOperationException("unit-topology-owned-occurrence-universe-invalid");
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
        return new V5UnitTopologyRequestE1(Version, SystemPrompt, user, Hashing.Sha256(user),
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user), occurrences);
    }

    public static V5UnitTopologyResultE1 Parse(JsonElement payload, int rawUtf8Bytes, int responseCap,
        IReadOnlyList<V5IssuedOccurrenceV1> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (responseCap < 1 || rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap) throw new InvalidOperationException("unit-topology-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(value => !RootKeys.Contains(value.Name)) || payload.EnumerateObject().Count() != 1 || !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("unit-topology-root-invalid");
        if (decisions.GetArrayLength() != occurrences.Count) throw new InvalidOperationException("unit-topology-decision-cardinality-invalid");
        var issued = occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var accepted = new Dictionary<string, V5UnitTopologyDecisionE1>(StringComparer.Ordinal);
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Any(value => !DecisionKeys.Contains(value.Name)) || decision.EnumerateObject().Count() != 2 || !decision.TryGetProperty("occurrence", out var occurrence) || occurrence.ValueKind != JsonValueKind.String || !decision.TryGetProperty("topology", out var topology) || topology.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("unit-topology-decision-schema-invalid");
            var id = occurrence.GetString()!; if (!issued.ContainsKey(id)) throw new InvalidOperationException("unit-topology-occurrence-not-issued");
            var topologyText = topology.GetString()!;
            if (!Enum.TryParse<V5UnitTopologyRoleE1>(topologyText, false, out var parsed) || !Enum.IsDefined(parsed) || topologyText != parsed.ToString()) throw new InvalidOperationException("unit-topology-not-in-enum");
            if (!accepted.TryAdd(id, new V5UnitTopologyDecisionE1(id, parsed))) throw new InvalidOperationException("unit-topology-occurrence-duplicate");
        }
        if (accepted.Count != issued.Count || issued.Keys.Any(id => !accepted.ContainsKey(id))) throw new InvalidOperationException("unit-topology-occurrence-omitted");
        return new V5UnitTopologyResultE1(occurrences.Select(value => accepted[value.Id]).ToArray());
    }
}
