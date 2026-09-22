using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Block grouping: which consecutive lines continue one another, and therefore become one source
/// occurrence.
/// <para>
/// The cases below are written as coordinates because the rule is a claim about typography, not
/// about a document. <see cref="PdfBlockGrouping.LegacyV1"/> stays the default while the candidate
/// is evidence - every frozen universe, Gold binding and preflight hash was taken over its output.
/// </para>
/// </summary>
public sealed class PdfSemanticBlockGrouperTests
{
    [Fact]
    public void MergesNearbySameStyleTitleLinesButStopsAfterSentence()
    {
        var lines = new[]
        {
            Ann(Line("Top 3 trust funds activated during the fiscal year", page: 1, y: 700)),
            Ann(Line("ended June 30, 2024, on the basis of Expected Funding", page: 1, y: 684)),
            Ann(Line("Recipients should retain this statement.", page: 1, y: 640)),
            Ann(Line("This next sentence must not merge.", page: 1, y: 624)),
        };

        var blocks = PdfSemanticBlockGrouper.Build(lines);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(2, blocks[0].LineCount);
        Assert.Contains("Expected Funding", blocks[0].Text);
        Assert.Equal("Recipients should retain this statement.", blocks[1].Text);
        Assert.Equal("This next sentence must not merge.", blocks[2].Text);
    }

    [Fact]
    public void IgnoresLinesExcludedByDeterministicFilter()
    {
        var annotations = new[]
        {
            Ann(Line("Heading Topic", page: 1, y: 700)),
            new PdfLineBlockAnnotation(
                Line("TOTAL $42 80%", page: 1, y: 680),
                Repeated: false,
                HeaderFooterZone: false,
                TableLike: true,
                PageNumber: false,
                Reason: "table-like"),
        };

        var blocks = PdfSemanticBlockGrouper.Build(annotations);

        Assert.Single(blocks);
        Assert.Equal("Heading Topic", blocks[0].Text);
    }

    // ---- what the document's own leading is ----------------------------------------------------

    [Fact]
    public void The_leading_is_the_smallest_gap_that_recurs_not_the_most_common_one()
    {
        // A document of short articles has more paragraph breaks than wrapped lines. Taking the
        // most common gap would hand back the break, and the grouper would then merge straight
        // through every one of them. The leading is the smallest gap the document uses repeatedly.
        var lines = Page(
            ("Article 1", 0),
            ("a paragraph that wraps once here", 14),
            ("Article 2", 20),
            ("another paragraph that wraps", 14),
            ("Article 3", 20),
            ("a third paragraph wrapping too", 14),
            ("Article 4", 20));

        Assert.Equal(1.4, PdfLinePitch.Estimate(lines), 3);
    }

    [Fact]
    public void An_unmeasurable_document_falls_back_to_a_plausible_leading()
    {
        Assert.Equal(PdfLinePitch.MinimumPitch, PdfLinePitch.Estimate([]));
        Assert.Equal(PdfLinePitch.MinimumPitch, PdfLinePitch.Estimate(Page(("One line only", 0))));

        // Nothing a document says makes six line-heights a leading.
        Assert.Equal(PdfLinePitch.MaximumPitch,
            PdfLinePitch.Estimate(Page(("Far", 0), ("Apart", 60), ("Again", 60))));
    }

    // ---- what may and may not be merged --------------------------------------------------------

    [Fact]
    public void A_wrapped_paragraph_stays_one_block()
    {
        var lines = Page(
            ("Professor Feenstra presented the empirical findings of an analysis", 0),
            ("aimed at simplifying the current methodology used by the tables", 14),
            ("for estimating imports and exports", 14));

        var blocks = Group(lines);

        Assert.Single(blocks);
        Assert.Equal(3, blocks[0].LineCount);
    }

    [Fact]
    public void A_wrapped_heading_stays_one_block()
    {
        // The case the line fix exposed: a heading long enough to wrap, with the paragraph beneath
        // it a full break away.
        var lines = Page(
            ("2. A Survey Based Approach to Adjustment for Quality in International Price", 0),
            ("Comparisons", 14),
            ("Professor Abe presented a research paper written in collaboration with", 20));

        var blocks = Group(lines);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(2, blocks[0].LineCount);
        Assert.Contains("Comparisons", blocks[0].Text);
    }

    [Fact]
    public void A_heading_does_not_absorb_the_paragraph_beneath_it()
    {
        // The residual defect. Both lines are the same face, the same colour and the same left
        // edge; the only thing that distinguishes them is that the gap is a paragraph break rather
        // than a leading, and an absolute ceiling wide enough for the leading clears the break too.
        var lines = Page(
            ("Africa", 0),
            ("Gregoire Mboya de Loubassou, African Development Bank, presented the", 20),
            ("status of implementation in the Africa region", 14));

        var legacy = Group(lines, PdfBlockGrouping.LegacyV1);
        var candidate = Group(lines);

        Assert.Single(legacy);
        Assert.Equal(2, candidate.Count);
        Assert.Equal("Africa", candidate[0].Text);
        Assert.Equal(2, candidate[1].LineCount);
    }

    [Fact]
    public void Consecutive_headings_stay_separate()
    {
        // The two headings sit a break apart, with body text around them so the page says what its
        // leading is. Two lines alone could not: a lone gap is the only gap, so it is the leading
        // by definition, and no rule reading the document can say otherwise.
        var lines = Page(
            ("Session IV: TAG Functioning and Terms of Reference for Task Forces", 0),
            ("1. TAG Composition and Terms of Reference", 20),
            ("The chair introduced the draft terms of reference and invited the group", 20),
            ("to comment on the composition of the task force", 14));

        var blocks = Group(lines);

        Assert.Equal(3, blocks.Count);
        Assert.Equal([1, 1, 2], blocks.Select(block => block.LineCount).ToArray());
    }

    [Fact]
    public void A_hanging_indent_continuation_stays_with_its_line()
    {
        // The continuation is indented, which is what a hanging indent means. Indentation is not
        // evidence of a new block on its own, and the gap is still one leading.
        var lines = new[]
        {
            Line("(a) the price of a good expressed in the currency of the country", page: 1, y: 700),
            Line("in which it was collected, adjusted for quality", page: 1, y: 686, left: 96),
        };

        var blocks = Group(lines);

        Assert.Single(blocks);
        Assert.Equal(2, blocks[0].LineCount);
    }

    [Fact]
    public void A_small_gap_alone_is_not_enough_to_merge()
    {
        // One leading apart in every case, and none of them continues the line above.
        var differentFont = new[]
        {
            Line("Heading in one face", page: 1, y: 700),
            Line("Body in another", page: 1, y: 686, fontName: "sans"),
        };
        var differentSize = new[]
        {
            Line("Heading at one size", page: 1, y: 700),
            Line("Body at another", page: 1, y: 686, fontSize: 18),
        };
        var acrossAPage = new[]
        {
            Line("Last line of page one", page: 1, y: 700),
            Line("first line of page two", page: 2, y: 686),
        };
        var afterASentence = new[]
        {
            Line("This sentence ends here.", page: 1, y: 700),
            Line("This next one must not merge.", page: 1, y: 686),
        };

        Assert.Equal(2, Group(differentFont).Count);
        Assert.Equal(2, Group(differentSize).Count);
        Assert.Equal(2, Group(acrossAPage).Count);
        Assert.Equal(2, Group(afterASentence).Count);
    }

    [Fact]
    public void Grouping_is_a_pure_function_of_its_input()
    {
        var lines = Page(
            ("A heading", 0),
            ("a paragraph that wraps", 20),
            ("onto a second line", 14),
            ("Another heading", 20));

        var first = Group(lines);
        var second = Group(lines);

        Assert.Equal(
            first.Select(block => $"{block.Page}|{block.TopY:F4}|{block.Text}"),
            second.Select(block => $"{block.Page}|{block.TopY:F4}|{block.Text}"));
        Assert.Equal([1, 2, 1], first.Select(block => block.LineCount).ToArray());
    }

    // ---- what the candidate would change on a real document ------------------------------------

    [Fact]
    public void The_shadow_block_grouping_separates_headings_from_what_follows_them()
    {
        var gold = CanonicalGoldRegistry.ResolveOccurrenceGoldAt("eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json", "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65", "DOC-0252");
        Assert.Equal(GoldHeadings, gold.Headings.Count);

        // Both sides read the repaired visual lines from f314255. This task changes one stage, and
        // measuring it against the unrepaired lines would credit it with the line fix as well.
        var lines = Doc0252Lines();
        var before = Doc0252Occurrences(PdfBlockGrouping.LegacyV1);
        var after = Doc0252Occurrences(PdfBlockGrouping.ContinuationV2);

        // Gold addresses occurrences by alias, and an alias is a position in the universe it was
        // written against - the one built with midpoint lines and the legacy ceiling. Its texts are
        // read from there once, then located by text on both candidate sides.
        var goldTexts = LocateTexts(gold, Doc0252Occurrences(PdfBlockGrouping.LegacyV1, PdfLineGrouping.MidpointV1));
        var beforeIndexes = PdfSourceOccurrenceBoundary.Locate(
            before, gold.Headings, goldTexts, out var beforeMissing);
        var afterIndexes = PdfSourceOccurrenceBoundary.Locate(
            after, gold.Headings, goldTexts, out var afterMissing);
        Assert.Empty(beforeMissing);

        var beforeRows = PdfSourceOccurrenceBoundary.Classify(before, gold.Headings, beforeIndexes);
        var afterRows = PdfSourceOccurrenceBoundary.Classify(after, gold.Headings, afterIndexes);
        var census = (IReadOnlyList<PdfBoundaryRow> rows) => rows
            .GroupBy(row => row.Boundary)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var transitions = beforeRows
            .Select((row, ordinal) => new
            {
                claim = row.Claim,
                from = row.Boundary,
                to = afterRows[ordinal].Boundary,
                representableBefore = row.FullyRepresentable,
                representableAfter = afterRows[ordinal].FullyRepresentable,
                goldText = row.GoldText,
                occurrenceBefore = row.OccurrenceText,
                occurrenceAfter = afterRows[ordinal].OccurrenceText,
            })
            .ToArray();

        // ---- every residual case, measured at each junction the grouper crossed ---------------
        var ceiling = PdfLinePitch.ContinuationCeiling(lines);
        var explained = beforeRows
            .Where(row => row.Boundary == "OVER_GROUPED")
            .Select(row =>
            {
                var occurrence = before[row.OccurrenceIndex];
                return new
                {
                    claim = row.Claim,
                    goldText = row.GoldText,
                    occurrenceText = row.OccurrenceText,
                    extraVisualLines = row.ExtraVisualLinesInOccurrence,
                    junctions = occurrence.Lines.Skip(1).Select((next, index) =>
                    {
                        var previous = occurrence.Lines[index];
                        var normalized = PdfLinePitch.NormalizedGap(previous, next);
                        return new
                        {
                            above = previous.Text,
                            below = next.Text,
                            yGap = Math.Round(previous.Y - next.Y, 2),
                            normalizedYGap = normalized is null ? (double?)null : Math.Round(normalized.Value, 3),
                            leftDelta = Math.Round(Math.Abs(previous.Left - next.Left), 2),
                            fontSizeDelta = Math.Round(Math.Abs(previous.FontSize - next.FontSize), 3),
                            legacyMerged = previous.Y - next.Y is > 0 and <= 22,
                            legacyReason = "The absolute 22pt ceiling clears a paragraph break at this body size, so nothing downstream of it was ever consulted.",
                            candidateMerges = normalized is not null && normalized.Value <= ceiling,
                        };
                    }).ToArray(),
                    boundaryAfter = afterRows[Array.IndexOf(beforeRows.ToArray(), row)].Boundary,
                };
            })
            .ToArray();

        // ---- over-splitting, detected without labels ------------------------------------------
        // A sentence that runs across a block boundary is a paragraph the grouper took apart. The
        // line fix restored the punctuation that makes this readable, so it can be counted rather
        // than eyeballed.
        var splitBefore = SentencesBrokenAcrossBlocks(before);
        var splitAfter = SentencesBrokenAcrossBlocks(after);

        var forensic = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestRepository.Root(),
                "eval/a99-closed-loop/occurrence-baseline-v1/causal-forensic.v1.json"
                    .Replace('/', Path.DirectorySeparatorChar))));
        var byClaim = transitions.ToDictionary(item => item.claim, StringComparer.Ordinal);

        // The boundary each claim had in the universe the provider run was actually shown. A loss
        // is only the model's if what it was given was sound: an omission on an occurrence that
        // fused a heading into a paragraph has a confounded cause, and calling it a model miss
        // would charge the model for the harness.
        var asRun = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestRepository.Root(),
                "eval/a99-closed-loop/representation/doc-0252-source-occurrence-boundary.v1.json"
                    .Replace('/', Path.DirectorySeparatorChar))))
            .RootElement.GetProperty("boundaryAudit").GetProperty("rows").EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("claim").GetString()!,
                row => row.GetProperty("boundary").GetString()!,
                StringComparer.Ordinal);

        var historical = forensic.RootElement.GetProperty("lostOccurrences").EnumerateArray()
            .Where(loss => loss.GetProperty("persistent").GetBoolean())
            .Select(loss => new
            {
                claim = loss.GetProperty("claim").GetString()!,
                firstLoss = loss.GetProperty("firstLoss").GetString()!,
            })
            .Where(loss => byClaim.ContainsKey(loss.claim))
            .Select(loss => new
            {
                loss.claim,
                loss.firstLoss,
                boundaryAsRun = asRun.GetValueOrDefault(loss.claim, "UNKNOWN"),
                boundaryAfter = byClaim[loss.claim].to,
                representableAfter = byClaim[loss.claim].representableAfter,
                verdict = Verdict(
                    loss.firstLoss,
                    asRun.GetValueOrDefault(loss.claim, "UNKNOWN"),
                    byClaim[loss.claim].to,
                    byClaim[loss.claim].representableAfter),
            })
            .OrderBy(item => item.claim, StringComparer.Ordinal)
            .ToArray();

        FreezeArtifact.AssertJson(Artifacts, "doc-0252-block-grouping-shadow.v1.json", new
        {
            artifactKind = "a99_pdf_block_grouping_shadow",
            schemaVersion = "a99-pdf-block-grouping-shadow-v1",
            finding = "PDF_SOURCE_OCCURRENCE_BOUNDARY_MISMATCH",
            intervention = "PDF_BLOCK_GROUPING_CONTINUATION",
            authorityId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,

            lineage = new
            {
                note = "Both sides read the repaired visual lines. Only block grouping differs, so the numbers below belong to this stage alone.",
                lineGrouping = nameof(PdfLineGrouping.VisualLineV2),
                activeAuthorityMoved = false,
                activeRuntimeUniverseSha256 = Universe(PdfLineGrouping.MidpointV1, PdfBlockGrouping.LegacyV1),
                lineFixedShadowSha256 = Universe(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.LegacyV1),
                blockFixedShadowSha256 = Universe(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.ContinuationV2),
            },

            rule = new
            {
                statement = "Two lines continue each other when the gap between them is no more than the document's own leading, plus a tolerance.",
                leadingDefinition = "The smallest gap, in line-heights, that recurs in at least 5% of the document's line pairs.",
                continuationTolerance = PdfLinePitch.ContinuationTolerance,
                measuredLeading = Math.Round(PdfLinePitch.Estimate(lines), 3),
                continuationCeiling = Math.Round(ceiling, 3),
                whyNotAbsolute = "Measured across this repository's PDF corpus the leading and the paragraph break are always separate populations but never in the same place twice - one document wraps at 1.05 line-heights and breaks at 1.45, another wraps at 1.40 and breaks at 2.25. No fixed ceiling sits between both pairs.",
                degenerateCase = "A document with too few line pairs to measure has no leading to read. The estimate is clamped to a plausible range and falls back to its lower bound, which merges nothing that a single measured gap would not already justify.",
                normalizedBy = "max(declared font size, drawn line height). DOC-0252 reports a font size of 1.0 for every glyph, so height is what carries the scale there.",
            },

            counts = new
            {
                visualLines = lines.Count,
                blocksBefore = before.Count,
                blocksAfter = after.Count,
                occurrencesBefore = before.Count,
                occurrencesAfter = after.Count,
                occurrenceIsBlock = "one for one in this pipeline",
                linesPerBlockBefore = Histogram(before),
                linesPerBlockAfter = Histogram(after),
            },

            boundary = new
            {
                total = GoldHeadings,
                before = census(beforeRows),
                after = census(afterRows),
                transitionCensus = transitions
                    .GroupBy(item => $"{item.from} -> {item.to}")
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                regressionsFromExact = transitions
                    .Where(item => item.from == "EXACT_SOURCE_BOUNDARY" && item.to != "EXACT_SOURCE_BOUNDARY")
                    .ToArray(),
                transitions,
            },

            representability = new
            {
                fullyRepresentableBefore = beforeRows.Count(row => row.FullyRepresentable),
                fullyRepresentableAfter = afterRows.Count(row => row.FullyRepresentable),
                notRepresentableAfter = afterRows.Where(row => !row.FullyRepresentable)
                    .Select(PdfSourceOccurrenceBoundary.Serialize).ToArray(),
                headingsNotFoundInShadow = afterMissing,
            },

            residualCasesExplained = new
            {
                note = "Every over-grouped case before this change, with the measurement at each junction its occurrence crossed.",
                count = explained.Length,
                rows = explained,
            },

            overSplitting = new
            {
                note = "A sentence continuing across a block boundary: the earlier block ends without sentence-final punctuation and the next begins in lower case. Label-free, and only readable because the line fix put the punctuation back.",
                legitimateMultilineBlocksBefore = before.Count(block => block.LineCount > 1),
                legitimateMultilineBlocksAfter = after.Count(block => block.LineCount > 1),
                sentencesBrokenBefore = splitBefore.Length,
                sentencesBrokenAfter = splitAfter.Length,
                // Accounted for rather than left as a number. Almost none of them are the gap
                // rule's doing: a block may hold at most four lines and may not continue a line
                // longer than 130 characters, so a long paragraph is cut wherever those limits
                // fall. Both predate this task, are untouched by it, and are the same on each
                // side - which is what makes them the next thing to look at, not this one.
                whyBrokenAfter = BreakReasons(after, ceiling),
                whyBrokenBefore = BreakReasons(before, ceiling),
                incorrectlySplitIntroduced = splitAfter.Except(splitBefore, StringComparer.Ordinal).ToArray(),
                repairedByThisChange = splitBefore.Except(splitAfter, StringComparer.Ordinal).Count(),
            },

            historicalPersistentLosses = new
            {
                note = "Representation only. No provider response was replayed: those requests carry the old aliases, and scoring them against a different universe would invent a baseline that was never run.",
                source = "eval/a99-closed-loop/occurrence-baseline-v1/causal-forensic.v1.json",
                census = historical.GroupBy(item => item.verdict)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                rows = historical,
            },

            status = "SHADOW_ONLY_ACTIVE_AUTHORITY_UNCHANGED",
        });

        // Acceptance, asserted rather than described.
        Assert.Empty(afterMissing);
        Assert.Equal(0, census(afterRows).GetValueOrDefault("FRAGMENTED"));
        Assert.Equal(GoldHeadings, afterRows.Count(row => row.FullyRepresentable));
        Assert.True(census(afterRows).GetValueOrDefault("OVER_GROUPED")
            < census(beforeRows).GetValueOrDefault("OVER_GROUPED"));
        Assert.Empty(splitAfter.Except(splitBefore, StringComparer.Ordinal));
    }

    [Fact]
    public void Regrouping_changes_no_extracted_text()
    {
        // Blocks are a view over lines, so this should be trivially true - and it is asserted
        // because "should be" is how a projection quietly loses a separator.
        var before = Doc0252Occurrences(PdfBlockGrouping.LegacyV1);
        var after = Doc0252Occurrences(PdfBlockGrouping.ContinuationV2);

        Assert.Equal(Glyphs(before), Glyphs(after));
        Assert.Equal(
            before.SelectMany(block => block.Lines).Select(line => line.Text),
            after.SelectMany(block => block.Lines).Select(line => line.Text));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private const int GoldHeadings = 41;
    private const string Doc0252 = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Artifacts = "eval/a99-closed-loop/representation";

    private static string Doc0252Path => System.IO.Path.Combine(
        TestRepository.Root(), Doc0252.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<PdfLine> Doc0252Lines(
        PdfLineGrouping grouping = PdfLineGrouping.VisualLineV2)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(Doc0252Path);
        return PdfLineExtraction.ExtractLines(document, grouping);
    }

    private static IReadOnlyList<PdfSemanticBlock> Doc0252Occurrences(
        PdfBlockGrouping grouping, PdfLineGrouping lineGrouping = PdfLineGrouping.VisualLineV2) =>
        PdfSemanticBlockGrouper.Build(
            PdfLineBlockFilter.Analyze(Doc0252Lines(lineGrouping)),
            includeRiskLines: true, grouping: grouping);

    private static string Universe(PdfLineGrouping lines, PdfBlockGrouping blocks) =>
        PdfCanonicalSourceUniverseBuilder
            .Build(Doc0252Path, Doc0252Lines(lines), blocks)
            .SourceUniverseSha256;

    private static string[] LocateTexts(PdfGoldDocument gold, IReadOnlyList<PdfSemanticBlock> reference)
    {
        var aliases = PdfSourceOccurrenceBoundary.Aliases(reference.Count);
        return gold.Headings
            .Select(heading => heading.VerbatimText ?? reference[Array.IndexOf(aliases, heading.SourceAlias)].VerbatimText)
            .ToArray();
    }

    private static Dictionary<string, int> Histogram(IReadOnlyList<PdfSemanticBlock> blocks) =>
        new()
        {
            ["1"] = blocks.Count(block => block.LineCount == 1),
            ["2"] = blocks.Count(block => block.LineCount == 2),
            ["3+"] = blocks.Count(block => block.LineCount >= 3),
        };

    private static string[] Glyphs(IReadOnlyList<PdfSemanticBlock> blocks) =>
        blocks
            .SelectMany(block => block.Lines)
            .SelectMany(line => line.Projection.SpanMap.Select(entry =>
                $"{line.Projection.RawParserText.Substring(entry.RawStart, entry.RawLength)}" +
                $"|{entry.Left:F3}|{entry.Bottom:F3}|{entry.Top:F3}"))
            .OrderBy(glyph => glyph, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Which guard stopped each sentence-crossing merge. Attributed to the first predicate that
    /// refused, in the order <c>CanMerge</c> applies them, so a limit this task did not touch is
    /// not read as damage it caused.
    /// </summary>
    /// <summary>
    /// What a historical loss can be attributed to, given the boundary the run was shown and the
    /// boundary the candidate produces.
    /// <para>
    /// A claim whose boundary was already sound when the run happened and was lost anyway is the
    /// model's. A claim whose boundary was defective then and is sound now has had its
    /// representation resolved - and nothing here says the model would now find it, because that
    /// question needs a run against the new universe and this task makes none.
    /// </para>
    /// </summary>
    private static string Verdict(
        string firstLoss, string boundaryAsRun, string boundaryAfter, bool representableAfter) =>
        boundaryAfter is not ("EXACT_SOURCE_BOUNDARY" or "NORMALIZATION_ONLY")
            ? representableAfter ? "REPRESENTABLE_BUT_NOT_EXACT" : "UNRESOLVED"
            : boundaryAsRun != "EXACT_SOURCE_BOUNDARY"
                ? "REPRESENTATION_RESOLVED"
                : firstLoss == "MODEL_NOT_EMITTED"
                    ? "TRUE_MODEL_OMISSION"
                    : "MODEL_ERROR_ON_A_SOUND_BOUNDARY";

    private static Dictionary<string, int> BreakReasons(
        IReadOnlyList<PdfSemanticBlock> blocks, double ceiling)
    {
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 1; index < blocks.Count; index++)
        {
            var above = blocks[index - 1];
            var below = blocks[index];
            if (above.Page != below.Page) continue;

            var tail = above.VerbatimText.TrimEnd();
            var head = below.VerbatimText.TrimStart();
            if (tail.Length == 0 || head.Length == 0) continue;
            if (".:;?!".Contains(tail[^1]) || !char.IsLower(head[0])) continue;

            var last = above.Lines[^1];
            var first = below.Lines[0];
            var gap = PdfLinePitch.NormalizedGap(last, first);
            var reason =
                above.LineCount >= 4 ? "FOUR_LINE_CAP"
                : PdfTextUtilities.Readable(last.Text).Length > 130 ? "LINE_LONGER_THAN_130_CHARACTERS"
                : gap is null ? "NO_MEASURABLE_GAP"
                : gap > ceiling ? "GAP_ABOVE_THE_DOCUMENT_LEADING"
                : last.FontName != first.FontName ? "DIFFERENT_FONT"
                : last.FillColorKey != first.FillColorKey ? "DIFFERENT_COLOUR"
                : Math.Abs(last.BoldRatio - first.BoldRatio) > 0.30 ? "DIFFERENT_WEIGHT"
                : Math.Abs(last.ItalicRatio - first.ItalicRatio) > 0.30 ? "DIFFERENT_SLANT"
                : Math.Abs(last.Left - first.Left) > 24 ? "LEFT_EDGE_MOVED"
                : Math.Abs(last.FontSize - first.FontSize) > 1.1 ? "DIFFERENT_SIZE"
                : "RISK_LINE_OR_UNATTRIBUTED";
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        return reasons;
    }

    private static string[] SentencesBrokenAcrossBlocks(IReadOnlyList<PdfSemanticBlock> blocks)
    {
        var broken = new List<string>();
        for (var index = 1; index < blocks.Count; index++)
        {
            var above = blocks[index - 1];
            var below = blocks[index];
            if (above.Page != below.Page) continue;

            var tail = above.VerbatimText.TrimEnd();
            var head = below.VerbatimText.TrimStart();
            if (tail.Length == 0 || head.Length == 0) continue;
            if (".:;?!".Contains(tail[^1])) continue;
            if (!char.IsLower(head[0])) continue;

            broken.Add($"{above.Page}|{tail[^Math.Min(40, tail.Length)..]} || {head[..Math.Min(40, head.Length)]}");
        }

        return [.. broken];
    }

    private static IReadOnlyList<PdfSemanticBlock> Group(
        IReadOnlyList<PdfLine> lines, PdfBlockGrouping grouping = PdfBlockGrouping.ContinuationV2) =>
        PdfSemanticBlockGrouper.Build(lines.Select(Ann).ToArray(), grouping: grouping);

    /// <summary>A page of lines, each placed a stated number of points below the one before.</summary>
    private static PdfLine[] Page(params (string Text, double Below)[] entries)
    {
        var lines = new List<PdfLine>();
        var y = 700.0;
        foreach (var entry in entries)
        {
            y -= entry.Below;
            lines.Add(Line(entry.Text, page: 1, y: y));
        }

        return [.. lines];
    }

    private static PdfLineBlockAnnotation Ann(PdfLine line) =>
        new(line, Repeated: false, HeaderFooterZone: false, TableLike: false, PageNumber: false, Reason: "semantic-candidate");

    private static PdfLine Line(
        string text, int page, double y,
        double fontSize = 10, double left = 72, string fontName = "serif") => new(
        Page: page,
        Y: y,
        FontSize: fontSize,
        Text: text,
        BoldRatio: 0.8,
        LeadingBoldPrefix: "",
        ItalicRatio: 0,
        Left: left,
        Right: 420,
        FontName: fontName,
        FillColorKey: "0.00,0.20,0.40",
        Bottom: y - 5,
        Top: y + 5);
}
