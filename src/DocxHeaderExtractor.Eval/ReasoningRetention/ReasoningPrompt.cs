using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

public static class ReasoningPrompt
{
    public const string Version = "a99-reasoning-preserving-v1";

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
  "schemaVersion": "a99-reasoning-bounded-v1",
  "requestId": "exact requestId supplied by the harness",
  "semanticPassId": "exact semanticPassId supplied by the harness",
  "ownedRange": { "start": 0, "end": 12 },
  "complete": true,
  "headings": [
    {
      "sourceId": "exact supplied sourceId",
      "start": 0,
      "end": 12,
      "semanticRole": "DOCUMENT_TITLE|PART|CHAPTER|SECTION|SUBSECTION|ARTICLE|CLAUSE_HEADING|ANNEX_HEADING|LOCAL_INDEX_TITLE|AGENDA_NAVIGATION_HEADING|TOC_ENTRY|FRONT_MATTER|CONTENT_HEADING|OTHER_STRUCTURAL_LABEL",
      "proposedLevel": 1,
      "proposedParentKey": "optional structural element id",
      "confidence": 0.0,
      "evidenceCodes": ["SEMANTIC_SECTION_BOUNDARY"]
    }
  ],
  "decisionEvidence": []
}

All spans are UTF-16 offsets into the exact rawText of the named sourceId.
The response may omit duplicated heading text; the harness materializes exact text from rawText.
Emit headings only when the source ordinal belongs to ownedRange. The full visible context may
contain other source occurrences for global reasoning, but those occurrences are not owned by
this response.
The response is an untrusted proposal and will be hard-validated locally.

Use the exact canonical sourceId value from each SOURCE_OCCURRENCE. The sourceOccurrenceId
is an explicit context alias only; do not invent or shorten source identities.
""";

    public static string BuildUser(ReasoningContextSegment segment, bool shadow) =>
        $"""
TASK={Version}
route={(shadow ? "REASONING_PRESERVING_SHADOW" : "MODEL_CAPABILITY_CEILING")}
The candidateHint fields are optional attention hints only. They do not restrict visibility.
Inspect all SOURCE_OCCURRENCE blocks below, including rows whose candidateHint.candidate is false.
The visible source ordinal range is {segment.VisibleStartOrdinal?.ToString() ?? "empty"}..{segment.VisibleEndOrdinal?.ToString() ?? "empty"}.
The only output-owned range is {segment.OwnedStartOrdinal?.ToString() ?? "empty"}..{segment.OwnedEndOrdinal?.ToString() ?? "empty"}.
Return an ownedRange with those exact inclusive endpoints, and emit no heading outside it.

{segment.Text}
""";

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
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("complete", out var complete) ||
            complete.ValueKind != JsonValueKind.True)
            throw new FormatException("reasoning-response-complete-marker-missing");

        if (!root.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("reasoning-response-headings-missing");

        var headings = new List<ReasoningHeadingProposal>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("sourceId", out var sourceId) || sourceId.ValueKind != JsonValueKind.String ||
                !TrySpan(item, out var start, out var end) ||
                !item.TryGetProperty("semanticRole", out var role) || role.ValueKind != JsonValueKind.String)
                throw new FormatException("reasoning-response-heading-schema-invalid");
            headings.Add(new ReasoningHeadingProposal
            {
                SourceId = sourceId.GetString() ?? "",
                HeadingSpan = new DocxHeaderExtractor.Core.Models.StructuralSpan(start, end),
                Text = item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? "" : "",
                SemanticRole = role.GetString() ?? "UNKNOWN",
                ProposedLevel = item.TryGetProperty("proposedLevel", out var level) && level.TryGetInt32(out var l) ? l : null,
                ProposedParent = item.TryGetProperty("proposedParentKey", out var parentKey)
                    ? parentKey.GetString()
                    : item.TryGetProperty("proposedParent", out var parent) ? parent.GetString() : null,
                Confidence = item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                DecisionEvidence = ReadEvidence(item, "decisionEvidence"),
            });
        }

        ReasoningOrdinalRange? ownedRange = null;
        if (root.TryGetProperty("ownedRange", out var range) && TrySpan(range, out var ownedStart, out var ownedEnd))
            ownedRange = new ReasoningOrdinalRange(ownedStart, ownedEnd);
        return new ReasoningModelResponse(
            root.TryGetProperty("documentSummary", out var summary) ? summary.GetString() : null,
            headings,
            ReadEvidence(root, "decisionEvidence"),
            OwnedRange: ownedRange,
            Complete: true,
            RequestId: root.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null,
            SemanticPassId: root.TryGetProperty("semanticPassId", out var semanticPassId) ? semanticPassId.GetString() : null,
            AttemptId: root.TryGetProperty("attemptId", out var attemptId) ? attemptId.GetString() : null);
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

    private static bool TrySpan(JsonElement value, out int start, out int end)
    {
        start = end = 0;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty("headingSpan", out var headingSpan))
            value = headingSpan;
        return value.TryGetProperty("start", out var s) && s.TryGetInt32(out start) &&
            value.TryGetProperty("end", out var e) && e.TryGetInt32(out end);
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
