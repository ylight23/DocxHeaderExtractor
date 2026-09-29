namespace DocxHeaderExtractor.Core.Models;

/// <summary>What canonicalization made of one claim's parts, or why it refused them.</summary>
public sealed record SemanticSourcePartCanonicalization(
    SemanticSourcePartsStatus Status,
    IReadOnlyList<SemanticSourcePart> Parts,
    string? Reason = null)
{
    public bool IsCanonical => Status == SemanticSourcePartsStatus.Bound;
}

/// <summary>
/// Turns what a model said it meant into the coordinate form the binder reads.
/// <para>
/// The model names an occurrence and, when it means only part of one, quotes the words. Whether
/// that is the whole atom or a piece of it is not a judgement - it is a comparison against the
/// source, and the harness holds the source. Asking the model for it instead cost four approved
/// headings in one run: they were named correctly, quoted correctly, and refused because the reply
/// also declared WHOLE_ALIAS while carrying the quote. Across every approved claim in the corpus,
/// 42 of 42 parts declare a mode that this comparison recovers, with no counterexample.
/// </para>
/// <para>
/// Refusing is still a real outcome. A quote that does not occur in the atom it names, or that
/// occurs twice with nothing to say which was meant, is not repaired into a whole-atom selection -
/// that would bind a claim the model did not make.
/// </para>
/// </summary>
public static class SemanticSourcePartCanonicalizer
{
    /// <summary>
    /// The mode a v2 reply carries before the harness derives the real one. It exists so a part can
    /// travel from decoder to canonicalizer without a coordinate decision attached, and it must
    /// never reach the binder: the binder is strict on purpose and refuses what it does not know.
    /// </summary>
    public const string PendingSelectionMode = "HARNESS_DERIVED";

    public static SemanticSourcePartCanonicalization Canonicalize(
        IReadOnlyList<SemanticSourceAtom> atoms,
        IReadOnlyList<SemanticSourcePart> parts)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count == 0)
            return Refuse(SemanticSourcePartsStatus.NoParts, "a claim must name at least one source part");

        var byAlias = new Dictionary<string, SemanticSourceAtom>(StringComparer.Ordinal);
        foreach (var atom in atoms) byAlias.TryAdd(atom.Alias, atom);

        var canonical = new List<SemanticSourcePart>(parts.Count);
        foreach (var part in parts)
        {
            if (!byAlias.TryGetValue(part.SourceAlias ?? string.Empty, out var atom))
                return Refuse(SemanticSourcePartsStatus.UnknownAlias,
                    $"'{part.SourceAlias}' is not an atom in this source");

            // CASE A: nothing quoted. The claim is the occurrence it named.
            if (string.IsNullOrEmpty(part.VerbatimText))
            {
                canonical.Add(part with
                {
                    SelectionMode = CanonicalSemanticSelectionMode.WholeAlias,
                    VerbatimText = null,
                });
                continue;
            }

            // CASE B: the quote is the whole atom. The same claim, said twice; the redundant quote
            // is dropped rather than carried into a form the binder forbids.
            if (string.Equals(part.VerbatimText, atom.Text, StringComparison.Ordinal))
            {
                canonical.Add(part with
                {
                    SelectionMode = CanonicalSemanticSelectionMode.WholeAlias,
                    VerbatimText = null,
                });
                continue;
            }

            // CASE B2: the quote is the whole atom plus whitespace at its edges. With reasoning off,
            // the model quotes continuation lines of a multipart claim as " PUBLISHING" - the join
            // space it would type when reading the lines as one sentence. Byte-exact comparison
            // refused every such claim (A99 T5B: 4 of 7 claims in one SRC-089 leaf). Only boundary
            // whitespace, and only when what remains IS the atom: any other difference stays refused.
            if (part.VerbatimText.Length != part.VerbatimText.Trim().Length &&
                atom.Text.Length > 0 &&
                string.Equals(part.VerbatimText.Trim(), atom.Text, StringComparison.Ordinal))
            {
                canonical.Add(part with
                {
                    SelectionMode = CanonicalSemanticSelectionMode.WholeAlias,
                    VerbatimText = null,
                });
                continue;
            }

            var occurrences = Occurrences(atom.Text, part.VerbatimText);

            // CASE D: the quote is not in the atom it names. Refused, never widened to the atom.
            if (occurrences == 0)
                return Refuse(SemanticSourcePartsStatus.TextNotInAtom,
                    $"the quoted text does not occur in '{atom.Alias}'");

            // CASE E: more than one place it could be, and nothing said which. The fields that
            // settle this already exist; without one of them the harness will not choose.
            if (occurrences > 1 &&
                part.Occurrence is null &&
                string.IsNullOrEmpty(part.LeftExactContext) &&
                string.IsNullOrEmpty(part.RightExactContext))
            {
                return Refuse(SemanticSourcePartsStatus.AmbiguousSelection,
                    $"the quoted text occurs {occurrences} times in '{atom.Alias}' and nothing says which");
            }

            // CASE C: an exact piece of the atom. The quote is kept verbatim and the binder resolves
            // it to offsets, using whatever disambiguation the claim carried.
            canonical.Add(part with { SelectionMode = CanonicalSemanticSelectionMode.VerbatimText });
        }

        return new SemanticSourcePartCanonicalization(SemanticSourcePartsStatus.Bound, canonical);
    }

    private static int Occurrences(string text, string quote)
    {
        if (quote.Length == 0) return 0;
        var count = 0;
        var index = text.IndexOf(quote, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(quote, index + 1, StringComparison.Ordinal);
        }
        return count;
    }

    private static SemanticSourcePartCanonicalization Refuse(SemanticSourcePartsStatus status, string reason) =>
        new(status, [], reason);
}
