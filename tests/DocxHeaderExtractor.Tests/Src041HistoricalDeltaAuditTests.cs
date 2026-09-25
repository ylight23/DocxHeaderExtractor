using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC041_HISTORICAL_297_DELTA_AUDIT_V1 - a provenance / source-evidence audit, not a tuning step.
/// <para>
/// Question (user, 2026-09-25): what source-only evidence explains why the historical review reached 297 while
/// the current occurrence-level review reached 280? Prohibited: V1.1 predictions, the held-out score (ba93900)
/// as a search list, any other model output, targeting the delta, inferring missing occurrences from count
/// arithmetic, and altering the current Gold silently. Evidence admitted: the historical authority's own
/// records, and historical human/structural references that carry occurrence identities in this PDF.
/// </para>
/// <para>
/// Output: A recoverable historical heading occurrences, B historical over-count patterns, C policy-definition
/// differences, D the unrecoverable count-only difference.
/// </para>
/// </summary>
public sealed partial class Src041HistoricalDeltaAuditTests
{
    private const string GoldPath = "eval/a99-closed-loop/gold/SRC-041.gold.json";
    private const string PreviousFreeze = "eval/a99-closed-loop/canonical-semantic-gold-vnext/semantic/SRC-041.semantic-freeze.v1.json";
    private const string Bridge = "keys/occurrence-bridge/041_IBRD_Financial_Statements_June_2025.occurrence-bridge.json";

    [Fact]
    public void Freeze_the_historical_delta_audit()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        var claims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(c => (
            Page: c.GetProperty("boundParts")[0].GetProperty("page").GetInt32(),
            Text: c.GetProperty("projectedText").GetString()!,
            FirstLineText: c.GetProperty("boundParts")[0].GetProperty("text").GetString()!,
            Pattern: c.GetProperty("pattern").GetString()!)).ToArray();
        using var previous = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(PreviousFreeze)));
        var record = previous.RootElement.GetProperty("authorityRecord");
        var reaudit = record.GetProperty("vnextReauditResult");
        using var bridge = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Bridge)));
        Assert.False(bridge.RootElement.GetProperty("usesModel").GetBoolean());
        Assert.False(bridge.RootElement.GetProperty("usesPipelineOutput").GetBoolean());

        // A.1 - the five occurrences the historical re-audit added (292 -> 297), named in its own record.
        var added = reaudit.GetProperty("addedTrueHeadingOccurrences").EnumerateArray()
            .Select(a => (Text: a.GetProperty("text").GetString()!, Count: a.GetProperty("countAdded").GetInt32())).ToArray();
        var addedCoverage = added.Select(a => new
        {
            historicalText = a.Text,
            historicalCount = a.Count,
            currentGoldOccurrences = claims.Count(c => Squash(c.Text) == Squash(a.Text)),
        }).ToArray();

        // A.2 - a historical reviewed structural reference with line identities in this PDF (no model, no
        // pipeline output). An occurrence is the same when page and first-line text agree (or the reference
        // holds the first line of a longer title).
        var bridgeRows = bridge.RootElement.GetProperty("occurrences").EnumerateArray().Select(o => (
            Page: o.GetProperty("page").GetInt32(),
            Text: o.GetProperty("goldText").GetString()!,
            Method: o.GetProperty("reviewMethod").GetString()!)).ToArray();
        var bridgeMatch = bridgeRows.Select(o => (Row: o, Match: claims.FirstOrDefault(c => c.Page == o.Page &&
            (Squash(c.Text) == Squash(o.Text) || Squash(c.FirstLineText) == Squash(o.Text))))).ToArray();
        var bridgeAbsent = bridgeMatch.Where(m => m.Match.Text is null).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/source-review-v1", "SRC-041.historical-delta-audit.v1.json", new
        {
            artifactKind = "a99_gold_historical_delta_audit",
            study = "SRC041_HISTORICAL_297_DELTA_AUDIT_V1",
            kind = "provenance / source-evidence audit; not a tuning step",
            modelProviderVlmCalls = 0,
            inputAuthority = new
            {
                source = new { path = previous.RootElement.GetProperty("sourcePath").GetString(), sha256 = previous.RootElement.GetProperty("sourceSha256").GetString() },
                previousTotal = record.GetProperty("semanticHeadingTotal").GetInt32(),
                currentOccurrenceGold = claims.Length,
                currentGold = new { path = GoldPath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(GoldPath)), commit = "3dca86f" },
            },
            prohibited = new[]
            {
                "V1.1 predictions", "the held-out score ba93900 as a candidate search list", "any other model output (e.g. scaleup-real-harness-v1/SRC-041)",
                "targeting the delta", "inferring missing occurrences from count arithmetic", "altering the current Gold silently",
            },
            evidenceAdmitted = new[]
            {
                new { path = PreviousFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(PreviousFreeze)), what = "the historical authority's own record (approvedAt 2026-09-12)" },
                new { path = Bridge, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Bridge)), what = "a reviewed structural reference with PDF line identities, 2026-08-26, usesModel false, usesPipelineOutput false; partial (not the 297 review)" },
            },
            evidenceSearchedAndAbsent = new[]
            {
                "no occurrence list, span or per-heading note for the historical 292 proposal: the freeze says 'no occurrence list or span was synthesized from the total'",
                "research-r2 annotation: DOC-0126 (this PDF) has a source packet but no annotator-a, annotator-b or adjudication file",
                "review/holdout-manifest DOC-0221: a DOCX packet READY_FOR_HUMAN_REVIEW, never reviewed",
                "todo10_8/outline_json: a single placeholder entry, no headings",
            },
            A_recoverableHistoricalHeadingOccurrences = new
            {
                reauditAdditions = new
                {
                    historical = $"{reaudit.GetProperty("previousProposal").GetInt32()} -> {reaudit.GetProperty("result").GetInt32()} (+{reaudit.GetProperty("delta").GetInt32()})",
                    coverage = addedCoverage,
                    note = reaudit.GetProperty("notes").GetString(),
                },
                reviewedStructuralReference = new
                {
                    occurrences = bridgeRows.Length,
                    byReviewMethod = bridgeRows.GroupBy(r => r.Method).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                    inCurrentGold = bridgeRows.Length - bridgeAbsent.Length,
                    absentFromCurrentGold = bridgeAbsent.Select(m => new { m.Row.Page, m.Row.Text }).ToArray(),
                    note = "a title the reference records by its first line only is the same occurrence as the current multi-line claim",
                },
                historicalHeadingOccurrencesAbsentFromCurrentGold = bridgeAbsent.Length + addedCoverage.Count(a => a.currentGoldOccurrences < a.historicalCount),
            },
            B_historicalOverCountPatterns = new
            {
                established = 0,
                reason = "the 292 base carries no occurrence identities, so no historical occurrence can be shown to have been counted and later excluded",
            },
            C_policyDefinitionDifferences = new
            {
                note = "decisions fixed after the 2026-09-12 review; an inventory of what changed in the definition, NOT a decomposition of the delta",
                later = new[]
                {
                    new { what = "FINANCIAL_PROCUREMENT_HEADING_POLICY_V1 (caption rule, row/column labels, field labels, running headers)", decidedAt = "2026-09-24" },
                    new { what = "financial-occurrence-distinctions addendum (statement / note / caption / column / row)", decidedAt = "2026-09-25" },
                    new { what = "OCCURRENCE_CLASSIFICATION_PRINCIPLES_V1 (classify occurrences, not strings; chart-internal labels; contents openers vs entries)", decidedAt = "2026-09-25" },
                    new { what = "SRC-041 A3 / A4 / A5 occurrence decisions", decidedAt = "2026-09-25" },
                },
                unchanged = "the historical re-audit already treated repeated/continuation display titles as distinct heading occurrences - the current A2 decision agrees",
            },
            D_unrecoverableCountOnlyDifference = new
            {
                HISTORICAL_AUTHORITY_DELTA = claims.Length - record.GetProperty("semanticHeadingTotal").GetInt32(),
                EXACT_CAUSE = "NOT_RECOVERABLE",
                reason = "the historical total has no occurrence list; every historical occurrence that can be identified is in the current Gold",
            },
            conclusion = new
            {
                CURRENT_GOLD_280 = "UNCHANGED",
                goldRevision = "none: no source-level evidence of an occurrence the current Gold misses",
                heldOutScore = "ba93900 stays the held-out score against the Gold frozen at the reveal",
            },
        });
    }

    private static string Squash(string value) => Spaces().Replace(value, "").Replace('\'', '’').Replace(",", "").ToLowerInvariant();

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
