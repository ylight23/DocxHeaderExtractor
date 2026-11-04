using System.Text;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// A model's classification of one uncertain section: MERGE into a candidate (its index in <see cref="GatherUncertain.Candidates"/>), NEW (a topic
/// of its own, to become a new heading), or UNSURE. Advice for the reader only: it is never applied or written by the code.
/// </summary>
public sealed record GatherUncertainVerdict(int Index, string Decision, int? Candidate, string Reason);

public sealed record GatherUncertainReviewResult(IReadOnlyList<GatherUncertainVerdict> Verdicts, string? Error);

/// <summary>Asks a model to classify the sections the heading chain could not place. Reads only the text it is given; changes nothing.</summary>
public static class GatherUncertainReview
{
    public const string Version = "gather-uncertain-v1";
    public const int MaxRequestBytes = 100_000;
    public const string System =
        "A source document is being merged into a target document by heading. For each item you get a source heading, its parent heading, the " +
        "content under it, and candidate headings of the target with the content they already have. Decide MERGE if the source section is about " +
        "the same topic as one candidate (give that candidate's number); NEW if it is a different topic that should become its own heading; " +
        "UNSURE if the given text is not enough to decide. Use only the given text, no outside knowledge. Write the reason in the language of the " +
        "documents, in one short sentence. Return JSON only: " +
        "{\"verdicts\":[{\"id\":0,\"decision\":\"MERGE|NEW|UNSURE\",\"candidate\":0,\"reason\":\"...\"}]} with exactly one verdict per id.";

    public static async Task<GatherUncertainReviewResult> ReviewAsync(IReadOnlyList<GatherUncertain> items, IGatherModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(model);
        if (items.Count == 0) return new([], null);
        var list = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            var u = items[i];
            var candidates = new JsonArray();
            for (var c = 0; c < u.Candidates.Count; c++)
                candidates.Add(new JsonObject
                {
                    ["number"] = c, ["heading"] = u.Candidates[c].Heading, ["path"] = u.Candidates[c].Chain,
                    ["content"] = new JsonArray(u.Candidates[c].Content.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                });
            list.Add(new JsonObject
            {
                ["id"] = i, ["sourceHeading"] = u.SourceHeading, ["sourceParent"] = u.SourceParent,
                ["content"] = new JsonArray(u.Facts.Select(f => (JsonNode)JsonValue.Create(f.Render())!).ToArray()),
                ["candidates"] = candidates,
            });
        }
        // Vietnamese text is sent as written, not as \u escapes: the model reads the words, and the request is smaller.
        var payload = new JsonObject { ["items"] = list }.ToJsonString(new global::System.Text.Json.JsonSerializerOptions
            { Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (Encoding.UTF8.GetByteCount(System) + Encoding.UTF8.GetByteCount(payload) > MaxRequestBytes) return AllUnsure(items, "REQUEST_TOO_LARGE");
        string response;
        try { response = await model.CompleteAsync(System, payload, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return AllUnsure(items, "CALL_FAILED:" + e.GetType().Name); }
        try
        {
            if (JsonNode.Parse(response) is not JsonObject root || root["verdicts"] is not JsonArray verdicts) return AllUnsure(items, "FORMAT_INVALID");
            var found = new Dictionary<int, GatherUncertainVerdict>();
            foreach (var node in verdicts)
            {
                if (node is not JsonObject o || o["id"] is not JsonValue idv || !idv.TryGetValue<int>(out var id) || id < 0 || id >= items.Count) continue;
                if (found.ContainsKey(id)) { found[id] = new(id, "UNSURE", null, "DUPLICATE_VERDICT"); continue; }
                var decision = o["decision"] is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : "";
                var reason = o["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : "";
                int? candidate = o["candidate"] is JsonValue cv && cv.TryGetValue<int>(out var cn) ? cn : null;
                found[id] = decision switch
                {
                    _ when string.IsNullOrWhiteSpace(reason) => new(id, "UNSURE", null, "INVALID_VERDICT"),
                    "MERGE" when candidate is not { } n || n < 0 || n >= items[id].Candidates.Count => new(id, "UNSURE", null, "INVALID_CANDIDATE: " + reason),
                    "MERGE" => new(id, "MERGE", candidate, reason),
                    "NEW" or "UNSURE" => new(id, decision, null, reason),
                    _ => new(id, "UNSURE", null, "INVALID_VERDICT"),
                };
            }
            return new(Enumerable.Range(0, items.Count).Select(i => found.GetValueOrDefault(i) ?? new(i, "UNSURE", null, "MISSING_VERDICT")).ToList(), null);
        }
        catch (global::System.Text.Json.JsonException) { return AllUnsure(items, "FORMAT_INVALID"); }
    }

    static GatherUncertainReviewResult AllUnsure(IReadOnlyList<GatherUncertain> items, string error) =>
        new(Enumerable.Range(0, items.Count).Select(i => new GatherUncertainVerdict(i, "UNSURE", null, error)).ToList(), error);
}
