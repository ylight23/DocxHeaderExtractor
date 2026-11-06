using DocxHeaderExtractor.DocumentProcessing.Changes;

namespace DocxHeaderExtractor.Tests;

/// <summary>The project's Myers against an independent answer (LCS by dynamic programming) and its resource limit.</summary>
public sealed class MyersTests
{
    static int Lcs(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
            for (var j = 1; j <= b.Count; j++)
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
        return table[a.Count, b.Count];
    }

    /// <summary>Applying the script to a gives b, every Equal pairs equal items, and indexes only move forward.</summary>
    static void AssertValid(IReadOnlyList<int> a, IReadOnlyList<int> b, IReadOnlyList<Edit> edits)
    {
        var rebuilt = new List<int>();
        int lastOld = -1, lastNew = -1;
        foreach (var e in edits)
        {
            if (e.Kind != EditKind.Insert) { Assert.True(e.OldIndex > lastOld); lastOld = e.OldIndex; }
            if (e.Kind != EditKind.Delete) { Assert.True(e.NewIndex > lastNew); lastNew = e.NewIndex; rebuilt.Add(b[e.NewIndex]); }
            if (e.Kind == EditKind.Equal) Assert.Equal(a[e.OldIndex], b[e.NewIndex]);
        }
        Assert.Equal(a.Count - 1, lastOld);
        Assert.Equal(b, rebuilt);
    }

    [Fact]
    public void The_script_is_valid_and_as_short_as_the_longest_common_subsequence_allows()
    {
        var random = new Random(20261106);
        for (var round = 0; round < 500; round++)
        {
            // Small alphabets give many repeats, the case where two shortest scripts can pair different copies.
            var alphabet = random.Next(1, 6);
            var a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(alphabet)).ToList();
            var b = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(alphabet)).ToList();
            var edits = Myers.Diff(a, b, (x, y) => x == y, Myers.DefaultMaxEditDistance, out var complete);
            Assert.True(complete);
            AssertValid(a, b, edits);
            Assert.Equal(a.Count + b.Count - 2 * Lcs(a, b), edits.Count(e => e.Kind != EditKind.Equal));
        }
    }

    [Fact]
    public void Beyond_the_limit_the_middle_is_all_changed_and_reported_incomplete()
    {
        // Common prefix and suffix, then 10 different items on each side: the shortest script needs 20 steps; the limit is 5.
        var a = new[] { 1, 2 }.Concat(Enumerable.Range(100, 10)).Concat([3, 4]).ToList();
        var b = new[] { 1, 2 }.Concat(Enumerable.Range(200, 10)).Concat([3, 4]).ToList();
        var edits = Myers.Diff(a, b, (x, y) => x == y, 5, out var complete);
        Assert.False(complete);
        AssertValid(a, b, edits);
        // Nothing in the middle is paired; the trimmed prefix and suffix still are.
        Assert.Equal([(0, 0), (1, 1), (12, 12), (13, 13)], edits.Where(e => e.Kind == EditKind.Equal).Select(e => (e.OldIndex, e.NewIndex)));

        var within = Myers.Diff(a, b, (x, y) => x == y, 20, out var done);
        Assert.True(done);
        Assert.Equal(20, within.Count(e => e.Kind != EditKind.Equal));
    }
}
