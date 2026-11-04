using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.DocumentProcessing.Gather;

// Qualification: re-check recorded refused proposals against the current checker. Calls no model: the units are rebuilt from the payloads the
// model was sent (id, heading path, heading text, row label, column header, text), so the replay sees exactly what the model saw.
internal static class MonthlyCheckReplay
{
    public static int Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("monthly-check-replay <run-dir> <targeted-repair.json> <output.json>");
        var run = Path.GetFullPath(args[1]);
        var units = UnitsFromCalls(Directory.GetFiles(Path.Combine(run, "calls"), "*.request.json"));
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var stillOpen = new HashSet<string>(StringComparer.Ordinal);
        using (var targeted = JsonDocument.Parse(File.ReadAllText(args[2])))
            foreach (var result in targeted.RootElement.GetProperty("results").EnumerateArray())
                foreach (var remaining in result.GetProperty("Repair").GetProperty("Remaining").EnumerateArray())
                    stillOpen.Add(result.GetProperty("Index").GetInt32() + ":" + remaining.GetProperty("Index").GetInt32());

        var rows = new List<Row>();
        foreach (var file in Directory.GetFiles(run, "group-*.json").Order(StringComparer.Ordinal))
        {
            using var group = JsonDocument.Parse(File.ReadAllText(file));
            var index = group.RootElement.GetProperty("Index").GetInt32();
            var topic = group.RootElement.GetProperty("Topic").GetString()!;
            var refused = group.RootElement.GetProperty("Scope").GetProperty("Selection").GetProperty("Refused").Deserialize<FactRefusal[]>(opts)!;
            foreach (var refusal in refused)
            {
                var source = refusal.Proposal.Parts[0].UnitId.Split('|')[0];
                var result = GatherCheck.Check(units[source], [refusal.Proposal]);
                var kept = result.Kept.SingleOrDefault();
                rows.Add(new Row(index, topic, refusal.Index, stillOpen.Contains(index + ":" + refusal.Index), refusal.Reason,
                    refusal.Proposal.Parts.Select(p => $"{p.Role} @{p.UnitId}: {p.Quote}").ToArray(),
                    kept is null ? "REFUSED" : "KEPT", result.Refused.SingleOrDefault()?.Reason, kept?.Render(),
                    kept?.Parts.Select(p => p.UnitId).Distinct().ToArray(), kept?.Flags.ToArray()));
            }
        }
        File.WriteAllText(Path.GetFullPath(args[3]), JsonSerializer.Serialize(new
        {
            run, prompt = GatherPrompt.Version, modelCalls = 0, unitsRebuilt = units.ToDictionary(x => x.Key, x => x.Value.Count),
            refused = rows.Count, nowKept = rows.Count(r => r.Now == "KEPT"), rows,
        }, opts));
        Console.WriteLine($"refused={rows.Count} nowKept={rows.Count(r => r.Now == "KEPT")} stillOpenBefore={rows.Count(r => r.UnresolvedAfterTargetedRepair)} " +
            $"stillOpenNowKept={rows.Count(r => r.UnresolvedAfterTargetedRepair && r.Now == "KEPT")}");
        return 0;
    }

    /// <summary>The units of each source as sent in the selection requests, in document order.</summary>
    static Dictionary<string, List<GatherUnit>> UnitsFromCalls(IEnumerable<string> requests)
    {
        var byId = new Dictionary<string, GatherUnit>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            using var call = JsonDocument.Parse(File.ReadAllText(request));
            if (!call.RootElement.GetProperty("system").GetString()!.StartsWith("Select", StringComparison.Ordinal)) continue;
            var user = call.RootElement.GetProperty("user").GetString()!;
            using var payload = JsonDocument.Parse(user[user.IndexOf("{\"units\"", StringComparison.Ordinal)..]);
            foreach (var u in payload.RootElement.GetProperty("units").EnumerateArray())
            {
                var id = u.GetProperty("unitId").GetString()!;
                if (byId.ContainsKey(id)) continue;
                var text = u.GetProperty("text").GetString()!;
                var headingText = Str(u, "headingText");
                var heading = headingText is not null && text.StartsWith(headingText, StringComparison.Ordinal);
                var location = id[(id.IndexOf('|') + 1)..];
                byId[id] = new GatherUnit(id, id.Split('|')[0], location, Str(u, "rowLabel") is null ? "body" : "table", false, heading, text,
                    u.GetProperty("heading").GetString() ?? "", "pipeline", u.GetProperty("headingCertain").GetBoolean(), null, null, false,
                    Str(u, "rowLabel"), Str(u, "rowLabelUnitId"), Str(u, "columnHeader"), Str(u, "columnHeaderUnitId"), id)
                { HeadingEnd = heading ? headingText!.Length : null };
            }
        }
        return byId.Values.GroupBy(u => u.SourceId).ToDictionary(g => g.Key, g => g.OrderBy(u => OrderKey(u.Location), StringComparer.Ordinal).ToList());
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string OrderKey(string location) => Regex.Replace(location, @"\d+", m => m.Value.PadLeft(6, '0'));

    sealed record Row(int Group, string Topic, int ProposalIndex, bool UnresolvedAfterTargetedRepair, string WasReason, string[] Parts,
        string Now, string? NowReason, string? KeptText, string[]? KeptUnits, string[]? Notes);
}
