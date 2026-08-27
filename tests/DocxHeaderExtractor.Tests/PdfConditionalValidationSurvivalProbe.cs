using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A2: conditional validation survival. Reads a frozen live-canary <c>pdf-hierarchy-facts</c> artifact
/// (produced once, offline from here on - see <see cref="PdfLiveCanaryReplayProbe"/> for why the model
/// is never re-run) and asks, of the reviewed occurrences A1 already found selected@160 for this
/// document, how many have a validated fact in the frozen artifact.
/// <para>
/// The denominator is A1's <c>selected</c> set, not the full reviewed population: an occurrence whose
/// candidate never reached the semantic lane (rank &gt; 160, or no covering candidate at all) was never
/// exposed to the validator and must not be counted against it. Counting it would blame semantic
/// validation for a candidate-construction or ranking loss that happened earlier in the pipeline.
/// </para>
/// <para>
/// The join is occurrence identity, not text: A1's <see cref="PdfExtractorQualityBenchmarkProbe.Classify"/>
/// already resolved each occurrence to its required source line indexes (from the reviewed bridge where
/// one exists, from canonical-text join otherwise) and this probe converts those to the same
/// <c>lineId</c> string (<see cref="PdfCandidateProvenance.LineId"/>) the artifact's own
/// <c>items[].lineIds</c> are written in. A validated fact counts for an occurrence only if its
/// <c>lineIds</c> is a superset of the occurrence's required line ids - never by matching heading text.
/// </para>
/// <para>
/// A2b first-loss trace, model-free. Three of <c>PdfProposalValidator.IsEligibleHeading</c>'s gates -
/// structural scope, domain role, evidence-origin trust - depend only on candidate construction, never
/// on any analyst decision, and <c>PdfLayoutEvidenceOutline</c> dispatches the full <c>selected</c> set
/// to the analyst before that gate is ever consulted (<c>PdfProposalValidator.Validate</c> runs on the
/// analyst's returned decisions, after the fact). So a candidate a deterministic gate rejects was still
/// sent to the model - its answer was simply never going to matter, because <c>IsEligibleHeading</c> is
/// an unconditional <c>&amp;&amp;</c> chain. This lets a NOT VALIDATED occurrence be classified without
/// a live decision: report it as deterministically excluded when a gate already rejects its covering
/// candidate, and leave it genuinely unresolved only when none do - that remainder is the one case where
/// the analyst's actual role/span would matter, and only <c>PdfStageCheckpoint</c> persists that.
/// </para>
/// </summary>
public sealed class PdfConditionalValidationSurvivalProbe
{
    [Fact]
    public void Report()
    {
        var stem = Environment.GetEnvironmentVariable("BENCH_A2_DOC");
        var artifactPath = Environment.GetEnvironmentVariable("BENCH_A2_ARTIFACT");
        var output = Environment.GetEnvironmentVariable("BENCH_A2_REPORT");
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(artifactPath) || string.IsNullOrWhiteSpace(output))
            return;

        var corpus = Path.Combine(PdfExtractorQualityBenchmarkProbe.RepositoryRoot(), "todo10_8", "heading_corpus_95_word");
        var population = PdfExtractorQualityBenchmarkProbe.Populations(corpus)
            .FirstOrDefault(p => p.Stem == stem);
        if (population.Occurrences is null || population.Occurrences.Count == 0)
        {
            File.WriteAllText(output, $"doc={stem}: no reviewed population found (check BENCH_A2_DOC).");
            return;
        }

        var docxPath = Path.Combine(corpus, population.Relative);
        var classifications = PdfExtractorQualityBenchmarkProbe.Classify(docxPath, population.Occurrences);
        var reviewed = classifications.Count;
        var full = classifications.Count(c => c.Status == "full");
        var selected = classifications.Where(c => c.Selected).ToList();

        // Independent ranking pass, offline and model-free, so a NOT VALIDATED occurrence can be shown
        // beside whichever candidate on its own page WAS validated instead, by rank.
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);
        var contexts = PdfCandidateContextBuilder.Build(snapshot.CandidateBlocks, snapshot.Annotations);
        var ranked = PdfCandidateRanker.Rank(snapshot.CandidateBlocks, contexts);
        var rankOf = ranked.Select((item, index) => (item.SourceId, Rank: index + 1))
            .ToDictionary(x => x.SourceId, x => x.Rank, StringComparer.Ordinal);

        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var row = document.RootElement.GetProperty("rows")[0];
        var laneStatus = row.TryGetProperty("semanticLaneStatus", out var s) ? s.GetString() : null;
        var itemElements = row.GetProperty("items").EnumerateArray().ToList();
        var items = itemElements
            .Select(item => item.GetProperty("lineIds").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal))
            .ToList();
        var validatedByPage = itemElements
            .SelectMany(item => item.GetProperty("lineIds").EnumerateArray().Select(lineId => (
                Page: lineId.GetString()!.Split('|')[0],
                LineText: lineId.GetString()!.Split('|')[4],
                SourceFactId: item.GetProperty("sourceFactId").GetString() ?? "")))
            .ToList();

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"doc={stem} artifact={Path.GetFileName(artifactPath)} semanticLaneStatus={laneStatus ?? "(absent)"}");
        if (laneStatus is not "complete")
            Line("WARNING: semanticLaneStatus is not \"complete\" - validated counts below may be partial, not a true zero-vs-nonzero result.");
        Line("");
        Line($"reviewed={reviewed} full={full} selected@160={selected.Count} validatedItemsInArtifact={items.Count}");
        Line("");

        var validated = 0;
        var evaluatorJoinFailures = 0;
        var deterministicallyExcluded = 0;
        var genuinelyUnresolved = 0;
        foreach (var occ in selected)
        {
            var hit = items.Any(lineIds => occ.RequiredLineIds.All(lineIds.Contains));
            if (hit) { validated++; continue; }

            var page = occ.RequiredLineIds.Count > 0 ? occ.RequiredLineIds[0].Split('|')[0] : "?";

            // Category 5 check (A2b): rule out a lineId-geometry join artifact before treating this as
            // model behaviour. Page AND text must both match - text alone is not occurrence identity,
            // since a title repeated elsewhere (a TOC entry, a window slice) has the same canonical text
            // on a different physical page and is a different occurrence, not a join defect.
            var occurrenceText = PdfTextUtilities.CanonicalForMatch(occ.Occurrence.Label);
            var textMatch = validatedByPage.FirstOrDefault(v =>
                v.Page == page && PdfTextUtilities.CanonicalForMatch(v.LineText) == occurrenceText);
            var joinFailure = textMatch.LineText is not null;
            if (joinFailure) evaluatorJoinFailures++;

            var samePageTextMatchElsewhere = validatedByPage
                .Where(v => v.Page != page && PdfTextUtilities.CanonicalForMatch(v.LineText) == occurrenceText)
                .ToList();
            var samePage = validatedByPage.Where(v => v.Page == page).ToList();

            // Deterministic eligibility (model-free): three of IsEligibleHeading's gates depend only on
            // candidate construction. A candidate that fails one of these was still dispatched to the
            // analyst (selection has no domain filter), but its verdict could never have mattered.
            string? deterministicReason = null;
            if (occ.CoveringCandidateId is { } id && contexts.TryGetValue(id, out var ctx))
            {
                var structuralScopeRejected = ctx.Source.StructuralScope is
                    "table" or "running_page_artifact" or "table_of_contents" or "code_or_grammar" or "reference_list" or "index_terms";
                var domainRejected = DocumentDomainPolicy.IsExcludedFromOutline(ctx.Source.DomainRole);
                var untrustedOrigins = ctx.Source.EvidenceDetails
                    .Where(e => e.Origin is not ("layout_parser" or "marker_parser" or "scope_detector"))
                    .Select(e => e.Origin).Distinct().ToArray();
                if (structuralScopeRejected) deterministicReason = $"structuralScope:{ctx.Source.StructuralScope}";
                else if (domainRejected) deterministicReason = $"domainRole:{ctx.Source.DomainRole}";
                else if (untrustedOrigins.Length > 0) deterministicReason = $"untrustedEvidenceOrigins:{string.Join(",", untrustedOrigins)}";
            }

            Line($"NOT VALIDATED: {Trim(occ.Occurrence.Label)}");
            Line($"    candidate={occ.CoveringCandidateId} rank={occ.CoveringRank} page={page}");
            Line($"    requiredLineIds: {string.Join(" | ", occ.RequiredLineIds)}");
            if (joinFailure)
                Line($"    CATEGORY=VALIDATED_BUT_EVALUATOR_JOIN_FAILED (same page {page}, text matches validated item sourceFactId={textMatch.SourceFactId} on a different lineId)");
            else if (deterministicReason is not null)
            {
                deterministicallyExcluded++;
                Line($"    CATEGORY=DETERMINISTIC_SCOPE_EXCLUDED ({deterministicReason}) - excluded by PdfProposalValidator.IsEligibleHeading");
                Line("      regardless of analyst decision; candidate was still sent to the analyst (no pre-dispatch domain filter),");
                Line("      the verdict just could never have mattered. No checkpoint needed to explain this occurrence.");
            }
            else
            {
                genuinelyUnresolved++;
                Line("    CATEGORY=unresolved (all deterministic gates pass; whether the analyst proposed the right role/span");
                Line("      for this occurrence is not decidable without checkpoint data, which this run did not capture)");
            }
            if (samePageTextMatchElsewhere.Count > 0)
                foreach (var v in samePageTextMatchElsewhere)
                    Line($"    NOTE: same canonical text validated on a DIFFERENT page ({v.Page}, sourceFactId={v.SourceFactId}) - a duplicate title elsewhere (e.g. TOC/window slice), not this occurrence.");
            if (samePage.Count > 0)
            {
                Line($"    same-page validated instead:");
                foreach (var v in samePage.DistinctBy(x => x.SourceFactId))
                {
                    var rank = rankOf.TryGetValue(v.SourceFactId, out var r) ? r.ToString() : "unranked";
                    Line($"      {v.SourceFactId} rank={rank}: {Trim(v.LineText)}");
                }
            }
        }

        Line("");
        Line($"conditional validation survival = validated / selected@160 = {validated}/{selected.Count} " +
             $"= {(selected.Count == 0 ? 0.0 : validated / (double)selected.Count):P1}");
        Line($"end-to-validation recall (composed, secondary) = validated / reviewed = {validated}/{reviewed} " +
             $"= {(reviewed == 0 ? 0.0 : validated / (double)reviewed):P1}");
        Line("");
        Line("Of the not-validated occurrences:");
        Line($"  evaluator-join failure (category 5, ruled IN) = {evaluatorJoinFailures}");
        Line($"  deterministically scope/domain-excluded, model-output-independent = {deterministicallyExcluded}");
        Line($"  genuinely unresolved (needs a live analyst decision to classify) = {genuinelyUnresolved}");
        Line("");
        Line("Denominator is selected@160, not reviewed: an occurrence whose candidate never reached the");
        Line("semantic lane (no covering candidate, or covering candidate ranked > 160) is excluded, not");
        Line("counted as a semantic-validation loss. A deterministically-excluded occurrence WAS dispatched");
        Line("to the analyst (selection has no domain filter - see PdfLayoutEvidenceOutline.cs), so it is not");
        Line("a candidate-construction or selection loss either; PdfProposalValidator.Validate discards it");
        Line("after the fact, regardless of what the analyst answered, so \"semantic validation quality\" was");
        Line("never actually exercised on it.");

        File.WriteAllText(output, report.ToString());
    }

    private static string Trim(string value)
    {
        var single = value.Length <= 90 ? value : value[..90] + "...";
        return single.Replace('\n', ' ').Replace('\r', ' ');
    }
}
