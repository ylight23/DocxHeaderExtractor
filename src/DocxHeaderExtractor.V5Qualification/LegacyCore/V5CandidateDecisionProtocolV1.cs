using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>The only two decisions a model may make about an issued candidate.</summary>
public enum V5CandidateDecisionKind
{
    HEADING,
    REPRESENTATION,
}

/// <summary>An accepted decision. <see cref="Candidate"/> carries the frozen endpoint; nothing here came from model text.</summary>
public sealed record V5AcceptedCandidateDecisionV1(
    int OriginalOrdinal,
    V5IssuedCandidateV1 Candidate,
    V5CandidateDecisionKind Kind);

public sealed record V5QuarantinedCandidateDecisionV1(int OriginalOrdinal, string Reason);

public sealed record V5CandidateDecisionResultV1(
    int RawDecisionCount,
    IReadOnlyList<V5AcceptedCandidateDecisionV1> Accepted,
    IReadOnlyList<V5QuarantinedCandidateDecisionV1> Quarantined,
    int DuplicateDecisionsCollapsed)
{
    /// <summary>Issued, locally valid decisions after duplicate/conflict handling but before the
    /// fail-closed overlap-cluster projection. Scorers use this only to distinguish model choice
    /// from contract loss; production projects <see cref="Accepted"/> exclusively.</summary>
    public IReadOnlyList<V5AcceptedCandidateDecisionV1> AcceptedBeforeOverlapQuarantine { get; init; } = [];
    public IReadOnlyList<V5AcceptedCandidateDecisionV1> Headings =>
        Accepted.Where(item => item.Kind == V5CandidateDecisionKind.HEADING).ToArray();
}

/// <summary>
/// Layers 2 and 3 of the candidate-authority lane: the model-facing request (candidate texts and
/// read-only relation evidence, nothing else selectable) and the decision binder, which validates only that every id
/// was issued and every kind is in the enum. It never
/// interprets a model-authored locator - there is none to interpret.
/// </summary>
public static class V5CandidateDecisionProtocolV1
{
    public const string Version = "v5-candidate-authority-decision-1";

    public const string SystemPrompt = """
        You are reading a document. The harness has already located every source extent you may select and issued each one a candidate id (C#). Candidates may overlap: one source line may appear alone, trimmed, or joined with the next lines as a longer candidate. Decide, for the candidates in this request, which ones function as a heading at their own location, choosing the candidate whose extent is the complete heading.

        Some candidates only repeat, list, or refer to structure that occurs elsewhere in the document (for example an entry that names a section located elsewhere). Mark those as representations. Relations (R#), when supplied, are read-only reasoning evidence; never return an R#.

        Return one JSON object with exactly this shape: {"decisions":[{"candidate":"C17","kind":"HEADING"},{"candidate":"C18","kind":"REPRESENTATION"}]}. kind is HEADING or REPRESENTATION. Use only issued candidate ids. contextOnlyEvidence and relations are read-only and have no selectable ids. Omit candidates that are neither. Output no other property, text, reason, level, or confidence. Do not use any external answer key.
        """;

    private static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly HashSet<string> DecisionKeys = new(["candidate", "kind"], StringComparer.Ordinal);

    /// <param name="contextOnlyEvidence">Read-only text; issued no id and never selectable.</param>
    /// <param name="documentContext">Optional neutral, read-only document-wide context object.</param>
    public static V5FreeHeadingRequestV1 Compose(V5CandidateUniverseV1 universe,
        IReadOnlyList<(int Page, string Text)> contextOnlyEvidence, JsonElement? documentContext)
    {
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(contextOnlyEvidence);
        if (documentContext is { ValueKind: not JsonValueKind.Object })
            throw new ArgumentException("document context must be a JSON object", nameof(documentContext));
        var candidates = universe.Candidates.Select(item => new { id = item.Id, page = item.Page, text = item.Text }).ToArray();
        var relations = universe.Relations.Select(item => new { id = item.Id, candidate = item.CandidateId, targetPage = item.TargetPage, targetText = item.TargetText }).ToArray();
        var context = contextOnlyEvidence.Select(item => new { page = item.Page, text = item.Text }).ToArray();
        var user = documentContext is { } wide
            ? JsonSerializer.Serialize(new { protocolVersion = Version, candidates, relations, contextOnlyEvidence = context, documentContext = wide }, WireJson)
            : JsonSerializer.Serialize(new { protocolVersion = Version, candidates, relations, contextOnlyEvidence = context }, WireJson);
        return new V5FreeHeadingRequestV1(Version, SystemPrompt, user, Hashing.Sha256(user),
            Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
    }

    /// <summary>
    /// Root-level malformation is fatal (thrown); every decision-contained defect quarantines only that
    /// decision, keyed by its original ordinal. Identical repeats collapse; a candidate given two
    /// different decisions is ambiguous and all of its decisions are quarantined - never resolved by guess.
    /// </summary>
    public static V5CandidateDecisionResultV1 Parse(JsonElement payload, int rawUtf8Bytes, int responseCap, V5CandidateUniverseV1 universe)
    {
        ArgumentNullException.ThrowIfNull(universe);
        if (responseCap < 1 || rawUtf8Bytes < 0 || rawUtf8Bytes > responseCap)
            throw new InvalidOperationException("candidate-decision-response-byte-cap-exceeded");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 1 ||
            !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("candidate-decision-root-invalid");

        var valid = new List<V5AcceptedCandidateDecisionV1>();
        var quarantined = new List<V5QuarantinedCandidateDecisionV1>();
        var ordinal = 0;
        foreach (var decision in decisions.EnumerateArray())
        {
            var reason = Validate(decision, universe, out var accepted, ordinal);
            if (reason is null) valid.Add(accepted!);
            else quarantined.Add(new V5QuarantinedCandidateDecisionV1(ordinal, reason));
            ordinal++;
        }

        var accepted2 = new List<V5AcceptedCandidateDecisionV1>();
        var collapsed = 0;
        foreach (var group in valid.GroupBy(item => item.Candidate.Id, StringComparer.Ordinal))
        {
            var distinct = group.Select(item => item.Kind).Distinct().Count();
            if (distinct == 1)
            {
                accepted2.Add(group.First());
                collapsed += group.Count() - 1;
                continue;
            }
            quarantined.AddRange(group.Select(item => new V5QuarantinedCandidateDecisionV1(item.OriginalOrdinal, "candidate-conflicting-decisions")));
        }
        // An overlapping cluster has no deterministic winner.  Quarantine every accepted heading
        // in that connected cluster; representations and disjoint heading candidates survive.
        var beforeOverlap = accepted2.OrderBy(item => item.OriginalOrdinal).ToArray();
        var headings = accepted2.Where(item => item.Kind == V5CandidateDecisionKind.HEADING).ToArray();
        var conflictIds = OverlapConflictIds(headings);
        if (conflictIds.Count != 0)
        {
            foreach (var conflict in accepted2.Where(item => conflictIds.Contains(item.Candidate.Id)).ToArray())
                quarantined.Add(new V5QuarantinedCandidateDecisionV1(conflict.OriginalOrdinal, "candidate-overlap-conflict"));
            accepted2.RemoveAll(item => conflictIds.Contains(item.Candidate.Id));
        }
        return new V5CandidateDecisionResultV1(ordinal,
            accepted2.OrderBy(item => item.OriginalOrdinal).ToArray(),
            quarantined.OrderBy(item => item.OriginalOrdinal).ToArray(), collapsed)
        { AcceptedBeforeOverlapQuarantine = beforeOverlap };
    }

    private static string? Validate(JsonElement decision, V5CandidateUniverseV1 universe, out V5AcceptedCandidateDecisionV1? accepted, int ordinal)
    {
        accepted = null;
        if (decision.ValueKind != JsonValueKind.Object) return "decision-not-object";
        var properties = decision.EnumerateObject().ToArray();
        if (properties.Any(property => !DecisionKeys.Contains(property.Name)) ||
            properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            return "decision-field-not-in-contract";
        if (!decision.TryGetProperty("candidate", out var candidateElement) || candidateElement.ValueKind != JsonValueKind.String ||
            !universe.TryCandidate(candidateElement.GetString()!, out var candidate))
            return "candidate-not-issued";
        if (!decision.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<V5CandidateDecisionKind>(kindElement.GetString(), ignoreCase: false, out var kind) ||
            !Enum.IsDefined(kind) || kindElement.GetString() != kind.ToString())
            return "decision-kind-not-in-enum";
        accepted = new V5AcceptedCandidateDecisionV1(ordinal, candidate, kind);
        return null;
    }

    private static HashSet<string> OverlapConflictIds(IReadOnlyList<V5AcceptedCandidateDecisionV1> headings)
    {
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        for (var left = 0; left < headings.Count; left++)
        for (var right = left + 1; right < headings.Count; right++)
        {
            if (!Overlaps(headings[left].Candidate.Endpoint, headings[right].Candidate.Endpoint)) continue;
            conflicted.Add(headings[left].Candidate.Id);
            conflicted.Add(headings[right].Candidate.Id);
        }
        return conflicted;
    }

    private static bool Overlaps(BoundClaimEndpoint left, BoundClaimEndpoint right) =>
        left.Parts.Any(a => right.Parts.Any(b => string.Equals(a.Alias, b.Alias, StringComparison.Ordinal) &&
            Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
}
