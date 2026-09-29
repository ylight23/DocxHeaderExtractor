namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Deterministic, machine-readable statement of the three legal provider-wire shapes for a source
/// selection - whole atom, strict substring, and a region spanning several atoms - carried in the
/// v2.1 request itself rather than left implicit in prose. Generic Core: no document-specific or
/// heading vocabulary, no document contract input, so it never drifts per-document and never needs
/// a document-specific test fixture to exercise.
/// <para>
/// This exists because the offline audit of the 31-pack cohort's 160 exact-text-binding refusals
/// found the model routinely re-typing a whole atom's text into <c>verbatimText</c> - 502 of 534
/// successful verbatim uses were exactly the whole atom, and most of the 169 part-level failures
/// were the model's own re-typed copy differing from the source by spacing, Unicode form or
/// punctuation (74 + 37 + 4 of 169) rather than a genuine multi-atom span (20 of 169). None of that
/// is a binder problem: the wire already supports alias-only and multi-part selection, the model
/// simply was not being told, structurally, that copying text is exceptional. This policy and the
/// accompanying instruction wording is the fix; the exact binder itself is untouched.
/// </para>
/// </summary>
public static class V5SourceSelectionPolicy
{
    public const string Version = "v5-source-selection-policy-1";
    public const string WholeAtomMode = "WHOLE_ATOM_ALIAS_ONLY";

    public static object Generate() => new
    {
        version = Version,
        @default = WholeAtomMode,
        wholeAtom = new
        {
            when = "the intended source selection is the entire named atom",
            shape = new { sourceAlias = "<alias>" },
            verbatimText = "MUST_BE_OMITTED",
        },
        strictSubstring = new
        {
            when = "the intended source selection is strictly smaller than the named atom",
            shape = new { sourceAlias = "<alias>", verbatimText = "<exact substring copied byte-for-byte from the atom text>" },
            rule = "never rewrite, normalize, repair, respell, re-space or paraphrase; copy the substring exactly as it appears",
        },
        multiAtomRegion = new
        {
            when = "the intended source selection spans more than one evidence atom",
            shape = new object[]
            {
                new { sourceAlias = "<first>" },
                new { sourceAlias = "<second>" },
            },
            rule = "emit one sourcePart per contributing alias, in source order; never combine more than one atom's text into a single sourcePart",
            boundaryRule = "a fully-included atom (first, middle or last) uses sourceAlias alone; only a boundary atom that is genuinely partially included may carry verbatimText",
        },
    };
}
