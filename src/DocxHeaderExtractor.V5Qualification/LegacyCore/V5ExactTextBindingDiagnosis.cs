using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Offline decomposition of one <see cref="V5BindingQualifier.FamilyExactTextBinding"/> refusal - a
/// provider quote that failed exact binding against the source atom its own sourceAlias named. This
/// never repairs, corrects or fuzzy-matches anything; it only explains, deterministically, why the
/// exact binder was right to refuse. One row per failing source part, not per claim: a claim whose
/// subject and object both fail gets two rows.
/// <para>
/// Precedence (v2, corrected): A ambiguity/bad-context, B exact match on another visible alias, C
/// same-atom mechanical equivalence (NFC/NFKC/whitespace/punctuation/case on the NAMED atom alone),
/// D true multi-atom span (only once C has been exhausted), E paraphrase/wrong/other. v1 checked
/// multi-atom before the same-atom mechanical checks, so any same-atom spacing/Unicode/punctuation
/// difference with a following visible atom present was misclassified as multi-atom; v1's artifact is
/// historical and frozen with that bug, not corrected in place.
/// </para>
/// </summary>
public enum V5ExactTextFailureCategory
{
    /// <summary>The quoted text does not belong to the named alias but matches exactly one other visible alias.</summary>
    WRONG_ALIAS_TEXT,

    /// <summary>Named atom alone cannot explain the quote under any mechanical transform, but a genuine
    /// contiguous sequence of two or more visible atoms does, with the later atom(s) proven necessary.</summary>
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

/// <summary>Whether the NAMED atom alone, under each mechanical transform, explains the quote. Always
/// computed and preserved regardless of which category ultimately wins.</summary>
public sealed record V5SameAtomEquivalence(
    bool Exact, bool Nfc, bool Nfkc, bool WhitespaceCollapse, bool WhitespaceRemoval, bool Punctuation, bool CaseFold)
{
    public bool AnyMechanicalMatch => Nfc || Nfkc || WhitespaceCollapse || WhitespaceRemoval || Punctuation || CaseFold;
}

/// <summary>How a genuine multi-atom span, once found, needed to be compared. Null fields mean no
/// multi-atom span was found at all.</summary>
public sealed record V5MultiAtomEquivalence(bool Exact, bool WhitespaceNormalized, bool UnicodeNormalized);

public sealed record V5ExactTextCounterfactuals(
    /// <summary>The named alias exists in the atom catalog. A syntactic fact, not evidence the model
    /// pointed at the right thing - see <see cref="WholeAliasRepresentsSameObservedSelection"/>.</summary>
    bool NamedAliasWouldSyntacticallyBind,
    /// <summary>True when treating this endpoint as the whole named atom (no verbatimText) would keep
    /// the same real-world selection the model was evidently trying to make - false for
    /// WRONG_ALIAS_TEXT and COMPLETELY_WRONG_TEXT (dropping verbatimText there would silently accept
    /// the wrong atom), null when genuinely unknown.</summary>
    bool? WholeAliasRepresentsSameObservedSelection,
    /// <summary>A corrected, >=2-alias sequence genuinely accounts for the quote (later atom proven necessary).</summary>
    bool MultipartSpanExplainsQuote,
    /// <summary>The canonical alias-only sourceParts for that sequence were verified through the real binder.</summary>
    bool MultipartAliasSequenceWouldBind,
    IReadOnlyList<string>? MinimalAliasSequence,
    /// <summary>The last atom in <see cref="MinimalAliasSequence"/> is provably required: the sequence
    /// without it does not explain the quote. False whenever <see cref="MinimalAliasSequence"/> is null.</summary>
    bool LaterAtomContribution,
    bool WouldBindWithExistingDisambiguation,
    V5SameAtomEquivalence SameAtom,
    V5MultiAtomEquivalence? MultiAtom)
{
    /// <summary>Back-compat alias for the v1 field name; identical to <see cref="NamedAliasWouldSyntacticallyBind"/>.</summary>
    public bool WouldMatchAfterNfc => SameAtom.Nfc;
    public bool WouldMatchAfterNfkc => SameAtom.Nfkc;
    public bool WouldMatchAfterWhitespaceCollapse => SameAtom.WhitespaceCollapse;
    public bool WouldMatchAfterWhitespaceRemoval => SameAtom.WhitespaceRemoval;
    public bool WouldMatchAfterPunctuationNormalization => SameAtom.Punctuation;
}

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
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var byOrdinal = atoms.ToDictionary(a => a.Ordinal);
        var atom = byAlias[sourceAlias];
        var neighbors = Enumerable.Range(atom.Ordinal - NeighborWindow, NeighborWindow * 2 + 1)
            .Where(ordinal => ordinal != atom.Ordinal && byOrdinal.ContainsKey(ordinal) && visibleAliases.Contains(byOrdinal[ordinal].Alias))
            .Select(ordinal => (Alias: byOrdinal[ordinal].Alias, Text: byOrdinal[ordinal].Text))
            .ToArray<(string Alias, string Text)>();

        var quote = verbatimText ?? string.Empty;

        V5ExactTextFailureRow Row(V5ExactTextFailureCategory category, V5ExactTextFaultDomain domain, string reason, V5ExactTextCounterfactuals cf) =>
            new(documentId, packId, proposalOrdinal, predicate, endpoint, partIndex, sourceAlias, verbatimText, occurrence,
                leftExactContext, rightExactContext, refusalReason, atom.Text, atom.Ordinal, neighbors, category, domain, reason, cf);

        // ---- A: ambiguity / bad context (unchanged from v1) ----------------------------------------
        var ambiguous = refusalReason.Contains(" occurrences of that text, not ", StringComparison.Ordinal) ||
            (refusalReason.Contains(" contains that text ", StringComparison.Ordinal) && refusalReason.EndsWith("nothing says which", StringComparison.Ordinal));
        if (ambiguous)
        {
            var hits = CountOccurrences(atom.Text, quote);
            var same = ComputeSameAtomEquivalence(atom.Text, quote) with { Exact = true };
            return Row(V5ExactTextFailureCategory.REPEATED_SUBSTRING_WITHOUT_DISAMBIGUATION, V5ExactTextFaultDomain.AMBIGUOUS_EXACT_SELECTION,
                $"'{quote}' occurs {hits} times in {sourceAlias}; occurrence supplied={occurrence?.ToString() ?? "no"}, " +
                $"leftExactContext supplied={(!string.IsNullOrEmpty(leftExactContext)).ToString().ToLowerInvariant()}, " +
                $"rightExactContext supplied={(!string.IsNullOrEmpty(rightExactContext)).ToString().ToLowerInvariant()}",
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, WouldBindWithExistingDisambiguation: hits >= 1, same, null));
        }

        var wrongContext = ContainsOrdinal(atom.Text, quote) && refusalReason.EndsWith("not in the context given", StringComparison.Ordinal);
        if (wrongContext)
        {
            var same = ComputeSameAtomEquivalence(atom.Text, quote) with { Exact = true };
            return Row(V5ExactTextFailureCategory.OTHER, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' occurs verbatim in {sourceAlias}, but the supplied leftExactContext/rightExactContext does not match any occurrence",
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, false, same, null));
        }

        // ---- B: exact (unnormalized) match on exactly one other visible alias ----------------------
        var elsewhere = atoms.Where(item => item.Alias != sourceAlias && visibleAliases.Contains(item.Alias) &&
            ContainsOrdinal(item.Text, quote)).ToArray();
        var sameAtom = ComputeSameAtomEquivalence(atom.Text, quote);
        if (elsewhere.Length == 1)
        {
            return Row(V5ExactTextFailureCategory.WRONG_ALIAS_TEXT, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' does not occur in {sourceAlias} but occurs exactly in visible alias {elsewhere[0].Alias}",
                new V5ExactTextCounterfactuals(true, false, false, false, null, false, false, sameAtom, null));
        }

        // ---- C: same-atom mechanical equivalence, checked BEFORE any multi-atom search --------------
        // This is the v1 fix: a spacing/Unicode/punctuation/case difference fully explained by the
        // named atom alone must never be shadowed by an incidental multi-atom substring match.
        if (sameAtom.WhitespaceRemoval && !sameAtom.WhitespaceCollapse)
            return Row(V5ExactTextFailureCategory.EXTRACTION_SPACING_DIFFERENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} once all whitespace is removed from both - a spacing-only difference",
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, false, sameAtom, null));
        if (sameAtom.WhitespaceCollapse)
            return Row(V5ExactTextFailureCategory.EXTRACTION_SPACING_DIFFERENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} once repeated whitespace is collapsed to a single space",
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, false, sameAtom, null));
        if (sameAtom.Nfc || sameAtom.Nfkc)
            return Row(V5ExactTextFailureCategory.UNICODE_EQUIVALENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} after Unicode normalization ({(sameAtom.Nfc ? "NFC" : "")}{(sameAtom.Nfc && sameAtom.Nfkc ? "/" : "")}{(sameAtom.Nfkc ? "NFKC" : "")}); " +
                DescribeCodePointDifference(atom.Text, quote),
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, false, sameAtom, null));
        if (sameAtom.Punctuation)
            return Row(V5ExactTextFailureCategory.PUNCTUATION_DIFFERENCE, V5ExactTextFaultDomain.SOURCE_REPRESENTATION,
                $"'{quote}' matches {sourceAlias} once punctuation is folded to a common form",
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, false, sameAtom, null));
        if (sameAtom.CaseFold)
            return Row(V5ExactTextFailureCategory.CASE_DIFFERENCE, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' matches {sourceAlias} except for letter case",
                new V5ExactTextCounterfactuals(true, true, false, false, null, false, false, sameAtom, null));

        // ---- D: true multi-atom span - only reached once every same-atom explanation has failed -----
        var multipart = FindMinimalAliasSequence(byOrdinal, atom, quote, visibleAliases);
        if (multipart is not null)
        {
            var (sequence, laterAtomContribution, multiAtomEq) = multipart.Value;
            var wouldBind = laterAtomContribution && VerifyAliasOnlySequenceBinds(atoms, sequence);
            return Row(V5ExactTextFailureCategory.MULTI_ATOM_OVERQUOTE, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' spans {sourceAlias} plus {sequence.Count - 1} following visible atom(s): {string.Join(" + ", sequence)}" +
                (laterAtomContribution ? "" : " (later atom did not prove necessary - not treated as multi-atom)"),
                new V5ExactTextCounterfactuals(true, true, laterAtomContribution, wouldBind, sequence, laterAtomContribution,
                    false, sameAtom, multiAtomEq));
        }

        // ---- E: paraphrase / wrong / other -----------------------------------------------------------
        var overlap = WordOverlapRatio(atom.Text, quote);
        if (overlap >= 0.5)
            return Row(V5ExactTextFailureCategory.PARTIAL_PARAPHRASE_OR_REWRITE, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' shares {overlap:P0} of its words with {sourceAlias} but is not a normalization-equivalent quote of it",
                new V5ExactTextCounterfactuals(true, false, false, false, null, false, false, sameAtom, null));
        if (overlap <= 0.05)
            return Row(V5ExactTextFailureCategory.COMPLETELY_WRONG_TEXT, V5ExactTextFaultDomain.MODEL_REFERENCE,
                $"'{quote}' shares essentially no words with {sourceAlias} and does not occur verbatim in any neighboring visible atom",
                new V5ExactTextCounterfactuals(true, false, false, false, null, false, false, sameAtom, null));

        return Row(V5ExactTextFailureCategory.OTHER, V5ExactTextFaultDomain.MIXED,
            $"'{quote}' partially overlaps {sourceAlias} ({overlap:P0} word overlap) without matching any of the checked mechanical explanations",
            new V5ExactTextCounterfactuals(true, null, false, false, null, false, false, sameAtom, null));
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

    private static V5SameAtomEquivalence ComputeSameAtomEquivalence(string atomText, string quote) => new(
        Exact: ContainsOrdinal(atomText, quote),
        Nfc: MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormC), atomText, quote),
        Nfkc: MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormKC), atomText, quote),
        WhitespaceCollapse: MatchesAfter(CollapseWhitespace, atomText, quote),
        WhitespaceRemoval: MatchesAfter(RemoveWhitespace, atomText, quote),
        Punctuation: MatchesAfter(NormalizePunctuation, atomText, quote),
        CaseFold: ContainsOrdinal(atomText.ToUpperInvariant(), quote.ToUpperInvariant()));

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

    /// <summary>
    /// The minimal alias sequence starting at <paramref name="first"/>, in source order, whose
    /// concatenated text contains the quote under whitespace removal - only called once the caller has
    /// already confirmed <paramref name="first"/> alone cannot explain the quote under any mechanical
    /// transform. Explicitly re-verifies that the last appended atom is necessary (removing it leaves
    /// the shorter combination unable to explain the quote) before returning a sequence, so a
    /// same-atom coincidence with a following atom can never be mistaken for a real span.
    /// </summary>
    private static (IReadOnlyList<string> Sequence, bool LaterAtomContribution, V5MultiAtomEquivalence Equivalence)? FindMinimalAliasSequence(
        IReadOnlyDictionary<int, SemanticSourceAtom> byOrdinal, SemanticSourceAtom first, string quote, IReadOnlySet<string> visibleAliases)
    {
        var normalizedQuote = RemoveWhitespace(quote);
        if (normalizedQuote.Length == 0) return null;
        var normalizedFirst = RemoveWhitespace(first.Text);
        // The quote must genuinely start with this atom's own content (whitespace aside) for an
        // overquote reading to make sense - otherwise the atom is simply unrelated to the quote. The
        // caller has already ruled out normalizedFirst == normalizedQuote (that would have matched a
        // same-atom mechanical check), so a strict prefix is the only remaining multi-atom case.
        if (!normalizedQuote.StartsWith(normalizedFirst, StringComparison.Ordinal) || normalizedQuote.Length <= normalizedFirst.Length)
            return null;

        var sequence = new List<string> { first.Alias };
        var previousNormalized = normalizedFirst;
        var combinedRaw = new StringBuilder(first.Text);
        for (var ordinal = first.Ordinal + 1; ordinal < first.Ordinal + 1 + MultiAtomSearchWindow; ordinal++)
        {
            if (!byOrdinal.TryGetValue(ordinal, out var next) || !visibleAliases.Contains(next.Alias)) break;
            combinedRaw.Append(' ').Append(next.Text);
            var combinedNormalized = RemoveWhitespace(combinedRaw.ToString());
            sequence.Add(next.Alias);
            if (combinedNormalized.Contains(normalizedQuote, StringComparison.Ordinal))
            {
                // Later-atom necessity, proven rather than assumed: the sequence without this last
                // atom must NOT already have explained the quote (it did not, or we would already
                // have returned on the previous iteration - this is a defensive re-check).
                var laterAtomContribution = !previousNormalized.Contains(normalizedQuote, StringComparison.Ordinal);
                var equivalence = new V5MultiAtomEquivalence(
                    Exact: ContainsOrdinal(combinedRaw.ToString(), quote),
                    WhitespaceNormalized: true,
                    UnicodeNormalized: MatchesAfter(t => t.Normalize(System.Text.NormalizationForm.FormKC), combinedRaw.ToString(), quote));
                return (sequence, laterAtomContribution, equivalence);
            }
            previousNormalized = combinedNormalized;
        }
        return null;
    }

    /// <summary>Verifies, through the real binder, that the alias-only (no verbatimText) sourceParts
    /// for this exact sequence would bind - never applied to production, diagnostic confirmation only.</summary>
    private static bool VerifyAliasOnlySequenceBinds(IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlyList<string> sequence)
    {
        var parts = sequence.Select(alias => new SemanticSourcePart(alias, CanonicalSemanticSelectionMode.WholeAlias)).ToArray();
        return SemanticSourcePartBinder.Bind(atoms, parts).IsBound;
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
