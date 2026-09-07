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
      "confidence": 0.0,
      "evidenceCodes": ["SEMANTIC_SECTION_BOUNDARY"]
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
                !item.TryGetProperty("semanticRole", out var role) || role.ValueKind != JsonValueKind.String)
            throw new FormatException("reasoning-response-heading-schema-invalid");
            headings.Add(new ReasoningModelHeadingProposal
            {
                Start = start,
                End = end,
                SemanticRole = role.GetString() ?? "UNKNOWN",
                ProposedLevel = item.TryGetProperty("proposedLevel", out var level) && level.TryGetInt32(out var l) ? l : null,
                Confidence = item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                DecisionEvidence = ReadEvidence(item, "decisionEvidence"),
            });
        }

        return new ReasoningModelResponse(
            headings,
            ReadEvidence(root, "decisionEvidence"));
    }

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
