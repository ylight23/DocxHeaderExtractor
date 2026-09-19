using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The authority chain for PDF semantic Gold, built before any provider call.
/// <para>
/// DOC-0252 has an approved semantic total and a separately materialized occurrence Gold. The
/// occurrence artifact is created only from the human-owned decision authority after deterministic
/// conversion, validation and reconciliation; it is never synthesized from the total.
/// </para>
/// <para>
/// This file keeps the semantic freeze lineage and source binding assertions in place. The
/// occurrence loader/materializer tests own the second, human-approved occurrence lineage.
/// </para>
/// </summary>
public sealed class PdfGoldAuthorityTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string GoldFreeze =
        "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/DOC-0252.semantic-freeze.v1.json";
    private const string Worksheet = "eval/a99-closed-loop/pdf-gold-doc0252";

    private const string AuthoritativeSourceSha =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const int AuthoritativeTotal = 41;

    // ---- the source universe a reviewer works from -------------------------------------------

    [Fact]
    public async Task The_worksheet_is_the_whole_source_universe_and_nothing_a_model_said()
    {
        // Parser output only: every occurrence, unlabelled, in document order. A worksheet that
        // arrived pre-marked would make the review a confirmation of a model's answer, which is
        // exactly what Gold may not be.
        var (catalog, aliases) = await SourceAsync();

        var rows = catalog.Units
            .OrderBy(unit => unit.SourceOrdinal)
            .Select(unit => new
            {
                sourceAlias = aliases.Single(alias => alias.SourceId == unit.SourceId).Alias,
                sourceId = unit.SourceId,
                ordinal = unit.SourceOrdinal,
                page = unit.SourceAnchor.Page,
                verbatimText = unit.Text,
                characters = unit.Text.Length,
            })
            .ToArray();

        FreezeArtifact.AssertJson(Worksheet, "source-universe.v1.json", new
        {
            artifactKind = "a99_pdf_gold_source_universe",
            schemaVersion = "a99-pdf-gold-source-universe-v1",
            documentId = "DOC-0252",
            sourcePath = Pdf,
            sourceSha256 = AuthoritativeSourceSha,
            projectionVersion = PdfSourceTextProjection.CurrentVersion,
            providerCalls = 0,
            derivedFrom = "PDF parser occurrences only; no model output and no heuristic selection",
            // The approved semantic total is deliberately absent from anything a reviewer opens.
            // Knowing the answer is 41 before starting turns the first pass into a search for 41,
            // and a reviewer who is one over will drop a borderline row to make it fit rather than
            // record the uncertainty. Reconciliation happens after the pass is frozen, where a
            // disagreement is a finding to re-examine instead of a target to hit.
            reviewInstruction =
                "Decide each occurrence on the document's own terms: is this a true heading " +
                "occurrence? Mark NEEDS_REVIEW rather than guessing.",
            occurrences = rows.Length,
            rows,
        });

        Assert.True(rows.Length > 0);
    }

    [Fact]
    public void The_existing_gold_exposes_the_approved_occurrence_freeze()
    {
        // Pinned because occurrence evaluation is allowed only after the approved occurrence
        // authority has been converted and frozen.
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), GoldFreeze)));
        var root = freeze.RootElement;

        Assert.Equal("PDF", root.GetProperty("mediaType").GetString());
        Assert.Equal(AuthoritativeSourceSha, root.GetProperty("sourceSha256").GetString());
        Assert.Equal(AuthoritativeTotal, root.GetProperty("semanticHeadingTotal").GetInt32());
        Assert.True(root.GetProperty("capabilities").GetProperty("semanticEvaluable").GetBoolean());
        Assert.True(root.GetProperty("capabilities").GetProperty("occurrenceEvaluable").GetBoolean());
        Assert.Equal(
            "eval/a99-closed-loop/canonical-semantic-gold-vnext/occurrence/DOC-0252.occurrence-gold.v1.json",
            root.GetProperty("occurrenceArtifact").GetString());
    }

    [Fact]
    public async Task The_source_the_gold_names_is_the_source_this_lane_reads()
    {
        // Lineage, checked rather than assumed. Gold against one rendering of a document and a run
        // against another is the failure mode that produces an unexplainable metric.
        var file = UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(AuthoritativeSourceSha, file.Sha256);
        var (catalog, _) = await SourceAsync();
        Assert.NotEmpty(catalog.Units);
    }

    // ---- the validator -----------------------------------------------------------------------

    [Fact]
    public async Task Gold_that_binds_to_the_catalog_reports_no_issue()
    {
        var (catalog, aliases) = await SourceAsync();
        var gold = Fixture(catalog, aliases, count: 3);

        Assert.Empty(PdfGoldValidator.Validate(gold, catalog, aliases));
    }

    [Fact]
    public async Task An_alias_outside_the_source_universe_is_a_gold_defect_not_a_miss()
    {
        var (catalog, aliases) = await SourceAsync();
        var gold = Fixture(catalog, aliases, count: 1) with
        {
            Headings = [new PdfGoldHeading("S9999", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")],
        };

        var issue = Assert.Single(PdfGoldValidator.Validate(gold, catalog, aliases));
        Assert.Equal(PdfGoldValidator.AliasOutsideUniverse, issue.Code);
    }

    [Fact]
    public async Task Gold_text_must_be_the_verbatim_projection_not_the_readable_rendering()
    {
        // The specific trap for a PDF. DisplayText repairs spacing its own way; the model and the
        // binder use the glyph projection. Gold written against the readable form would not bind,
        // and the failure would look like a model error.
        var (catalog, aliases) = await SourceAsync();
        var blocks = Blocks().ToDictionary(block => block.Id, StringComparer.Ordinal);
        var divergent = catalog.Units.First(unit =>
            blocks.TryGetValue(unit.SourceId, out var block) &&
            !string.Equals(block.DisplayText, block.VerbatimText, StringComparison.Ordinal));
        var alias = aliases.Single(item => item.SourceId == divergent.SourceId).Alias;

        var asDisplayed = new PdfGoldDocument("DOC-0252", AuthoritativeSourceSha,
            [new PdfGoldHeading(alias, CanonicalSemanticSelectionMode.VerbatimText, "SECTION")
            {
                VerbatimText = blocks[divergent.SourceId].DisplayText,
            }]);

        var issue = Assert.Single(PdfGoldValidator.Validate(asDisplayed, catalog, aliases));
        Assert.Equal(PdfGoldValidator.TextNotInSource, issue.Code);
    }

    [Fact]
    public void Text_repeated_inside_one_occurrence_needs_a_disambiguator()
    {
        var catalog = SyntheticCatalog(("p1", "Africa and then Africa again"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var ambiguous = Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.VerbatimText, "SECTION")
        {
            VerbatimText = "Africa",
        }]);

        var issue = Assert.Single(PdfGoldValidator.Validate(ambiguous, catalog, aliases));
        Assert.Equal(PdfGoldValidator.AmbiguousBinding, issue.Code);

        var resolved = Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.VerbatimText, "SECTION")
        {
            VerbatimText = "Africa",
            Occurrence = 2,
        }]);
        Assert.Empty(PdfGoldValidator.Validate(resolved, catalog, aliases));
    }

    [Fact]
    public void A_context_disambiguator_that_matches_nothing_is_reported_rather_than_guessed()
    {
        var catalog = SyntheticCatalog(("p1", "Africa and then Africa again"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);

        var issue = Assert.Single(PdfGoldValidator.Validate(
            Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.VerbatimText, "SECTION")
            {
                VerbatimText = "Africa",
                LeftExactContext = "Europe ",
            }]),
            catalog, aliases));

        Assert.Equal(PdfGoldValidator.DisambiguatorUnresolved, issue.Code);
    }

    [Fact]
    public void A_review_that_does_not_reconcile_with_the_approved_total_is_a_finding()
    {
        var catalog = SyntheticCatalog(("p1", "Opening"), ("p2", "Closing"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var gold = Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")])
            with { SemanticHeadingTotal = 2 };

        var issue = Assert.Single(PdfGoldValidator.Validate(gold, catalog, aliases));
        Assert.Equal(PdfGoldValidator.TotalDisagreesWithAuthority, issue.Code);
    }

    [Fact]
    public void The_two_authorities_keep_separate_lineage_and_a_conflict_is_reported_not_resolved()
    {
        // The approved total and the occurrence review are two acts by two reviewers at two times.
        // A disagreement is a question for them; adopting the new count silently would erase an
        // approval nobody withdrew.
        var catalog = SyntheticCatalog(("p1", "Opening"), ("p2", "Closing"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var gold = Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")]) with
        {
            SemanticHeadingTotal = 2,
            SemanticHeadingTotalAuthority = new PdfGoldAuthorityRecord("USER", "2026-09-12"),
            OccurrenceAuthority = new PdfGoldAuthorityRecord("HUMAN_REVIEWED", "2026-09-19")
            {
                Reviewer = "reviewer-under-test",
                SourceUniverseSha256 = "0000",
            },
        };

        var issue = Assert.Single(PdfGoldValidator.Validate(gold, catalog, aliases));
        Assert.Equal(PdfGoldValidator.TotalDisagreesWithAuthority, issue.Code);
        Assert.Contains("approved by USER on 2026-09-12", issue.Detail, StringComparison.Ordinal);
        // Neither authority was rewritten by the check.
        Assert.Equal(2, gold.SemanticHeadingTotal);
        Assert.Equal("HUMAN_REVIEWED", gold.OccurrenceAuthority!.Authority);
    }

    [Fact]
    public void Claiming_occurrence_truth_without_an_occurrence_review_is_refused()
    {
        var catalog = SyntheticCatalog(("p1", "Opening"));
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var gold = Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")]) with
        {
            Capabilities = new PdfGoldCapabilities { SemanticEvaluable = true, OccurrenceEvaluable = true },
        };

        var issue = Assert.Single(PdfGoldValidator.Validate(gold, catalog, aliases));
        Assert.Equal(PdfGoldValidator.OccurrenceAuthorityMissing, issue.Code);
    }

    // ---- the evaluator -----------------------------------------------------------------------

    [Fact]
    public void A_wrong_parent_does_not_turn_a_correct_heading_into_a_missed_one()
    {
        // The separation that matters. Folding the two together sends the next investigation after
        // a recall problem that does not exist.
        var gold = Gold([
            new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, "SECTION"),
            new PdfGoldHeading("S0002", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")
            {
                ParentSourceAlias = "S0001",
            }]);

        var evaluation = PdfGoldEvaluator.Evaluate(gold, [
            new PdfPredictedHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias),
            new PdfPredictedHeading("S0002", CanonicalSemanticSelectionMode.WholeAlias, ParentSourceAlias: "S0009"),
        ]);

        Assert.Equal(2, evaluation.Semantic.TruePositive);
        Assert.Equal(0, evaluation.Semantic.FalseNegative);
        Assert.Equal(1.0, evaluation.Semantic.Recall);
        // The error lands in the structural metric, where it belongs.
        Assert.Equal(1, evaluation.Structural.Adjudicated);
        Assert.Equal(0, evaluation.Structural.Agreed);
        Assert.Equal(1, evaluation.Structural.Disagreed);
        Assert.Contains("expected S0001, got S0009", Assert.Single(evaluation.Structural.Disagreements));
    }

    [Fact]
    public void A_relation_gold_never_adjudicated_is_excluded_rather_than_failed()
    {
        // Gold silent about a parent is not Gold asserting there is none.
        var gold = Gold([new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")]);

        var evaluation = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, ParentSourceAlias: "S0000")]);

        Assert.Equal(0, evaluation.Structural.Adjudicated);
        Assert.Equal(1.0, evaluation.Structural.Accuracy);
        Assert.Equal(1, evaluation.Semantic.TruePositive);
    }

    [Fact]
    public void Membership_counts_misses_and_spurious_headings_by_name()
    {
        var gold = Gold([
            new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias, "SECTION"),
            new PdfGoldHeading("S0002", CanonicalSemanticSelectionMode.WholeAlias, "SECTION")]);

        var evaluation = PdfGoldEvaluator.Evaluate(gold, [
            new PdfPredictedHeading("S0001", CanonicalSemanticSelectionMode.WholeAlias),
            new PdfPredictedHeading("S0003", CanonicalSemanticSelectionMode.WholeAlias),
        ]);

        Assert.Equal(1, evaluation.Semantic.TruePositive);
        Assert.Equal(["S0002"], evaluation.Semantic.MissedAliases);
        Assert.Equal(["S0003"], evaluation.Semantic.SpuriousAliases);
        Assert.Equal(0.5, evaluation.Semantic.Recall);
        Assert.Equal(0.5, evaluation.Semantic.Precision);
    }

    [Fact]
    public void The_same_text_in_two_occurrences_is_two_headings_not_one()
    {
        // Identity is the occurrence, never the words. A minutes document repeats agenda labels,
        // and matching on text would silently merge them.
        var gold = Gold([
            new PdfGoldHeading("S0001", CanonicalSemanticSelectionMode.VerbatimText, "SECTION") { VerbatimText = "Agenda" },
            new PdfGoldHeading("S0007", CanonicalSemanticSelectionMode.VerbatimText, "SECTION") { VerbatimText = "Agenda" }]);

        var evaluation = PdfGoldEvaluator.Evaluate(gold,
            [new PdfPredictedHeading("S0001", CanonicalSemanticSelectionMode.VerbatimText, "Agenda")]);

        Assert.Equal(1, evaluation.Semantic.TruePositive);
        Assert.Equal(["S0007"], evaluation.Semantic.MissedAliases);
    }

    [Fact]
    public async Task The_evaluator_runs_offline_against_a_frozen_prediction_and_spends_no_provider_call()
    {
        // Acceptance 7: the whole chain executes with no provider. The prediction here is the
        // deterministic no-model run, which legitimately finds nothing - what is proved is that
        // Gold, validation and scoring compose, not that the lane is any good.
        var (catalog, aliases) = await SourceAsync();
        var gold = Fixture(catalog, aliases, count: 3);
        var issues = PdfGoldValidator.Validate(gold, catalog, aliases);

        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });
        var predicted = document.Structure.Elements
            .Select(element => new PdfPredictedHeading(
                element.Sources[0].SourceId, CanonicalSemanticSelectionMode.WholeAlias))
            .ToArray();

        var evaluation = PdfGoldEvaluator.Evaluate(gold, predicted, issues);

        Assert.Empty(evaluation.GoldIssues);
        Assert.Equal(3, evaluation.GoldRows);
        Assert.Equal(0, evaluation.Semantic.TruePositive);
        Assert.Equal(3, evaluation.Semantic.FalseNegative);
        Assert.Equal(0, document.Provenance.ProviderCalls);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// A fixture for exercising the machinery, taken from the top of the source universe. It is
    /// explicitly not an assertion about which occurrences are headings in DOC-0252 - that is the
    /// human review this file exists to prepare for.
    /// </summary>
    private static PdfGoldDocument Fixture(
        DocumentSourceCatalog catalog, IReadOnlyList<SemanticSourceAlias> aliases, int count) =>
        new("DOC-0252", AuthoritativeSourceSha,
            catalog.Units.OrderBy(unit => unit.SourceOrdinal).Take(count)
                .Select(unit => new PdfGoldHeading(
                    aliases.Single(alias => alias.SourceId == unit.SourceId).Alias,
                    CanonicalSemanticSelectionMode.WholeAlias,
                    "SECTION"))
                .ToArray())
        {
            FinalAuthority = "FIXTURE_NOT_AUTHORITY",
        };

    private static PdfGoldDocument Gold(IReadOnlyList<PdfGoldHeading> headings) =>
        new("DOC-TEST", AuthoritativeSourceSha, headings);

    private static DocumentSourceCatalog SyntheticCatalog(params (string Id, string Text)[] units) =>
        new(units.Select((unit, index) => new DocumentSourceUnit(
            unit.Id, index, unit.Text,
            new SourceAnchor { SourceType = "pdf", ParagraphId = unit.Id, ParagraphIndex = index },
            new StructuralSpan(0, unit.Text.Length))));

    private static async Task<(DocumentSourceCatalog Catalog, IReadOnlyList<SemanticSourceAlias> Aliases)> SourceAsync()
    {
        var document = await PdfCanonicalExtraction.RunAsync(
            UploadedFile.FromLocalPath(Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });
        return (document.SourceCatalog, SemanticSourceAliasCatalog.FromCatalog(document.SourceCatalog));
    }

    private static IReadOnlyList<PdfSemanticBlock> Blocks()
    {
        var path = Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
