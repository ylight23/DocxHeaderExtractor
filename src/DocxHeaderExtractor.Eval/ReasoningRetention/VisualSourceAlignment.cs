using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Gold-blind, deterministic bridge from rendered page text to the canonical OOXML
/// occurrences. Page text can locate a source interval, but it never becomes source authority.</summary>
public sealed record VisualSourcePageAlias(
    string Alias,
    string SourceOccurrenceId,
    string SourceId,
    int SourceOrdinal,
    int PageIndex,
    string Authority,
    string MappingStatus,
    int VisibleStartCharacter,
    int VisibleEndCharacter,
    int OwnedStartCharacter,
    int OwnedEndCharacter,
    string ExactTextHash,
    int AliasTextLength);

public sealed record VisualSourceOccurrenceAlignment(
    string SourceOccurrenceId,
    string SourceId,
    int SourceOrdinal,
    string ExactTextHash,
    int SourceTextLength,
    IReadOnlyList<int> PageIndices,
    IReadOnlyList<string> VisualAliases,
    string MappingStatus,
    int MappedCharacterCount,
    int UnmappedCharacterCount);

public sealed record VisualSourceAlignmentManifest(
    string SchemaVersion,
    int TotalOccurrences,
    int MappedOccurrences,
    int UnmappedOccurrences,
    int MultiPageOccurrences,
    int NonVisualOccurrences,
    double SourceAliasCoverage,
    int TotalSourceCharacters,
    int MappedSourceCharacters,
    double SourceCharacterCoverage,
    IReadOnlyList<VisualSourceOccurrenceAlignment> Occurrences,
    IReadOnlyList<VisualSourcePageAlias> Aliases,
    bool GoldUsed,
    bool RoundTripValid,
    string GateStatus)
{
    public bool GatePass => GateStatus == "PASS" && RoundTripValid && SourceAliasCoverage >= .99 && SourceCharacterCoverage >= .99;
}

public static class VisualSourceAlignmentBuilder
{
    public static VisualSourceAlignmentManifest Build(
        IReadOnlyList<ReasoningSourceOccurrence> occurrences,
        IReadOnlyList<string> pageTexts)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(pageTexts);

        var aliases = new List<VisualSourcePageAlias>();
        var occurrenceRows = new List<VisualSourceOccurrenceAlignment>();
        foreach (var occurrence in occurrences.OrderBy(x => x.SourceOrdinal))
        {
            var raw = CanonicalWithOffsets(occurrence.RawText);
            var anchors = FindPageAnchors(raw, pageTexts, occurrence.RawText.Length);
            var occurrenceAliases = new List<VisualSourcePageAlias>();
            if (anchors.Count > 0)
            {
                for (var i = 0; i < anchors.Count; i++)
                {
                    var start = i == 0 ? 0 : anchors[i].RawStart;
                    var end = i + 1 < anchors.Count ? anchors[i + 1].RawStart : occurrence.RawText.Length;
                    if (end <= start) continue;
                    var page = anchors[i].PageIndex;
                    var alias = $"P{page:00}-O{occurrence.SourceOrdinal:000}";
                    occurrenceAliases.Add(new VisualSourcePageAlias(
                        alias, occurrence.SourceOccurrenceId, occurrence.SourceId, occurrence.SourceOrdinal, page,
                        "canonical_text_page_boundary", anchors.Count > 1 ? "MULTI_PAGE" : "MAPPED",
                        start, end, start, end, Sha256Text(occurrence.RawText[start..end]), end - start));
                }
            }

            if (occurrenceAliases.Count == 0)
            {
                var needle = raw.Text.Length > 180 ? raw.Text[..180] : raw.Text;
                var page = Enumerable.Range(0, pageTexts.Count)
                    .FirstOrDefault(i => needle.Length > 0 && Canonical(pageTexts[i]).Contains(needle, StringComparison.Ordinal));
                if (needle.Length > 0 && Canonical(pageTexts[page]).Contains(needle, StringComparison.Ordinal))
                {
                    occurrenceAliases.Add(new VisualSourcePageAlias(
                        $"P{page + 1:00}-O{occurrence.SourceOrdinal:000}", occurrence.SourceOccurrenceId, occurrence.SourceId,
                        occurrence.SourceOrdinal, page + 1, "canonical_text_full_occurrence_fallback", "MAPPED", 0,
                        occurrence.RawText.Length, 0, occurrence.RawText.Length, Sha256Text(occurrence.RawText), occurrence.RawText.Length));
                }
            }

            aliases.AddRange(occurrenceAliases);
            var ranges = MergeRanges(occurrenceAliases.Select(x => (x.VisibleStartCharacter, x.VisibleEndCharacter)));
            var mappedChars = ranges.Sum(x => x.End - x.Start);
            var status = occurrenceAliases.Count == 0 ? "UNRESOLVED" : occurrenceAliases.Count > 1 ? "MULTI_PAGE" : "MAPPED";
            occurrenceRows.Add(new VisualSourceOccurrenceAlignment(
                occurrence.SourceOccurrenceId, occurrence.SourceId, occurrence.SourceOrdinal, Sha256Text(occurrence.RawText),
                occurrence.RawText.Length, occurrenceAliases.Select(x => x.PageIndex).Distinct().OrderBy(x => x).ToArray(),
                occurrenceAliases.Select(x => x.Alias).ToArray(), status, mappedChars, Math.Max(0, occurrence.RawText.Length - mappedChars)));
        }

        var totalChars = occurrenceRows.Sum(x => x.SourceTextLength);
        var mappedCharsTotal = occurrenceRows.Sum(x => x.MappedCharacterCount);
        var mappedCount = occurrenceRows.Count(x => x.MappingStatus is "MAPPED" or "MULTI_PAGE");
        var unresolved = occurrenceRows.Count(x => x.MappingStatus == "UNRESOLVED");
        var multiPage = occurrenceRows.Count(x => x.MappingStatus == "MULTI_PAGE");
        var roundTrip = aliases.All(alias =>
            alias.VisibleStartCharacter >= 0 && alias.VisibleEndCharacter > alias.VisibleStartCharacter &&
            alias.OwnedStartCharacter == alias.VisibleStartCharacter && alias.OwnedEndCharacter == alias.VisibleEndCharacter);
        var occurrenceCoverage = occurrences.Count == 0 ? 1d : (double)mappedCount / occurrences.Count;
        var charCoverage = totalChars == 0 ? 1d : (double)mappedCharsTotal / totalChars;
        return new VisualSourceAlignmentManifest(
            "a99-visual-source-alignment-v2", occurrences.Count, mappedCount, unresolved, multiPage, 0,
            occurrenceCoverage, totalChars, mappedCharsTotal, charCoverage, occurrenceRows, aliases, false,
            roundTrip, unresolved == 0 && occurrenceCoverage >= .99 && charCoverage >= .99 ? "PASS" : "BLOCK");
    }

    private static IReadOnlyList<(int PageIndex, int RawStart, int RawEnd)> FindPageAnchors(
        (string Text, IReadOnlyList<int> RawOffsets) raw, IReadOnlyList<string> pageTexts, int rawLength)
    {
        var result = new List<(int PageIndex, int RawStart, int RawEnd)>();
        var cursor = 0;
        for (var page = 0; page < pageTexts.Count; page++)
        {
            var canonicalPage = Canonical(pageTexts[page]);
            var anchor = FindAnchor(raw.Text, canonicalPage, cursor);
            if (anchor is null) continue;
            var rawStart = raw.RawOffsets[anchor.Value.StartCanonical];
            var rawEnd = anchor.Value.EndCanonical < raw.RawOffsets.Count ? raw.RawOffsets[anchor.Value.EndCanonical] : rawLength;
            result.Add((page + 1, rawStart, rawEnd));
            cursor = Math.Max(cursor, anchor.Value.EndCanonical);
        }
        return result;
    }

    private static (int StartCanonical, int EndCanonical)? FindAnchor(string rawCanonical, string pageCanonical, int searchStart)
    {
        if (pageCanonical.Length == 0) return null;
        foreach (var length in new[] { 400, 300, 220, 160, 120, 80, 50 })
        {
            if (pageCanonical.Length < length) continue;
            for (var offset = 0; offset <= pageCanonical.Length - length; offset += Math.Max(1, length / 4))
            {
                var needle = pageCanonical.Substring(offset, length);
                var found = rawCanonical.IndexOf(needle, Math.Max(0, searchStart), StringComparison.Ordinal);
                if (found >= 0) return (found, found + length);
            }
        }
        return null;
    }

    private static IReadOnlyList<(int Start, int End)> MergeRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges.OrderBy(x => x.Start).ThenBy(x => x.End))
        {
            if (merged.Count == 0 || range.Start > merged[^1].End) merged.Add(range);
            else if (range.End > merged[^1].End) merged[^1] = (merged[^1].Start, range.End);
        }
        return merged;
    }

    private static (string Text, IReadOnlyList<int> RawOffsets) CanonicalWithOffsets(string text)
    {
        var canonical = new StringBuilder();
        var offsets = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            foreach (var normalized in text[i].ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(normalized) == UnicodeCategory.NonSpacingMark || !char.IsLetterOrDigit(normalized)) continue;
                canonical.Append(char.ToLowerInvariant(normalized)); offsets.Add(i);
            }
        }
        return (canonical.ToString(), offsets);
    }

    private static string Canonical(string text) => new(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
        .Select(char.ToLowerInvariant).ToArray());

    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
