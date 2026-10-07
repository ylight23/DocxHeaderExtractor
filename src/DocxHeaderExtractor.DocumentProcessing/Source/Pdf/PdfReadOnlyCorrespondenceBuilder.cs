using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

/// <summary>
/// Builds only the read-only occurrence correspondences required by the live PDF function pass.
/// Candidate ids and candidate decisions are deliberately not part of this production surface.
///
/// The candidate shapes and relation ordering mirror the frozen V5 candidate-authority policy
/// (whole, one-token prefix/suffix, and bounded two/three-atom same-page extents; eight nearest
/// correspondence targets per shape) so replacing that qualification implementation does not
/// silently change the F1 request evidence.
/// </summary>
public static class PdfReadOnlyCorrespondenceBuilder
{
    private const int MaxMultipartParts = 3;
    private const int MaxRelationsPerCandidate = 8;

    private sealed record Candidate(int PrimaryOrdinal, string PrimaryAlias, string Text, IReadOnlyList<string> Aliases);
    private sealed record Target(string Identity, string Text, int Page, int Ordinal, HashSet<string> Aliases);

    public static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Build(
        IReadOnlyList<SemanticSourceAtom> ownedAtoms,
        IReadOnlyList<SemanticSourceAtom> documentAtoms)
    {
        ArgumentNullException.ThrowIfNull(ownedAtoms);
        ArgumentNullException.ThrowIfNull(documentAtoms);

        var owned = ownedAtoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        var document = documentAtoms.OrderBy(atom => atom.Ordinal).ThenBy(atom => atom.Alias, StringComparer.Ordinal).ToArray();
        if (owned.Select(atom => atom.Alias).Distinct(StringComparer.Ordinal).Count() != owned.Length)
            throw new InvalidOperationException("pdf-correspondence-owned-atom-duplicated");
        var ordinalByAlias = document.ToDictionary(atom => atom.Alias, atom => atom.Ordinal, StringComparer.Ordinal);
        if (owned.Any(atom => !ordinalByAlias.ContainsKey(atom.Alias)))
            throw new InvalidOperationException("pdf-correspondence-owned-atom-not-in-document");

        var candidates = BuildCandidates(owned);
        var targets = BuildTargets(document);
        var exact = targets.GroupBy(target => Normalize(target.Text), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var compact = targets.GroupBy(target => Compact(Normalize(target.Text)), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var result = new Dictionary<string, List<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate.Text);
            if (normalized.Length == 0) continue;
            var own = candidate.Aliases.ToHashSet(StringComparer.Ordinal);
            var tierMatches = (exact.TryGetValue(normalized, out var exactMatches) ? exactMatches : [])
                .Where(target => !target.Aliases.Overlaps(own)).ToArray();
            if (tierMatches.Length == 0)
                tierMatches = (compact.TryGetValue(Compact(normalized), out var compactMatches) ? compactMatches : [])
                    .Where(target => !target.Aliases.Overlaps(own)).ToArray();

            var issued = tierMatches
                .OrderBy(target => Math.Abs((long)target.Ordinal - ordinalByAlias[candidate.PrimaryAlias]))
                .ThenBy(target => target.Identity, StringComparer.Ordinal)
                .Take(MaxRelationsPerCandidate)
                .OrderBy(target => target.Ordinal)
                .ThenBy(target => target.Identity, StringComparer.Ordinal);
            foreach (var target in issued)
            {
                if (!result.TryGetValue(candidate.PrimaryAlias, out var list))
                    result[candidate.PrimaryAlias] = list = [];
                if (!list.Any(value => value.TargetPage == target.Page && value.TargetText == target.Text))
                    list.Add(new V5ReadOnlyCorrespondenceV1(target.Page, target.Text));
            }
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<V5ReadOnlyCorrespondenceV1>)pair.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<Candidate> BuildCandidates(IReadOnlyList<SemanticSourceAtom> owned)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < owned.Count; index++)
        {
            var atom = owned[index];
            Add(index, [Whole(atom)]);
            if (StrictPrefix(atom) is { } prefix) Add(index, [prefix]);
            if (StrictSuffix(atom) is { } suffix) Add(index, [suffix]);

            for (var length = 2; length <= MaxMultipartParts && index + length <= owned.Count; length++)
            {
                var window = owned.Skip(index).Take(length).ToArray();
                if (!window.Zip(window.Skip(1)).All(pair => pair.Second.Ordinal == pair.First.Ordinal + 1 && pair.Second.Page == pair.First.Page))
                    break;
                Add(index, window.Select(Whole).ToArray());
                if (StrictPrefix(window[^1]) is { } lastPrefix)
                    Add(index, window[..^1].Select(Whole).Append(lastPrefix).ToArray());
            }
        }
        return candidates;

        void Add(int primaryIndex, IReadOnlyList<SemanticSourcePart> parts)
        {
            var binding = SemanticSourcePartBinder.Bind(owned, parts);
            if (!binding.IsBound)
                throw new InvalidOperationException($"pdf-correspondence-candidate-does-not-bind:{binding.Status}");
            if (!seen.Add(binding.Identity)) return;
            candidates.Add(new Candidate(owned[primaryIndex].Ordinal, parts[0].SourceAlias,
                string.Join(" ", binding.Parts.Select(part => part.Text)),
                binding.Parts.Select(part => part.Alias).ToArray()));
        }
    }

    private static IReadOnlyList<Target> BuildTargets(IReadOnlyList<SemanticSourceAtom> document)
    {
        var targets = new List<Target>();
        for (var index = 0; index < document.Count; index++)
        {
            var atom = document[index];
            targets.Add(new Target($"{atom.Alias}:0-{atom.Text.Length}", atom.Text, atom.Page, atom.Ordinal, [atom.Alias]));
            if (index + 1 < document.Count && document[index + 1].Ordinal == atom.Ordinal + 1 && document[index + 1].Page == atom.Page)
            {
                var next = document[index + 1];
                targets.Add(new Target($"{atom.Alias}:0-{atom.Text.Length}|{next.Alias}:0-{next.Text.Length}",
                    atom.Text + " " + next.Text, atom.Page, atom.Ordinal, [atom.Alias, next.Alias]));
            }
        }
        return targets;
    }

    private static SemanticSourcePart Whole(SemanticSourceAtom atom) =>
        new(atom.Alias, CanonicalSemanticSelectionMode.WholeAlias);

    private static SemanticSourcePart? StrictPrefix(SemanticSourceAtom atom)
    {
        var text = atom.Text;
        var end = text.TrimEnd().Length;
        var lastTokenStart = end;
        while (lastTokenStart > 0 && !char.IsWhiteSpace(text[lastTokenStart - 1])) lastTokenStart--;
        var prefixEnd = lastTokenStart;
        while (prefixEnd > 0 && char.IsWhiteSpace(text[prefixEnd - 1])) prefixEnd--;
        var start = text.Length - text.TrimStart().Length;
        return Verbatim(atom, start, prefixEnd);
    }

    private static SemanticSourcePart? StrictSuffix(SemanticSourceAtom atom)
    {
        var text = atom.Text;
        var start = text.Length - text.TrimStart().Length;
        var firstTokenEnd = start;
        while (firstTokenEnd < text.Length && !char.IsWhiteSpace(text[firstTokenEnd])) firstTokenEnd++;
        var suffixStart = firstTokenEnd;
        while (suffixStart < text.Length && char.IsWhiteSpace(text[suffixStart])) suffixStart++;
        var end = text.TrimEnd().Length;
        return Verbatim(atom, suffixStart, end);
    }

    private static SemanticSourcePart? Verbatim(SemanticSourceAtom atom, int start, int end)
    {
        if (start < 0 || end <= start || end > atom.Text.Length || (start == 0 && end == atom.Text.Length)) return null;
        if (char.IsLowSurrogate(atom.Text[start]) || (end < atom.Text.Length && char.IsLowSurrogate(atom.Text[end]))) return null;
        var value = atom.Text[start..end];
        var occurrence = 0;
        for (var at = atom.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = atom.Text.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            occurrence++;
            if (at == start) return new SemanticSourcePart(atom.Alias, CanonicalSemanticSelectionMode.VerbatimText, value, occurrence);
        }
        return null;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
            builder.Append(rune.ToString());
        }
        return builder.ToString().ToUpperInvariant();
    }

    private static string Compact(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
