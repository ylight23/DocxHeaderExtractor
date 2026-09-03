using System.Globalization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Builds coordinates in the original .NET string. No normalization, trimming or truncation is
/// performed; surrogate pairs and combining sequences remain one exact UTF-16 slice.
/// </summary>
public static class FactProposalOffsetMapBuilder
{
    public static IReadOnlyList<FactProposalOffsetSource> Build(
        IReadOnlyList<FactSourceExcerpt> sources,
        int? maximumSourceCharacters = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var totalCharacters = sources.Sum(source => source.Text.Length);
        if (maximumSourceCharacters is not null && totalCharacters > maximumSourceCharacters.Value)
            throw new InvalidOperationException("fact-model-source-context-budget-exceeded");

        return sources.Select(source =>
        {
            var starts = StringInfo.ParseCombiningCharacters(source.Text);
            var offsets = starts.Select((start, index) =>
            {
                var end = index + 1 < starts.Length ? starts[index + 1] : source.Text.Length;
                return new FactTextOffset(start, end, source.Text[start..end]);
            }).ToArray();
            return new FactProposalOffsetSource(source.SourceId, source.Text, offsets);
        }).ToArray();
    }
}
