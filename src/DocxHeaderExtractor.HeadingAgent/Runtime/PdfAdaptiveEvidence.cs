using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>Compact, measurement-only projection of the existing PDF facts for visual page packets.</summary>
internal static class PdfAdaptiveEvidence
{
    internal static JsonArray Compact(JsonArray facts)
    {
        var result = new JsonArray();
        foreach (var value in facts)
        {
            var fact = value!.AsObject();
            var item = new JsonObject();
            foreach (var key in new[] { "alias", "page", "block", "segments", "leftEdgeSharedBy", "layout", "style", "leftEdge", "fontSizeVsPageMedianPt" })
                if (fact[key] is { } field) item[key] = field.DeepClone();
            // Repetition is useful, but the full list is unbounded and does not improve a page-local decision.
            if (fact["repeatedText"] is JsonObject repeated)
            {
                var compact = new JsonObject();
                foreach (var key in new[] { "count", "exactCount" }) if (repeated[key] is { } field) compact[key] = field.DeepClone();
                if (repeated["shown"] is JsonArray shown)
                    compact["shown"] = new JsonArray(shown.Take(3).Select(x => x!.DeepClone()).ToArray());
                item["repeatedText"] = compact;
            }
            result.Add(item);
        }
        return result;
    }
}
