using System.Diagnostics;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Scan;

/// <summary>How long the provider took: the calls of one stage (count, median, 90th percentile, slowest, the sum) and, for the conversation, the
/// units that took longest. Measured around each call, so it says whether a stage is slow because each call is slow or because there are many.</summary>
public sealed record CallStats(int Calls, double P50Seconds, double P90Seconds, double MaxSeconds, double TotalSeconds);

/// <summary>One unit of the conversation (a segment, or the grouping): its calls and their seconds, one after another.</summary>
public sealed record UnitTime(string UnitId, int Calls, double Seconds);

/// <summary>A thread-safe log of call durations, by stage and unit.</summary>
public sealed class CallTimings
{
    readonly object gate = new();
    readonly List<(string Unit, double Seconds)> calls = [];

    /// <summary>Runs <paramref name="call"/> and records how long it took, failed or not.</summary>
    public T Time<T>(string unit, Func<T> call)
    {
        var watch = Stopwatch.StartNew();
        try { return call(); }
        finally { lock (gate) calls.Add((unit, watch.Elapsed.TotalSeconds)); }
    }

    public CallStats Stats()
    {
        double[] s;
        lock (gate) s = calls.Select(c => c.Seconds).Order().ToArray();
        if (s.Length == 0) return new CallStats(0, 0, 0, 0, 0);
        double At(double q) => Math.Round(s[Math.Min(s.Length - 1, (int)Math.Floor(q * s.Length))], 1);
        return new CallStats(s.Length, At(0.5), At(0.9), Math.Round(s[^1], 1), Math.Round(s.Sum(), 1));
    }

    /// <summary>The units whose calls took longest in all, slowest first.</summary>
    public IReadOnlyList<UnitTime> SlowestUnits(int count)
    {
        lock (gate)
            return calls.GroupBy(c => c.Unit).Select(g => new UnitTime(g.Key, g.Count(), Math.Round(g.Sum(c => c.Seconds), 1)))
                .OrderByDescending(u => u.Seconds).Take(count).ToArray();
    }
}
