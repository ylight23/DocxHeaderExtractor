using System.Text.Json;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// Line-level check of a streamed (SSE) reply for the V6 agent route (D4.11d). <see cref="P7F1QSse"/> reads only "data:" lines and flags an
/// error only when a payload has an "error" key, so "event: error" followed by a payload without that key would pass it. This guard is
/// separate on purpose: F1Q's historical parsing is unchanged. In every real F1Q stream (1,443 raw bodies surveyed) the only lines are
/// "data:" events, blank lines and the ": OPENROUTER PROCESSING" comment, every data event is an object with choices (or error), and
/// nothing follows [DONE]; anything else is refused here. Returns a failure code, or null.
/// </summary>
internal static class P7F1V6SseGuard
{
    public static string? Check(string raw)
    {
        var done = false;
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ':') continue;                       // blank separator, or a comment such as the keep-alive
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            if (field == "event")
            {
                var name = colon < 0 ? "" : line[(colon + 1)..].Trim();
                return name.Equals("error", StringComparison.OrdinalIgnoreCase) ? "STREAM_ERROR_EVENT" : "STREAM_NAMED_EVENT";
            }
            if (field != "data") return "STREAM_FIELD_UNKNOWN";
            var data = colon < 0 ? "" : line[(colon + 1)..].Trim();
            if (data == "[DONE]") { done = true; continue; }
            if (done) return "STREAM_DATA_AFTER_DONE";
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }                                    // malformed JSON is reported by the assembly check
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return "STREAM_EVENT_SHAPE_UNKNOWN";
                if (!root.TryGetProperty("choices", out _) && !root.TryGetProperty("error", out _)) return "STREAM_EVENT_SHAPE_UNKNOWN";
            }
        }
        return null;
    }
}
