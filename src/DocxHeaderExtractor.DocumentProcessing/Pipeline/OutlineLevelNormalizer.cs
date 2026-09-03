using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Normalizes an ordered heading sequence without depending on a legacy pipeline owner.</summary>
public static class OutlineLevelNormalizer
{
    public static void NormalizeLevels(List<HeadingRecord> headings)
    {
        if (headings.Count == 0) return;

        var stack = new List<int>();
        foreach (var heading in headings.OrderBy(item => item.Index))
        {
            if (heading.Level is not { } raw) continue;
            while (stack.Count > 0 && stack[^1] >= raw) stack.RemoveAt(stack.Count - 1);
            stack.Add(raw);
            heading.Level = stack.Count;
        }
    }
}
