using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Glyph = DocxHeaderExtractor.DocumentProcessing.Pipeline.PdfVisualLineBucket.Glyph;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Visual-line reconstruction: which glyphs a PDF puts on one line, and how this repository decides.
/// <para>
/// The geometry cases below are written as coordinates rather than as documents on purpose. A rule
/// justified only by the document that exposed it is a rule fitted to that document; these say what
/// the rule claims about typography, and a real PDF is then measured against the claim rather than
/// being the claim.
/// </para>
/// <para>
/// <see cref="PdfLineGrouping.MidpointV1"/> stays the default while this work is evidence. Every
/// frozen source universe, Gold binding and provider preflight hash in the repository was taken
/// over V1's output, so changing it here would move the authority a separate migration has to
/// decide on.
/// </para>
/// </summary>
public sealed class PdfLineExtractionTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string ActiveUniverseSha =
        "cb3c9af67a7f17fd9560b56cf23bb9648a9fcfe3ea4eb5d333ac7282176bfc66";
    private const int AuthoritativeTotal = 41;
    private const string Artifacts = "eval/a99-closed-loop/representation";

    // 11pt body text: cap box roughly 7.3 high, single leading 13.4, a period barely 1.7.
    private const double Body = 11.0;

    // ---- the rule, stated as geometry ---------------------------------------------------------

    [Fact]
    public void A_punctuation_box_inside_a_text_line_belongs_to_that_line()
    {
        // The defect this exists for. A period rests on the baseline and rises barely above it, so
        // its midpoint sits a third of a cap height below the midpoint of the text beside it.
        var line = Line(Glyph(baseline: 425.5, top: 435.02, bottom: 425.51));
        var period = Glyph(baseline: 425.5, top: 429.07, bottom: 427.41, fontSize: Body);

        Assert.True(line.Accepts(period));
    }

    [Fact]
    public void Two_ordinary_lines_stay_apart_even_when_their_boxes_touch()
    {
        // Set tightly enough that a descender on the upper line reaches into the ascenders below.
        // The boxes overlap; the baselines are a full leading apart, and that is what decides.
        var upper = Line(Glyph(baseline: 430.0, top: 437.3, bottom: 427.0));
        var lower = Glyph(baseline: 416.6, top: 428.0, bottom: 413.6);

        Assert.False(upper.Accepts(lower));
    }

    [Fact]
    public void A_superscript_stays_on_the_line_it_is_raised_from()
    {
        // Raised by about a third of an em and set smaller. Baseline alone would call it a new
        // line; it is inside the line's band, and the tolerance is scaled to the line, not to it.
        var line = Line(Glyph(baseline: 300.0, top: 307.3, bottom: 300.0));
        var superscript = Glyph(baseline: 303.6, top: 308.3, bottom: 303.6, fontSize: 7.0);
        var subscript = Glyph(baseline: 297.2, top: 301.9, bottom: 297.2, fontSize: 7.0);

        Assert.True(line.Accepts(superscript));
        Assert.True(Line(Glyph(baseline: 300.0, top: 307.3, bottom: 300.0)).Accepts(subscript));
    }

    [Fact]
    public void A_line_opened_by_a_raised_glyph_still_takes_its_baseline_from_the_body_text()
    {
        // Glyphs arrive by baseline, so a superscript can open a line. If it kept that raised
        // baseline as the line's own, the body text would join and then the next line would look
        // only half a leading away. The line takes its baseline from its largest glyph instead.
        var line = new PdfVisualLineBucket();
        line.Add(Glyph(baseline: 303.6, top: 308.3, bottom: 303.6, fontSize: 7.0));
        var body = Glyph(baseline: 300.0, top: 307.3, bottom: 300.0);
        Assert.True(line.Accepts(body));
        line.Add(body);

        Assert.False(line.Accepts(Glyph(baseline: 286.6, top: 293.9, bottom: 286.6)));
    }

    [Fact]
    public void A_taller_font_on_the_next_line_does_not_reach_back_into_this_one()
    {
        // A heading below body text: its box is tall enough to overlap the line above, and its own
        // height makes the overlap a large fraction of the smaller box. Baselines keep them apart.
        var body = Line(Glyph(baseline: 400.0, top: 407.3, bottom: 400.0));
        var heading = Glyph(baseline: 385.0, top: 398.0, bottom: 385.0, fontSize: 18.0);

        Assert.False(body.Accepts(heading));
    }

    [Fact]
    public void Splitting_is_a_pure_function_of_the_order_it_is_given()
    {
        var glyphs = new[]
        {
            Glyph(baseline: 430.0, top: 437.3, bottom: 430.0),
            Glyph(baseline: 430.0, top: 431.7, bottom: 430.0, fontSize: Body),   // a period
            Glyph(baseline: 416.6, top: 423.9, bottom: 416.6),
        };

        var first = PdfVisualLineBucket.Split(glyphs, glyph => glyph);
        var second = PdfVisualLineBucket.Split(glyphs, glyph => glyph);

        Assert.Equal([2, 1], first.Select(line => line.Count).ToArray());
        Assert.Equal(first.Select(line => line.ToArray()), second.Select(line => line.ToArray()));
    }

    // ---- where a row stops being one region ----------------------------------------------------

    [Fact]
    public void A_corridor_that_the_rows_around_it_keep_open_divides_a_row()
    {
        // A two-sided masthead. Two rows, text on both sides of the same whitespace, and nothing
        // crossing it - which is what makes it a corridor rather than a wide space.
        var rows = new[]
        {
            Row((60, 120), (300, 520)),
            Row((60, 110), (300, 500)),
        };

        var cuts = PdfVisualRegion.Cuts(rows, wordGap: 3);

        Assert.Equal([0], cuts[0]);
        Assert.Equal([0], cuts[1]);
    }

    [Fact]
    public void Ordinary_word_spacing_never_divides_a_row()
    {
        var rows = new[]
        {
            Row((60, 100), (104, 150), (154, 200), (204, 260)),
            Row((60, 110), (114, 170), (174, 230)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 4), row => Assert.Empty(row));
    }

    [Fact]
    public void A_gap_no_neighbouring_row_agrees_with_is_left_alone()
    {
        // Tab-aligned metadata between two ordinary lines of prose. The gap is wide, and the rows
        // above and below run straight through where it sits, so there is no region boundary here.
        var rows = new[]
        {
            Row((60, 500)),
            Row((60, 120), (300, 520)),
            Row((60, 500)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 3), row => Assert.Empty(row));
    }

    [Fact]
    public void Two_columns_divide_every_row_that_spans_them()
    {
        var rows = Enumerable.Range(0, 6).Select(_ => Row((60, 280), (320, 540))).ToArray();
        var cuts = PdfVisualRegion.Cuts(rows, wordGap: 3);

        Assert.All(cuts, row => Assert.Equal([0], row));
    }

    [Fact]
    public void A_centred_heading_is_one_region()
    {
        // Wide margins either side, and no text beyond them. A margin is not a corridor: there is
        // nothing on the far side of it for this row to be separate from.
        var rows = new[]
        {
            Row((60, 520)),
            Row((200, 380)),
            Row((60, 520)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 3), row => Assert.Empty(row));
        Assert.Equal(0, PdfVisualRegion.ClearWidthAround(Row((200, 380)), middle: 100));
    }

    [Fact]
    public void A_table_row_is_divided_where_its_columns_are()
    {
        var rows = new[]
        {
            Row((60, 140), (200, 300), (360, 460)),
            Row((60, 130), (200, 290), (360, 450)),
            Row((60, 135), (200, 295), (360, 455)),
        };

        Assert.All(PdfVisualRegion.Cuts(rows, wordGap: 4), row => Assert.Equal([0, 1], row));
    }

    [Fact]
    public void Cutting_is_a_pure_function_of_its_input()
    {
        var rows = new[]
        {
            Row((60, 120), (300, 520)),
            Row((60, 110), (300, 500)),
            Row((60, 500)),
        };

        Assert.Equal(
            PdfVisualRegion.Cuts(rows, wordGap: 3).Select(row => row.ToArray()),
            PdfVisualRegion.Cuts(rows, wordGap: 3).Select(row => row.ToArray()));
        Assert.Empty(PdfVisualRegion.Cuts(rows, wordGap: 0)[0]);
    }

    [Fact]
    public void A_raised_glyph_stays_in_the_segment_it_belongs_to()
    {
        // Segmentation is horizontal and the vertical rule is untouched, so a superscript sitting
        // inside one region's x-range must travel with that region and not with the other.
        var rows = new[]
        {
            Row((60, 120), (121, 126), (300, 520)),
            Row((60, 110), (300, 500)),
        };

        var cuts = PdfVisualRegion.Cuts(rows, wordGap: 3);

        Assert.Equal([1], cuts[0]);
    }

    private static IReadOnlyList<PdfVisualRegion.Box> Row(params (double Left, double Right)[] boxes) =>
        boxes.Select(box => new PdfVisualRegion.Box(box.Left, box.Right)).ToArray();

    // ---- the rule, measured on a real document ------------------------------------------------

    [Fact]
    public void The_candidate_reconstruction_conserves_every_glyph_the_parser_extracted()
    {
        foreach (var candidate in Candidates) ConservesEveryGlyph(candidate);
    }

    private static void ConservesEveryGlyph(PdfLineGrouping candidate)
    {
        // Grouping identity may change; extracted content may not. Checked against the letters
        // PdfPig produced as well as against V1, so a candidate cannot pass by losing the same
        // glyph the incumbent loses.
        var letters = Letters()
            .Select(letter => Atom(letter))
            .OrderBy(atom => atom, AtomOrder)
            .ToArray();
        var before = Atoms(Lines(PdfLineGrouping.MidpointV1));
        var after = Atoms(Lines(candidate));

        Assert.Equal(letters.Length, after.Length);
        Assert.Equal(letters, after);
        Assert.Equal(before, after);

        // Duplication is measured against the source, not against uniqueness. This document draws
        // ten glyphs twice at identical coordinates, and both reconstructions carry both copies; a
        // bare distinctness check would read that as the candidate inventing text.
        Assert.Equal(
            letters.Length - letters.Distinct().Count(),
            after.Length - after.Distinct().Count());
    }

    [Fact]
    public void The_candidate_reconstruction_reads_in_source_order()
    {
        foreach (var candidate in Candidates) ReadsInSourceOrder(candidate);
    }

    private static void ReadsInSourceOrder(PdfLineGrouping candidate)
    {
        // The property the repair depends on: a period returns to its own line and a divided row
        // becomes several, both of which move where text sits in the document stream, and that
        // stream must still be the page's reading order. Successive entries either go down the
        // page, or share a row and then run left to right without overlapping.
        var lines = Lines(candidate);

        foreach (var page in lines.GroupBy(line => line.Page))
        {
            var ordered = page.ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var line = ordered[index];
                // Reading order, stated as the disjunction it actually is: an entry is either
                // lower on the page than the one before it, or beside it on an overlapping band
                // and further right. The second case exists only once rows can be divided, and it
                // is what carries a bullet's dash and its text in the order a reader takes them.
                var lower = line.Y < previous.Y;
                var beside =
                    line.Left > previous.Right &&
                    line.Top > previous.Bottom && line.Bottom < previous.Top;
                Assert.True(lower || beside,
                    $"page {page.Key} entry {index} is neither below nor beside the one before it");
            }
        }

        foreach (var line in lines)
        {
            var spans = line.Projection.SpanMap;
            for (var index = 1; index < spans.Count; index++)
                Assert.True(spans[index].Left >= spans[index - 1].Left,
                    $"glyphs out of left-to-right order on '{line.Text}'");
        }
    }

    [Fact]
    public void The_candidate_reconstruction_is_deterministic()
    {
        foreach (var candidate in Candidates) IsDeterministic(candidate);
    }

    private static void IsDeterministic(PdfLineGrouping candidate)
    {
        Assert.Equal(
            Lines(candidate).Select(line => $"{line.Page}|{line.Y:F4}|{line.Left:F4}|{line.Text}"),
            Lines(candidate).Select(line => $"{line.Page}|{line.Y:F4}|{line.Left:F4}|{line.Text}"));
        Assert.Equal(Universe(candidate, PdfBlockGrouping.LegacyV1), Universe(candidate, PdfBlockGrouping.LegacyV1));
    }

    [Fact]
    public void The_active_runtime_universe_does_not_move()
    {
        // The whole point of the seam. V1 is still what production builds, and its identity is the
        // one every frozen Gold, preflight and baseline artifact names.
        Assert.Equal(ActiveUniverseSha, Universe(PdfLineGrouping.MidpointV1, PdfBlockGrouping.LegacyV1));
        Assert.NotEqual(ActiveUniverseSha, Universe(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.LegacyV1));
    }

    // ---- what the candidate would change ------------------------------------------------------

    [Fact]
    public void The_shadow_universe_repairs_the_boundaries_the_audit_found()
    {
        var gold = CanonicalGoldRegistry.ResolveOccurrenceGoldAt("eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json", "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65", "DOC-0252");
        Assert.Equal(AuthoritativeTotal, gold.Headings.Count);

        var before = Occurrences(PdfLineGrouping.MidpointV1, PdfBlockGrouping.LegacyV1);
        var after = Occurrences(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.LegacyV1);

        // BEFORE resolves by alias, which is authoritative there. AFTER cannot: a candidate
        // reconstruction renumbers every occurrence, so resolving by alias would compare a heading
        // against whatever inherited its number. The text locator is used for both, and on the
        // BEFORE side it is checked against the alias before being trusted on the other.
        var aliases = PdfSourceOccurrenceBoundary.Aliases(before.Count);
        var byAlias = gold.Headings.Select(heading => Array.IndexOf(aliases, heading.SourceAlias)).ToArray();
        Assert.DoesNotContain(-1, byAlias);
        var goldTexts = gold.Headings
            .Select((heading, ordinal) => heading.VerbatimText ?? before[byAlias[ordinal]].VerbatimText)
            .ToArray();

        Assert.Equal(byAlias, PdfSourceOccurrenceBoundary.Locate(
            before, gold.Headings, goldTexts, out var beforeMissing));
        Assert.Empty(beforeMissing);

        var afterIndexes = PdfSourceOccurrenceBoundary.Locate(
            after, gold.Headings, goldTexts, out var afterMissing);

        var beforeRows = PdfSourceOccurrenceBoundary.Classify(before, gold.Headings, byAlias);
        var afterRows = PdfSourceOccurrenceBoundary.Classify(after, gold.Headings, afterIndexes);

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

        var census = (IReadOnlyList<PdfBoundaryRow> rows) => rows
            .GroupBy(row => row.Boundary)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // ---- residual over-grouping, and which stage owns it ----------------------------------
        // Line reconstruction decides what is on a line; the block grouper decides how many lines
        // an occurrence spans. In this pipeline a block is an occurrence one-for-one, so an
        // occurrence covering more visual lines than its heading is the block rule's doing, and the
        // predicate values that let it merge are reported rather than inferred.
        var afterLines = PdfSourceOccurrenceBoundary.VisualLines(after);
        var residual = afterRows
            .Where(row => row.Boundary == "OVER_GROUPED")
            .Select(row =>
            {
                var occurrence = after[row.OccurrenceIndex];
                var spans = occurrence.LineCount > 1;
                return new
                {
                    claim = row.Claim,
                    root = spans ? "BLOCK_GROUPING_CAUSED" : "LINE_RECONSTRUCTION_CAUSED",
                    occurrenceParserLines = occurrence.LineCount,
                    headingVisualLines = row.HeadingVisualLines.Count,
                    extraVisualLines = row.ExtraVisualLinesInOccurrence,
                    mergeEvidence = occurrence.Lines.Skip(1).Select((line, index) => new
                    {
                        yGap = Math.Round(occurrence.Lines[index].Y - line.Y, 2),
                        leftDelta = Math.Round(Math.Abs(occurrence.Lines[index].Left - line.Left), 2),
                        fontSizeDelta = Math.Round(Math.Abs(occurrence.Lines[index].FontSize - line.FontSize), 3),
                        previousEndsWithPeriod = occurrence.Lines[index].Text.TrimEnd().EndsWith('.'),
                    }).ToArray(),
                };
            })
            .ToArray();

        // ---- counterfactual: representation only, never a score -------------------------------
        var forensic = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestRepository.Root(),
                "eval/a99-closed-loop/occurrence-baseline-v1/causal-forensic.v1.json"
                    .Replace('/', Path.DirectorySeparatorChar))));
        var byClaim = transitions.ToDictionary(item => item.claim, StringComparer.Ordinal);
        var counterfactual = forensic.RootElement.GetProperty("lostOccurrences").EnumerateArray()
            .Where(loss => loss.GetProperty("persistent").GetBoolean())
            .Select(loss => loss.GetProperty("claim").GetString()!)
            .Where(byClaim.ContainsKey)
            .Select(claim => new
            {
                claim,
                representableBefore = byClaim[claim].representableBefore,
                representableAfter = byClaim[claim].representableAfter,
            })
            .OrderBy(item => item.claim, StringComparer.Ordinal)
            .ToArray();

        // ---- is the tolerance fitted to this document? ----------------------------------------
        // If the baseline spread inside a reconstructed line is far below the gap to the next line,
        // the threshold has room on both sides and is not balanced on this document's numbers.
        var separation = Separation();

        FreezeArtifact.AssertJson(Artifacts, "doc-0252-visual-line-shadow.v1.json", new
        {
            artifactKind = "a99_pdf_visual_line_shadow",
            schemaVersion = "a99-pdf-visual-line-shadow-v1",
            finding = "PDF_SOURCE_OCCURRENCE_BOUNDARY_MISMATCH",
            intervention = "PDF_VISUAL_LINE_RECONSTRUCTION",
            authorityId = "DOC-0252",
            providerCalls = 0,
            modelCalls = 0,

            activeGrouping = nameof(PdfLineGrouping.MidpointV1),
            candidateGrouping = nameof(PdfLineGrouping.VisualLineV2),
            activeAuthorityMoved = false,
            currentRuntimeUniverseSha256 = Universe(PdfLineGrouping.MidpointV1, PdfBlockGrouping.LegacyV1),
            shadowRuntimeUniverseSha256 = Universe(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.LegacyV1),

            rule = new
            {
                statement = "A glyph joins a line when its baseline is within half the line's scale of the line's baseline, and its vertical box overlaps the line's band by at least half of the smaller of the two heights.",
                baselineToleranceFactor = PdfVisualLineBucket.BaselineTolerance,
                overlapRatio = PdfVisualLineBucket.OverlapRatio,
                overlapDenominator = "min(glyph height, line band height) - symmetric, so a small mark is judged against its own size rather than against the line's",
                whyNotMidpoint = "A glyph's midpoint is a property of its shape. A period and a capital on one baseline differ by about a third of the cap height, which is why the incumbent rule separates them.",
                notFittedTo = "DOC-0252. Half an em sits between a super/subscript shift (about a third of an em) and a line's leading (at least one em); the separation measured below shows the margin on this document rather than defining the rule from it.",
                separation,
            },

            occurrences = new { before = before.Count, after = after.Count },
            visualLines = new
            {
                before = PdfSourceOccurrenceBoundary.VisualLines(before).Count,
                after = afterLines.Count,
                splitAcrossOccurrencesBefore =
                    PdfSourceOccurrenceBoundary.VisualLines(before).Count(line => line.SplitAcrossOccurrences),
                splitAcrossOccurrencesAfter = afterLines.Count(line => line.SplitAcrossOccurrences),
                corroboration = "The audit reconstructs visual lines independently, by overlapping parser lines. Over the candidate it finds the same count the candidate itself produced, which is the agreement of two different methods rather than one method agreeing with itself; over the incumbent it finds a few more, being unable to recover at line level what only glyph-level grouping can.",
            },

            punctuation = new
            {
                before = Serialize(PdfSourceOccurrenceBoundary.PunctuationCensus(
                    before, beforeRows.Select(row => row.OccurrenceIndex).Distinct().ToArray())),
                after = Serialize(PdfSourceOccurrenceBoundary.PunctuationCensus(
                    after, afterRows.Select(row => row.OccurrenceIndex).Distinct().ToArray())),
            },

            boundary = new
            {
                total = AuthoritativeTotal,
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
                note = "Whether the complete approved heading exists inside one occurrence. This is the question the binder has to answer, and it is reported apart from the boundary label because an over-grouped occurrence still contains its heading.",
                fullyRepresentableBefore = beforeRows.Count(row => row.FullyRepresentable),
                fullyRepresentableAfter = afterRows.Count(row => row.FullyRepresentable),
                notRepresentableAfter = afterRows.Where(row => !row.FullyRepresentable)
                    .Select(PdfSourceOccurrenceBoundary.Serialize).ToArray(),
                headingsNotFoundInShadow = afterMissing,
            },

            residualOverGrouping = new
            {
                note = "A block is an occurrence one-for-one in this pipeline, so BLOCK_GROUPING_CAUSED and OCCURRENCE_GROUPING_CAUSED are one stage, not two. Nothing downstream of line reconstruction was changed by this task.",
                roots = residual.GroupBy(item => item.root)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                rows = residual,
            },

            counterfactual = new
            {
                note = "Representation only. No provider response was replayed against this universe: those requests carry the old aliases, and scoring them here would invent a baseline that was never run.",
                source = "eval/a99-closed-loop/occurrence-baseline-v1/causal-forensic.v1.json",
                persistentLossesConsidered = counterfactual.Length,
                previouslyUnrepresentableNowRepresentable = counterfactual
                    .Count(item => !item.representableBefore && item.representableAfter),
                stillUnrepresentable = counterfactual.Count(item => !item.representableAfter),
                rows = counterfactual,
            },

            glyphConservation = "PASS",
            status = "SHADOW_ONLY_ACTIVE_AUTHORITY_UNCHANGED",
        });

        // Acceptance, asserted rather than described.
        Assert.Empty(afterMissing);
        Assert.True(census(afterRows).GetValueOrDefault("FRAGMENTED")
            < census(beforeRows).GetValueOrDefault("FRAGMENTED"));
        Assert.True(census(afterRows).GetValueOrDefault("EXACT_SOURCE_BOUNDARY")
            > census(beforeRows).GetValueOrDefault("EXACT_SOURCE_BOUNDARY"));
        Assert.True(afterRows.Count(row => row.FullyRepresentable)
            > beforeRows.Count(row => row.FullyRepresentable));
    }

    // ---- what the segment candidate does to a real document and to the corpus -------------------

    [Fact]
    public void The_segment_universe_keeps_every_approved_heading_representable()
    {
        var gold = CanonicalGoldRegistry.ResolveOccurrenceGoldAt("eval/a99-closed-loop/gold-current/documents/DOC-0252.legacy-occurrence.gold.v1.json", "51e2f708e7953dd6ffbe6c1b55ee2ddec430c26edd8dc51ddf71e7a13aa20b65", "DOC-0252");
        var reference = Occurrences(PdfLineGrouping.MidpointV1, PdfBlockGrouping.LegacyV1);
        var aliases = PdfSourceOccurrenceBoundary.Aliases(reference.Count);
        var goldTexts = gold.Headings
            .Select(heading => heading.VerbatimText ?? reference[Array.IndexOf(aliases, heading.SourceAlias)].VerbatimText)
            .ToArray();

        // Blocks still form the occurrence, as they do on the other side of this comparison. The
        // line-atom catalog below is built and measured, but binding a heading that wraps needs a
        // contract for naming several atoms, and that is a later decision.
        var after = Occurrences(PdfLineGrouping.VisualLineSegmentV3, PdfBlockGrouping.ContinuationV2);
        var indexes = PdfSourceOccurrenceBoundary.Locate(after, gold.Headings, goldTexts, out var missing);
        var rows = PdfSourceOccurrenceBoundary.Classify(after, gold.Headings, indexes);
        var census = rows.GroupBy(row => row.Boundary)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var segments = Lines(PdfLineGrouping.VisualLineSegmentV3);

        FreezeArtifact.AssertJson(Artifacts, "doc-0252-visual-line-segment-shadow.v1.json", new
        {
            artifactKind = "a99_pdf_visual_line_segment_shadow",
            schemaVersion = "a99-pdf-visual-line-segment-shadow-v1",
            intervention = "PDF_VISUAL_LINE_SEGMENT_AUTHORITY",
            providerCalls = 0,
            modelCalls = 0,

            rule = new
            {
                statement = "A row is divided where a gap much wider than the page's own word space sits in a corridor that a neighbouring row also leaves open while carrying text on both sides of it.",
                candidateGapFactor = PdfVisualRegion.CandidateGapFactor,
                neighbourWindow = PdfVisualRegion.NeighbourWindow,
                corridorSupportRatio = PdfVisualRegion.CorridorSupportRatio,
                supportingRowsRequired = PdfVisualRegion.SupportingRowsRequired,
                whyNotGapAlone = "A wide gap is not evidence of a second region. Tab-aligned metadata, a folio beside a running header, a justified last line and a signature line all leave one. What separates two regions is whitespace that persists across rows with text on both sides of it.",
                riskCensusIsNotASplitRule = "The corpus count of rows with a jump over three line-heights is a risk census. It is reported beside the number actually divided, and the two are not the same measurement.",
            },

            lineage = new
            {
                activeAuthorityMoved = false,
                activeRuntimeUniverseSha256 = Universe(PdfLineGrouping.MidpointV1, PdfBlockGrouping.LegacyV1),
                lineFixedShadowSha256 = Universe(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.LegacyV1),
                blockFixedShadowSha256 = Universe(PdfLineGrouping.VisualLineV2, PdfBlockGrouping.ContinuationV2),
                lineSegmentShadowSha256 = Universe(PdfLineGrouping.VisualLineSegmentV3, PdfBlockGrouping.ContinuationV2),
            },

            doc0252 = new
            {
                visualRows = Lines(PdfLineGrouping.VisualLineV2).Count,
                visualLineSegments = segments.Count,
                occurrences = after.Count,
                // The future coordinate system, counted through the builder that owns it. Nothing
                // routes through it yet; it is here so the atom count is a fact rather than a plan.
                lineAtomCatalogUnits = PdfSegmentAtomCatalog.FromSegments(segments).Count,
                boundary = census,
                fullyRepresentable = rows.Count(row => row.FullyRepresentable),
                notRepresentable = rows.Where(row => !row.FullyRepresentable)
                    .Select(PdfSourceOccurrenceBoundary.Serialize).ToArray(),
                headingsNotFound = missing,
                claims = rows.Select(PdfSourceOccurrenceBoundary.Serialize).ToArray(),
            },

            corpus = CorpusCensus(),
        });

        Assert.Empty(missing);
        Assert.Equal(AuthoritativeTotal, rows.Count(row => row.FullyRepresentable));
        Assert.Equal(0, census.GetValueOrDefault("FRAGMENTED"));
    }

    [Fact]
    public void The_adjudicated_layout_cases_come_out_as_a_reader_would_read_them()
    {
        // The rule is judged against rows a person looked at, held in their own artifact so the
        // rule cannot be written to agree with them. Each case is re-derived from its document
        // rather than compared with a stored outcome.
        using var adjudication = JsonDocument.Parse(File.ReadAllText(
            System.IO.Path.Combine(TestRepository.Root(),
                $"{Artifacts}/visual-line-segment-adjudication.v1.json"
                    .Replace('/', System.IO.Path.DirectorySeparatorChar))));

        var results = new List<(string Case, string Expected, string Actual)>();
        foreach (var item in adjudication.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = item.GetProperty("caseId").GetString()!;
            var expected = item.GetProperty("expected").GetString()!;
            var parts = caseId.Split('|');
            var row = FindRow(parts[0], int.Parse(parts[1][1..]), double.Parse(parts[2]));
            Assert.True(row is not null, $"{caseId} no longer names a row in its document");
            results.Add((caseId, expected, row!.Count > 1 ? "DIVIDED" : "WHOLE"));
        }

        // A case the reader marked as two regions that the rule leaves whole is a miss, and it is
        // declared as one in the artifact. Dividing a row the reader called single is the failure
        // this gate exists for, because an atom that splits one region cannot be bound to it.
        var falseSplits = results
            .Where(item => item.Expected == "WHOLE" && item.Actual == "DIVIDED")
            .ToArray();
        var missed = results
            .Where(item => item.Expected == "DIVIDED" && item.Actual == "WHOLE")
            .ToArray();
        var declaredMisses = results.Count(item => item.Expected == "UNDER_SPLIT");

        Assert.Empty(falseSplits.Select(item => item.Case));
        Assert.Empty(missed.Select(item => item.Case));
        Assert.All(results.Where(item => item.Expected == "UNDER_SPLIT"),
            item => Assert.Equal("WHOLE", item.Actual));
        Assert.True(declaredMisses > 0, "the artifact should keep recording what this rule misses");
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Both candidate reconstructions. Neither is active; both must hold the invariants.</summary>
    private static readonly PdfLineGrouping[] Candidates =
        [PdfLineGrouping.VisualLineV2, PdfLineGrouping.VisualLineSegmentV3];

    private static PdfVisualLineBucket Line(Glyph first)
    {
        var bucket = new PdfVisualLineBucket();
        bucket.Add(first);
        return bucket;
    }

    private static Glyph Glyph(double baseline, double top, double bottom, double fontSize = Body) =>
        new(baseline, top, bottom, fontSize);

    private static string Path_ => System.IO.Path.Combine(
        TestRepository.Root(), Pdf.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static IReadOnlyList<Letter> Letters()
    {
        using var document = PdfDocument.Open(Path_);
        return document.GetPages()
            .SelectMany(page => page.Letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value)))
            .ToArray();
    }

    private static IReadOnlyList<PdfLine> Lines(PdfLineGrouping grouping)
    {
        using var document = PdfDocument.Open(Path_);
        return PdfLineExtraction.ExtractLines(document, grouping);
    }

    /// <summary>One extracted glyph, identified by what it is and where it was drawn.</summary>
    private static string Atom(Letter letter) =>
        $"{letter.Value}|{letter.BoundingBox.Left:F3}|{letter.BoundingBox.Bottom:F3}|{letter.BoundingBox.Top:F3}";

    private static readonly IComparer<string> AtomOrder = StringComparer.Ordinal;

    private static string[] Atoms(IReadOnlyList<PdfLine> lines) =>
        lines
            .SelectMany(line => line.Projection.SpanMap.Select(entry =>
                $"{line.Projection.RawParserText.Substring(entry.RawStart, entry.RawLength)}" +
                $"|{entry.Left:F3}|{entry.Bottom:F3}|{entry.Top:F3}"))
            .OrderBy(atom => atom, AtomOrder)
            .ToArray();

    /// <summary>
    /// How much room the baseline tolerance actually had on this document.
    /// <para>
    /// A threshold is only meaningful next to the distance between the two populations it
    /// separates. The spread inside a reconstructed line should be near zero, and the gap to the
    /// next line should clear the tolerance that applied to it - reported as a ratio, because the
    /// tolerance is scaled per line and a raw gap in points cannot be compared with it.
    /// </para>
    /// </summary>
    private static object Separation()
    {
        using var document = PdfDocument.Open(Path_);
        var within = new List<double>();
        var safety = new List<double>();

        foreach (var page in document.GetPages())
        {
            var ordered = page.Letters
                .Where(letter => !string.IsNullOrWhiteSpace(letter.Value))
                .OrderByDescending(letter => letter.StartBaseLine.Y)
                .ThenBy(letter => letter.BoundingBox.Left)
                .ThenBy(letter => letter.Value, StringComparer.Ordinal)
                .ToArray();

            var grouped = PdfVisualLineBucket.Split(ordered, PdfVisualLineBucket.Of);
            foreach (var line in grouped)
                within.Add(line.Max(l => l.StartBaseLine.Y) - line.Min(l => l.StartBaseLine.Y));

            for (var index = 1; index < grouped.Count; index++)
            {
                var above = grouped[index - 1];
                var scale = above.Max(l => Math.Max(l.FontSize, l.BoundingBox.Top - l.BoundingBox.Bottom));
                var tolerance = Math.Max(1.0, scale * PdfVisualLineBucket.BaselineTolerance);
                var gap = above.Min(l => l.StartBaseLine.Y) - grouped[index].Max(l => l.StartBaseLine.Y);
                safety.Add(gap / tolerance);
            }
        }

        return new
        {
            baselineSpreadWithinALine = new
            {
                max = Math.Round(within.Max(), 3),
                median = Math.Round(Median(within), 3),
            },
            // Gap to the next line, divided by the tolerance that line was judged with. Above 1
            // means the pair was never close to merging; the minimum is how close this document
            // came to the threshold anywhere.
            gapToNextLineInToleranceUnits = new
            {
                min = Math.Round(safety.Min(), 3),
                median = Math.Round(Median(safety), 3),
                pairsWithin25PercentOfTheThreshold = safety.Count(ratio => ratio < 1.25),
                pairs = safety.Count,
            },
        };
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static IReadOnlyList<PdfSemanticBlock> Occurrences(
        PdfLineGrouping lines, PdfBlockGrouping blocks) =>
        PdfSemanticBlockGrouper.Build(
            PdfLineBlockFilter.Analyze(Lines(lines)), includeRiskLines: true, grouping: blocks);

    private static string Universe(PdfLineGrouping lines, PdfBlockGrouping blocks) =>
        PdfCanonicalSourceUniverseBuilder.Build(Path_, Lines(lines), blocks).SourceUniverseSha256;

    /// <summary>
    /// One reconstructed row, recovered from the segments it was divided into. Segments are emitted
    /// row by row and left to right, so a segment beginning to the right of the one before it on an
    /// overlapping band belongs to the same row.
    /// </summary>
    private static List<List<PdfLine>> Rows(IReadOnlyList<PdfLine> segments)
    {
        var rows = new List<List<PdfLine>>();
        foreach (var segment in segments)
        {
            var open = rows.Count > 0 ? rows[^1] : null;
            var beside = open is not null &&
                open[^1].Page == segment.Page &&
                segment.Left > open[^1].Right &&
                segment.Top > open[^1].Bottom && segment.Bottom < open[^1].Top;
            if (beside) open!.Add(segment);
            else rows.Add([segment]);
        }

        return rows;
    }

    /// <summary>Every horizontal gap inside a row, in line-heights, however the row was divided.</summary>
    private static IEnumerable<double> RowGaps(IReadOnlyList<PdfLine> row)
    {
        var scale = row.Max(segment => Math.Max(segment.FontSize, (segment.Top ?? 0) - (segment.Bottom ?? 0)));
        if (scale <= 0) yield break;

        foreach (var segment in row)
        {
            var spans = segment.Projection.SpanMap;
            for (var index = 1; index < spans.Count; index++)
                yield return (spans[index].Left - spans[index - 1].Right) / scale;
        }

        for (var index = 1; index < row.Count; index++)
            yield return (row[index].Left - row[index - 1].Right) / scale;
    }

    /// <summary>
    /// The whole corpus under the candidate, so the rule is judged on documents it was not designed
    /// against. Samples are taken deterministically, by file name and row order, and are evidence
    /// for adjudication rather than an answer.
    /// </summary>
    private static object CorpusCensus()
    {
        var pdfs = Directory
            .GetFiles(System.IO.Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100"),
                "*.pdf", SearchOption.AllDirectories)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        int documents = 0, rowsTotal = 0, segmentsTotal = 0, unreadable = 0;
        int one = 0, two = 0, three = 0, riskRows = 0, riskSplit = 0, documentsWithDivided = 0;
        var divided = new List<object>();
        var wideButWhole = new List<object>();

        foreach (var path in pdfs)
        {
            IReadOnlyList<PdfLine> segments;
            try
            {
                using var document = PdfDocument.Open(path);
                segments = PdfLineExtraction.ExtractLines(document, PdfLineGrouping.VisualLineSegmentV3);
            }
            catch (Exception)
            {
                // A document this parser cannot open says nothing about segmentation. It is counted
                // out rather than counted as clean.
                unreadable++;
                continue;
            }

            documents++;
            segmentsTotal += segments.Count;
            var rows = Rows(segments);
            rowsTotal += rows.Count;

            var name = System.IO.Path.GetFileName(path);
            var dividedHere = 0;
            var wideHere = 0;
            foreach (var row in rows)
            {
                if (row.Count == 1) one++;
                else if (row.Count == 2) two++;
                else three++;

                var wide = RowGaps(row).Any(gap => gap > 3.0);
                if (wide) riskRows++;

                if (row.Count > 1)
                {
                    dividedHere++;
                    if (wide) riskSplit++;
                    if (dividedHere <= 2) divided.Add(Case(name, row));
                }
                else if (wide && ++wideHere <= 2)
                {
                    wideButWhole.Add(Case(name, row));
                }
            }

            if (dividedHere > 0) documentsWithDivided++;
        }

        return new
        {
            documents,
            unreadableDocuments = unreadable,
            visualRows = rowsTotal,
            totalSegments = segmentsTotal,
            rowsWith1Segment = one,
            rowsWith2Segments = two,
            rowsWith3PlusSegments = three,
            documentsWithMultiSegmentRows = documentsWithDivided,
            riskRowsWithGapOver3LineHeights = riskRows,
            riskRowsActuallyDivided = riskSplit,
            note = "The two risk numbers are not the same measurement. The first counts rows with wide whitespace; the second counts rows the corridor rule divided.",
            dividedSamples = divided,
            wideButUndividedSamples = wideButWhole,
        };
    }

    private static object Case(string document, IReadOnlyList<PdfLine> row) => new
    {
        caseId = $"{document}|p{row[0].Page}|{row[0].Y:F1}",
        page = row[0].Page,
        rowText = string.Join("  ", row.Select(segment => segment.Text)),
        segments = row.Select(segment => new
        {
            text = segment.Text,
            left = Math.Round(segment.Left, 1),
            right = Math.Round(segment.Right, 1),
        }).ToArray(),
    };

    /// <summary>The row a case names, found again in its own document by page and height.</summary>
    private static List<PdfLine>? FindRow(string document, int page, double y)
    {
        var path = Directory
            .GetFiles(System.IO.Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_100"),
                document, SearchOption.AllDirectories)
            .OrderBy(item => item, StringComparer.Ordinal)
            .FirstOrDefault();
        if (path is null) return null;

        using var pdf = PdfDocument.Open(path);
        var rows = Rows(PdfLineExtraction.ExtractLines(pdf, PdfLineGrouping.VisualLineSegmentV3));
        return rows.FirstOrDefault(row =>
            row[0].Page == page && Math.Abs(row[0].Y - y) < 0.05);
    }

    private static object[] Serialize(IReadOnlyList<PdfSourceOccurrenceBoundary.PdfPunctuationRow> rows) =>
        rows.Select(object (row) => new
        {
            mark = row.Mark,
            occurrences = row.Occurrences,
            sharingAVisualLineWithText = row.SharingAVisualLineWithText,
            immediatelyBeforeAGoldOccurrence = row.ImmediatelyBeforeAGoldOccurrence,
        }).ToArray();
}
