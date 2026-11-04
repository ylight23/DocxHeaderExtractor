using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A review verdict is advisory; it does not modify or write the selected facts.</summary>
public sealed record GatherReviewVerdict(int Index, string Decision, string Reason);
public sealed record GatherReviewResult(IReadOnlyList<GatherReviewVerdict> Verdicts, string? Error, int PreviouslyRefused);

/// <summary>An independent semantic review against the goal and exact source text. It cannot establish recall.</summary>
public static class GatherSemanticReview
{
    public const string Version = "gather-review-v1";
    public const int MaxRequestBytes = 100_000;
    public const string System = "Review each proposed fact independently against the goal and supplied original source units. " +
        "Decide KEEP only if the proposed information is supported by its sources and belongs in the goal's scope. " +
        "Decide EXCLUDE if clearly out of scope or misleading; decide UNSURE if evidence or grouping is insufficient. " +
        "Consider the role of each person, event, date and the full source context. Do not add information or use external knowledge. " +
        "Return exactly one verdict per id, with a short reason, as JSON: {\"verdicts\":[{\"id\":0,\"decision\":\"KEEP|EXCLUDE|UNSURE\",\"reason\":\"...\"}]}.";

    public static async Task<GatherReviewResult> ReviewAsync(string goal, GatherRun run, IReadOnlyList<GatherSource> sources,
        IGatherModel model, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var units = sources.SelectMany(s => s.Units).ToDictionary(u => u.Id, StringComparer.Ordinal);
        var items = new JsonArray();
        for (var i = 0; i < run.Facts.Count; i++)
        {
            var fact = run.Facts[i];
            var parts = new JsonArray();
            foreach (var part in fact.Parts)
            {
                if (!units.TryGetValue(part.UnitId, out var unit) || part.Start is not int start || part.End is not int end ||
                    start < 0 || end <= start || end > unit.Text.Length || unit.Text[start..end] != part.Text)
                    return AllUnsure(run, "SOURCE_MISMATCH");
                parts.Add(new JsonObject { ["role"] = part.Role, ["quote"] = part.Text, ["unitId"] = unit.Id,
                    ["sourceText"] = unit.Text, ["heading"] = unit.HeadingPath, ["headingCertain"] = unit.PathCertain });
            }
            items.Add(new JsonObject { ["id"] = i, ["parts"] = parts });
        }
        if (run.Facts.Count == 0) return new GatherReviewResult([], null, run.Refused.Count);
        var payload = new JsonObject { ["goal"] = goal, ["candidates"] = items }.ToJsonString();
        if (Encoding.UTF8.GetByteCount(System) + Encoding.UTF8.GetByteCount(payload) > MaxRequestBytes)
            return AllUnsure(run, "REVIEW_REQUEST_TOO_LARGE");
        string response;
        try { response = await model.CompleteAsync(System, payload, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return AllUnsure(run, "REVIEW_CALL_FAILED:" + e.GetType().Name); }
        try
        {
            var root = JsonNode.Parse(response) as JsonObject;
            if (root?["verdicts"] is not JsonArray verdicts) return AllUnsure(run, "REVIEW_FORMAT_INVALID");
            var found = new Dictionary<int, GatherReviewVerdict>();
            var duplicates = new HashSet<int>();
            foreach (var item in verdicts)
            {
                if (item is not JsonObject o || o["id"] is not JsonValue idv || !idv.TryGetValue<int>(out var id) || id < 0 || id >= run.Facts.Count)
                    return AllUnsure(run, "REVIEW_UNKNOWN_ID");
                if (found.ContainsKey(id)) { duplicates.Add(id); continue; }
                var decision = o["decision"] is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : "";
                var reason = o["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : "";
                if (decision is not ("KEEP" or "EXCLUDE" or "UNSURE") || string.IsNullOrWhiteSpace(reason))
                    found[id] = new(id, "UNSURE", "INVALID_VERDICT");
                else found[id] = new(id, decision, reason);
            }
            return new GatherReviewResult(Enumerable.Range(0, run.Facts.Count).Select(i =>
                duplicates.Contains(i) ? new GatherReviewVerdict(i, "UNSURE", "DUPLICATE_VERDICT") :
                found.GetValueOrDefault(i) ?? new GatherReviewVerdict(i, "UNSURE", "MISSING_VERDICT")).ToList(), null, run.Refused.Count);
        }
        catch (JsonException) { return AllUnsure(run, "REVIEW_FORMAT_INVALID"); }
    }

    static GatherReviewResult AllUnsure(GatherRun run, string error) =>
        new(Enumerable.Range(0, run.Facts.Count).Select(i => new GatherReviewVerdict(i, "UNSURE", error)).ToList(), error, run.Refused.Count);
}
