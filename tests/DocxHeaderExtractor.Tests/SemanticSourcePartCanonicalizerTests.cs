using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The coordinate mode, derived rather than declared.
/// <para>
/// A model that names an occurrence and quotes the words it means has said everything needed. Which
/// of the two selection modes that amounts to is a comparison against the source, and asking the
/// model to perform it cost four approved headings in one measured run - named correctly, quoted
/// correctly, refused for saying WHOLE_ALIAS while carrying the quote.
/// </para>
/// <para>
/// Refusal stays real. A quote that is not in the atom it names, or that is in it twice with
/// nothing to say which, is refused rather than widened into a whole-atom claim the model never made.
/// </para>
/// </summary>
public sealed class SemanticSourcePartCanonicalizerTests
{
    private const string Doc0252Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    // ---- the five rules --------------------------------------------------------------------------

    [Fact]
    public void An_alias_with_no_quote_means_the_whole_occurrence()
    {
        var atoms = Atoms("Chapter One");

        var canonical = Canonicalize(atoms, Part("L0000:S0"));

        var part = Assert.Single(canonical.Parts);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode);
        Assert.Null(part.VerbatimText);
    }

    [Fact]
    public void A_quote_equal_to_the_whole_occurrence_is_the_whole_occurrence()
    {
        // The shape that was refused in the field: the same claim stated twice.
        var atoms = Atoms("Chapter One");

        var canonical = Canonicalize(atoms, Part("L0000:S0", "Chapter One"));

        var part = Assert.Single(canonical.Parts);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode);
        Assert.Null(part.VerbatimText);
    }

    [Fact]
    public void A_quote_that_is_part_of_the_occurrence_stays_a_quote()
    {
        var atoms = Atoms("Chapter One. The rest of the paragraph follows here.");

        var canonical = Canonicalize(atoms, Part("L0000:S0", "Chapter One"));

        var part = Assert.Single(canonical.Parts);
        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, part.SelectionMode);
        Assert.Equal("Chapter One", part.VerbatimText);
    }

    [Fact]
    public void A_quote_that_is_not_in_the_occurrence_is_refused()
    {
        var atoms = Atoms("Chapter One");

        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, [Part("L0000:S0", "Chapter Two")]);

        Assert.False(canonical.IsCanonical);
        Assert.Equal(SemanticSourcePartsStatus.TextNotInAtom, canonical.Status);
        Assert.Empty(canonical.Parts);
    }

    [Fact]
    public void A_quote_that_occurs_twice_with_nothing_to_choose_is_refused()
    {
        var atoms = Atoms("Africa Gregoire and the Development Bank in Africa");

        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, [Part("L0000:S0", "Africa")]);

        Assert.False(canonical.IsCanonical);
        Assert.Equal(SemanticSourcePartsStatus.AmbiguousSelection, canonical.Status);
    }

    [Fact]
    public void A_quote_that_occurs_twice_is_resolved_when_the_claim_says_which()
    {
        var atoms = Atoms("Africa Gregoire and the Development Bank in Africa");

        var byOrdinal = Canonicalize(atoms, Part("L0000:S0", "Africa") with { Occurrence = 2 });
        var byContext = Canonicalize(atoms, Part("L0000:S0", "Africa") with { LeftExactContext = "in " });

        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, Assert.Single(byOrdinal.Parts).SelectionMode);
        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, Assert.Single(byContext.Parts).SelectionMode);

        // And the binder, which is unchanged and still strict, resolves them to different places.
        var first = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(byOrdinal.Parts));
        Assert.True(first.IsBound);
        Assert.Equal("L0000:S0:44-50", first.Identity);
    }

    [Fact]
    public void An_unknown_alias_is_refused_rather_than_guessed()
    {
        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(Atoms("Chapter One"), [Part("L9999:S0")]);

        Assert.False(canonical.IsCanonical);
        Assert.Equal(SemanticSourcePartsStatus.UnknownAlias, canonical.Status);
    }

    [Fact]
    public void A_mixed_multi_part_claim_keeps_its_order_and_its_two_modes()
    {
        var atoms = Atoms("A heading that begins here", "and finishes on this row. Then prose continues.");

        var canonical = Canonicalize(atoms,
            Part("L0000:S0"),
            Part("L0001:S0", "and finishes on this row."));

        Assert.Equal(2, canonical.Parts.Count);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, canonical.Parts[0].SelectionMode);
        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, canonical.Parts[1].SelectionMode);

        var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
        Assert.True(bound.IsBound);
        Assert.Equal(["L0000:S0", "L0001:S0"], bound.Parts.Select(part => part.Alias));
    }

    // ---- the invariants that matter ---------------------------------------------------------------

    [Fact]
    public void Every_approved_gold_part_derives_the_mode_it_already_records()
    {
        // The primary invariant: canonicalization must reproduce the approved corpus exactly, not
        // merely fail to contradict it.
        var plan = PdfSourceAdapter.Build(TestRepository.Path(Doc0252Pdf));
        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().ToArray();
        Assert.Equal(41, claims.Length);

        var parts = 0;
        foreach (var claim in claims)
        {
            var declared = claim.GetProperty("sourceParts").EnumerateArray().Select(part => new SemanticSourcePart(
                part.GetProperty("sourceAlias").GetString()!,
                part.GetProperty("selectionMode").GetString()!,
                part.TryGetProperty("verbatimText", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null))
                .ToArray();
            parts += declared.Length;

            // What a v2 reply would have carried for the same claim: the alias, and a quote only
            // where the claim is part of an occurrence.
            var asV2 = declared.Select(part => new SemanticSourcePart(
                part.SourceAlias, SemanticSourcePartCanonicalizer.PendingSelectionMode,
                part.SelectionMode == CanonicalSemanticSelectionMode.VerbatimText ? part.VerbatimText : null))
                .ToArray();

            var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, asV2);
            Assert.True(canonical.IsCanonical, $"{claim.GetProperty("identity").GetString()}: {canonical.Status}");
            Assert.Equal(declared.Select(part => part.SelectionMode), canonical.Parts.Select(part => part.SelectionMode));

            // And the coordinates it resolves to are the identity Gold already records.
            var bound = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
            Assert.True(bound.IsBound);
            Assert.Equal(claim.GetProperty("identity").GetString(), bound.Identity);
        }

        Assert.Equal(42, parts);
    }

    [Fact]
    public void The_multi_part_gold_claim_survives_canonicalization_as_two_parts()
    {
        var plan = PdfSourceAdapter.Build(TestRepository.Path(Doc0252Pdf));
        using var gold = CanonicalGoldRegistry.ResolveAt(HistoricalGoldVintages.Doc0252R1Path, HistoricalGoldVintages.Doc0252R1Sha256);
        var claim = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .Single(item => item.GetProperty("sourceParts").GetArrayLength() > 1);

        var asV2 = claim.GetProperty("sourceParts").EnumerateArray().Select(part => new SemanticSourcePart(
            part.GetProperty("sourceAlias").GetString()!,
            SemanticSourcePartCanonicalizer.PendingSelectionMode))
            .ToArray();

        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, asV2);
        var bound = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));

        Assert.Equal(2, canonical.Parts.Count);
        Assert.Equal(claim.GetProperty("identity").GetString(), bound.Identity);
        Assert.Equal(["L0359:S0", "L0360:S0"], bound.Parts.Select(part => part.Alias));
    }

    [Fact]
    public void The_four_claims_the_encoding_discarded_would_now_bind()
    {
        // A counterfactual, not a rescore. EXP_MASTHEAD_METADATA's recorded result stands: under the
        // contract it actually ran on, these four were refused and scored as false negatives.
        var plan = PdfSourceAdapter.Build(TestRepository.Path(Doc0252Pdf));
        var directory = TestRepository.Path(
            "eval/a99-closed-loop/exp-masthead-metadata-experiment-v1/DOC-0252/r1");
        using var capture = JsonDocument.Parse(
            File.ReadAllText(Directory.GetFiles(directory, "*transport-capture.v1.json").Single()));
        var call = capture.RootElement.GetProperty("calls").EnumerateArray()
            .Single(item => item.GetProperty("packId").GetString()!.EndsWith("PACK_005", StringComparison.Ordinal));
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(
            Convert.FromBase64String(call.GetProperty("rawResponseUtf8Base64").GetString()!)));

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["L0400:S0"] = "L0400:S0:0-44",
            ["L0420:S0"] = "L0420:S0:0-40",
            ["L0470:S0"] = "L0470:S0:0-45",
            ["L0507:S0"] = "L0507:S0:0-30",
        };

        var recovered = 0;
        var refusedUnderV1 = 0;
        foreach (var heading in reply.RootElement.GetProperty("headings").EnumerateArray())
        {
            var alias = heading.GetProperty("sourceParts")[0].GetProperty("sourceAlias").GetString()!;
            if (!expected.ContainsKey(alias)) continue;

            // As it was sent: WHOLE_ALIAS carrying the quote. The binder refuses this, and did.
            var asSent = heading.GetProperty("sourceParts").EnumerateArray().Select(part => new SemanticSourcePart(
                part.GetProperty("sourceAlias").GetString()!,
                part.GetProperty("selectionMode").GetString()!,
                part.GetProperty("verbatimText").GetString()))
                .ToArray();
            var underV1 = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(asSent));
            Assert.Equal(SemanticSourcePartsStatus.UnexpectedVerbatimText, underV1.Status);
            refusedUnderV1++;

            // The same words, canonicalized: the quote is the whole atom, so the claim is the atom.
            var asV2 = asSent.Select(part => new SemanticSourcePart(
                part.SourceAlias, SemanticSourcePartCanonicalizer.PendingSelectionMode, part.VerbatimText))
                .ToArray();
            var canonical = SemanticSourcePartCanonicalizer.Canonicalize(plan.Atoms, asV2);
            Assert.True(canonical.IsCanonical);
            var bound = SemanticSourcePartBinder.Bind(plan.Atoms, new SemanticSourcePartsProposal(canonical.Parts));
            Assert.True(bound.IsBound);
            Assert.Equal(expected[alias], bound.Identity);
            recovered++;
        }

        Assert.Equal(4, refusedUnderV1);
        Assert.Equal(4, recovered);
    }

    // ---- the other lanes, and v1 ------------------------------------------------------------------

    [Fact]
    public void The_other_lanes_are_untouched()
    {
        Assert.Equal("91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e",
            SemanticCoordinateContract.DocxAliasSpan.SchemaHash());
        Assert.Equal("ALIAS_SPAN", SemanticCoordinateContract.DocxAliasSpan.Binding.BindingId);
    }

    [Fact]
    public void No_v2_input_can_reach_the_binder_in_the_shape_the_binder_refuses()
    {
        // The v1 audit found four layers that admitted WHOLE_ALIAS carrying verbatimText and one, the
        // binder, that refused it. Under v2 the count of layers that admit it is zero - not because a
        // fifth check was added, but because the only producer of the mode never emits that pair.
        var atoms = Atoms("Chapter One. The rest of the paragraph follows here.", "A second row");

        string?[] quotes =
        [
            null, "", "Chapter One", "Chapter One. The rest of the paragraph follows here.",
            "e", "here.", "The rest",
        ];

        var seen = 0;
        foreach (var quote in quotes)
        {
            var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, [Part("L0000:S0", quote)]);
            if (!canonical.IsCanonical) continue;

            foreach (var part in canonical.Parts)
            {
                seen++;
                Assert.Contains(part.SelectionMode,
                    new[] { CanonicalSemanticSelectionMode.WholeAlias, CanonicalSemanticSelectionMode.VerbatimText });
                Assert.NotEqual(SemanticSourcePartCanonicalizer.PendingSelectionMode, part.SelectionMode);

                // The refused pair, in either direction.
                if (part.SelectionMode == CanonicalSemanticSelectionMode.WholeAlias)
                    Assert.Null(part.VerbatimText);
                else
                    Assert.False(string.IsNullOrEmpty(part.VerbatimText));

                // And the binder, asked directly, agrees.
                var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal([part]));
                Assert.True(bound.IsBound, $"'{quote}' -> {part.SelectionMode}: {bound.Status}");
            }
        }

        Assert.True(seen >= 5, $"only {seen} shapes exercised");
    }

    // ---- boundary whitespace (A99 T5B) -------------------------------------------------------------

    [Fact]
    public void A_multipart_claim_whose_continuation_quotes_carry_a_join_space_binds_as_whole_atoms()
    {
        // The field shape under reasoning=none (SRC-089): each continuation line quoted with the
        // space the model would type joining the lines into one title.
        var atoms = Atoms("Chapter II", "PUBLISHING");

        var canonical = Canonicalize(atoms, Part("L0000:S0", "Chapter II"), Part("L0001:S0", " PUBLISHING"));

        Assert.All(canonical.Parts, part =>
        {
            Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode);
            Assert.Null(part.VerbatimText);
        });
        var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
        Assert.True(bound.IsBound, bound.Status.ToString());
    }

    [Theory]
    [InlineData(" PUBLISHING")]
    [InlineData("PUBLISHING ")]
    [InlineData("\tPUBLISHING\n")]
    [InlineData("  PUBLISHING  ")]
    public void Whitespace_only_at_the_edges_of_a_whole_atom_quote_is_the_whole_atom(string quote)
    {
        var canonical = Canonicalize(Atoms("PUBLISHING"), Part("L0000:S0", quote));

        var part = Assert.Single(canonical.Parts);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, part.SelectionMode);
    }

    [Theory]
    [InlineData(" PUBLISHING HOUSE", "PUBLISHING  HOUSE")] // inner whitespace differs
    [InlineData(" PUBLISHING", "PUBLISHING HOUSE")]        // trimmed quote is only part of the atom
    [InlineData(" publishing", "PUBLISHING")]              // case differs
    [InlineData("   ", "PUBLISHING")]                      // nothing but whitespace
    public void Anything_beyond_edge_whitespace_on_a_whole_atom_is_still_refused(string quote, string atomText)
    {
        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(Atoms(atomText), [Part("L0000:S0", quote)]);

        Assert.False(canonical.IsCanonical);
        Assert.Equal(SemanticSourcePartsStatus.TextNotInAtom, canonical.Status);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static SemanticSourcePartCanonicalization Canonicalize(
        IReadOnlyList<SemanticSourceAtom> atoms, params SemanticSourcePart[] parts)
    {
        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, parts);
        Assert.True(canonical.IsCanonical, canonical.Reason);
        return canonical;
    }

    private static SemanticSourcePart Part(string alias, string? quote = null) =>
        new(alias, SemanticSourcePartCanonicalizer.PendingSelectionMode, quote);

    private static SemanticSourceAtom[] Atoms(params string[] texts) =>
        texts.Select((text, index) => new SemanticSourceAtom(
            $"L{index:0000}:S0", $"atom-{index}", index, 1, index, 0, text)).ToArray();
}
