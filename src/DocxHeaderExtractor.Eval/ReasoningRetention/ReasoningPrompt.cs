using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public static class ReasoningPrompt
{
    public const string Version = "a99-reasoning-preserving-v2";

    public const string System = """
You are a document-structure extraction evaluator.
Read the supplied source occurrences as a whole. Identify semantic structural headings
and their hierarchy, including headings whose style is Normal and excluding body prose,
metadata, and navigation-only material when its role is distinct.

Formatting, numbering, layout, and candidate signals are evidence only. They do not decide
whether a source occurrence is visible or semantically a heading. Inspect every supplied
source occurrence. Never invent text or a source identity. Return only compact JSON evidence,
never private chain-of-thought.

Return exactly:
{
  "schemaVersion": "a99-reasoning-bounded-v2",
  "headings": [
    {
      "start": 0,
      "end": 12,
      "semanticRole": "DOCUMENT_TITLE|PART|CHAPTER|SECTION|SUBSECTION|ARTICLE|CLAUSE_HEADING|ANNEX_HEADING|LOCAL_INDEX_TITLE|AGENDA_NAVIGATION_HEADING|TOC_ENTRY|FRONT_MATTER|CONTENT_HEADING|OTHER_STRUCTURAL_LABEL",
      "proposedLevel": 1,
      "proposedParentLocalId": null,
      "confidence": 0.0,
      "decisionEvidence": []
    }
  ],
  "decisionEvidence": []
}

start and end are UTF-16 offsets relative to the harness-owned output range, not document offsets.
The harness adds the owned range start and binds the result to the canonical source. The response
must not contain sourceId, sourceOccurrenceId, source paths, request ids, attempt ids, semantic pass
ids, parent ids, or a completion marker. The response may omit duplicated heading text; the harness
materializes exact text from parser-owned rawText. The full visible context may contain other
source occurrences for global reasoning, but those occurrences are not owned by this response.
The response is an untrusted proposal and will be hard-validated locally.
semanticRole, proposedLevel, proposedParentLocalId, confidence, and decisionEvidence are required
for every heading. semanticRole must use the closed vocabulary shown above. proposedParentLocalId
is null when no parent is proposed; it is local response metadata, never a source identity.
""";

    public static string BuildUser(
        ReasoningContextSegment segment,
        bool shadow,
        ReasoningOwnedOutputScope? ownedOutputScope = null)
    {
        var scope = ownedOutputScope ?? (segment.OwnedSourceOccurrenceId is not null &&
            segment.OwnedStartCharacter is { } start && segment.OwnedEndCharacter is { } end
            ? new ReasoningOwnedOutputScope
            {
                CanonicalSourceId = segment.OwnedSourceOccurrenceId,
                SourceOccurrenceId = segment.OwnedSourceOccurrenceId,
                RawTextLength = end,
                OwnedStart = start,
                OwnedEnd = end,
            }
            : null);
        return $"""
TASK={Version}
route={(shadow ? "REASONING_PRESERVING_SHADOW" : "MODEL_CAPABILITY_CEILING")}
The candidateHint fields are optional attention hints only. They do not restrict visibility.
Inspect all SOURCE_OCCURRENCE blocks below, including rows whose candidateHint.candidate is false.
The visible source ordinal range is {segment.VisibleStartOrdinal?.ToString() ?? "empty"}..{segment.VisibleEndOrdinal?.ToString() ?? "empty"}.
The harness-owned output scope is source={scope?.CanonicalSourceId ?? "none"};
ownedSourceOccurrenceId={scope?.SourceOccurrenceId ?? "none"};
rawTextLength={scope?.RawTextLength.ToString() ?? "0"};
ownedCharacters={scope?.OwnedStart.ToString() ?? "empty"}..{scope?.OwnedEnd.ToString() ?? "empty"}.
Return local start/end offsets within that owned character range only.
The headings array is scoped to exactly that one owned source occurrence. Emit headings only
whose text is contained in that owned occurrence's raw text and whose offsets refer to that
occurrence; never project a heading from another visible SOURCE_OCCURRENCE into this scope.
Other visible occurrences are context for reasoning only and must contribute zero output items
to this response. If the owned occurrence is not a heading, return an empty headings array.
Emit each exact local span at most once. A source occurrence may have zero or one heading
proposal in this response; never repeat the same start/end pair, even with a different role.
If a boundary is uncertain, omit the proposal rather than emitting a duplicate.

{segment.Text}
""";
    }

    public static string BuildRequestId(string documentId, string segmentId, string configurationSignature) =>
        $"{Version}:{documentId}:{segmentId}:{configurationSignature}";

    /// <summary>
    /// Batched-throughput instruction supplement: several owned occurrences are addressed by a
    /// local ownedIndex (never a source identity) in one request/response, instead of one
    /// occurrence per call. Append after <see cref="System"/>; never replaces it.
    /// </summary>
    public const string BatchedInstruction = """

Batched throughput mode: this request owns SEVERAL source occurrences at once, each introduced by
an OWNED_OCCURRENCE[ownedIndex] marker below. Every heading you return must include "ownedIndex"
naming exactly which owned occurrence it belongs to. start/end are local UTF-16 offsets within
that one owned occurrence's raw text only -- never offsets into the whole request or into another
occurrence. An occurrence may have zero, one, or more than one heading. Occurrences shown only as
plain SOURCE_OCCURRENCE context (no OWNED_OCCURRENCE marker) are for reasoning only and must
contribute zero output items.
""";

    public static string BuildUserBatched(
        ReasoningContextSegment segment,
        bool shadow,
        IReadOnlyList<ReasoningOwnedOutputScope> ownedScopes)
    {
        var mapping = string.Join("\n", ownedScopes.Select(s =>
            $"OWNED_OCCURRENCE[{s.OwnedIndex}] = sourceOccurrenceId {s.SourceOccurrenceId} (rawTextLength={s.RawTextLength})"));
        return $"""
TASK={Version}
route={(shadow ? "REASONING_PRESERVING_SHADOW" : "MODEL_CAPABILITY_CEILING")}
The candidateHint fields are optional attention hints only. They do not restrict visibility.
Inspect all SOURCE_OCCURRENCE blocks below, including rows whose candidateHint.candidate is false.
The visible source ordinal range is {segment.VisibleStartOrdinal?.ToString() ?? "empty"}..{segment.VisibleEndOrdinal?.ToString() ?? "empty"}.
This request owns {ownedScopes.Count} of the source occurrences shown below (identified by their
sourceOccurrenceId field), each assigned a local ownedIndex:
{mapping}
Every heading you return must include "ownedIndex" naming exactly which of those owned occurrences
it belongs to. start/end are local UTF-16 offsets within that one owned occurrence's own rawText
only -- never offsets into another occurrence. An owned occurrence may have zero, one, or more than
one heading. Any other visible SOURCE_OCCURRENCE not listed above is context for reasoning only and
must contribute zero output items. Emit each exact (ownedIndex,start,end) triple at most once; never
repeat it, even with a different role. If a boundary is uncertain, omit the proposal rather than
emitting a duplicate.

{segment.Text}
""";
    }
}

public static class ReasoningModelResponseParser
{
    public static ReasoningModelResponse Parse(string raw)
    {
        var json = ExtractObject(raw);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var forbidden in new[]
        {
            "requestId", "attemptId", "semanticPassId", "sourceId", "sourceOccurrenceId", "ownedRange"
        })
        {
            if (root.TryGetProperty(forbidden, out _))
                throw new FormatException($"reasoning-response-control-identity-forbidden:{forbidden}");
        }
        if (!root.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("reasoning-response-headings-missing");

        var headings = new List<ReasoningModelHeadingProposal>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                item.TryGetProperty("sourceId", out _) ||
                item.TryGetProperty("sourceOccurrenceId", out _) ||
                item.TryGetProperty("headingSpan", out _) ||
                item.TryGetProperty("proposedParent", out _) ||
                item.TryGetProperty("proposedParentId", out _) ||
                !TryLocalSpan(item, out var start, out var end) ||
                !item.TryGetProperty("semanticRole", out var role) || role.ValueKind != JsonValueKind.String ||
                !IsAllowedSemanticRole(role.GetString()) ||
                !item.TryGetProperty("proposedLevel", out var level) ||
                !(level.ValueKind == JsonValueKind.Null || level.TryGetInt32(out _)) ||
                !item.TryGetProperty("proposedParentLocalId", out var parent) ||
                !(parent.ValueKind == JsonValueKind.Null || parent.ValueKind == JsonValueKind.String) ||
                !item.TryGetProperty("confidence", out var confidence) || !confidence.TryGetDouble(out var score) ||
                !item.TryGetProperty("decisionEvidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
            throw new FormatException("reasoning-response-heading-schema-invalid");
            headings.Add(new ReasoningModelHeadingProposal
            {
                Start = start,
                End = end,
                SemanticRole = role.GetString() ?? "UNKNOWN",
                ProposedLevel = level.ValueKind == JsonValueKind.Null ? null : level.GetInt32(),
                ProposedParentLocalId = parent.ValueKind == JsonValueKind.String ? parent.GetString() : null,
                Confidence = score,
                DecisionEvidence = ReadEvidence(item, "decisionEvidence"),
                OwnedIndex = item.TryGetProperty("ownedIndex", out var ownedIndex) && ownedIndex.ValueKind == JsonValueKind.Number
                    ? ownedIndex.GetInt32() : null,
            });
        }

        return new ReasoningModelResponse(
            headings,
            ReadEvidence(root, "decisionEvidence"));
    }

    private static bool IsAllowedSemanticRole(string? role) => role?.Trim().ToUpperInvariant() is
        "DOCUMENT_TITLE" or "PART" or "CHAPTER" or "SECTION" or "SUBSECTION" or "ARTICLE" or
        "CLAUSE_HEADING" or "ANNEX_HEADING" or "LOCAL_INDEX_TITLE" or "AGENDA_NAVIGATION_HEADING" or
        "TOC_ENTRY" or "FRONT_MATTER" or "CONTENT_HEADING" or "OTHER_STRUCTURAL_LABEL";

    private static IReadOnlyList<ReasoningDecisionEvidence> ReadEvidence(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new ReasoningDecisionEvidence(
                item.TryGetProperty("evidenceType", out var type) ? type.GetString() ?? "" : "",
                item.TryGetProperty("sourceReference", out var source) ? source.GetString() ?? "" : "",
                item.TryGetProperty("shortEvidenceCode", out var code) ? code.GetString() ?? "" : ""))
            .ToArray();
    }

    private static bool TryLocalSpan(JsonElement value, out int start, out int end)
    {
        start = end = 0;
        if (value.TryGetProperty("localStart", out var localStart) &&
            localStart.TryGetInt32(out start) &&
            value.TryGetProperty("localEnd", out var localEnd) &&
            localEnd.TryGetInt32(out end))
            return true;
        return value.TryGetProperty("start", out var plainStart) &&
            plainStart.TryGetInt32(out start) &&
            value.TryGetProperty("end", out var plainEnd) &&
            plainEnd.TryGetInt32(out end);
    }

    private static string ExtractObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("reasoning-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new JsonException("reasoning-response-json-incomplete");
        return raw[start..(end + 1)];
    }
}
