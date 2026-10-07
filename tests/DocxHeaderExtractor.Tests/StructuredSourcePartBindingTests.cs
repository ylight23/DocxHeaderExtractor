using DocxHeaderExtractor.Core.Semantics.Canonical;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Structured source-part binding: a semantic claim as an ordered list of exact selections over
/// coordinate atoms, rather than as one alias.
/// <para>
/// The three shapes a heading takes are one contract here. "World Bank" is one whole atom, a run-in
/// heading is part of one atom, and a heading that wraps is several - and none of them needs the
/// parser's block boundary to agree with the semantic one. That assumption is what produced both
/// failure modes this work has been chasing, and naming only aliases would reproduce it one level
/// further down, because a segment can still hold a heading followed by body text.
/// </para>
/// <para>
/// Shadow throughout. The active contract, universe and evaluator are untouched; nothing transports
/// under this schema and no authority is migrated onto it.
/// </para>
/// </summary>
public sealed class StructuredSourcePartBindingTests
{
    private const string Doc0252 = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Artifacts = "eval/a99-closed-loop/representation";
    private const int ApprovedHeadings = 41;
    private const string ActiveSemanticContractHash =
        "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e";

    // ---- one atom -------------------------------------------------------------------------------

    [Fact]
    public void A_heading_that_is_a_whole_atom_binds_as_one_part()
    {
        var atoms = Page(("World Bank", 0, 0), ("Haishan Fu, World Bank, presented", 1, 0));

        var bound = Bind(atoms, Whole("L0000:S0"));

        Assert.True(bound.IsBound);
        Assert.Equal("L0000:S0:0-10", bound.Identity);
        Assert.Equal("World Bank", SemanticSourceProjection.Render(bound.Parts));
    }

    [Fact]
    public void A_run_in_heading_binds_as_part_of_one_atom()
    {
        // The case a segment cannot be made small enough to solve. The heading is a prefix of a
        // line that continues into prose, so partial selection has to survive into the new model.
        var atoms = Page(("Construction Wages. Prices were collected quarterly.", 0, 0));

        var bound = Bind(atoms, Verbatim("L0000:S0", "Construction Wages"));

        Assert.True(bound.IsBound);
        Assert.Equal("L0000:S0:0-18", bound.Identity);
        Assert.Equal("Construction Wages", bound.Parts[0].Text);
    }

    // ---- several atoms --------------------------------------------------------------------------

    [Fact]
    public void A_heading_that_wraps_binds_across_two_rows()
    {
        var atoms = Page(
            ("2. A Survey Based Approach in International Price", 0, 0),
            ("Comparisons", 1, 0),
            ("Professor Abe presented a research paper", 2, 0));

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0001:S0"));

        Assert.True(bound.IsBound);
        Assert.Equal("L0000:S0:0-49|L0001:S0:0-11", bound.Identity);
        Assert.Equal(SemanticSourceLocality.NextRowCompatible, bound.Parts[1].LocalityFromPrevious);
        Assert.Equal(
            "2. A Survey Based Approach in International Price Comparisons",
            SemanticSourceProjection.Render(bound.Parts));
    }

    [Fact]
    public void A_bullet_and_its_text_are_two_segments_of_one_row()
    {
        var atoms = Page(("–", 0, 0), ("Paul Schreyer, OECD", 0, 1), ("–", 1, 0), ("Paul Konijn", 1, 1));

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0000:S1"));

        Assert.True(bound.IsBound);
        Assert.Equal(SemanticSourceLocality.SameRowNextSegment, bound.Parts[1].LocalityFromPrevious);
        Assert.Equal("– Paul Schreyer, OECD", SemanticSourceProjection.Render(bound.Parts));
    }

    [Fact]
    public void A_word_broken_across_rows_keeps_its_hyphen()
    {
        // Rendering joins without a space, and the hyphen stays. Nothing in the geometry tells a
        // word broken at a line end from a compound that was already hyphenated, and keeping the
        // character is the choice that can be undone.
        var atoms = Page(("interna-", 0, 0), ("tional comparisons", 1, 0));

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0001:S0"));

        Assert.True(bound.IsBound);
        Assert.Equal("interna-tional comparisons", SemanticSourceProjection.Render(bound.Parts));
        Assert.Equal(["interna-", "tional comparisons"], bound.Parts.Select(part => part.Text).ToArray());
    }

    [Fact]
    public void Rendering_is_never_the_identity()
    {
        // Two different selections that read the same are still two different claims.
        var atoms = Page(("Annex 2", 0, 0), ("Annex 2", 1, 0));

        var first = Bind(atoms, Whole("L0000:S0"));
        var second = Bind(atoms, Whole("L0001:S0"));

        Assert.Equal(
            SemanticSourceProjection.Render(first.Parts),
            SemanticSourceProjection.Render(second.Parts));
        Assert.NotEqual(first.Identity, second.Identity);
    }

    // ---- GENERIC_MULTIPART_BINDER_V2: explicit parts are validated, never discovered ------------

    [Fact]
    public void Explicit_non_adjacent_parts_on_one_page_bind_exactly()
    {
        // Two-column layout: the title's second line resumes after a row of the other column.
        var atoms = Page(("Part One Title", 0, 0), ("other column text", 1, 0), ("continued here", 2, 0));

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0002:S0"));

        Assert.True(bound.IsBound);
        Assert.Equal("L0000:S0:0-14|L0002:S0:0-14", bound.Identity);
        Assert.Equal(SemanticSourceLocality.SamePageNonAdjacent, bound.Parts[1].LocalityFromPrevious);
    }

    [Fact]
    public void Explicit_cross_page_parts_bind_exactly()
    {
        var atoms = new[]
        {
            Atom("L0000:S0", 0, page: 1, row: 7, segment: 0, "Clause 9.9"),
            Atom("L0001:S0", 1, page: 1, row: 8, segment: 0, "body before the break"),
            Atom("L0002:S0", 2, page: 2, row: 0, segment: 0, "Title After The Break"),
        };

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0002:S0"));

        Assert.True(bound.IsBound);
        Assert.Equal("L0000:S0:0-10|L0002:S0:0-21", bound.Identity);
        Assert.Equal(SemanticSourceLocality.CrossPage, bound.Parts[1].LocalityFromPrevious);
    }

    [Fact]
    public void An_intervening_atom_is_not_pulled_into_the_claim()
    {
        // The binder is not a nearest-neighbour completer: the claim is exactly the parts named.
        var atoms = Page(("Alpha", 0, 0), ("unrelated middle", 1, 0), ("Omega", 2, 0));

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0002:S0"));

        Assert.Equal(["L0000:S0", "L0002:S0"], bound.Parts.Select(part => part.Alias));
        Assert.DoesNotContain("unrelated", SemanticSourceProjection.Render(bound.Parts));
    }

    [Fact]
    public void Explicit_distant_parts_still_fail_every_exactness_rule()
    {
        var atoms = new[]
        {
            Atom("L0000:S0", 0, page: 1, row: 0, segment: 0, "First Line Of A Title"),
            Atom("L0001:S0", 1, page: 1, row: 1, segment: 0, "body"),
            Atom("L0002:S0", 2, page: 2, row: 0, segment: 0, "Second Line"),
        };

        Assert.Equal(SemanticSourcePartsStatus.UnknownAlias,
            Bind(atoms, Whole("L0000:S0"), Whole("L0009:S0")).Status);
        Assert.Equal(SemanticSourcePartsStatus.TextNotInAtom,
            Bind(atoms, Whole("L0000:S0"), Verbatim("L0002:S0", "Second  Line")).Status);
        Assert.Equal(SemanticSourcePartsStatus.OutOfSourceOrder,
            Bind(atoms, Whole("L0002:S0"), Whole("L0000:S0")).Status);
        Assert.Equal(SemanticSourcePartsStatus.OverlappingParts,
            Bind(atoms, Verbatim("L0000:S0", "First Line Of"), Verbatim("L0000:S0", "Of A Title"), Whole("L0002:S0")).Status);
    }

    [Fact]
    public void A_source_whose_hash_does_not_match_is_refused_before_binding()
    {
        // The binder takes no hash; the pipeline that feeds it does, and refuses the whole source.
        var error = Assert.Throws<InvalidOperationException>(() => CanonicalSemanticPipeline.RunAliases(
            [], [], new string('a', 64), new string('b', 64), binding: SemanticCoordinateBinding.SourceParts,
            atoms: Page(("Title", 0, 0))));
        Assert.Equal("source-hash-mismatch", error.Message);
    }

    // ---- what is refused ------------------------------------------------------------------------

    [Fact]
    public void Parts_in_the_wrong_order_are_refused_rather_than_sorted()
    {
        var atoms = Page(("International Price", 0, 0), ("Comparisons", 1, 0));

        var bound = Bind(atoms, Whole("L0001:S0"), Whole("L0000:S0"));

        Assert.Equal(SemanticSourcePartsStatus.OutOfSourceOrder, bound.Status);
    }

    [Fact]
    public void The_same_selection_twice_is_refused()
    {
        var atoms = Page(("World Bank", 0, 0));

        Assert.Equal(
            SemanticSourcePartsStatus.DuplicatePart,
            Bind(atoms, Whole("L0000:S0"), Whole("L0000:S0")).Status);
    }

    [Fact]
    public void Selections_that_overlap_are_refused()
    {
        var atoms = Page(("Session IV: TAG Functioning", 0, 0));

        Assert.Equal(
            SemanticSourcePartsStatus.OverlappingParts,
            Bind(atoms,
                Verbatim("L0000:S0", "Session IV: TAG"),
                Verbatim("L0000:S0", "TAG Functioning")).Status);
    }

    [Fact]
    public void Text_that_occurs_twice_in_one_atom_must_say_which()
    {
        var atoms = Page(("Africa and then Africa again", 0, 0));

        Assert.Equal(
            SemanticSourcePartsStatus.AmbiguousSelection,
            Bind(atoms, Verbatim("L0000:S0", "Africa")).Status);

        var byOccurrence = Bind(atoms, Verbatim("L0000:S0", "Africa") with { Occurrence = 2 });
        Assert.True(byOccurrence.IsBound);
        Assert.Equal(16, byOccurrence.Parts[0].Start);

        var byContext = Bind(atoms,
            Verbatim("L0000:S0", "Africa") with { RightExactContext = " again" });
        Assert.True(byContext.IsBound);
        Assert.Equal(16, byContext.Parts[0].Start);
    }

    [Fact]
    public void Text_the_atom_does_not_contain_is_refused_rather_than_repaired()
    {
        var atoms = Page(("1. Global office update", 0, 0));

        Assert.Equal(
            SemanticSourcePartsStatus.TextNotInAtom,
            Bind(atoms, Verbatim("L0000:S0", "1 Global office update")).Status);
        Assert.Equal(
            SemanticSourcePartsStatus.UnknownAlias,
            Bind(atoms, Whole("L9999:S0")).Status);
    }

    [Fact]
    public void Binding_is_deterministic()
    {
        var atoms = Page(("International Price", 0, 0), ("Comparisons", 1, 0));
        var proposal = Proposal(Whole("L0000:S0"), Whole("L0001:S0"));

        Assert.Equal(
            SemanticSourcePartBinder.Bind(atoms, proposal).Identity,
            SemanticSourcePartBinder.Bind(atoms, proposal).Identity);
    }

    // ---- DOC-0252, with no blocks anywhere -------------------------------------------------------

    // ---- helpers ---------------------------------------------------------------------------------

    private static SemanticSourcePartsBinding Bind(
        IReadOnlyList<SemanticSourceAtom> atoms, params SemanticSourcePart[] parts) =>
        SemanticSourcePartBinder.Bind(atoms, Proposal(parts));

    private static SemanticSourcePartsProposal Proposal(params SemanticSourcePart[] parts) =>
        new(parts, IsHeading: true);

    private static SemanticSourcePart Whole(string alias) =>
        new(alias, CanonicalSemanticSelectionMode.WholeAlias);

    private static SemanticSourcePart Verbatim(string alias, string text) =>
        new(alias, CanonicalSemanticSelectionMode.VerbatimText, text);

    private static SemanticSourceAtom Atom(
        string alias, int ordinal, int page, int row, int segment, string text) =>
        new(alias, $"p{page}:{ordinal}", ordinal, page, row, segment, text);

    private static SemanticSourceAtom[] Page(params (string Text, int Row, int Segment)[] entries) =>
        entries.Select((entry, index) => Atom(
            $"L{entry.Row:0000}:S{entry.Segment}", index, 1, entry.Row, entry.Segment, entry.Text)).ToArray();

    private static string Doc0252Path => System.IO.Path.Combine(
        TestRepository.Root(), Doc0252.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<PdfLine> Segments()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        return PdfLineExtraction.ExtractLines(document);
    }

    private static IReadOnlyList<SemanticSourceAtom> Doc0252Atoms() =>
        PdfSegmentAtomCatalog.FromSegments(Segments());

    private static string Reduce(string text) =>
        new(text.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character))
            .Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// How often the corpus offers a local chain at all, and how long it can get. Descriptive: a
    /// chain being available says nothing about whether any heading uses it.
    /// </summary>
    private static object CorpusChains()
    {
        var pdfs = Directory
            .GetFiles(System.IO.Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100"),
                "*.pdf", SearchOption.AllDirectories)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        int documents = 0, atomsTotal = 0, sameRow = 0, nextRow = 0, hyphenEnding = 0;
        var chains = new Dictionary<int, int>();

        foreach (var path in pdfs)
        {
            IReadOnlyList<SemanticSourceAtom> atoms;
            try
            {
                using var document = PdfDocument.Open(path);
                atoms = PdfSegmentAtomCatalog.FromSegments(
                    PdfLineExtraction.ExtractLines(document));
            }
            catch (Exception)
            {
                continue;
            }

            documents++;
            atomsTotal += atoms.Count;

            var run = 1;
            for (var index = 1; index < atoms.Count; index++)
            {
                var locality = SemanticSourcePartBinder.Locality(atoms[index - 1], atoms[index]);
                if (locality == SemanticSourceLocality.SameRowNextSegment) sameRow++;
                if (locality == SemanticSourceLocality.NextRowCompatible) nextRow++;
                if (atoms[index - 1].Text.EndsWith('-')) hyphenEnding++;

                if (locality is SemanticSourceLocality.SameRowNextSegment or SemanticSourceLocality.NextRowCompatible)
                {
                    run++;
                }
                else
                {
                    chains[run] = chains.GetValueOrDefault(run) + 1;
                    run = 1;
                }
            }

            chains[run] = chains.GetValueOrDefault(run) + 1;
        }

        return new
        {
            note = "Descriptive only. These are the chains the locality rule would admit, not headings.",
            documents,
            atoms = atomsTotal,
            adjacentPairsSameRow = sameRow,
            adjacentPairsNextRow = nextRow,
            partsEndingInAHyphen = hyphenEnding,
            maximalLocalChains = new
            {
                length1 = chains.GetValueOrDefault(1),
                length2 = chains.GetValueOrDefault(2),
                length3 = chains.GetValueOrDefault(3),
                length4Plus = chains.Where(entry => entry.Key >= 4).Sum(entry => entry.Value),
                longest = chains.Count == 0 ? 0 : chains.Keys.Max(),
            },
        };
    }
}
