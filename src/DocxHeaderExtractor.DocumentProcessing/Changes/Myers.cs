namespace DocxHeaderExtractor.DocumentProcessing.Changes;

internal enum EditKind { Equal, Delete, Insert }

/// <summary>One step of an edit script: Equal pairs <see cref="OldIndex"/> with <see cref="NewIndex"/>; Delete has no new index; Insert has no old one.</summary>
internal readonly record struct Edit(EditKind Kind, int OldIndex, int NewIndex);

/// <summary>
/// Myers' O((N+M)D) shortest edit script, with the common prefix and suffix trimmed first. The trace keeps, for each step d, only the diagonals
/// that step can read (-d-1..d+1), so memory grows with D². The search stops at <see cref="DefaultMaxEditDistance"/> steps (about 64 MB of
/// trace per call, on top of the inputs,
/// the result and any calls running at the same time): beyond it the middle part is returned as all deletes then all inserts, a valid but not shortest script that pairs nothing there, so a
/// caller never reads two different parts as the same; <c>complete</c> is false and the caller must show the comparison as not done in full.
/// </summary>
internal static class Myers
{
    public const int DefaultMaxEditDistance = 4000;

    /// <summary>A lower limit for the current async flow only (tests); every caller in that flow sees it, other flows keep the default.</summary>
    internal static readonly AsyncLocal<int?> LimitOverride = new();

    /// <summary>The limit callers pass: the default, or the current flow's override.</summary>
    public static int Limit => LimitOverride.Value ?? DefaultMaxEditDistance;

    /// <summary>
    /// The script within <see cref="Limit"/>, for callers whose use stays safe with an incomplete script: the middle is then all deletes and
    /// inserts, so nothing there is paired. Callers that report alignment use the overload with <c>complete</c>.
    /// </summary>
    public static List<Edit> Diff<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equal) =>
        Diff(a, b, equal, Limit, out _);

    public static List<Edit> Diff<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equal, int maxEditDistance, out bool complete)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEditDistance);
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && equal(a[prefix], b[prefix])) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && equal(a[a.Count - 1 - suffix], b[b.Count - 1 - suffix])) suffix++;

        var edits = new List<Edit>(a.Count + b.Count);
        for (var i = 0; i < prefix; i++) edits.Add(new Edit(EditKind.Equal, i, i));
        edits.AddRange(Core(a, b, prefix, a.Count - prefix - suffix, b.Count - prefix - suffix, equal, maxEditDistance, out complete));
        for (var i = 0; i < suffix; i++) edits.Add(new Edit(EditKind.Equal, a.Count - suffix + i, b.Count - suffix + i));
        return edits;
    }

    static List<Edit> Core<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, int offset, int n, int m, Func<T, T, bool> equal, int maxEditDistance,
        out bool complete)
    {
        var result = new List<Edit>();
        complete = true;
        if (n == 0 && m == 0) return result;
        var max = Math.Min(n + m, maxEditDistance);
        var shift = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            trace.Add(v[(shift - d - 1)..(shift + d + 2)]);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[k - 1 + shift] < v[k + 1 + shift]) ? v[k + 1 + shift] : v[k - 1 + shift] + 1;
                var y = x - k;
                while (x < n && y < m && equal(a[offset + x], b[offset + y])) { x++; y++; }
                v[k + shift] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }

        if (found < 0)
        {
            complete = false;
            for (var i = 0; i < n; i++) result.Add(new Edit(EditKind.Delete, offset + i, -1));
            for (var j = 0; j < m; j++) result.Add(new Edit(EditKind.Insert, -1, offset + j));
            return result;
        }

        int cx = n, cy = m;
        for (var d = found; d >= 0; d--)
        {
            var prev = trace[d];   // diagonal k is at prev[k + d + 1]
            var k = cx - cy;
            var prevK = k == -d || (k != d && prev[k - 1 + d + 1] < prev[k + 1 + d + 1]) ? k + 1 : k - 1;
            var prevX = prev[prevK + d + 1];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { result.Add(new Edit(EditKind.Equal, offset + cx - 1, offset + cy - 1)); cx--; cy--; }
            if (d > 0)
            {
                if (cx == prevX) result.Add(new Edit(EditKind.Insert, -1, offset + cy - 1));
                else result.Add(new Edit(EditKind.Delete, offset + cx - 1, -1));
            }
            cx = prevX;
            cy = prevY;
        }
        result.Reverse();
        return result;
    }
}
