using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// ONTOLOGY_FUNCTIONALITY_REVIEW_V1: a purely offline scan of the already-approved Gold corpus for
/// occurrences that show the same dual-function topology ITEM-CCE2C592 ("Agenda") showed - a short,
/// generic, artifact-naming-shaped claim immediately followed by a run of more granular claims that
/// plausibly nest beneath it. Zero model, provider, or VLM calls; nothing here re-reviews or changes
/// any approved Gold.
/// <para>
/// The corpus-coverage constraint this file surfaces first, before any finding: only 2 of the 21
/// approved authorities (DOC-0001, DOC-0252) materialize claim-level data (identity, semanticRole,
/// approved text) at all - the other 19 record only an approved heading total, per their own freeze
/// artifacts ("Semantic total is authoritative; no occurrence list or span was synthesized from the
/// total"), already established by <see cref="NoneRelationPolicyCorpusAuditTests"/>. That means this
/// review can only examine 48 of 3955 approved headings (about 1.2% of the corpus) - it answers "is
/// Agenda a boundary case or a symptom, within the part of the corpus we can actually see," not "is
/// Agenda a boundary case or a symptom in the corpus."
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
        Assert.Equal(21, registry.Length);
        var carriers = registry.Where(a => a.MaterializedSemanticClaims > 0).ToArray();
        Assert.Equal(2, carriers.Length);
        Assert.Equal(new[] { "DOC-0001", "DOC-0252" }, carriers.Select(a => a.AuthorityId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Freeze_ontology_functionality_review()
    {
        var registry = ReadRegistry();
        var totalHeadings = registry.Sum(a => a.SemanticHeadingTotal);
        var carriers = registry.Where(a => a.MaterializedSemanticClaims > 0).ToArray();
        var reviewableClaims = carriers.Sum(a => a.MaterializedSemanticClaims);

        var doc0001 = ScanAuthority("DOC-0001");
        var doc0252 = ScanAuthority("DOC-0252");
        var allCandidates = doc0001.Candidates.Concat(doc0252.Candidates).ToArray();
        var allTopologyOnly = doc0001.TopologyOnlyMatches.Concat(doc0252.TopologyOnlyMatches).ToArray();

        FreezeArtifact.AssertJson(Root, "ontology-functionality-review.v1.json", new
        {
            artifactKind = "a99_ontology_functionality_review",
            schemaVersion = "a99-ontology-functionality-review-v1",
            task = "ONTOLOGY_FUNCTIONALITY_REVIEW_V1",
            status = "SCANNED_OFFLINE_ZERO_CALLS",
            modelCalls = 0,
            providerCalls = 0,
            vlmCalls = 0,

            corpusCoverage = new
            {
                authorities = registry.Length,
                authoritiesCarryingClaimLevelData = carriers.Length,
                claimCarryingAuthorities = carriers.Select(a => a.AuthorityId).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                approvedHeadingsInCorpus = totalHeadings,
                reviewableClaimsInCorpus = reviewableClaims,
                coverageOfApprovedHeadings = Math.Round((double)reviewableClaims / totalHeadings, 4),
                caveat = "This review can only examine claims from DOC-0001 and DOC-0252 - the other 19 authorities record only an approved heading total with no per-heading role or text, so 'no dual-function candidate found there' cannot be claimed; it can only be said that no such candidate is visible with the data currently approved.",
            },

            heuristicDefinition = new
            {
                candidateCriteria = "A claim is a DUAL_FUNCTION_CANDIDATE iff (a) its approved text is at most " + WordCountCeiling + " words (short and generic, not a self-describing structural heading with its own numbering or descriptive phrase), AND (b) it is immediately followed, in document order, by at least " + MinNestedChildren + " consecutive claims whose semanticRole differs from its own (a run of more-granular content that plausibly nests beneath it).",
                topologyOnlyCriteria = "A claim is a TOPOLOGY_ONLY_MATCH iff it satisfies (b) above but not (a) - it precedes a run of nested-looking children but its own text is a normal, self-describing structural heading (e.g. carries its own numbering or a full descriptive phrase), so the topology alone is not read as artifact-naming ambiguity.",
                excludedByDesign = "The top-level document title (position 0 in every document) trivially precedes the rest of the document's structure by definition - that is not evidence of dual-function ambiguity, it is what a document title always does. Only an EMBEDDED claim (not the document's own opening title) showing this pattern is reported as a candidate.",
                notModelInferred = "This heuristic is a deterministic function of already-approved Gold text and role fields - no model was asked to judge any claim's function.",
            },

            doc0001 = new { authorityId = "DOC-0001", claimsScanned = doc0001.ClaimsScanned, candidates = doc0001.Candidates, topologyOnlyMatches = doc0001.TopologyOnlyMatches, note = "No claim in this authority carries a semanticRole at all (all null) - it is a flat run of numbered chapter/article headings (Chương N / N.N / N.N.N), each fully self-describing with its own numbering. None is short/generic enough to qualify as a candidate under this heuristic." },
            doc0252 = new { authorityId = "DOC-0252", claimsScanned = doc0252.ClaimsScanned, candidates = doc0252.Candidates, topologyOnlyMatches = doc0252.TopologyOnlyMatches },

            findings = new
            {
                totalCandidates = allCandidates.Length,
                candidateItemIds = allCandidates.Select(c => c.Identity).ToArray(),
                totalTopologyOnlyMatches = allTopologyOnly.Length,
                headline = allCandidates.Length == 1 && allCandidates[0].Identity == "L0519:S0:0-6"
                    ? "Across the 48 reviewable claims (1.2% of the approved corpus), exactly one DUAL_FUNCTION_CANDIDATE was found: ITEM-CCE2C592 ('Agenda', L0519:S0:0-6) - the same item already independently identified as ambiguous through model disagreement. ITEM-505430BB (the other DocumentTitle claim added by the R1->R2 correction) does NOT match: its immediate successor is another DocumentTitle claim ('Agenda' itself), not a run of nested content, so it reads as pure IDENTITY with no topology signal of its own substructure. One topology-only contrast case was found (L0556, 'Annex 2: List of Participants', an AppendixHeading followed by 4 LocalSubheading children) - its own text is fully self-describing, unlike 'Agenda', so it does not read as artifact-naming-ambiguous despite the matching topology."
                    : "See candidates and topologyOnlyMatches above.",
            },

            conclusion = new
            {
                singleLabelOntologyProblem = "NOT_SUPPORTED_BY_REVIEWABLE_DATA",
                agendaIsABoundaryCase = "SUPPORTED_BY_REVIEWABLE_DATA",
                reasoning = "Within the 48 claims this repository can actually examine, dual-function ambiguity is rare (1 candidate) rather than systemic - the other 47 materialized claims, including a topologically similar appendix heading, do not show it. This is evidence AGAINST the single-label ontology being broadly broken and FOR Agenda being a specific, rare boundary case, at least in the reviewable slice of the corpus.",
                properCaveat = "This conclusion is scoped to the 1.2% of the corpus with claim-level data. It is not evidence that no other occurrence in the remaining 98.8% would show the same pattern if that data were ever materialized - the review is honest about what it can and cannot see, not a claim that the rest of the corpus was checked and found clean.",
            },

            productionOrSchemaChanged = false,
            goldChanged = false,
            crossDocumentGeneralization = false,
            generalizationEstablished = false,
            scope = "DOC-0001 and DOC-0252 only, the two authorities with claim-level data. Not evidence about the remaining 19 authorities' unmaterialized headings.",
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

        return new ScanResult(rows.Length, candidates.ToArray(), topologyOnly.ToArray());
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
    private sealed record Row(string? Identity, string? Alias, string? Role, string? Text);
    private sealed record CandidateEntry(string Identity, string Role, string Text, int WordCount, int NestedChildCount, string[] NestedChildRoles);
    private sealed record ScanResult(int ClaimsScanned, CandidateEntry[] Candidates, CandidateEntry[] TopologyOnlyMatches);
}
