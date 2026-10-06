using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Qualification-only P6T-F1 total function-membership protocol.
/// It deliberately classifies source-occurrence function without anchors, segments, extents, or candidates.
/// </summary>
public enum V5OccurrenceFunctionF1 { ESTABLISHES_STRUCTURE, REPRESENTS_STRUCTURE, OTHER }

public sealed record V5TotalOccurrenceFunctionRequestF1(
    string ProtocolVersion,
    string SystemPrompt,
    string UserMessage,
    string UserMessageSha256,
    int SystemPromptUtf8Bytes,
    int UserMessageUtf8Bytes,
    IReadOnlyList<V5IssuedOccurrenceV1> Occurrences);

public sealed record V5OccurrenceFunctionDecisionF1(string OccurrenceId, V5OccurrenceFunctionF1 Function);

public sealed record V5TotalOccurrenceFunctionResultF1(IReadOnlyList<V5OccurrenceFunctionDecisionF1> Decisions);

public static class V5TotalOccurrenceFunctionProtocolF1
{
    public const string Version = "v5-total-occurrence-function-membership-1";

    public static readonly string SystemPrompt = QualifiedPromptText.Canonicalize("""
        You are classifying the document-level semantic function of every issued source occurrence. Classify every issued O# exactly once. Do not decide where a unit starts or ends and do not construct an extent.

        ESTABLISHES_STRUCTURE means the occurrence itself contributes wording that establishes or names document structure at its own source location. If a structural heading is split across multiple source occurrences, every occurrence that contributes wording to that heading receives ESTABLISHES_STRUCTURE.

        REPRESENTS_STRUCTURE means the occurrence lists, summarizes, points to, repeats, or navigates document structure whose operative structural location is elsewhere. Examples can include table-of-contents or index-like entries.

        OTHER means the occurrence performs neither function.

        These labels describe function membership only. Do not return starts, continuations, segment boundaries, candidate ids, source parts, spans, locators, hierarchy, relations, confidence, reasons, or extent. contextOnlyEvidence and read-only correspondences are reasoning-only and are never selectable output.

        Return exactly one JSON object with exactly this shape: {"decisions":[{"occurrence":"O1","function":"OTHER"}]}. Emit exactly one decision for every issued O#; do not omit any decision. occurrence must be an issued id and function must be exactly ESTABLISHES_STRUCTURE, REPRESENTS_STRUCTURE, or OTHER.
        """);

    private static readonly JsonSerializerOptions Json = CanonicalJson.Options;
    private static readonly HashSet<string> RootKeys = new(["decisions"], StringComparer.Ordinal);
    private static readonly HashSet<string> DecisionKeys = new(["occurrence", "function"], StringComparer.Ordinal);

    public static V5TotalOccurrenceFunctionRequestF1 ComposeWithReadOnlyCorrespondences(
        IReadOnlyList<SemanticSourceAtom> ownedAtoms,
        IReadOnlyList<(int Page, string Text)> contextOnlyEvidence,
        IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> correspondences)
    {
        ArgumentNullException.ThrowIfNull(ownedAtoms);
        ArgumentNullException.ThrowIfNull(contextOnlyEvidence);
        ArgumentNullException.ThrowIfNull(correspondences);
        var ordered = ownedAtoms.OrderBy(value => value.Ordinal).ThenBy(value => value.Alias, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(value => value.Alias).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidOperationException("function-membership-owned-occurrence-universe-invalid");
        var occurrences = ordered.Select((atom, index) => new V5IssuedOccurrenceV1($"O{index + 1}", atom)).ToArray();
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            occurrences = occurrences.Select(value => new
            {
                id = value.Id,
                page = value.Atom.Page,
                text = value.Atom.Text,
                correspondences = correspondences.TryGetValue(value.Atom.Alias, out var targets)
                    ? targets.Select(target => new { targetPage = target.TargetPage, targetText = target.TargetText }).ToArray()
                    : Array.Empty<object>(),
            }).ToArray(),
            contextOnlyEvidence = contextOnlyEvidence.Select(value => new { page = value.Page, text = value.Text }).ToArray(),
        }, Json);
        return new V5TotalOccurrenceFunctionRequestF1(Version, SystemPrompt, user, Hashing.Sha256(user),
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user), occurrences);
    }

    public static V5TotalOccurrenceFunctionResultF1 Parse(
        JsonElement payload,
        int rawUtf8Bytes,
        int responseCap,
        IReadOnlyList<V5IssuedOccurrenceV1> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (responseCap < 1 || rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap)
            throw new InvalidOperationException("function-membership-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(value => !RootKeys.Contains(value.Name)) || payload.EnumerateObject().Count() != 1 || !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("function-membership-root-invalid");
        if (decisions.GetArrayLength() != occurrences.Count)
            throw new InvalidOperationException("function-membership-decision-cardinality-invalid");
        var issued = occurrences.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var accepted = new Dictionary<string, V5OccurrenceFunctionDecisionF1>(StringComparer.Ordinal);
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Any(value => !DecisionKeys.Contains(value.Name)) || decision.EnumerateObject().Count() != 2 || !decision.TryGetProperty("occurrence", out var occurrence) || occurrence.ValueKind != JsonValueKind.String || !decision.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("function-membership-decision-schema-invalid");
            var id = occurrence.GetString()!;
            if (!issued.ContainsKey(id)) throw new InvalidOperationException("function-membership-occurrence-not-issued");
            var functionText = function.GetString()!;
            if (!Enum.TryParse<V5OccurrenceFunctionF1>(functionText, false, out var parsed) || !Enum.IsDefined(parsed) || functionText != parsed.ToString())
                throw new InvalidOperationException("function-membership-not-in-enum");
            if (!accepted.TryAdd(id, new V5OccurrenceFunctionDecisionF1(id, parsed)))
                throw new InvalidOperationException("function-membership-occurrence-duplicate");
        }
        if (accepted.Count != issued.Count || issued.Keys.Any(id => !accepted.ContainsKey(id)))
            throw new InvalidOperationException("function-membership-occurrence-omitted");
        return new V5TotalOccurrenceFunctionResultF1(occurrences.Select(value => accepted[value.Id]).ToArray());
    }
}
