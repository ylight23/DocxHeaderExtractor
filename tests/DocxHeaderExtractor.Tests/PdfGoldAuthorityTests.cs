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
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRepository.Root(), GoldFreeze)));
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
        var file = UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar)));

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
            UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
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

    // ---- reconfirmation of the semantic decision ----------------------------------------------

    [Fact]
    public void The_source_reconfirmation_is_recorded_beside_Gold_and_never_inside_it()
    {
        // A re-audit that confirms a decision is a second observation of it, not a second decision.
        // Writing it into canonical Gold would move goldSha256, and the executed baseline names
        // that hash as the authority it ran against - so the file that proves the baseline honest
        // would have been edited by the audit that came after it. The event lives beside Gold
        // instead, in the DOC-0252 authority-audit pack that already holds this document's review
        // lineage, and Gold stays byte-identical.
        var entry = CanonicalGoldRegistry.Entry("DOC-0252");
        using var gold = CanonicalGoldRegistry.Resolve("DOC-0252");   // throws on hash mismatch
        var semantic = gold.RootElement.GetProperty("semantic");
        var approval = gold.RootElement.GetProperty("approval");

        var claims = semantic.GetProperty("claims").EnumerateArray()
            .Select(claim => new
            {
                sourceAlias = claim.GetProperty("sourceAlias").GetString()!,
                selectionMode = claim.GetProperty("selectionMode").GetString()!,
                verbatimText = claim.GetProperty("verbatimText").GetString(),
            })
            .ToArray();

        Assert.Equal(AuthoritativeTotal, claims.Length);
        Assert.Equal(AuthoritativeTotal, semantic.GetProperty("approvedSemanticTotal").GetInt32());
        Assert.Equal(AuthoritativeTotal, entry.SemanticHeadingTotal);

        // The baseline's own record of what it ran against, read from the run rather than restated.
        var run = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestRepository.Root(), "eval/a99-closed-loop/occurrence-baseline-v1/run.v1.json"
                .Replace('/', Path.DirectorySeparatorChar))));
        var executionHash = run.RootElement.GetProperty("runs").EnumerateArray()
            .Where(item => item.GetProperty("documentId").GetString() == "DOC-0252")
            .Select(item => item.GetProperty("goldHash").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .Single();
        Assert.Equal(entry.GoldSha256, executionHash);
        Assert.Equal(entry.GoldSha256, CanonicalArtifactHash.OfTextFile(
            Path.Combine(TestRepository.Root(), entry.CanonicalGoldPath.Replace('/', Path.DirectorySeparatorChar))));

        FreezeArtifact.AssertJson(Worksheet, "gold-source-reconfirmation.v1.json", new
        {
            artifactKind = "a99_semantic_gold_source_reconfirmation",
            schemaVersion = "a99-semantic-gold-source-reconfirmation-v1",
            kind = "SEMANTIC_GOLD_SOURCE_RECONFIRMATION",
            authorityId = "DOC-0252",
            result = "CONFIRMED",
            providerCalls = 0,
            modelCalls = 0,

            semanticHeadingTotal = AuthoritativeTotal,
            add = 0,
            remove = 0,
            needsReview = 0,
            membershipChanged = false,

            basis = new[] { "ORIGINAL_SOURCE_VISUAL_REAUDIT", "INDEPENDENT_41_CLAIM_SOURCE_DIFF" },

            // Referenced, never restated: the decision this confirms was approved once, by USER, on
            // its own date. A reconfirmation that carried its own approval date would read as a
            // second approval and would quietly become the one later work cites.
            reconfirms = new
            {
                approvalAuthority = approval.GetProperty("authority").GetString(),
                userFinalApproval = approval.GetProperty("userFinalApproval").GetBoolean(),
                approvalBasis = approval.GetProperty("approvalBasis").GetString(),
                approvedAt = approval.GetProperty("approvedAt").GetString(),
                canonicalGoldPath = entry.CanonicalGoldPath,
            },
            isReplacementAuthority = false,

            // One Gold root, one hash, unmoved. The baseline needs no successor lineage because
            // nothing about the file it executed against changed.
            goldLineage = new
            {
                baselineExecutionGoldSha256 = executionHash,
                currentCanonicalGoldSha256 = entry.GoldSha256,
                canonicalGoldModified = false,
                claimSetEquivalent = true,
                provenanceRecordedOutsideCanonicalGold = true,
            },

            // The diff itself, so the confirmation can be re-checked without rerunning the audit.
            claims,

            notes = new[]
            {
                "Punctuation and text-layer differences between the rendered PDF and a claim's verbatimText are representation differences, not membership differences.",
                "The S0616 mismatch is caused by source-occurrence fragmentation, not by Gold membership; see eval/a99-closed-loop/representation/doc-0252-source-occurrence-boundary.v1.json.",
                "This audit was not blind to the approved total of 41; the original occurrence review was not either, and both exposures stay recorded rather than being described as independent.",
            },
        });
    }

    // ---- how the source represents each approved heading --------------------------------------

    [Fact]
    public void Every_approved_heading_is_classified_against_the_visual_line_the_page_actually_has()
    {
        // PDF_SOURCE_OCCURRENCE_BOUNDARY_MISMATCH. The question is not whether the 41 headings are
        // right - that is settled - but whether the occurrence universe can express them at all.
        // Two things have to disagree for that to be measurable, so the boundary is taken from the
        // page's own geometry and the grouper's answer is compared against it. Nothing a model
        // produced is consulted anywhere in this test.
        var gold = CanonicalGoldRegistry.ResolveOccurrenceGold("DOC-0252");
        Assert.Equal(AuthoritativeTotal, gold.Headings.Count);

        var occurrences = Blocks();
        var aliases = PdfSourceOccurrenceBoundary.Aliases(occurrences.Count);

        // The offsets inside the classifier read an occurrence as its lines joined by a single
        // space. If that stopped being how the projection composes, every span in this census would
        // be off by a little and still look plausible, so it is checked rather than assumed.
        Assert.All(occurrences, occurrence => Assert.Equal(
            string.Join(PdfSourceOccurrenceBoundary.Separator,
                occurrence.Lines.Select(line => line.Projection.VerbatimText)),
            occurrence.VerbatimText));

        // Here the alias is authoritative, so it resolves the claims directly - and doubles as the
        // check that the text locator agrees with it before that locator is trusted on a universe
        // where aliases have been renumbered.
        var byAlias = gold.Headings.Select(heading => Array.IndexOf(aliases, heading.SourceAlias)).ToArray();
        Assert.DoesNotContain(-1, byAlias);
        var located = PdfSourceOccurrenceBoundary.Locate(
            occurrences, gold.Headings,
            gold.Headings.Select((heading, ordinal) =>
                heading.VerbatimText ?? occurrences[byAlias[ordinal]].VerbatimText).ToArray(),
            out var unresolved);
        Assert.Empty(unresolved);
        Assert.Equal(byAlias, located);

        var rows = PdfSourceOccurrenceBoundary.Classify(occurrences, gold.Headings, byAlias);
        var visualLines = PdfSourceOccurrenceBoundary.VisualLines(occurrences);

        var census = rows.GroupBy(row => row.Boundary)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // ---- what the measured baseline lost, against what the source could express -------------
        // The 13 persistent superset losses are not assumed to share a cause. Each is joined to its
        // own measured boundary, and a loss whose boundary is clean would be a finding in itself.
        var forensic = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TestRepository.Root(),
                "eval/a99-closed-loop/occurrence-baseline-v1/causal-forensic.v1.json"
                    .Replace('/', Path.DirectorySeparatorChar))));
        var boundaryOf = rows.ToDictionary(row => row.Claim, row => row.Boundary, StringComparer.Ordinal);
        var losses = forensic.RootElement.GetProperty("lostOccurrences").EnumerateArray()
            .Where(loss => loss.GetProperty("persistent").GetBoolean())
            .Select(loss => new
            {
                claim = loss.GetProperty("claim").GetString()!,
                firstLoss = loss.GetProperty("firstLoss").GetString()!,
            })
            .Select(loss => new
            {
                loss.claim,
                loss.firstLoss,
                boundary = boundaryOf.GetValueOrDefault(loss.claim, "CLAIM_NOT_IN_AUDIT"),
            })
            .OrderBy(loss => loss.claim, StringComparer.Ordinal)
            .ToArray();

        // ---- punctuation ----------------------------------------------------------------------
        var punctuation = PdfSourceOccurrenceBoundary.PunctuationCensus(
            occurrences, rows.Select(row => row.OccurrenceIndex).Distinct().ToArray());
        var dots = punctuation.Single(item => item.Mark == ".");

        FreezeArtifact.AssertJson("eval/a99-closed-loop/representation",
            "doc-0252-source-occurrence-boundary.v1.json", new
        {
            artifactKind = "a99_source_occurrence_boundary_audit",
            schemaVersion = "a99-source-occurrence-boundary-audit-v1",
            finding = "PDF_SOURCE_OCCURRENCE_BOUNDARY_MISMATCH",
            authorityId = "DOC-0252",
            sourceSha256 = AuthoritativeSourceSha,
            providerCalls = 0,
            modelCalls = 0,

            defectOwner = "HARNESS_SOURCE_OCCURRENCE_LAYER",
            whatIsNotTheDefect = new[]
            {
                "GOLD_MEMBERSHIP: reconfirmed at 41, add 0, remove 0 - see pdf-gold-doc0252/gold-source-reconfirmation.v1.json.",
                "TEXT_EXTRACTION: the glyphs are present. Every punctuation mark counted below was extracted; it was placed in an occurrence of its own.",
                "BINDER: binding a heading to exactly one occurrence is the contract that keeps coordinates with the harness. Relaxing it would hide this defect rather than fix it.",
            },

            textLayerVersusGrouping = new
            {
                verdict = "TEXT_EXTRACTION_PRESENT_AND_OCCURRENCE_GROUPING_WRONG",
                note = "An earlier report from this session said the trailing period was absent from the PDF text layer. That was wrong and is corrected here: the period is extracted, and then separated from the line it belongs to.",
                mechanism = "PdfLineExtraction buckets glyphs by vertical midpoint with a tolerance scaled to glyph height. A period and a capital sharing one baseline differ in midpoint by more than that tolerance, so the period becomes its own line, its own block, and its own occurrence.",
                evidence = "S0616 spans [425.51, 435.02] and S0617, the period belonging to its list number, spans [427.41, 429.07] - strictly inside it, therefore the same visual line by geometry.",
            },

            modes = new object[]
            {
                new
                {
                    mode = "OVER_GROUPING",
                    statement = "One occurrence carries a heading together with the text that follows it, so no proposal can be both semantically right and exactly bindable.",
                    claims = census.GetValueOrDefault("OVER_GROUPED"),
                    persistentSupersetLossesInBaseline = 13,
                    note = "The 13 come from occurrence-baseline-v1/causal-forensic.v1.json and are not re-derived here; each claim below carries its own measured boundary rather than inheriting a shared cause.",
                },
                new
                {
                    mode = "FRAGMENTATION",
                    statement = "One visual line is split across several occurrences, so the heading it carries exists in no occurrence at all.",
                    claims = census.GetValueOrDefault("FRAGMENTED"),
                    worked = new
                    {
                        claim = "S0616",
                        sourceHeading = "2. A Survey Based Approach to Adjustment for Quality Differences in Services in International Price Comparisons",
                        modelSemanticDecision = "CORRECT",
                        sourceRepresentation = "INSUFFICIENT_FOR_EXACT_BINDING",
                        finalScore = "FN under the current occurrence authority",
                        note = "Diagnosis, not a score. The measured baseline stands as measured; what changes is who owns the loss.",
                    },
                },
            },

            boundaryAudit = new
            {
                total = rows.Count,
                census,
                rows = rows.Select(PdfSourceOccurrenceBoundary.Serialize).ToArray(),
            },

            persistentBaselineLosses = new
            {
                source = "eval/a99-closed-loop/occurrence-baseline-v1/causal-forensic.v1.json",
                note = "Measured scores are reproduced unchanged and joined to this audit's boundary classification. Diagnosis is added; nothing is rescored.",
                byFirstLossAndBoundary = losses
                    .GroupBy(loss => $"{loss.firstLoss} + {loss.boundary}")
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                losses,
            },

            punctuationCensus = new
            {
                sourceOccurrences = occurrences.Count,
                visualLines = visualLines.Count,
                visualLinesSplitAcrossOccurrences = visualLines.Count(line => line.SplitAcrossOccurrences),
                dotOnlyOccurrences = dots.Occurrences,
                dotImmediatelyBeforeAGoldAlias = dots.ImmediatelyBeforeAGoldOccurrence,
                byMark = punctuation.Select(item => new
                {
                    mark = item.Mark,
                    occurrences = item.Occurrences,
                    sharingAVisualLineWithText = item.SharingAVisualLineWithText,
                    immediatelyBeforeAGoldOccurrence = item.ImmediatelyBeforeAGoldOccurrence,
                }).ToArray(),
            },

            causalInterpretation = new
            {
                family = "SOURCE_REPRESENTATION_INTERVENTION_JUSTIFIED",
                expandedInto = new[] { "OVER_GROUPING", "FRAGMENTATION" },
                chain = new[]
                {
                    "PDF glyphs: present and correct.",
                    "Line reconstruction: splits a baseline by glyph midpoint.",
                    "Occurrence grouping: under-splits headings into body, over-splits lines into punctuation.",
                    "Model: largely locates the right region.",
                    "Binder and scorer: apply the current contract correctly.",
                    "Result: FN and FP owned by the source layer, not by the model.",
                },
                interventionTarget = "BOUNDARY_RECONSTRUCTION_FROM_PDF_TEXT_ATOMS",
                notAnIntervention = new[]
                {
                    "Reducing the grouper's maximum block size. It would not fix fragmentation and would deepen it.",
                    "Letting the binder span or subdivide occurrences freely. That moves coordinate authority to the model.",
                },
                status = "EVIDENCE_FROZEN_NO_CODE_CHANGE",
            },
        });

        // The two modes are both real, and the census is not silently empty on either side.
        Assert.Equal(AuthoritativeTotal, rows.Count);
        Assert.Contains(rows, row => row.Boundary == "FRAGMENTED");
        Assert.Contains(rows, row => row.Boundary == "OVER_GROUPED");
        Assert.Equal(166, dots.Occurrences);
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
            UploadedFile.FromLocalPath(Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar))),
            new PipelineOptions { DisableLlm = true });
        return (document.SourceCatalog, SemanticSourceAliasCatalog.FromCatalog(document.SourceCatalog));
    }

    private static IReadOnlyList<PdfSemanticBlock> Blocks()
    {
        var path = Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        IReadOnlyList<PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            lines = PdfLineExtraction.ExtractLines(document);
        }

        return PdfSemanticBlockGrouper.Build(PdfLineBlockFilter.Analyze(lines), includeRiskLines: true);
    }

}
