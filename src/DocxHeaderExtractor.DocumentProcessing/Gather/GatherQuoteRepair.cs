using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>One bounded repair decision, linked to the original refused proposal index.</summary>
namespace DocxHeaderExtractor.DocumentProcessing.Gather;

public sealed record GatherQuoteRepairDecision(int OriginalIndex, string Status, string Reason, GatheredFact? Fact,
    ProposedFact? Attempted = null, int Round = 1);
public sealed record GatherQuoteRepairResult(IReadOnlyList<GatherQuoteRepairDecision> Decisions, IReadOnlyList<FactRefusal> Remaining);

/// <summary>
/// Asks the model to repair refused source quotes, then applies the same exact-quote binder and per-proposal source ownership.
/// Missing, malformed, duplicate or unfixable results stay refused; original rejections remain available to the caller.
/// </summary>
public static class GatherQuoteRepair
{
    public const string Version = "gather-quote-repair-v2";
    public const string System = "Repair rejected source quotes against the exact unit text provided. A heading path is context, not text of the unit. " +
        "Return a JSON object. For each index return exactly one item in {\"repairs\":[{\"index\":0,\"decision\":\"REPLACE|UNSURE\",\"reason\":\"short reason\"," +
        "\"parts\":[{\"unitId\":\"...\",\"quote\":\"exact contiguous text from that unit\",\"role\":\"label|value|text\"}]}]}. " +
        "Use only the unit IDs provided for that index. Do not quote a heading unless it occurs inside that unit's text. " +
        "Use partChecks, which reports whether each original quote has exactly one match in its unit text. " +
        "Preserve supported information without inventing or paraphrasing. If a contextual label is not in the unit, omit that label; " +
        "a uniquely matched content quote may stand alone if it conveys the information. " +
        "if no exact repair is supported, return UNSURE and no parts.";

    public static async Task<GatherQuoteRepairResult> RepairAsync(string goal, IReadOnlyList<FactRefusal> refused,
        IReadOnlyList<GatherSource> sources, IGatherModel model, CancellationToken ct = default,
        IReadOnlyDictionary<int, GatherQuoteRepairDecision>? previous = null, int round = 1)
    {
        var byId = sources.SelectMany(s => s.Units).ToDictionary(u => u.Id, StringComparer.Ordinal);
        var eligible = refused.Where(r => r.Reason is "QUOTE_NOT_FOUND" or "QUOTE_MISSING" or "QUOTE_AMBIGUOUS")
            .Where(r => r.Proposal is { Parts.Count: > 0 } && r.Proposal.Parts.All(p => byId.ContainsKey(p.UnitId))).ToArray();
        var decisions = new List<GatherQuoteRepairDecision>();
        foreach (var chunk in eligible.Chunk(8))
        {
            var requests = chunk.Select(r => new
            {
                index = r.Index, rejection = r.Reason, originalParts = r.Proposal!.Parts,
                partChecks = r.Proposal.Parts.Select(p => new { unitId = p.UnitId, role = p.Role,
                    match = QuoteMatch(byId[p.UnitId].Text, p.Quote) }).ToArray(),
                previousAttempt = previous?.GetValueOrDefault(r.Index) is { } prior
                    ? new { validation = prior.Reason, parts = prior.Attempted?.Parts } : null,
                units = r.Proposal.Parts.Select(p => byId[p.UnitId]).DistinctBy(u => u.Id)
                    .Select(u => new { unitId = u.Id, sourceText = u.Text, headingContext = u.HeadingPath }).ToArray(),
            }).ToArray();
            var user = JsonSerializer.Serialize(new { goal, rejected = requests });
            string response;
            try { response = await model.CompleteAsync(System, user, ct).ConfigureAwait(false); }
            catch (GatherModelFailureException e) when (!e.Retryable)
            {
                decisions.AddRange(chunk.Select(r => new GatherQuoteRepairDecision(r.Index, "UNRESOLVED", "REPAIR_NONRETRYABLE:" + e.Code, null)));
                continue;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                decisions.AddRange(chunk.Select(r => new GatherQuoteRepairDecision(r.Index, "UNRESOLVED", "REPAIR_CALL_FAILED:" + e.GetType().Name, null)));
                continue;
            }
            try
            {
                if (JsonNode.Parse(response) is not JsonObject root || root["repairs"] is not JsonArray list)
                    throw new JsonException("REPAIR_ARRAY_MISSING");
                var grouped = new Dictionary<int, List<JsonObject>>();
                var allowed = chunk.Select(r => r.Index).ToHashSet();
                foreach (var item in list)
                {
                    if (item is not JsonObject obj || obj["index"] is not JsonValue v || !v.TryGetValue<int>(out var id) || !allowed.Contains(id))
                        throw new JsonException("REPAIR_UNKNOWN_INDEX");
                    if (!grouped.TryGetValue(id, out var items)) grouped[id] = items = [];
                    items.Add(obj);
                }
                foreach (var original in chunk)
                {
                    if (!grouped.TryGetValue(original.Index, out var items) || items.Count != 1)
                    {
                        decisions.Add(new(original.Index, "UNRESOLVED", "REPAIR_MISSING_OR_DUPLICATE", null));
                        continue;
                    }
                    var obj = items[0];
                    var decision = obj["decision"]?.GetValue<string>();
                    var reason = obj["reason"]?.GetValue<string>();
                    if (decision == "UNSURE" && !string.IsNullOrWhiteSpace(reason))
                    {
                        decisions.Add(new(original.Index, "UNRESOLVED", "MODEL_UNSURE: " + reason, null));
                        continue;
                    }
                    if (decision != "REPLACE" || string.IsNullOrWhiteSpace(reason) || obj["parts"] is not JsonArray parts || parts.Count == 0)
                    {
                        decisions.Add(new(original.Index, "UNRESOLVED", "REPAIR_NOT_REPLACED", null));
                        continue;
                    }
                    var allowUnits = original.Proposal!.Parts.Select(p => p.UnitId).ToHashSet(StringComparer.Ordinal);
                    var proposed = new List<FactPart>();
                    foreach (var part in parts)
                    {
                        if (part is not JsonObject po || po["unitId"] is not JsonValue id || !id.TryGetValue<string>(out var unitId) ||
                            po["quote"] is not JsonValue q || !q.TryGetValue<string>(out var quote) ||
                            po["role"] is not JsonValue roleValue || !roleValue.TryGetValue<string>(out var role) || !allowUnits.Contains(unitId))
                        { proposed.Clear(); break; }
                        proposed.Add(new FactPart(unitId, -1, -1, role, quote));
                    }
                    var validated = GatherCheck.Check(allowUnits.Select(id => byId[id]).ToArray(), [new ProposedFact(proposed)]);
                    if (validated.Refused.Count > 0 || validated.Kept.Count != 1)
                        decisions.Add(new(original.Index, "UNRESOLVED", "REPAIR_INVALID:" + (validated.Refused.FirstOrDefault()?.Reason ?? "UNKNOWN"), null, new ProposedFact(proposed)));
                    else
                        decisions.Add(new(original.Index, "REPAIRED", reason, validated.Kept[0]));
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                // No partial acceptance from a malformed batch response.
                decisions.RemoveAll(d => chunk.Any(r => r.Index == d.OriginalIndex));
                decisions.AddRange(chunk.Select(r => new GatherQuoteRepairDecision(r.Index, "UNRESOLVED", "REPAIR_FORMAT_INVALID", null)));
            }
        }
        var repairedIds = decisions.Where(d => d.Status == "REPAIRED").Select(d => d.OriginalIndex).ToHashSet();
        return new GatherQuoteRepairResult(decisions.OrderBy(d => d.OriginalIndex).Select(d => d with { Round = round }).ToArray(),
            refused.Where(r => !repairedIds.Contains(r.Index)).ToArray());
    }

    static string QuoteMatch(string source, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return "MISSING";
        var first = source.IndexOf(quote, StringComparison.Ordinal);
        if (first < 0) return "NOT_FOUND";
        return source.IndexOf(quote, first + 1, StringComparison.Ordinal) >= 0 ? "AMBIGUOUS" : "UNIQUE";
    }
}
