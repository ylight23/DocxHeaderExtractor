using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.HeadingAgent.Grouping;

/// <summary>Exact substring in the selected source representation; invalid offsets are never widened or moved.</summary>
internal static class SelectedHeadingText
{
    internal static (TextOffsetSpan Span, string Text) Resolve(string alias, string sourceText, TextOffsetSpan? extent)
    {
        var span = extent ?? new(0, sourceText.Length);
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length)
            throw new InvalidOperationException("HEADING_EXTENT_OUTSIDE_LINE:" + alias);
        return (span, sourceText[span.Start..span.End]);
    }
}
