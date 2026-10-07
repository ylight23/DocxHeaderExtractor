using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Three ways of reading the same 1,013 source occurrences, so a reviewer can work through all of
/// them without any of them being pre-judged.
/// <para>
/// Each view is an exhaustive partition, not a shortlist. A candidate filter would decide the
/// question the review exists to answer: anything it left out could never become Gold, and the
/// recall ceiling would quietly become the filter rather than the source universe.
/// </para>
/// <para>
/// No view carries a likelihood, a score, a recommendation, a predicted role, or candidate
/// attention. Candidate attention used to be the subtle case: it was parser-owned routing evidence
/// and exactly the heuristic the evaluation is meant to test. The contract no longer exposes that
/// slot, so a reviewer cannot be anchored by it.
/// </para>
/// <para>
/// SHORT_TEXT is a reading aid and nothing more. Those rows stay in the partition and still need a
/// decision: a page number is a parser fact, not a Gold gate.
/// </para>
/// </summary>
public sealed class PdfGoldReviewPackTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string Pack = "eval/a99-closed-loop/pdf-gold-doc0252";

    private const string SourceSha = "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";

    /// <summary>
    /// The three answers a reviewer may give. Kept separate from the role on purpose: encoding the
    /// role into the decision would make membership and role one number, and they have to be
    /// measurable apart - a heading found with the wrong role is not a heading that was missed.
    /// </summary>
    private static readonly string[] AllowedDecisions = ["HEADING", "NOT_HEADING", "NEEDS_REVIEW"];

    /// <summary>
    /// A source occurrence is a parser artefact, not a semantic unit, so the membership decision
    /// and the headings found inside it are different questions. S0043, S0460 and S0573 each carry
    /// a session heading and a numbered sub-heading in one fused two-line block; one answer per
    /// occurrence could not say which heading, with which boundary, in which role.
    /// </summary>
    private static readonly object ClaimContract = new
    {
        NOT_HEADING = "headingClaims must be empty",
        HEADING = "headingClaims must name at least one heading; several are allowed",
        NEEDS_REVIEW = "leave the claims as they are; a second pass settles it",
        selectionMode = "WHOLE_ALIAS when the heading is the entire occurrence, else VERBATIM_TEXT",
        verbatimText = "for VERBATIM_TEXT: the exact heading text, copied from sourceText",
        occurrence = "1-based, only when that text appears more than once in this occurrence",
        mapping = "each claim becomes exactly one PdfGoldHeading, field for field",
    };

    /// <summary>
    /// One occurrence as a reviewer receives it: the membership decision, and a blank claim to fill
    /// in. A source occurrence is a parser artefact, not a semantic unit - line grouping fuses
    /// neighbouring lines, so one occurrence can hold more than one heading - so the claims are a
    /// list. Delete it for NOT_HEADING; add to it when the occurrence carries several headings.
    /// </summary>
    private static PdfReviewOccurrence ReviewRow(string alias, int page, int ordinal, string text) =>
        new(alias, page, ordinal, text) { HeadingClaims = [new PdfReviewHeadingClaim()] };

    [Fact]
    public async Task A_duplicate_text_group_offers_no_way_to_answer_for_the_whole_group()
    {
        // The correction that matters here. Grouping identical wording makes an inconsistency
        // visible; it must not make one answer cover several occurrences. The same string can be a
        // table-of-contents entry, a body heading, a running header and a mention in prose, so same
        // text is neither the same occurrence nor the same node.
        await RowsAsync();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, "review-duplicate-text-index.v1.json")));

        Assert.Equal("PER_OCCURRENCE", document.RootElement.GetProperty("decisionScope").GetString());
        var repeated = document.RootElement.GetProperty("groups").EnumerateArray()
            .Where(group => group.GetProperty("repeated").GetBoolean()).ToArray();
        Assert.NotEmpty(repeated);

        foreach (var group in document.RootElement.GetProperty("groups").EnumerateArray())
        {
            // No group-level answer exists to be filled in.
            foreach (var field in new[] { "humanDecision", "semanticRole", "parentSourceAlias", "headingClaims" })
                Assert.False(group.TryGetProperty(field, out _), $"group carries {field}");

            // Every occurrence in the group carries its own, still undecided.
            var rows = group.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(group.GetProperty("occurrences").GetInt32(), rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("humanDecision").ValueKind);
                // The claims live on the occurrence, never on the group.
                Assert.Equal(1, row.GetProperty("headingClaims").GetArrayLength());
            });

            // Aliases within a group are distinct occurrences, never one repeated row.
            var aliases = rows.Select(row => row.GetProperty("sourceAlias").GetString()!).ToArray();
            Assert.Equal(aliases.Length, aliases.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void A_claim_maps_onto_a_gold_heading_field_for_field()
    {
        // Mechanical on purpose. Anything the conversion had to infer would be this code deciding
        // what a reviewer meant.
        var reviewed = new PdfReviewOccurrence("S0100", 3, 99, "Africa and then Africa again")
        {
            HumanDecision = PdfGoldReview.Heading,
            HeadingClaims =
            [
                new PdfReviewHeadingClaim
                {
                    SelectionMode = CanonicalSemanticSelectionMode.VerbatimText,
                    VerbatimText = "Africa",
                    Occurrence = 2,
                    LeftExactContext = "then ",
                    RightExactContext = " again",
                    SemanticRole = "SECTION",
                    ParentSourceAlias = "S0099",
                },
            ],
        };

        var heading = Assert.Single(PdfGoldReview.ToGoldHeadings([reviewed]));

        Assert.Equal("S0100", heading.SourceAlias);
        Assert.Equal(CanonicalSemanticSelectionMode.VerbatimText, heading.SelectionMode);
        Assert.Equal("Africa", heading.VerbatimText);
        Assert.Equal(2, heading.Occurrence);
        Assert.Equal("then ", heading.LeftExactContext);
        Assert.Equal(" again", heading.RightExactContext);
        Assert.Equal("SECTION", heading.SemanticRole);
        Assert.Equal("S0099", heading.ParentSourceAlias);
    }

    [Fact]
    public void Conversion_refuses_a_review_that_is_not_finished()
    {
        // Correctness must not depend on a caller remembering to check first. Before this, skipping
        // Check produced Gold with empty-string selection mode and role, and nothing downstream
        // looked for those - a malformed Gold would have travelled a long way before anything said
        // so.
        var unfinished = new PdfReviewOccurrence("S0001", 1, 0, "Opening")
        {
            HumanDecision = PdfGoldReview.Heading,
            HeadingClaims = [new PdfReviewHeadingClaim()],
        };

        Assert.False(PdfGoldReview.TryToGoldHeadings([unfinished], out var headings, out var issues));
        Assert.Empty(headings);
        Assert.Contains(issues, issue => issue.Code == PdfGoldReview.ClaimSelectionMissing);

        var error = Assert.Throws<InvalidOperationException>(() => PdfGoldReview.ToGoldHeadings([unfinished]));
        Assert.Contains(PdfGoldReview.ClaimRoleMissing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_conversion_ever_produces_an_empty_selection_mode_or_role()
    {
        // The property, not the path: whatever comes out is a heading someone actually asserted.
        var reviews = new[]
        {
            new PdfReviewOccurrence("S0001", 1, 0, "A") { HumanDecision = PdfGoldReview.NotHeading },
            new PdfReviewOccurrence("S0002", 1, 1, "B")
            {
                HumanDecision = PdfGoldReview.Heading,
                HeadingClaims =
                [
                    new PdfReviewHeadingClaim
                    {
                        SelectionMode = CanonicalSemanticSelectionMode.WholeAlias,
                        SemanticRole = "SECTION",
                        ReviewNote = "kept with the review, not with the Gold",
                    },
                ],
            },
        };

        Assert.True(PdfGoldReview.TryToGoldHeadings(reviews, out var headings, out var issues));
        Assert.Empty(issues);
        Assert.All(headings, heading =>
        {
            Assert.False(string.IsNullOrWhiteSpace(heading.SelectionMode));
            Assert.False(string.IsNullOrWhiteSpace(heading.SemanticRole));
        });
    }

    [Fact]
    public void A_review_note_stays_with_the_review_and_never_enters_the_gold_heading()
    {
        // Deliberate, and stated so the "maps field for field" claim is true of the semantic fields
        // and honest about the one that is not. A rationale explains a decision; it is not part of
        // what the document is held to contain, and growing Gold to carry it would grow the thing
        // every evaluation compares against.
        var reviewed = new PdfReviewOccurrence("S0001", 1, 0, "Opening")
        {
            HumanDecision = PdfGoldReview.Heading,
            HeadingClaims =
            [
                new PdfReviewHeadingClaim
                {
                    SelectionMode = CanonicalSemanticSelectionMode.WholeAlias,
                    SemanticRole = "SECTION",
                    ReviewNote = "bold, opens the session, matches the agenda",
                },
            ],
        };

        var heading = Assert.Single(PdfGoldReview.ToGoldHeadings([reviewed]));

        Assert.DoesNotContain(typeof(PdfGoldHeading).GetProperties(),
            property => property.Name.Contains("Note", StringComparison.OrdinalIgnoreCase));
        // The semantic fields a claim does carry are all present on the heading.
        Assert.Equal("SECTION", heading.SemanticRole);
        Assert.Equal(CanonicalSemanticSelectionMode.WholeAlias, heading.SelectionMode);
    }

    [Fact]
    public void Gold_written_by_hand_without_a_role_is_refused_by_the_validator_too()
    {
        // The conversion is one way in; it is not the only one. A Gold assembled by another tool
        // has to meet the same bar.
        var catalog = SyntheticCatalog(("p1", "Opening"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var byHand = new PdfGoldDocument("DOC-TEST", "0000",
            [new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, string.Empty)]);

        var issue = Assert.Single(PdfGoldValidator.Validate(byHand, catalog, aliases));
        Assert.Equal(PdfGoldValidator.SemanticRoleMissing, issue.Code);
    }

    [Theory]
    [InlineData(null, 1, PdfGoldReview.DecisionMissing)]
    [InlineData("NOT_HEADING", 1, PdfGoldReview.NotHeadingCarriesClaims)]
    [InlineData("HEADING", 0, PdfGoldReview.HeadingWithoutClaim)]
    [InlineData("NEEDS_REVIEW", 1, PdfGoldReview.StillNeedsReview)]
    [InlineData("MAYBE", 1, PdfGoldReview.DecisionUnknown)]
    public void The_decision_and_its_claims_have_to_agree_before_anything_is_frozen(
        string? decision, int claims, string expected)
    {
        var reviewed = new PdfReviewOccurrence("S0001", 1, 0, "Opening")
        {
            HumanDecision = decision,
            HeadingClaims = claims == 0
                ? []
                : [new PdfReviewHeadingClaim
                {
                    SelectionMode = CanonicalSemanticSelectionMode.WholeAlias,
                    SemanticRole = "SECTION",
                }],
        };

        Assert.Equal(expected, Assert.Single(PdfGoldReview.Check([reviewed])).Code);
    }

    [Fact]
    public void A_claim_that_does_not_say_enough_is_reported_rather_than_defaulted()
    {
        var reviewed = new PdfReviewOccurrence("S0001", 1, 0, "Opening")
        {
            HumanDecision = PdfGoldReview.Heading,
            HeadingClaims = [new PdfReviewHeadingClaim()],
        };

        var codes = PdfGoldReview.Check([reviewed]).Select(issue => issue.Code).ToArray();

        Assert.Contains(PdfGoldReview.ClaimSelectionMissing, codes);
        Assert.Contains(PdfGoldReview.ClaimRoleMissing, codes);
    }

    [Fact]
    public async Task Membership_and_role_are_separate_fields_in_every_view()
    {
        // So they stay separately measurable: a heading found with the wrong role is a role error,
        // not a missed heading.
        await RowsAsync();

        foreach (var view in AllViews.Where(view => view != "source-universe.v1.json"))
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, view)));
            Assert.Equal(
                ["HEADING", "NOT_HEADING", "NEEDS_REVIEW"],
                document.RootElement.GetProperty("allowedDecisions").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray());
            Assert.Equal("PER_OCCURRENCE", document.RootElement.GetProperty("decisionScope").GetString());
        }
    }

    [Fact]
    public async Task No_view_carries_a_judgement_a_reviewer_could_anchor_on()
    {
        await RowsAsync();
        string[] forbidden =
        [
            "likelyHeading", "candidate", "candidateAttention", "score", "recommendation",
            "predictedRole", "modelOutput", "heuristicMatch", "attention", "prediction",
            "likelihood", "confidence",
        ];

        foreach (var view in AllViews)
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, view)));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectNames(document.RootElement, names);

            // Field names only. The document's own text legitimately contains words like
            // "candidate" - "Peru is an accession candidate to the OECD" - and scanning values
            // would reject the source for saying what it says.
            foreach (var needle in forbidden)
                Assert.DoesNotContain(names, name => name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task No_view_shows_the_approved_heading_total()
    {
        // A first pass that knows the answer is 41 becomes a search for 41. Reconciliation is a
        // separate step, after the pass is frozen.
        await RowsAsync();

        foreach (var view in AllViews)
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, view)));
            Assert.False(document.RootElement.TryGetProperty("authoritativeSemanticHeadingTotal", out _),
                $"{view} shows the reviewer the answer");
            Assert.False(document.RootElement.TryGetProperty("semanticHeadingTotal", out _));
        }
    }

    [Fact]
    public async Task Short_text_is_a_reading_group_and_still_needs_a_decision()
    {
        await RowsAsync();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, "review-by-parser-context.v1.json")));

        var shortRows = document.RootElement.GetProperty("pages").EnumerateArray()
            .SelectMany(page => page.GetProperty("scopes").EnumerateArray())
            .SelectMany(scope => scope.GetProperty("rows").EnumerateArray())
            .Where(row => row.GetProperty("readingGroup").GetString() == "SHORT_TEXT")
            .ToArray();

        Assert.NotEmpty(shortRows);
        Assert.All(shortRows, row =>
            Assert.Equal(JsonValueKind.Null, row.GetProperty("humanDecision").ValueKind));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static readonly string[] AllViews =
    [
        "source-universe.v1.json",
        "review-by-page.v1.json",
        "review-by-parser-context.v1.json",
        "review-duplicate-text-index.v1.json",
    ];

    private static void CollectNames(JsonElement element, HashSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    into.Add(property.Name);
                    CollectNames(property.Value, into);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectNames(item, into);
                break;
        }
    }

    private static DocumentSourceCatalog SyntheticCatalog(params (string Id, string Text)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id, index, unit.Text,
            new SourceAnchor { SourceType = "pdf", ParagraphId = unit.Id, ParagraphIndex = index },
            new StructuralSpan(0, unit.Text.Length))));

    private sealed record Row(string Alias, string SourceId, int Page, int Ordinal, string Text);

    private static async Task<IReadOnlyList<Row>> RowsAsync()
    {
        var document = await PdfExtractionPipeline.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });
        var aliases = SemanticSourceAliasCatalog.FromCatalog(document.SourceCatalog)
            .ToDictionary(alias => alias.SourceId, alias => alias.Alias, StringComparer.Ordinal);
        return document.SourceCatalog.Units
            .OrderBy(unit => unit.SourceOrdinal)
            .Select(unit => new Row(
                aliases[unit.SourceId], unit.SourceId, unit.SourceAnchor.Page ?? 0, unit.SourceOrdinal, unit.Text))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, PdfSourceFacts> FactsAsync()
    {
        var path = Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        var annotations = PdfLineObservationAnalyzer.Analyze(lines);
        var blocks = PdfSemanticBlockGrouper.Build(annotations);
        return PdfSemanticSourceContextBuilder.Build(blocks, annotations)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Source, StringComparer.Ordinal);
    }

    private static List<string> AliasesOf(string view)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), Pack, view)));
        var aliases = new List<string>();
        Walk(document.RootElement, aliases);
        return aliases;

        static void Walk(JsonElement element, List<string> into)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("sourceAlias", out var alias) &&
                        alias.ValueKind == JsonValueKind.String)
                        into.Add(alias.GetString()!);
                    foreach (var property in element.EnumerateObject()) Walk(property.Value, into);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item, into);
                    break;
            }
        }
    }

    private static void AssertExhaustive(string view, IReadOnlyList<Row> rows)
    {
        var aliases = AliasesOf(view);
        Assert.Equal(rows.Count, aliases.Count);
        Assert.Equal(rows.Count, aliases.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Grouping only: case and whitespace folded so repeats sit together while reading.</summary>
    private static string Key(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string RelativeSize(double size, double body)
    {
        if (size <= 0 || body <= 0) return "unknown";
        var ratio = size / body;
        return ratio >= 1.25 ? "much-larger-than-body"
            : ratio >= 1.08 ? "larger-than-body"
            : ratio <= 0.85 ? "smaller-than-body"
            : "body";
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }

}
