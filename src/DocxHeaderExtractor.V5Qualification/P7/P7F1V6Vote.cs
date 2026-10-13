namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Repetition vote (D4.12): the same request is sent several times and a line is part of the result only when more than half of the complete
/// repetitions selected it. Lines selected at least once but not by a majority are reported as uncertain, never dropped silently. The vote reads
/// the decisions only: no text, no Gold, no model call. Fewer than MinCompleteReps complete repetitions give no result.
/// </summary>
internal static class P7F1V6Vote
{
    public const string Version = "P7_F1_V6_MAJORITY_VOTE_V1";
    public const int MinCompleteReps = 3;

    public sealed record Result(int CompleteReps, bool Enough, IReadOnlyDictionary<string, int> Votes, IReadOnlyList<string> Selected, IReadOnlyList<string> Uncertain);

    public static Result Reduce(IReadOnlyList<IReadOnlySet<string>> selectionPerCompleteRep, IReadOnlyList<string> readingOrder)
    {
        var n = selectionPerCompleteRep.Count;
        var votes = selectionPerCompleteRep.SelectMany(s => s).GroupBy(a => a, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var enough = n >= MinCompleteReps;
        var selected = enough ? readingOrder.Where(a => votes.GetValueOrDefault(a) * 2 > n).ToArray() : Array.Empty<string>();
        var uncertain = enough ? readingOrder.Where(a => votes.GetValueOrDefault(a) > 0 && votes[a] * 2 <= n).ToArray() : Array.Empty<string>();
        return new Result(n, enough, votes, selected, uncertain);
    }
}
