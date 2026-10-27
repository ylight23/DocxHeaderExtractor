using System.Text.Json.Nodes;
using DocxHeaderExtractor.HeadingAgent.Formats;

namespace DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>Scopes measured page geometry to aliases already shown by the request. No membership authority.</summary>
internal static class PdfRasterEvidence
{
    internal const string Prompt = "\nOptional pageGeometryEvidence contains OpenCV image measurements (ink, frames, whitespace) and relations derived from mapped PDF parser boxes. These are source observations, not heading or table labels. Use with text and context; geometry alone does not determine membership. UNAVAILABLE means geometry could not be measured, not that the page contains no structure.";

    internal static string Attach(string payload, IAgentSource source)
    {
        if (source is not AgentSource pdf || !pdf.RasterGeometryEnabled) return payload;
        var packet = JsonNode.Parse(payload)!.AsObject();
        var universe = pdf.Lines.ToDictionary(l => l.Alias, StringComparer.Ordinal);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var item in obj) Visit(item.Value);
            else if (node is JsonArray array) foreach (var item in array) Visit(item);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text) && universe.ContainsKey(text)) shown.Add(text);
        }
        Visit(packet);
        var pages = new JsonArray();
        foreach (var page in shown.Select(a => universe[a].Page ?? 0).Distinct().Order())
        {
            if (!pdf.RasterGeometry.TryGetValue(page, out var measurements)) continue;
            var scoped = measurements.DeepClone().AsObject();
            if (scoped["occurrences"] is JsonArray occurrences)
            {
                var items = occurrences.Where(o => shown.Contains(o!["alias"]!.GetValue<string>())).Select(o => o!.DeepClone()).ToArray();
                foreach (var item in items)
                    foreach (var field in new[] { "verticalOverlapPeers", "nearestLeftAlignedPeers" })
                        if (item[field] is JsonArray peers)
                            item[field] = new JsonArray(peers.Where(p => shown.Contains(p!.GetValue<string>())).Select(p => p!.DeepClone()).ToArray());
                scoped["pageOccurrenceCount"] = occurrences.Count;
                scoped["scopeOccurrenceCount"] = items.Length;
                scoped["occurrences"] = new JsonArray(items);
            }
            pages.Add(scoped);
        }
        packet["pageGeometryEvidence"] = new JsonObject
        {
            ["version"] = DocxHeaderExtractor.Sources.Pdf.PdfGeometryMeasurements.Version,
            ["semanticLabels"] = "NONE", ["scope"] = "aliases already shown in this packet; page-wide measurements remain marked as such",
            ["pages"] = pages
        };
        if (pdf.CompactGeometry) Compact(packet["pageGeometryEvidence"]!.AsObject());
        return packet.ToJsonString(AgentUnit.ReviewJson);
    }

    /// <summary>Lossless column encoding: measurements, nulls and peer identities are retained.
    /// The full object representation remains in the source store and evidence tools.</summary>
    internal static void Compact(JsonObject evidence)
    {
        evidence["encoding"] = "PDF_GEOMETRY_COLUMNS_V1";
        evidence["encodingNote"] = "Each occurrence row has values in occurrenceColumns order. Each page inherits sharedPageFacts; page fields override shared values. Measurements and coordinates are unchanged.";
        var pages = evidence["pages"]!.AsArray().OfType<JsonObject>().ToArray();
        var shared = new JsonObject();
        foreach (var field in new[] { "version", "measurementBasis", "semanticLabels", "coordinateOrigin", "transform", "renderer" })
        {
            if (pages.Length == 0 || !pages[0].ContainsKey(field)) continue;
            var value = pages[0][field];
            if (!pages.All(p => p.ContainsKey(field) && JsonNode.DeepEquals(value, p[field]))) continue;
            shared[field] = value?.DeepClone();
            foreach (var page in pages) page.Remove(field);
        }
        evidence["sharedPageFacts"] = shared;
        foreach (var page in pages)
        {
            if (page["occurrences"] is not JsonArray { Count: > 0 } occurrences) continue;
            var columns = occurrences[0]!.AsObject().Select(p => p.Key).ToArray();
            // Unknown future fields are preserved; heterogeneous shapes keep their object encoding.
            if (!occurrences.All(o => o is JsonObject obj && obj.Select(p => p.Key).SequenceEqual(columns))) continue;
            page["occurrenceColumns"] = new JsonArray(columns.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
            page["occurrences"] = new JsonArray(occurrences.Select(o => (JsonNode)new JsonArray(
                columns.Select(c => o![c]?.DeepClone()).ToArray())).ToArray());
        }
    }
}
