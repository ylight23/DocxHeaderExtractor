using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Offline decomposition of one <see cref="V5BindingQualifier.FamilyExactTextBinding"/> refusal - a
/// provider quote that failed exact binding against the source atom its own sourceAlias named. This
/// never repairs, corrects or fuzzy-matches anything; it only explains, deterministically, why the
/// exact binder was right to refuse. One row per failing source part, not per claim: a claim whose
/// subject and object both fail gets two rows.
/// </summary>
public enum V5ExactTextFailureCategory
{
    /// <summary>The quoted text does not belong to the named alias but matches exactly one other visible alias.</summary>
    WRONG_ALIAS_TEXT,

    /// <summary>The quote spans more than the named atom; the rest occurs in a following atom.</summary>
    MULTI_ATOM_OVERQUOTE,

    /// <summary>Differs from the atom only by inserted/removed whitespace (a PdfPig extraction artifact).</summary>
    EXTRACTION_SPACING_DIFFERENCE,

    /// <summary>Differs from the atom only by Unicode normalization form, ligatures, NBSP or quote/dash code points.</summary>
    UNICODE_EQUIVALENCE,

    /// <summary>Same text except punctuation (quote style, hyphen/dash, comma/period, bracket, slash, colon).</summary>
    PUNCTUATION_DIFFERENCE,

    /// <summary>Same text except letter case.</summary>
    CASE_DIFFERENCE,

    /// <summary>The atom contains the quoted text more than once and nothing in the proposal disambiguates it.</summary>
    REPEATED_SUBSTRING_WITHOUT_DISAMBIGUATION,

    /// <summary>Resembles the source but changes lexical content; no safe exact transform recovers it.</summary>
    PARTIAL_PARAPHRASE_OR_REWRITE,

    /// <summary>Unrelated to the named atom and not mappable to an obvious neighbor or multipart source.</summary>
    COMPLETELY_WRONG_TEXT,

    /// <summary>None of the above; <see cref="V5ExactTextFailureRow.DiagnosticReason"/> is always set here.</summary>
    OTHER,
}

/// <summary>Where the failure's cause most plausibly sits. Never a semantic-correctness judgement.</summary>
public enum V5ExactTextFaultDomain
{
    MODEL_REFERENCE,
    SOURCE_REPRESENTATION,
    AMBIGUOUS_EXACT_SELECTION,
    MIXED,
}

/// <summary>Every diagnostic normalization this audit measures but never applies to production binding.</summary>
public sealed record V5ExactTextCounterfactuals(
    bool WouldBindAsWholeAlias,
    bool WouldBindWithExactMultipart,
    IReadOnlyList<string>? ExactMultipartAliasSequence,
    bool WouldBindWithExistingDisambiguation,
    bool WouldMatchAfterNfc,
    bool WouldMatchAfterNfkc,
    bool WouldMatchAfterWhitespaceCollapse,
    bool WouldMatchAfterWhitespaceRemoval,
    bool WouldMatchAfterPunctuationNormalization);

public sealed record V5ExactTextFailureRow(
    string DocumentId,
    string PackId,
    int ProposalOrdinal,
    string Predicate,
    string Endpoint,
    int PartIndex,
    string SourceAlias,
    string? VerbatimText,
    int? Occurrence,
    string? LeftExactContext,
    string? RightExactContext,
    string RefusalReason,
    string SourceAtomText,
    int AtomOrdinal,
    IReadOnlyList<(string Alias, string Text)> NeighboringVisibleAtoms,
    V5ExactTextFailureCategory Category,
    V5ExactTextFaultDomain FaultDomain,
    string? DiagnosticReason,
    V5ExactTextCounterfactuals Counterfactuals);

/// <summary>
/// One part of one endpoint, walked across every proposal regardless of outcome, to measure how the
/// wire's two selection modes are actually used. <c>NECESSARY_SUBSTRING</c> is the only mode the
/// verbatimText field exists for; the others are evidence about the wire, not about this response.
/// </summary>
public enum V5SourcePartUsage
{
    ALIAS_ONLY,
    VERBATIM_NECESSARY_SUBSTRING,
    VERBATIM_REDUNDANT_WHOLE_ATOM_QUOTE,
    VERBATIM_REDUNDANT_NEAR_WHOLE_ATOM_QUOTE,
    VERBATIM_MULTI_ATOM_TEXT_IN_ONE_PART,
    VERBATIM_OTHER_FAILURE,
}

/// <summary>
/// Deterministic, provider-free classification of exact-text-binding refusals. Every comparison here
/// is read-only against frozen atoms and a frozen provider quote; nothing it computes is fed back into
/// <see cref="SemanticSourcePartBinder"/> or <see cref="ExactClaimBinderV2_1"/>.
/// </summary>
public static class V5ExactTextBindingAnalyzer
{
    private const int NeighborWindow = 2;
    private const int MultiAtomSearchWindow = 6;

    /// <summary>Whitespace collapsed to one space, trimmed. Diagnostic only.</summary>
    public static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>All whitespace removed. Diagnostic only.</summary>
    public static string RemoveWhitespace(string text) =>
        new(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    /// <summary>A fixed punctuation-equivalence fold (smart/straight quotes, dash variants, common marks). Diagnostic only.</summary>
    public static string NormalizePunctuation(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '‘' or '’' or 'ʼ' => '\'',
                '“' or '”' => '"',
                '–' or '—' or '−' => '-',
                '…' => '.',
                ' ' => ' ',
                _ => ch,
            });
        }
        var folded = builder.ToString();
        return new string(folded.Where(ch => !char.IsPunctuation(ch) || ch is '-').ToArray());
    }

    public static bool ContainsOrdinal(string haystack, string needle) =>
        needle.Length > 0 && haystack.Contains(needle, StringComparison.Ordinal);

    /// <summary>
    /// Analyzes one failing part. <paramref name="atoms"/> is the full document atom catalog (for
    /// neighbor/multi-atom search); <paramref name="visibleAliases"/> is this pack's own visible set
    /// (owned + context-only), never the whole document, matching what the model could actually see.
    /// </summary>
    public static V5ExactTextFailureRow Analyze(
        string documentId, string packId, int proposalOrdinal, string predicate, string endpoint, int partIndex,
        string sourceAlias, string? verbatimText, int? occurrence, string? leftExactContext, string? rightExactContext,
        string refusalReason, IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlySet<string> visibleAliases)
    {
        var byAlias = atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var byOrdinal = atoms.ToDictionary(atom => atom.Ordinal);
        var atom = byAlias[sourceAlias];
        var neighbors = Enumerable.Range(atom.Ordinal - NeighborWindow, NeighborWindow * 2 + 1)
            .Where(ordinal => ordinal != atom.Ordinal && byOrdinal.ContainsKey(ordinal) && visibleAliases.Contains(byOrdinal[ordinal].Alias))
            .Select(ordinal => (Alias: byOrdinal[ordinal].Alias, Text: byOrdinal[ordinal].Text))
            .ToArray<(string Alias, string Text)>();

        var quote = verbatimText ?? string.Empty;
        var ambiguous = refusalReason.Contains(" occurrences of that text, not ", StringComparison.Ordinal) ||
            refusalReason.Contains(" contains that text ", StringComparison.Ordinal) && refusalReason.EndsWith("nothing says which", StringComparison.Ordinal);

        if (ambiguous)
        {
            var hits = CountOccurrences(atom.Text, quote);
            return new V5ExactTextFailureRow(documentId, packId, proposalOrdinal, predicate, endpoint, partIndex,
                sourceAlias, verbatimText, occurrence, leftExactContext, rightExactContext, refusalReason,
                atom.Text, atom.Ordinal, neighbors,
                V5ExactTextFailureCategory.REPEATED_SUBSTRING_WITHOUT_DISAMBIGUATION, V5ExactTextFaultDomain.AMBIGUOUS_EXACT_SELECTION,
                $"'{quote}' occurs {hits} times in {sourceAlias}; occurrence supplied={occurrence?.ToString() ?? "no"}, " +
                $"leftExactContext supplied={(!string.IsNullOrEmpty(leftExactContext)).ToString().ToLowerInvariant()}, " +
                $"rightExactContext supplied={(!string.IsNullOrEmpty(rightExactContext)).ToString().ToLowerInvariant()}",
                new V5ExactTextCounterfactuals(true, false, null, WouldBindWithExistingDisambiguation: hits >= 1,
                    false, false, false, false, false));
        }

        // The quote is present verbatim but the supplied left/right context does not match where it occurs.
        var wrongContext = ContainsOrdinal(atom.Text, quote) && refusalReason.EndsWith("not in the context given", StringComparison.Ordinal);
        if (wrongContext)
        {
            return new V5ExactTextFailureRow(documentId, packId, proposalOrdinal, predicate, endpoint, partIndex,
                sourceAlias, verbatimText, occurrence, leftExactContext, rightExactContext, refusalReason,
                atom.Text, atom.Ordinal, neighbors,
                V5ExactTextFailureCategory.OTHER, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' occurs verbatim in {sourceAlias}, but the supplied leftExactContext/rightExactContext does not match any occurrence",
                new V5ExactTextCounterfactuals(true, false, null, false, false, false, false, false, false));
        }

        // ---- text genuinely absent from the named atom: work through the mechanical explanations ----
        var elsewhere = atoms.Where(item => item.Alias != sourceAlias && visibleAliases.Contains(item.Alias) &&
            ContainsOrdinal(item.Text, quote)).ToArray();
        if (elsewhere.Length == 1)
        {
            return Row(V5ExactTextFailureCategory.WRONG_ALIAS_TEXT, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' does not occur in {sourceAlias} but occurs exactly in visible alias {elsewhere[0].Alias}",
                new V5ExactTextCounterfactuals(true, false, null, false,
                    MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormC), atom.Text, quote),
                    MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormKC), atom.Text, quote),
                    MatchesAfter(CollapseWhitespace, atom.Text, quote), MatchesAfter(RemoveWhitespace, atom.Text, quote),
                    MatchesAfter(NormalizePunctuation, atom.Text, quote)));
        }

        var multiAtom = FindMinimalAliasSequence(atoms, byOrdinal, atom, quote, visibleAliases);
        if (multiAtom is not null)
        {
            return Row(V5ExactTextFailureCategory.MULTI_ATOM_OVERQUOTE, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' spans {sourceAlias} plus {multiAtom.Count - 1} following visible atom(s): {string.Join(" + ", multiAtom)}",
                new V5ExactTextCounterfactuals(true, true, multiAtom, false,
                    MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormC), atom.Text, quote),
                    MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormKC), atom.Text, quote),
                    MatchesAfter(CollapseWhitespace, atom.Text, quote), MatchesAfter(RemoveWhitespace, atom.Text, quote),
                    MatchesAfter(NormalizePunctuation, atom.Text, quote)));
        }

        var nfc = MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormC), atom.Text, quote);
        var nfkc = MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormKC), atom.Text, quote);
        var wsCollapse = MatchesAfter(CollapseWhitespace, atom.Text, quote);
        var wsRemove = MatchesAfter(RemoveWhitespace, atom.Text, quote);
        var punct = MatchesAfter(NormalizePunctuation, atom.Text, quote);
        var caseOnly = ContainsOrdinal(atom.Text.ToUpperInvariant(), quote.ToUpperInvariant());

        var counterfactuals = new V5ExactTextCounterfactuals(true, false, null, false, nfc, nfkc, wsCollapse, wsRemove, punct);
        if (wsRemove && !wsCollapse)
            return Row(V5ExactTextFailureCategory.EXTRACTION_SPACING_DIFFERENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} once all whitespace is removed from both - a spacing-only difference", counterfactuals);
        if (wsCollapse)
            return Row(V5ExactTextFailureCategory.EXTRACTION_SPACING_DIFFERENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} once repeated whitespace is collapsed to a single space", counterfactuals);
        if (nfc || nfkc)
            return Row(V5ExactTextFailureCategory.UNICODE_EQUIVALENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} after Unicode normalization ({(nfc ? "NFC" : "")}{(nfc && nfkc ? "/" : "")}{(nfkc ? "NFKC" : "")}); " +
                DescribeCodePointDifference(atom.Text, quote), counterfactuals);
        if (punct)
            return Row(V5ExactTextFailureCategory.PUNCTUATION_DIFFERENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} once punctuation is folded to a common form", counterfactuals);
        if (caseOnly)
            return Row(V5ExactTextFailureCategory.CASE_DIFFERENCE, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' matches {sourceAlias} except for letter case", counterfactuals);

        var overlap = WordOverlapRatio(atom.Text, quote);
        if (overlap >= 0.5)
            return Row(V5ExactTextFailureCategory.PARTIAL_PARAPHRASE_OR_REWRITE, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' shares {overlap:P0} of its words with {sourceAlias} but is not a normalization-equivalent quote of it", counterfactuals);
        if (overlap <= 0.05)
            return Row(V5ExactTextFailureCategory.COMPLETELY_WRONG_TEXT, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' shares essentially no words with {sourceAlias} and does not occur verbatim in any neighboring visible atom", counterfactuals);

        return Row(V5ExactTextFailureCategory.OTHER, V5ExactTextFaultDomain.MIXED,
            $"'{quote}' partially overlaps {sourceAlias} ({overlap:P0} word overlap) without matching any of the checked mechanical explanations", counterfactuals);

        V5ExactTextFailureRow Row(V5ExactTextFailureCategory category, V5ExactTextFaultDomain domain, string reason, V5ExactTextCounterfactuals cf) =>
            new(documentId, packId, proposalOrdinal, predicate, endpoint, partIndex, sourceAlias, verbatimText, occurrence,
                leftExactContext, rightExactContext, refusalReason, atom.Text, atom.Ordinal, neighbors, category, domain, reason, cf);
    }

    /// <summary>Classifies one part of one endpoint regardless of outcome, for the wire-usage census.</summary>
    public static V5SourcePartUsage ClassifyUsage(
        string? verbatimText, string atomText, bool bound, int? boundStart, int? boundEnd, V5ExactTextFailureCategory? failureCategory)
    {
        if (verbatimText is null) return V5SourcePartUsage.ALIAS_ONLY;
        if (!bound)
            return failureCategory == V5ExactTextFailureCategory.MULTI_ATOM_OVERQUOTE
                ? V5SourcePartUsage.VERBATIM_MULTI_ATOM_TEXT_IN_ONE_PART
                : V5SourcePartUsage.VERBATIM_OTHER_FAILURE;
        if (boundStart == 0 && boundEnd == atomText.Length) return V5SourcePartUsage.VERBATIM_REDUNDANT_WHOLE_ATOM_QUOTE;
        var spanned = atomText[boundStart!.Value..boundEnd!.Value];
        if (CollapseWhitespace(spanned) == CollapseWhitespace(atomText)) return V5SourcePartUsage.VERBATIM_REDUNDANT_NEAR_WHOLE_ATOM_QUOTE;
        return V5SourcePartUsage.VERBATIM_NECESSARY_SUBSTRING;
    }

    private static bool MatchesAfter(Func<string, string> normalize, string atomText, string quote) =>
        quote.Length > 0 && ContainsOrdinal(normalize(atomText), normalize(quote));

    private static int CountOccurrences(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The minimal alias sequence starting at <paramref name="first"/>, in source order, whose
    /// concatenated text contains the quote - or null if no such sequence exists within the search
    /// window. Compares with all whitespace removed on both sides so a genuine multi-atom span is
    /// still found even when the source also has an independent spacing difference (both are common
    /// PdfPig artifacts and can co-occur in the same quote); this is a diagnostic comparison only.</summary>
    private static IReadOnlyList<string>? FindMinimalAliasSequence(
        IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlyDictionary<int, SemanticSourceAtom> byOrdinal,
        SemanticSourceAtom first, string quote, IReadOnlySet<string> visibleAliases)
    {
        var normalizedQuote = RemoveWhitespace(quote);
        if (normalizedQuote.Length == 0) return null;
        // The quote must genuinely start with this atom's own content (whitespace aside) for an
        // overquote reading to make sense - otherwise the atom is simply unrelated to the quote.
        if (!normalizedQuote.StartsWith(RemoveWhitespace(first.Text), StringComparison.Ordinal))
            return null;

        var sequence = new List<string> { first.Alias };
        var combined = new StringBuilder(first.Text);
        for (var ordinal = first.Ordinal + 1; ordinal < first.Ordinal + 1 + MultiAtomSearchWindow; ordinal++)
        {
            if (!byOrdinal.TryGetValue(ordinal, out var next) || !visibleAliases.Contains(next.Alias)) break;
            combined.Append(' ').Append(next.Text);
            sequence.Add(next.Alias);
            if (RemoveWhitespace(combined.ToString()).Contains(normalizedQuote, StringComparison.Ordinal))
                return sequence;
        }
        return null;
    }

    private static double WordOverlapRatio(string atomText, string quote)
    {
        var atomWords = Words(atomText);
        var quoteWords = Words(quote);
        if (quoteWords.Count == 0) return 0;
        return quoteWords.Count(word => atomWords.Contains(word)) / (double)quoteWords.Count;
    }

    private static HashSet<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())
            .Where(word => word.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string DescribeCodePointDifference(string atomText, string quote)
    {
        var i = 0;
        while (i < atomText.Length && i < quote.Length && atomText[i] == quote[i]) i++;
        if (i >= atomText.Length || i >= quote.Length) return "difference at end of string";
        return $"first differing code point at index {i}: U+{(int)atomText[i]:X4} vs U+{(int)quote[i]:X4}";
    }
}
