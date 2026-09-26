using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// ONTOLOGY_FUNCTIONALITY_REVIEW_V1: a purely offline scan of the already-approved Gold corpus for
/// occurrences that show the same dual-function topology ITEM-CCE2C592 ("Agenda") showed - a short,
/// generic, artifact-naming-shaped claim immediately followed by a run of more granular claims that
/// plausibly nest beneath it. Zero model, provider, or VLM calls; nothing here re-reviews or changes
/// any approved Gold.
/// <para>
/// The corpus-coverage constraint this file surfaces first, before any finding: only the authorities
/// whose Gold has been itemised carry a materialized claims list in the live registry (the set is
/// derived from the registry, and grows as Gold is itemised in eval/a99-closed-loop/gold); the rest
/// record only an approved heading total. Materializing a claims list is necessary but not
/// sufficient for THIS heuristic: it needs a per-claim identity, text and role. Legacy-schema claims
/// (<c>sourceAlias</c> + <c>exactText</c>, no <c>identity</c>) scan as zero rows, and structured
/// claims itemised without roles parse but give the heuristic nothing to test. Only DOC-0252 is
/// actually reviewable by this heuristic today; every later itemisation is a genuine migration that
/// does not, by itself, extend what this specific heuristic can see.
/// <para>
/// This is a narrower claim than "only 2 documents ever had occurrence-level Gold materialized":
/// <c>strict-gold-v3</c> (PROVENANCE_ONLY, not the canonical authority) independently materializes
/// exact-text occurrence data for 5 documents - DOC-0001, DOC-0205, DOC-0252, DOC-0256, DOC-0258, 153
/// occurrences total. strict-gold-v3 is an older snapshot format, so its per-document counts do not
/// always match today's canonical totals: DOC-0205's own strict-gold-v3 snapshot (71) still disagrees
/// with the canonical total (72, since the migration added the title as a 72nd entry sourced from the
/// original docx directly, not from strict-gold-v3), DOC-0258's (24) disagrees with its canonical 37
/// for the same kind of reason (its 13 additions were kept by the user, not read from strict-gold-v3),
/// and DOC-0256's (24) disagrees with its canonical 34 likewise - per
/// <see cref="CanonicalGoldConsolidationTests"/>'s own rule that semantic truth is never reduced to fit
/// an older occurrence artifact. DOC-0252's strict-gold-v3
/// count (27) also predates its own canonical total (42, after the R1-&gt;R2 and R2-&gt;R3 corrections
/// this whole lineage made) - but DOC-0252 IS canonical, through its own separate, current
/// structured-source-parts occurrence pipeline, not through strict-gold-v3.
/// </para>
/// <para>
/// This review can only examine the 42 approved headings this heuristic's own <see cref="Row"/>
/// extraction can parse out of 3956 approved headings in the corpus (about 1.1%) - it answers "is
/// Agenda a boundary case or a symptom, within the part of the corpus this heuristic can actually
/// see," not "is Agenda a boundary case or a symptom in the corpus."
/// </para>
/// </summary>
public sealed class OntologyFunctionalityReviewV1Tests
{
    private const string Root = "eval/a99-closed-loop/ontology-functionality-review-v1";
    private const int WordCountCeiling = 2;
    private const int MinNestedChildren = 2;

    [Fact]
    public void Corpus_coverage_matches_the_already_established_finding()
    {
        var registry = ReadRegistry();
        // 21 authorities when this finding was established; SRC-095 (a held-out RFC, 2026-09-26) made 22,
        // SRC-089 (a held-out decree, 2026-09-27) 23.
        Assert.Equal(23, registry.Length);
        var carriers = registry.Where(a => a.MaterializedSemanticClaims > 0).ToArray();
        // Every authority that carries a materialized claims list, derived from the registry rather
        // than listed here - the set grows as Gold is itemised. Carrying claims is not the same as
        // being reviewable by THIS heuristic: legacy-schema claims (sourceAlias + exactText, no
        // identity) scan as zero rows, and structured claims with no assigned role parse but give the
        // heuristic nothing to test. Only DOC-0252's role-bearing claims are reviewable today.
        Assert.Contains(carriers, a => a.AuthorityId == "DOC-0252");
        Assert.Equal(42, ScanAuthority("DOC-0252").RoleBearingClaims);
        Assert.All(carriers.Where(a => a.AuthorityId != "DOC-0252"),
            a => Assert.Equal(0, ScanAuthority(a.AuthorityId).RoleBearingClaims));
    }

    /// <summary>
    /// "2/21 claim-reviewable" is not "2/21 ever had occurrence-level Gold" - strict-gold-v3
    /// (PROVENANCE_ONLY, non-canonical) independently materializes 153 occurrences across 5
    /// documents. This test pins that number so the distinction in the class doc comment stays true.
    /// </summary>
    [Fact]
    public void Strict_gold_v3_provenance_only_data_is_a_distinct_wider_set_than_canonical_claims()
    {
        var strictGoldV3 = ReadStrictGoldV3Counts();
        Assert.Equal(new[] { "DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258" },
            strictGoldV3.Select(d => d.AuthorityId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(153, strictGoldV3.Sum(d => d.HeadingCount));

        var registry = ReadRegistry();
        var canonicalTotals = registry.ToDictionary(a => a.AuthorityId, a => a.SemanticHeadingTotal, StringComparer.Ordinal);
        var mismatched = strictGoldV3.Where(d => d.HeadingCount != canonicalTotals[d.AuthorityId]).ToArray();
        // strict-gold-v3 is a stale snapshot relative to canonical Gold's current totals (DOC-0252's
        // own count moved twice since, via the R1->R2 and R2->R3 corrections this whole lineage made)
        // - only DOC-0001 still happens to agree. This is exactly why CanonicalGoldConsolidationTests
        // never imports the mismatched ones: semantic truth is never reduced to fit an older artifact.
        Assert.Equal(new[] { "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258" },
            mismatched.Select(d => d.AuthorityId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Freeze_ontology_functionality_review()
    {
        var registry = ReadRegistry();
        var totalHeadings = registry.Sum(a => a.SemanticHeadingTotal);
        var carriers = registry.Where(a => a.MaterializedSemanticClaims > 0).ToArray();
        var materializedClaimsAcrossCarriers = carriers.Sum(a => a.MaterializedSemanticClaims);

        var scans = carriers.Select(a => (a.AuthorityId, Scan: ScanAuthority(a.AuthorityId)))
            .OrderBy(x => x.AuthorityId, StringComparer.Ordinal).ToArray();
        var doc0252 = scans.Single(x => x.AuthorityId == "DOC-0252").Scan;
        var otherCarriers = scans.Where(x => x.AuthorityId != "DOC-0252").Select(x => x.AuthorityId).ToArray();
        var reviewableClaims = scans.Sum(x => x.Scan.RoleBearingClaims);
        var allCandidates = scans.SelectMany(x => x.Scan.Candidates).ToArray();
        var allTopologyOnly = scans.SelectMany(x => x.Scan.TopologyOnlyMatches).ToArray();
        var strictGoldV3 = ReadStrictGoldV3Counts();
        var canonicalTotals = registry.ToDictionary(a => a.AuthorityId, a => a.SemanticHeadingTotal, StringComparer.Ordinal);

        FreezeArtifact.AssertJson(Root, "ontology-functionality-review.v1.json", new
        {
            artifactKind = "a99_ontology_functionality_review",
            schemaVersion = "a99-ontology-functionality-review-v1",
            task = "ONTOLOGY_FUNCTIONALITY_REVIEW_V1",
            status = "SCANNED_OFFLINE_ZERO_CALLS",
            modelCalls = 0,
            providerCalls = 0,
            vlmCalls = 0,

            semanticAuthority = new { sources = registry.Length, approvedHeadings = totalHeadings },
            materializedClaimCarriers = new
            {
                count = materializedClaimsAcrossCarriers,
                authorities = carriers.Select(a => a.AuthorityId).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                note = $"Registry-level fact: these {carriers.Length} authorities carry a non-empty semantic.claims list. This is NOT the same as 'reviewable by this heuristic' - see ontologyReviewableClaims below, which is strictly narrower.",
            },
            ontologyReviewableClaims = new
            {
                count = reviewableClaims,
                ofApprovedHeadings = totalHeadings,
                fraction = Math.Round((double)reviewableClaims / totalHeadings, 4),
                authorities = new[] { "DOC-0252" },
                note = $"'Reviewable claims' means: claims this review's Row extraction can actually parse an identity, text, and role from. Every other authority carrying claims ({string.Join(", ", otherCarriers)}) contributes none: legacy-schema claims (sourceAlias/exactText, no identity) scan as zero rows, and structured claims itemised without roles parse but give the heuristic nothing to test. Only DOC-0252's role-bearing claims are reviewable today.",
            },
            corpusCoverage = new
            {
                authorities = registry.Length,
                authoritiesCarryingCanonicalClaimLevelData = carriers.Length,
                claimCarryingAuthorities = carriers.Select(a => a.AuthorityId).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                heuristicScannableAuthorities = 1,
                approvedHeadingsInCorpus = totalHeadings,
                reviewableClaimsInCorpus = reviewableClaims,
                coverageOfApprovedHeadings = Math.Round((double)reviewableClaims / totalHeadings, 4),
                caveat = $"This review can only examine canonical claims from DOC-0252 - of the other {registry.Length - 1} authorities, {registry.Length - carriers.Length} record only an approved heading total and {string.Join(", ", otherCarriers)} carry claims this heuristic cannot review, so 'no dual-function candidate found there' cannot be claimed; it can only be said that no such candidate is visible with the data this heuristic can actually read.",
                distinctFromOccurrenceLevelGoldEverMaterialized = new
                {
                    claim = $"1/{registry.Length} authority (DOC-0252) is claim-reviewable by THIS heuristic; {carriers.Length}/{registry.Length} carry a materialized claims list at the registry level. Neither of those is the same fact as '5 authorities ever had occurrence-level Gold materialized' in strict-gold-v3 (see strictGoldV3ProvenanceOnlyData below).",
                    strictGoldV3ProvenanceOnlyData = strictGoldV3.Select(d => new
                    {
                        authorityId = d.AuthorityId,
                        strictGoldV3HeadingCount = d.HeadingCount,
                        canonicalApprovedTotal = canonicalTotals[d.AuthorityId],
                        matchesCanonicalTotal = d.HeadingCount == canonicalTotals[d.AuthorityId],
                    }).OrderBy(d => d.authorityId, StringComparer.Ordinal).ToArray(),
                    strictGoldV3TotalOccurrences = strictGoldV3.Sum(d => d.HeadingCount),
                    explanation = "eval/a99-closed-loop/strict-gold-v3/ (marked PROVENANCE_ONLY in the lineage inventory, not the canonical authority) independently materializes exact-text occurrence data for 5 documents totaling 153 occurrences - an older snapshot format whose per-document counts do not always match today's canonical totals. DOC-0205 was migrated into canonical Gold's occurrence path directly from its own approved provenance (71 prior-approved headings plus one human-approved title addition) rather than from strict-gold-v3, which is why its own strict-gold-v3 count (71) still disagrees with the canonical total (72). DOC-0258 was migrated the same way - 24 historical headings plus 13 kept by the user in DOC0258_GOLD37_DELTA_AUDIT_V1, reaching its approved 37, on a source regenerated from its PDF - so its strict-gold-v3 count (24) also still disagrees; DOC-0256 likewise (24 historical plus the title and nine organisations = 34, in structured PDF atoms). CanonicalGoldConsolidationTests never reduces semantic truth to fit an older occurrence artifact. DOC-0252's strict-gold-v3 count also does not match its current canonical total, but DOC-0252 IS canonical - through a separate, current structured-source-parts occurrence pipeline, unrelated to this older strict-gold-v3 snapshot. Only DOC-0001's strict-gold-v3 count still agrees with its canonical total. Either way, none of DOC-0205/DOC-0256/DOC-0258's strict-gold-v3 data was usable directly by this review's heuristic, because that data is not canonically authoritative (and the canonical data they migrated into is not this heuristic's reviewable shape either).",
                },
            },

            heuristicDefinition = new
            {
                candidateCriteria = "A claim is a DUAL_FUNCTION_CANDIDATE iff (a) its approved text is at most " + WordCountCeiling + " words (short and generic, not a self-describing structural heading with its own numbering or descriptive phrase), AND (b) it is immediately followed, in document order, by at least " + MinNestedChildren + " consecutive claims whose semanticRole differs from its own (a run of more-granular content that plausibly nests beneath it).",
                topologyOnlyCriteria = "A claim is a TOPOLOGY_ONLY_MATCH iff it satisfies (b) above but not (a) - it precedes a run of nested-looking children but its own text is a normal, self-describing structural heading (e.g. carries its own numbering or a full descriptive phrase), so the topology alone is not read as artifact-naming ambiguity.",
                excludedByDesign = "The top-level document title (position 0 in every document) trivially precedes the rest of the document's structure by definition - that is not evidence of dual-function ambiguity, it is what a document title always does. Only an EMBEDDED claim (not the document's own opening title) showing this pattern is reported as a candidate.",
                notModelInferred = "This heuristic is a deterministic function of already-approved Gold text and role fields - no model was asked to judge any claim's function.",
            },

            doc0252 = new { authorityId = "DOC-0252", claimsScanned = doc0252.ClaimsScanned, candidates = doc0252.Candidates, topologyOnlyMatches = doc0252.TopologyOnlyMatches },
            otherClaimCarriers = scans.Where(x => x.AuthorityId != "DOC-0252").Select(x => new
            {
                authorityId = x.AuthorityId,
                claimsScanned = x.Scan.ClaimsScanned,
                roleBearingClaims = x.Scan.RoleBearingClaims,
                candidates = x.Scan.Candidates.Length,
                note = x.Scan.ClaimsScanned == 0
                    ? "legacy alias schema (no identity field): scans as zero rows"
                    : "structured claims itemised without roles: rows parse, nothing to test",
            }).ToArray(),

            findings = new
            {
                totalCandidates = allCandidates.Length,
                candidateItemIds = allCandidates.Select(c => c.Identity).ToArray(),
                totalTopologyOnlyMatches = allTopologyOnly.Length,
                headline = allCandidates.Length == 1 && allCandidates[0].Identity == "L0519:S0:0-6"
                    ? "Across the 42 reviewable claims (about 1.1% of the approved corpus - all from DOC-0252, the only authority whose occurrence schema this heuristic can parse), exactly one DUAL_FUNCTION_CANDIDATE was found: ITEM-CCE2C592 ('Agenda', L0519:S0:0-6) - the same item already independently identified as ambiguous through model disagreement. ITEM-505430BB (the other DocumentTitle claim added by the R1->R2 correction) does NOT match: its immediate successor is another DocumentTitle claim ('Agenda' itself), not a run of nested content, so it reads as pure IDENTITY with no topology signal of its own substructure. One topology-only contrast case was found (L0556, 'Annex 2: List of Participants', an AppendixHeading followed by 4 LocalSubheading children) - its own text is fully self-describing, unlike 'Agenda', so it does not read as artifact-naming-ambiguous despite the matching topology."
                    : "See candidates and topologyOnlyMatches above.",
            },

            conclusion = new
            {
                detectedDualFunctionCandidates = allCandidates.Length,
                topologyOnlyContrast = allTopologyOnly.Count(m => m.Identity == "L0556:S0:0-29"),
                singleLabelOntologyProblem = "NOT_SUPPORTED_BY_REVIEWABLE_DATA",
                agendaBoundaryCase = "SUPPORTED_BY_REVIEWABLE_DATA",
                heuristicRecallEstablished = false,
                corpusGeneralization = "NOT_ESTABLISHED",
                reasoning = $"Among the {reviewableClaims} semantic claims with enough data to review (all DOC-0252 - {string.Join(", ", otherCarriers)} carry claims this heuristic cannot review), this pre-registered heuristic detected exactly one dual-function candidate: Agenda. It did not detect evidence that the single-label ontology is a systemic problem. Coverage is only {reviewableClaims}/{totalHeadings}, and the heuristic's sensitivity/recall has not been established - a claim can be a genuine dual-function occurrence and still be missed if its label is longer than 2 words, its children carry several different semanticRoles instead of one homogeneous run, its nested content does not sit immediately after it, its artifact boundary is expressed through layout rather than a run of consecutive claims, its wording is not 'generic' even though it is both identity- and structure-bearing, or its authority's claims simply lack the identity/role fields this heuristic reads. So the remaining corpus cannot be inferred to be free of other boundary cases from this result.",
                annexContrastCaveat = "Annex 2: List of Participants is a useful contrast - it shows topology (a heading opening several nested children) is not by itself sufficient to imply ambiguity. But its exclusion (by the 'generic text' condition) only demonstrates this heuristic's current behavior, not a proven fact that Annex 2 is single-function - a different, more sensitive heuristic or a human review could still find it ambiguous.",
            },

            productionOrSchemaChanged = false,
            goldChanged = false,
            crossDocumentGeneralization = false,
            generalizationEstablished = false,
            scope = $"DOC-0252 only, the one authority whose claims this heuristic can review. {string.Join(", ", otherCarriers)} carry claims it cannot review. Not evidence about the remaining {registry.Length - carriers.Length} authorities' unmaterialized headings, and not evidence about this heuristic's sensitivity on any data it has not been tested against.",
        });
    }

    // ---------------------------------------------------------------------------------------

    private static ScanResult ScanAuthority(string authorityId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            TestRepository.Path($"eval/a99-closed-loop/gold-current/documents/{authorityId}.gold.v1.json")));
        var occurrenceEl = doc.RootElement.GetProperty("occurrence");
        var claims = occurrenceEl.TryGetProperty("claims", out var explicitClaims)
            ? explicitClaims.EnumerateArray().ToArray()
            : [];

        var rows = claims.Select(c => new Row(
            c.TryGetProperty("identity", out var id) ? id.GetString() : null,
            c.TryGetProperty("sourceAlias", out var alias) ? alias.GetString() : null,
            c.TryGetProperty("semanticRole", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() : null,
            c.TryGetProperty("approvedWording", out var wording) && wording.ValueKind == JsonValueKind.String ? wording.GetString()
                : c.TryGetProperty("verbatimText", out var verbatim) && verbatim.ValueKind == JsonValueKind.String ? verbatim.GetString()
                : null
        )).Where(r => r.Identity is not null).ToArray();

        var candidates = new List<CandidateEntry>();
        var topologyOnly = new List<CandidateEntry>();

        for (var i = 1; i < rows.Length; i++) // start at 1: exclude the document's own opening title (position 0)
        {
            var claim = rows[i];
            if (claim.Text is null || claim.Role is null) continue;
            if (i + 1 >= rows.Length) continue; // last claim in the document: nothing follows it

            // "Immediate children" = the homogeneous run of consecutive claims right after this one
            // that all share ONE role different from the claim's own - not "everything until the
            // same role recurs", which over-counts across unrelated later sections.
            var childRole = rows[i + 1].Role;
            if (childRole is null || childRole == claim.Role) continue;
            var childCount = 0;
            for (var j = i + 1; j < rows.Length && rows[j].Role == childRole; j++) childCount++;
            if (childCount < MinNestedChildren) continue;

            var wordCount = claim.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var entry = new CandidateEntry(claim.Identity!, claim.Role, claim.Text, wordCount, childCount, [childRole]);

            if (wordCount <= WordCountCeiling) candidates.Add(entry);
            else topologyOnly.Add(entry);
        }

        return new ScanResult(rows.Length, rows.Count(row => row.Role is not null), candidates.ToArray(), topologyOnly.ToArray());
    }

    private static StrictGoldV3Entry[] ReadStrictGoldV3Counts()
    {
        var ids = new[] { "DOC-0001", "DOC-0205", "DOC-0252", "DOC-0256", "DOC-0258" };
        return ids.Select(id =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(
                TestRepository.Path($"eval/a99-closed-loop/strict-gold-v3/{id}.strict-gold-v3.json")));
            var headings = doc.RootElement.GetProperty("headings");
            return new StrictGoldV3Entry(id, headings.GetArrayLength());
        }).ToArray();
    }

    private static Authority[] ReadRegistry()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            TestRepository.Path("eval/a99-closed-loop/gold-current/registry.v1.json")));
        return doc.RootElement.GetProperty("authorities").EnumerateArray()
            .Select(a => new Authority(
                a.GetProperty("authorityId").GetString()!,
                a.GetProperty("semanticHeadingTotal").GetInt32(),
                a.GetProperty("materializedSemanticClaims").GetInt32()))
            .ToArray();
    }

    private sealed record Authority(string AuthorityId, int SemanticHeadingTotal, int MaterializedSemanticClaims);
    private sealed record StrictGoldV3Entry(string AuthorityId, int HeadingCount);
    private sealed record Row(string? Identity, string? Alias, string? Role, string? Text);
    private sealed record CandidateEntry(string Identity, string Role, string Text, int WordCount, int NestedChildCount, string[] NestedChildRoles);
    private sealed record ScanResult(int ClaimsScanned, int RoleBearingClaims, CandidateEntry[] Candidates, CandidateEntry[] TopologyOnlyMatches);
}
