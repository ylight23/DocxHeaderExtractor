using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

// Issue #9 D4.12 (offline, no network): reads run-summary.json files written by d411j-run and applies the repetition vote to each arm and segment.
// Prints aliases and counts only, never line text. Also reports how well the odd repetitions and the even repetitions agree with each other.
internal static class D412Vote
{
    public static void Run(string[] summaryPaths)
    {
        foreach (var path in summaryPaths)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            Console.WriteLine($"== {root.GetProperty("Arm").GetString()} ({Path.GetFileName(Path.GetDirectoryName(path))})");
            var units = root.GetProperty("units").EnumerateArray().ToList();
            foreach (var seg in units.Select(u => u.GetProperty("UnitId").GetString()!).Distinct().Order())
            {
                var mine = units.Where(u => u.GetProperty("UnitId").GetString() == seg).OrderBy(u => u.GetProperty("Rep").GetInt32()).ToList();
                var complete = mine.Where(u => u.GetProperty("Status").GetString() == "COMPLETE").ToList();
                var order = complete.SelectMany(u => u.GetProperty("decisions").EnumerateObject().Select(p => p.Name)).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
                IReadOnlySet<string> Sel(JsonElement u) => u.GetProperty("decisions").EnumerateObject().Where(p => p.Value.GetString() == "SELECT").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var all = P7F1V6Vote.Reduce(complete.Select(Sel).ToList(), order);
                var odd = P7F1V6Vote.Reduce(complete.Where(u => u.GetProperty("Rep").GetInt32() % 2 == 1).Select(Sel).ToList(), order);
                var even = P7F1V6Vote.Reduce(complete.Where(u => u.GetProperty("Rep").GetInt32() % 2 == 0).Select(Sel).ToList(), order);
                var fenced = mine.Count(u => u.TryGetProperty("FenceUnwrapped", out var f) && f.ValueKind == JsonValueKind.True);
                Console.WriteLine($"{seg}: reps {mine.Count}, complete {complete.Count}, fence-unwrapped {fenced}, " +
                    (all.Enough ? $"SELECTED [{string.Join(",", all.Selected)}] UNCERTAIN [{string.Join(",", all.Uncertain.Select(a => a + ":" + all.Votes[a]))}]" : "not enough complete repetitions for a vote"));
                if (odd.Enough && even.Enough)
                    Console.WriteLine($"   split-half: odd reps ({odd.CompleteReps}) selected [{string.Join(",", odd.Selected)}]; even reps ({even.CompleteReps}) selected [{string.Join(",", even.Selected)}]; same set: {odd.Selected.SequenceEqual(even.Selected)}");
                else
                    Console.WriteLine($"   split-half: needs at least {P7F1V6Vote.MinCompleteReps} complete repetitions in each half (odd {odd.CompleteReps}, even {even.CompleteReps})");
            }
        }
    }
}
