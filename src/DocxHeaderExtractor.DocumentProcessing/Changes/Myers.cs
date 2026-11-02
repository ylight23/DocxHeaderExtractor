namespace DocxHeaderExtractor.DocumentProcessing.Changes;

internal enum EditKind { Equal, Delete, Insert }

/// <summary>One step of an edit script: Equal pairs <see cref="OldIndex"/> with <see cref="NewIndex"/>; Delete has no new index; Insert has no old one.</summary>
internal readonly record struct Edit(EditKind Kind, int OldIndex, int NewIndex);

/// <summary>
/// Myers' O((N+M)D) shortest edit script, with the common prefix and suffix trimmed first. Memory grows with D², so very different inputs are
/// costly; that is acceptable for the experimental P1 and is a known limit.
/// </summary>
internal static class Myers
{
    public static List<Edit> Diff<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equal)
    {
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && equal(a[prefix], b[prefix])) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && equal(a[a.Count - 1 - suffix], b[b.Count - 1 - suffix])) suffix++;

        var edits = new List<Edit>(a.Count + b.Count);
        for (var i = 0; i < prefix; i++) edits.Add(new Edit(EditKind.Equal, i, i));
        edits.AddRange(Core(a, b, prefix, a.Count - prefix - suffix, b.Count - prefix - suffix, equal));
        for (var i = 0; i < suffix; i++) edits.Add(new Edit(EditKind.Equal, a.Count - suffix + i, b.Count - suffix + i));
        return edits;
    }

    static List<Edit> Core<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, int offset, int n, int m, Func<T, T, bool> equal)
    {
        var result = new List<Edit>();
        if (n == 0 && m == 0) return result;
        var max = n + m;
        var shift = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[k - 1 + shift] < v[k + 1 + shift]) ? v[k + 1 + shift] : v[k - 1 + shift] + 1;
                var y = x - k;
                while (x < n && y < m && equal(a[offset + x], b[offset + y])) { x++; y++; }
                v[k + shift] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }

        int cx = n, cy = m;
        for (var d = found; d >= 0; d--)
        {
            var prev = trace[d];
            var k = cx - cy;
            var prevK = k == -d || (k != d && prev[k - 1 + shift] < prev[k + 1 + shift]) ? k + 1 : k - 1;
            var prevX = prev[prevK + shift];
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
