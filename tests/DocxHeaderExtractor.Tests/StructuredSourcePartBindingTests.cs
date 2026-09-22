using System.Text.Json;
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

    // ---- what is refused ------------------------------------------------------------------------

    [Fact]
    public void Parts_from_unrelated_places_are_refused()
    {
        var atoms = Page(("Session I", 0, 0), ("body", 1, 0), ("body", 2, 0), ("Annex 2", 3, 0));

        var bound = Bind(atoms, Whole("L0000:S0"), Whole("L0003:S0"));

        Assert.Equal(SemanticSourcePartsStatus.NonLocalChain, bound.Status);
        Assert.Empty(bound.Parts);
    }

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
    public void A_claim_that_crosses_a_page_is_refused_explicitly()
    {
        var atoms = new[]
        {
            Atom("L0000:S0", 0, page: 1, row: 0, segment: 0, "a heading continuing"),
            Atom("L0001:S0", 1, page: 2, row: 1, segment: 0, "onto the next page"),
        };

        Assert.Equal(
            SemanticSourcePartsStatus.PageTransitionNotSupported,
            Bind(atoms, Whole("L0000:S0"), Whole("L0001:S0")).Status);
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

    [Fact]
    public void Every_approved_heading_binds_directly_to_segment_atoms()
    {
        var atoms = Doc0252Atoms();
        var gold = CanonicalGoldRegistry.ResolveOccurrenceGoldAt("eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json", "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65", "DOC-0252");
        Assert.Equal(ApprovedHeadings, gold.Headings.Count);

        // Gold's texts come from the universe it was written against, where its aliases are
        // authoritative. From here on nothing block-shaped is consulted: the claims are located in
        // the atom stream by their text and bound by the part binder alone.
        var reference = Occurrences();
        var aliases = PdfSourceOccurrenceBoundary.Aliases(reference.Count);
        var goldTexts = gold.Headings
            .Select((heading, ordinal) =>
                heading.VerbatimText ?? reference[Array.IndexOf(aliases, heading.SourceAlias)].VerbatimText)
            .ToArray();

        var rows = new List<object>();
        int directlyBindable = 0, representable = 0, migration = 0;
        var partCounts = new List<int>();
        var cursorExact = 0;
        var cursorSource = 0;

        for (var ordinal = 0; ordinal < gold.Headings.Count; ordinal++)
        {
            var claim = $"{gold.Headings[ordinal].SourceAlias}#{ordinal}";

            // What canonical Gold says today, character for character.
            var exact = StructuredSourcePartLocator.Locate(atoms, goldTexts[ordinal], punctuationInsensitive: false, ref cursorExact);
            var exactBinding = exact is null
                ? new SemanticSourcePartsBinding(SemanticSourcePartsStatus.TextNotInAtom, [], "no atom run holds this text")
                : Bind(atoms, [.. exact]);
            if (exactBinding.IsBound) directlyBindable++;

            // The same heading written in the characters the source actually has. Where the two
            // differ it is Gold that is stale: the old line reconstruction dropped a period before
            // the claim was written, and this measures the source rather than that damage.
            var source = StructuredSourcePartLocator.Locate(atoms, goldTexts[ordinal], punctuationInsensitive: true, ref cursorSource);
            var sourceBinding = source is null
                ? new SemanticSourcePartsBinding(SemanticSourcePartsStatus.TextNotInAtom, [], "no atom run holds this heading")
                : Bind(atoms, [.. source]);
            if (sourceBinding.IsBound)
            {
                representable++;
                partCounts.Add(sourceBinding.Parts.Count);
            }

            var projected = SemanticSourceProjection.Render(sourceBinding.Parts);
            var stale = sourceBinding.IsBound && !exactBinding.IsBound;
            if (stale) migration++;

            rows.Add(new
            {
                claim,
                goldText = goldTexts[ordinal],
                canonicalGoldDirectlyBindable = exactBinding.IsBound,
                canonicalGoldStatus = exactBinding.Status.ToString(),
                sourceRepresentable = sourceBinding.IsBound,
                sourceStatus = sourceBinding.Status.ToString(),
                goldTextMigrationRequired = stale,
                parts = sourceBinding.Parts.Count,
                identity = sourceBinding.Identity,
                sourceParts = (source ?? []).Select(part => new
                {
                    part.SourceAlias,
                    part.SelectionMode,
                    part.VerbatimText,
                }).ToArray(),
                boundText = sourceBinding.Parts.Select(part => part.Text).ToArray(),
                locality = sourceBinding.Parts.Skip(1)
                    .Select(part => part.LocalityFromPrevious.ToString()).ToArray(),
                projectedText = projected,
            });
        }

        // S0616 is the case the whole architecture turns on: the model emitted the complete
        // heading, and under block authority no single occurrence held it. Proven here from the
        // approved wording, against atoms alone.
        var s0616 = StructuredSourcePartLocator.Locate(atoms,
            "2. A Survey Based Approach to Adjustment for Quality Differences in Services in International Price Comparisons",
            punctuationInsensitive: true, ref cursorSource);
        var s0616Binding = s0616 is null
            ? new SemanticSourcePartsBinding(SemanticSourcePartsStatus.TextNotInAtom, [], "not found")
            : Bind(atoms, [.. s0616]);

        // Block independence, shown rather than asserted: the two atoms S0616 binds are two
        // different occurrences of the active universe, and the binder never saw either.
        var activeOccurrences = Occurrences();
        var activeAliases = PdfSourceOccurrenceBoundary.Aliases(activeOccurrences.Count);
        var s0616Blocks = new List<object>();
        var from = 0;
        foreach (var part in s0616Binding.Parts)
        {
            // Forward from where the part before it landed. Short text like "Comparisons" occurs
            // all over a document, and the first match anywhere would be a different sentence.
            var index = -1;
            for (var at = from; at < activeOccurrences.Count; at++)
            {
                if (!Reduce(activeOccurrences[at].VerbatimText).Contains(Reduce(part.Text), StringComparison.Ordinal))
                    continue;
                index = at;
                break;
            }

            if (index >= 0) from = index;
            s0616Blocks.Add(new
            {
                part.Alias,
                activeOccurrence = index < 0 ? null : activeAliases[index],
                activeOccurrenceText = index < 0 ? null : activeOccurrences[index].VerbatimText,
            });
        }

        string? ActiveOccurrenceOf(object row) =>
            (string?)row.GetType().GetProperty("activeOccurrence")!.GetValue(row);

        FreezeArtifact.AssertJson(Artifacts, "doc-0252-structured-source-parts.v1.json", new
        {
            artifactKind = "a99_structured_source_part_binding",
            schemaVersion = "a99-structured-source-part-binding-v1",
            capability = "STRUCTURED_SOURCE_PART_BINDING",
            authorityId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,

            contract = new
            {
                activeSemanticContractSha256 = ActiveSemanticContractHash,
                multipartShadowContractSha256 = SemanticSourcePartsContract.SchemaHash(),
                shadowProtocolVersion = SemanticSourcePartsContract.ProtocolVersion,
                activeContractUnchanged = true,
                transportsUnderShadowContract = 0,
            },

            model = new
            {
                identity = "An ordered tuple of harness-resolved coordinates, one per part: alias plus start and end. Never a joined string, and never anything the model wrote.",
                projectionIsNotIdentity = "Rendering joins parts for reading. It is derived from the identity and never replaces it, so two selections that read alike stay two claims.",
                hyphenAtARowBreak = "Kept. Nothing in the geometry separates a word broken at a line end from a compound that was already hyphenated, and keeping the character is the choice that can be undone.",
                localityIsHarnessOwned = "Adjacent parts must be the same atom, the next segment of a row, or the next row of a page. Block membership is neither required nor sufficient.",
            },

            atoms = new
            {
                count = atoms.Count,
                addressing = "L{row}:S{segment} - a position that survives the document being rebuilt around it, unlike a running number.",
                first = atoms.Take(3).Select(atom => new { atom.Alias, atom.Page, atom.Text }).ToArray(),
            },

            audit = new
            {
                approvedHeadings = ApprovedHeadings,
                sourceHeadingsRepresentable = representable,
                canonicalGoldDirectlyBindable = directlyBindable,
                goldTextMigrationRequired = migration,
                partCountsMeasuredOver = "the texts canonical Gold records today, one of which is known to be truncated",
                headings1Part = partCounts.Count(count => count == 1),
                headings2Parts = partCounts.Count(count => count == 2),
                headings3PlusParts = partCounts.Count(count => count >= 3),
                maxPartsPerHeading = partCounts.Count == 0 ? 0 : partCounts.Max(),
                // S0616's recorded text stops at "International Price" because the line it was
                // written against had been cut there. Its approved wording needs two parts, so the
                // real cost of the coordinate model on this document is 40 headings of one part
                // and one of two - not 41 of one.
                headingsOverApprovedWording = new
                {
                    onePart = partCounts.Count(count => count == 1) - 1,
                    twoParts = 1,
                    maxParts = 2,
                    differsFromRecordedText = new[] { "S0616#20" },
                },
                rows,
            },

            s0616 = new
            {
                note = "The approved wording, from the user's source re-audit. Bound from atoms with no block reconstruction anywhere in the path.",
                bound = s0616Binding.IsBound,
                status = s0616Binding.Status.ToString(),
                parts = s0616Binding.Parts.Count,
                identity = s0616Binding.Identity,
                sourceParts = (s0616 ?? []).Select(part => new { part.SourceAlias, part.SelectionMode, part.VerbatimText }).ToArray(),
                boundText = s0616Binding.Parts.Select(part => part.Text).ToArray(),
                locality = s0616Binding.Parts.Skip(1).Select(part => part.LocalityFromPrevious.ToString()).ToArray(),
                projectedText = SemanticSourceProjection.Render(s0616Binding.Parts),
            },

            blockIndependence = new
            {
                note = "The binder takes atoms and a proposal. No block type appears in its signature, so a block boundary cannot authorize or refuse anything - and the case that used to be unbindable is the demonstration.",
                s0616PartsByActiveOccurrence = s0616Blocks,
                s0616CrossesAnActiveBlockBoundary =
                    s0616Blocks.Count > 1 &&
                    ActiveOccurrenceOf(s0616Blocks[0]) != ActiveOccurrenceOf(s0616Blocks[^1]),
            },

            correction = new
            {
                what = "The boundary classifier used in the two previous shadow audits took a WHOLE_ALIAS claim's text from the occurrence it was being compared against, so 30 of the 41 claims were compared with themselves and could only come out EXACT.",
                effect = "EXACT 38 / NORMALIZATION_ONLY 3 in doc-0252-block-grouping-shadow.v1.json and doc-0252-visual-line-segment-shadow.v1.json is not a text-level measurement for those 30. FRAGMENTED and OVER_GROUPED are unaffected, being comparisons of visual-line sets rather than of strings.",
                settledHere = "This audit compares against Gold's stored text and against the source text separately, which is what the two numbers above report.",
            },

            corpus = CorpusChains(),
        });

        Assert.Equal(ApprovedHeadings, representable);
        Assert.True(s0616Binding.IsBound);
        Assert.Equal(2, s0616Binding.Parts.Count);
        Assert.NotEqual(ActiveOccurrenceOf(s0616Blocks[0]), ActiveOccurrenceOf(s0616Blocks[^1]));
    }

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
        return PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3);
    }

    private static IReadOnlyList<SemanticSourceAtom> Doc0252Atoms() =>
        PdfSegmentAtomCatalog.FromSegments(Segments());

    /// <summary>The universe Gold's aliases belong to. Used to read its texts and nothing else.</summary>
    private static IReadOnlyList<PdfSemanticBlock> Occurrences()
    {
        using var document = PdfDocument.Open(Doc0252Path);
        var lines = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.MidpointV1);
        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }

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
                    PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3));
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
