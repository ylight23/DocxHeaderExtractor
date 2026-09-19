using System.Text.Json;
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
/// No view carries a likelihood, a score, a recommendation, a predicted role, or
/// CandidateAttention. That last one is the subtle case - it is real parser-owned routing evidence,
/// and it is exactly the heuristic the evaluation is meant to test. A reviewer who sees it is
/// anchored by it, and the Gold stops being independent of the thing it measures.
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

    /// <summary>The fields a reviewer fills in. Null means undecided, never "no".</summary>
    private static object ReviewRow(string alias, int page, int ordinal, string text) => new
    {
        sourceAlias = alias,
        page,
        sourceOrdinal = ordinal,
        verbatimText = text,
        humanDecision = (string?)null,
        semanticRole = (string?)null,
        // Null means the reviewer did not adjudicate this relation. It does not mean "root".
        parentSourceAlias = (string?)null,
        reviewNote = (string?)null,
    };

    [Fact]
    public async Task View_A_groups_every_occurrence_by_page()
    {
        var rows = await RowsAsync();

        FreezeArtifact.AssertJson(Pack, "review-by-page.v1.json", new
        {
            artifactKind = "a99_pdf_gold_review_view",
            schemaVersion = "a99-pdf-gold-review-view-v1",
            view = "BY_PAGE",
            documentId = "DOC-0252",
            allowedDecisions = AllowedDecisions,
            decisionScope = "PER_OCCURRENCE",
            sourceSha256 = SourceSha,
            providerCalls = 0,
            derivedFrom = "PDF parser occurrences only",
            exhaustive = true,
            occurrences = rows.Count,
            pages = rows.GroupBy(row => row.Page).OrderBy(group => group.Key).Select(group => new
            {
                page = group.Key,
                occurrences = group.Count(),
                rows = group.OrderBy(row => row.Ordinal)
                    .Select(row => ReviewRow(row.Alias, row.Page, row.Ordinal, row.Text)).ToArray(),
            }).ToArray(),
        });

        AssertExhaustive("review-by-page.v1.json", rows);
    }

    [Fact]
    public async Task View_B_adds_the_parser_evidence_that_is_not_a_judgement()
    {
        var rows = await RowsAsync();
        var facts = FactsAsync();
        var bodyFontSize = Median(facts.Values.Select(fact => fact.FontSize));

        FreezeArtifact.AssertJson(Pack, "review-by-parser-context.v1.json", new
        {
            artifactKind = "a99_pdf_gold_review_view",
            schemaVersion = "a99-pdf-gold-review-view-v1",
            view = "BY_PARSER_CONTEXT",
            documentId = "DOC-0252",
            allowedDecisions = AllowedDecisions,
            decisionScope = "PER_OCCURRENCE",
            sourceSha256 = SourceSha,
            providerCalls = 0,
            derivedFrom = "PDF parser occurrences only",
            excludedOnPurpose = new[]
            {
                "candidateAttention/heuristicMatch - the routing heuristic this evaluation tests",
                "domainRole - a classification, not an observation",
                "likelihood, score, recommendation, predictedRole - none exist here",
            },
            exhaustive = true,
            occurrences = rows.Count,
            pages = rows.GroupBy(row => row.Page).OrderBy(group => group.Key).Select(group => new
            {
                page = group.Key,
                scopes = group
                    .GroupBy(row => facts.TryGetValue(row.SourceId, out var fact) ? fact.StructuralScope : "unknown")
                    .OrderBy(scope => scope.Key, StringComparer.Ordinal)
                    .Select(scope => new
                    {
                        structuralScope = scope.Key,
                        occurrences = scope.Count(),
                        rows = scope.OrderBy(row => row.Ordinal).Select(row =>
                        {
                            var fact = facts.GetValueOrDefault(row.SourceId);
                            return new
                            {
                                sourceAlias = row.Alias,
                                page = row.Page,
                                sourceOrdinal = row.Ordinal,
                                verbatimText = row.Text,
                                readingGroup = row.Text.Trim().Length <= 3 ? "SHORT_TEXT" : "TEXT",
                                style = fact is null ? null : new
                                {
                                    bold = fact.BoldRatio >= 0.5,
                                    italic = fact.ItalicRatio >= 0.5,
                                    relativeFontSize = RelativeSize(fact.FontSize, bodyFontSize),
                                    lineCount = fact.LineCount,
                                },
                                markers = fact is null ? [] : CanonicalSemanticEngine.MarkerFactsOf(fact),
                                observedEvidence = fact?.ObservedEvidence ?? [],
                                humanDecision = (string?)null,
                                semanticRole = (string?)null,
                                parentSourceAlias = (string?)null,
                                reviewNote = (string?)null,
                            };
                        }).ToArray(),
                    }).ToArray(),
            }).ToArray(),
        });

        AssertExhaustive("review-by-parser-context.v1.json", rows);
    }

    [Fact]
    public async Task View_C_puts_occurrences_that_read_alike_next_to_each_other()
    {
        // A minutes document repeats agenda labels and running headers. Reviewing those apart, on
        // different pages, is how a reviewer ends up treating the same thing two ways; seeing them
        // together is how an inconsistency becomes visible.
        //
        // Comparison, not decision. The same string can be a table-of-contents entry, a body
        // heading, a running header and a passing mention in prose, so same text is neither the
        // same occurrence nor the same node. The grouping key is a reading convenience; the text a
        // reviewer judges stays the exact VerbatimText, and every occurrence keeps its own answer.
        var rows = await RowsAsync();

        var groups = rows
            .GroupBy(row => Key(row.Text), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                groupKey = group.Key,
                occurrences = group.Count(),
                repeated = group.Count() > 1,
                rows = group.OrderBy(row => row.Ordinal)
                    .Select(row => ReviewRow(row.Alias, row.Page, row.Ordinal, row.Text)).ToArray(),
            }).ToArray();

        FreezeArtifact.AssertJson(Pack, "review-duplicate-text-index.v1.json", new
        {
            artifactKind = "a99_pdf_gold_review_view",
            schemaVersion = "a99-pdf-gold-review-view-v1",
            view = "DUPLICATE_TEXT_INDEX",
            documentId = "DOC-0252",
            allowedDecisions = AllowedDecisions,
            decisionScope = "PER_OCCURRENCE",
            sourceSha256 = SourceSha,
            providerCalls = 0,
            derivedFrom = "PDF parser occurrences only",
            groupKeyNote = "Normalised for grouping only. Judge the exact verbatimText on each row.",
            groupingContract =
                "Grouped so the same wording can be compared in one place, never so it can be " +
                "decided in one place. Identical text is not identical semantics: the same string " +
                "can be a table-of-contents entry, a true section heading in the body, a running " +
                "header artefact and an ordinary mention in prose. Every occurrence keeps its own " +
                "decision. Applying one answer across a group is only ever a reviewer's explicit " +
                "act after confirming the occurrences really are treated alike.",
            exhaustive = true,
            occurrences = rows.Count,
            repeatedGroups = groups.Count(group => group.repeated),
            groups,
        });

        AssertExhaustive("review-duplicate-text-index.v1.json", rows);
    }

    [Fact]
    public async Task A_duplicate_text_group_offers_no_way_to_answer_for_the_whole_group()
    {
        // The correction that matters here. Grouping identical wording makes an inconsistency
        // visible; it must not make one answer cover several occurrences. The same string can be a
        // table-of-contents entry, a body heading, a running header and a mention in prose, so same
        // text is neither the same occurrence nor the same node.
        await RowsAsync();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, "review-duplicate-text-index.v1.json")));

        Assert.Equal("PER_OCCURRENCE", document.RootElement.GetProperty("decisionScope").GetString());
        var repeated = document.RootElement.GetProperty("groups").EnumerateArray()
            .Where(group => group.GetProperty("repeated").GetBoolean()).ToArray();
        Assert.NotEmpty(repeated);

        foreach (var group in document.RootElement.GetProperty("groups").EnumerateArray())
        {
            // No group-level answer exists to be filled in.
            foreach (var field in new[] { "humanDecision", "semanticRole", "parentSourceAlias" })
                Assert.False(group.TryGetProperty(field, out _), $"group carries {field}");

            // Every occurrence in the group carries its own, still undecided.
            var rows = group.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(group.GetProperty("occurrences").GetInt32(), rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Equal(JsonValueKind.Null, row.GetProperty("humanDecision").ValueKind);
                Assert.Equal(JsonValueKind.Null, row.GetProperty("semanticRole").ValueKind);
            });

            // Aliases within a group are distinct occurrences, never one repeated row.
            var aliases = rows.Select(row => row.GetProperty("sourceAlias").GetString()!).ToArray();
            Assert.Equal(aliases.Length, aliases.Distinct(StringComparer.Ordinal).Count());
        }
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
                File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, view)));
            Assert.Equal(
                ["HEADING", "NOT_HEADING", "NEEDS_REVIEW"],
                document.RootElement.GetProperty("allowedDecisions").EnumerateArray()
                    .Select(item => item.GetString()).ToArray());
            Assert.Equal("PER_OCCURRENCE", document.RootElement.GetProperty("decisionScope").GetString());
        }
    }

    [Fact]
    public async Task Every_view_is_a_partition_of_the_same_universe()
    {
        // The property that makes the pack safe to review: three readings, one set of occurrences.
        // If any view could drop a row, the recall ceiling would become that view.
        var rows = await RowsAsync();
        var universe = rows.Select(row => row.Alias).ToHashSet(StringComparer.Ordinal);

        foreach (var view in new[]
        {
            "review-by-page.v1.json",
            "review-by-parser-context.v1.json",
            "review-duplicate-text-index.v1.json",
        })
        {
            var aliases = AliasesOf(view);
            Assert.Equal(rows.Count, aliases.Count);
            Assert.Equal(universe.Count, aliases.Distinct(StringComparer.Ordinal).Count());
            Assert.Empty(universe.Except(aliases, StringComparer.Ordinal));
            Assert.Empty(aliases.Except(universe, StringComparer.Ordinal));
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
                File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, view)));
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
                File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, view)));
            Assert.False(document.RootElement.TryGetProperty("authoritativeSemanticHeadingTotal", out _),
                $"{view} shows the reviewer the answer");
            Assert.False(document.RootElement.TryGetProperty("semanticHeadingTotal", out _));
        }
    }

    [Fact]
    public async Task The_text_a_reviewer_judges_is_the_text_the_binder_binds()
    {
        // No trimming, no punctuation repair, no readable rendering. Gold written against anything
        // else would not bind, and that failure would look like a model error.
        var rows = await RowsAsync();
        var byAlias = rows.ToDictionary(row => row.Alias, row => row.Text, StringComparer.Ordinal);

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, "review-by-page.v1.json")));
        foreach (var page in document.RootElement.GetProperty("pages").EnumerateArray())
        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            var alias = row.GetProperty("sourceAlias").GetString()!;
            Assert.Equal(byAlias[alias], row.GetProperty("verbatimText").GetString());
        }
    }

    [Fact]
    public async Task Short_text_is_a_reading_group_and_still_needs_a_decision()
    {
        await RowsAsync();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, "review-by-parser-context.v1.json")));

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

    private sealed record Row(string Alias, string SourceId, int Page, int Ordinal, string Text);

    private static async Task<IReadOnlyList<Row>> RowsAsync()
    {
        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
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
        var path = Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        var annotations = PdfLineBlockFilter.Analyze(lines);
        var blocks = PdfSemanticBlockGrouper.Build(annotations, includeRiskLines: true);
        return PdfCandidateContextBuilder.Build(blocks, annotations)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Source, StringComparer.Ordinal);
    }

    private static List<string> AliasesOf(string view)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), Pack, view)));
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

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
