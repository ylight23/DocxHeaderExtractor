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
  "documentSummary": "optional short summary",
  "headings": [
    {
      "sourceId": "exact supplied sourceId",
      "headingSpan": { "start": 0, "end": 12 },
      "text": "exact source substring",
      "semanticRole": "DOCUMENT_TITLE|PART|CHAPTER|SECTION|SUBSECTION|ARTICLE|CLAUSE_HEADING|ANNEX_HEADING|LOCAL_INDEX_TITLE|AGENDA_NAVIGATION_HEADING|TOC_ENTRY|FRONT_MATTER|CONTENT_HEADING|OTHER_STRUCTURAL_LABEL",
      "proposedLevel": 1,
      "proposedParent": "optional structural element id",
      "confidence": 0.0,
      "decisionEvidence": [
        { "evidenceType": "semantic", "sourceReference": "sourceId", "shortEvidenceCode": "SEMANTIC_SECTION_BOUNDARY" }
      ]
    }
  ],
  "decisionEvidence": []
}

All spans are UTF-16 offsets into the exact rawText of the named sourceId.
The response is an untrusted proposal and will be hard-validated locally.
""";

    public static string BuildUser(ReasoningContextSegment segment, bool shadow) =>
        $"""
TASK={Version}
route={(shadow ? "REASONING_PRESERVING_SHADOW" : "MODEL_CAPABILITY_CEILING")}
The candidateHint fields are optional attention hints only. They do not restrict visibility.
Inspect all SOURCE_OCCURRENCE blocks below, including rows whose candidateHint.candidate is false.

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
        var headings = new List<ReasoningHeadingProposal>();
        if (root.TryGetProperty("headings", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("sourceId", out var sourceId) ||
                    !item.TryGetProperty("headingSpan", out var span) ||
                    !item.TryGetProperty("text", out var text) ||
                    !item.TryGetProperty("semanticRole", out var role))
                    continue;
                if (!TrySpan(span, out var start, out var end)) continue;
                headings.Add(new ReasoningHeadingProposal
                {
                    SourceId = sourceId.GetString() ?? "",
                    HeadingSpan = new DocxHeaderExtractor.Core.Models.StructuralSpan(start, end),
                    Text = text.GetString() ?? "",
                    SemanticRole = role.GetString() ?? "UNKNOWN",
                    ProposedLevel = item.TryGetProperty("proposedLevel", out var level) && level.TryGetInt32(out var l) ? l : null,
                    ProposedParent = item.TryGetProperty("proposedParent", out var parent) ? parent.GetString() : null,
                    Confidence = item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    DecisionEvidence = ReadEvidence(item, "decisionEvidence"),
                });
            }
        }
        return new ReasoningModelResponse(
            root.TryGetProperty("documentSummary", out var summary) ? summary.GetString() : null,
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

    private static bool TrySpan(JsonElement value, out int start, out int end)
    {
        start = end = 0;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("start", out var s) && s.TryGetInt32(out start) &&
            value.TryGetProperty("end", out var e) && e.TryGetInt32(out end);
    }

    private static string ExtractObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("reasoning-response-json-missing");
        return raw[start..(end + 1)];
    }
}
