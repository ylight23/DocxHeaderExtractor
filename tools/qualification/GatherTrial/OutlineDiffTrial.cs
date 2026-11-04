using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Compares two stored outlines offline (no model): outline-diff &lt;old.json&gt; &lt;new.json&gt;. Prints the differences by kind.</summary>
static class OutlineDiffTrial
{
    public static int Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("outline-diff <old-stored-outline.json> <new-stored-outline.json>");
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var old = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(args[1]))!;
        var next = JsonSerializer.Deserialize<StoredOutline>(File.ReadAllText(args[2]))!;
        var result = OutlineDiff.Compare(old, next);
        Console.WriteLine($"same file: {old.SourceSha256 == next.SourceSha256}; changed paragraphs: {result.ChangedParagraphs}; " +
            $"headings old {old.Headings.Count}, new {next.Headings.Count}, same {result.SameHeadings}; differences {result.Differences.Count}");
        foreach (var g in result.Differences.GroupBy(d => d.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-16} {g.Count(),3}  (context changed {g.Count(d => d.ContextChanged)}, uncertain {g.Count(d => d.Uncertain)})");
        foreach (var d in result.Differences)
            Console.WriteLine($"    {d.Kind,-16} {d.OldLocation ?? "-",-14} {d.NewLocation ?? "-",-14} | {Short(d.OldText)} -> {Short(d.NewText)}");
        return 0;
    }

    static string Short(string? text) => text is null ? "—" : text.Length > 45 ? text[..45] + "…" : text;
}
