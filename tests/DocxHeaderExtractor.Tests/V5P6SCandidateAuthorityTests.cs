using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P6S is deliberately a provider-free qualification lane.  It proves that candidate identifiers,
/// extents, and correspondence identifiers are issued by the harness before a model is involved.
/// It does not promote this protocol or score any provider output.
/// </summary>
public sealed class V5P6SCandidateAuthorityTests
{
    private const string Root = "artifacts/v5-p6s-candidate-authority";
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string Id, string Pdf, int Packs)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089, 7),
        ("SRC-095", SourcePdfCorpus.Src095, 24),
    ];

    [Fact]
    public void P6S_freezes_full31_harness_issued_candidate_universe_and_Gold_coverage()
    {
        // Candidate issue happens before the read-only coverage measurement below.  The generator
        // receives only source atoms plus the fixed P05 ownership plan: never Gold or model output.
        var rows = new List<Row>();
        var documentRows = new List<DocumentRow>();
        var allCandidateIdentities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var allRelationTargets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var candidateCount = 0; var relationCount = 0; var relationTruncated = 0; var ownedCount = 0;
        var candidateByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalBytes = 0; var totalP05V3Bytes = 0;

        foreach (var document in Documents)
        {
            var path = TestRepository.Path(document.Pdf);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(path);
            var byAlias = atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var packs = V5PdfPreflightBuilder.BuildV3(path, document.Id, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            Assert.Equal(document.Packs, packs.Count);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            var documentCandidates = 0;

            foreach (var (pack, ordinal) in packs.Select((value, index) => (value, index + 1)))
            {
                var owned = pack.OwnedAliases.Select(alias => byAlias[alias]).ToArray();
                var universe = V5CandidateUniverseV1.Build(owned, atoms, V5CandidatePolicyV1.Default);
                Assert.Equal(universe.Candidates.Count, universe.Candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(Enumerable.Range(1, universe.Candidates.Count).Select(index => $"C{index}"), universe.Candidates.Select(candidate => candidate.Id));
                Assert.Equal(universe.Relations.Count, universe.Relations.Select(relation => relation.Id).Distinct(StringComparer.Ordinal).Count());
                Assert.Equal(Enumerable.Range(1, universe.Relations.Count).Select(index => $"R{index}"), universe.Relations.Select(relation => relation.Id));

                foreach (var candidate in universe.Candidates)
                {
                    // The issued endpoint is re-bound under this pack's owned source authority.
                    // A context-only atom cannot enter a candidate even if it is source-visible.
                    var rebound = SemanticSourcePartBinder.Bind(owned, candidate.Parts);
                    Assert.True(rebound.IsBound, $"{document.Id}/{pack.PackId}/{candidate.Id}: {rebound.Status}");
                    Assert.Equal(candidate.Endpoint.Identity, new BoundClaimEndpoint(rebound.Parts).Identity);
                    Assert.All(rebound.Parts, part => Assert.Contains(part.Alias, pack.OwnedAliases));
                    Assert.True(identities.Add(candidate.SpanIdentity), $"duplicate candidate endpoint across one P05 owner: {candidate.SpanIdentity}");
                    candidateByKind[candidate.Kind.ToString()] = candidateByKind.GetValueOrDefault(candidate.Kind.ToString()) + 1;
                }
                foreach (var relation in universe.Relations)
                {
                    Assert.True(universe.TryCandidate(relation.CandidateId, out var candidate));
                    Assert.DoesNotContain(candidate.Endpoint.Parts, part => relation.TargetSpanIdentity.Contains($"{part.Alias}:", StringComparison.Ordinal));
                    targets.Add(relation.TargetSpanIdentity);
                }

                // Nothing in the model-visible request makes source aliases, UTF-16 coordinates,
                // or a context-only source selectable.  C#/R# are the only decision authority.
                var request = V5CandidateDecisionProtocolV1.Compose(universe, [], null);
                using var user = JsonDocument.Parse(request.UserMessage);
                Assert.Equal(V5CandidateDecisionProtocolV1.Version, user.RootElement.GetProperty("protocolVersion").GetString());
                Assert.True(user.RootElement.TryGetProperty("candidates", out var candidates));
                Assert.True(user.RootElement.TryGetProperty("relations", out var relations));
                Assert.All(candidates.EnumerateArray(), item =>
                {
                    Assert.Matches("^C[1-9][0-9]*$", item.GetProperty("id").GetString()!);
                    Assert.False(item.TryGetProperty("sourceAlias", out _));
                    Assert.False(item.TryGetProperty("sourceId", out _));
                    Assert.False(item.TryGetProperty("start", out _));
                    Assert.False(item.TryGetProperty("end", out _));
                    Assert.False(item.TryGetProperty("sourceParts", out _));
                });
                Assert.All(relations.EnumerateArray(), item =>
                {
                    Assert.Matches("^R[1-9][0-9]*$", item.GetProperty("id").GetString()!);
                    Assert.Matches("^C[1-9][0-9]*$", item.GetProperty("candidate").GetString()!);
                    Assert.False(item.TryGetProperty("target", out _));
                    Assert.False(item.TryGetProperty("sourceAlias", out _));
                });
                Assert.DoesNotContain("A0", request.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("H0", request.UserMessage, StringComparison.Ordinal);

                var provider = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(request.SystemPrompt, request.UserMessage,
                    pack.MaxCompletionTokens, Envelope);
                rows.Add(new Row(document.Id, ordinal, pack.PackId, pack.OwnedAliases.Count, universe.Candidates.Count,
                    universe.Relations.Count, universe.RelationsTruncated, request.UserMessageUtf8Bytes, provider.Bytes,
                    pack.ProviderRequestBytes, universe.Fingerprint, request.UserMessageSha256, provider.Hash));
                candidateCount += universe.Candidates.Count; documentCandidates += universe.Candidates.Count;
                relationCount += universe.Relations.Count; relationTruncated += universe.RelationsTruncated; ownedCount += owned.Length;
                totalBytes += provider.Bytes; totalP05V3Bytes += pack.ProviderRequestBytes;
            }
            allCandidateIdentities.Add(document.Id, identities);
            allRelationTargets.Add(document.Id, targets);
            documentRows.Add(new DocumentRow(document.Id, packs.Count, atoms.Count, documentCandidates, 0, 0, 0));
        }

        Assert.Equal(31, rows.Count); Assert.Equal(2884, ownedCount);
        Assert.Equal(18339, candidateCount); Assert.Equal(2015, relationCount);

        // First and only Gold use: coverage measurement after source-only candidate issue. Gold has
        // no route back into Build/Compose and is never modified.
        var coveredGold = 0; var relationTargetedGold = 0; var totalGold = 0;
        var coverage = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var index = 0; index < Documents.Length; index++)
        {
            var document = Documents[index];
            var gold = ExactScorer.ReadGold(TestRepository.Path($"eval/a99-closed-loop/gold/{document.Id}.gold.json"),
                ExactScorer.Universe.For("PDF", TestRepository.Path(document.Pdf)));
            var identities = gold.Select(item => item.Identity).ToArray();
            var covered = identities.Count(allCandidateIdentities[document.Id].Contains);
            var targeted = identities.Count(allRelationTargets[document.Id].Contains);
            Assert.Equal(gold.Count, covered);
            totalGold += gold.Count; coveredGold += covered; relationTargetedGold += targeted;
            coverage.Add(document.Id, new { goldClaims = gold.Count, goldExtentCovered = covered, goldExtentCoverage = (double)covered / gold.Count, goldTargetedByIssuedRelation = targeted });
            documentRows[index] = documentRows[index] with { GoldClaims = gold.Count, GoldExtentCovered = covered, GoldTargetedByIssuedRelation = targeted };
        }
        Assert.Equal(139, totalGold); Assert.Equal(totalGold, coveredGold); Assert.Equal(89, relationTargetedGold);

        FreezeArtifact.AssertJson(Root, "candidate-universe-coverage.v1.json", new
        {
            schemaVersion = "v5-p6s-candidate-authority-coverage-v1",
            lane = "P6S candidate-authority: harness issues C#/R#; model returns decisions only",
            protocolVersion = V5CandidateDecisionProtocolV1.Version,
            policy = new
            {
                version = V5CandidatePolicyV1.Version,
                V5CandidatePolicyV1.Default.MaxMultipartParts,
                V5CandidatePolicyV1.Default.StrictTokenTrim,
                V5CandidatePolicyV1.Default.MaxRelationsPerCandidate,
                family = "WHOLE every owned atom; STRICT_PREFIX/STRICT_SUFFIX drop one trailing/leading whitespace token; MULTIPART 2..3 owned atoms with consecutive source ordinals on one page (all whole, or last part STRICT_PREFIX)",
                binderLanguageEnumerable = false,
                binderLanguageNote = "the exact binder accepts any increasing owned-atom sequence with any scalar-boundary substring per part; that language is combinatorial and is not enumerated - coverage of the issued family is measured against Gold below",
            },
            invariants = new
            {
                candidateIdsRequestLocalContiguous = true, candidateIdentitiesUnique = true,
                everyCandidateRebindsToFrozenIdentity = true, contextOnlySelectable = false,
                modelAuthoredCoordinates = false, relationTargetsReadOnly = true,
                modelDecisionGrammar = "candidate C# + HEADING/REPRESENTATION only; R# is read-only reasoning evidence",
            },
            universe = new { packs = rows.Count, ownedOccurrences = ownedCount, candidates = candidateCount, candidatesByKind = candidateByKind,
                relations = relationCount, relationsTruncated = relationTruncated, maxCandidatesPerPack = rows.Max(row => row.Candidates) },
            requestBudget = new { totalProviderRequestBytes = totalBytes, totalP05V3ProviderRequestBytes = totalP05V3Bytes,
                maxProviderRequestBytes = rows.Max(row => row.ProviderRequestBytes), maxP05V3ProviderRequestBytes = rows.Max(row => row.P05V3ProviderRequestBytes) },
            goldCoverage = new { goldClaims = totalGold, goldExtentCovered = coveredGold, goldExtentCoverage = (double)coveredGold / totalGold, goldTargetedByIssuedRelation = relationTargetedGold },
            metricsRequiringProvider = new { semanticCandidateF1 = "NOT_RUN", relationSelectionAccuracy = "NOT_RUN", deterministicBindingSuccess = "NOT_RUN", finalExactOccurrenceF1 = "NOT_RUN" },
            documents = documentRows, packs = rows, providerCalls = 0, goldRead = true,
            goldUsage = "measurement only, after candidate issue; never an input to the generator", goldMutation = "NONE", sharedRuntime = "UNCHANGED",
        });
    }

    [Fact]
    public void P6S_parser_uses_only_issued_ids_and_quarantines_bad_decisions_without_harming_siblings()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0", "source-0", 0, 1, 0, 0, "1. Introduction"),
            new SemanticSourceAtom("L1", "source-1", 1, 1, 1, 0, "Scope"),
            new SemanticSourceAtom("L2", "source-2", 2, 2, 0, 0, "1. Introduction"),
        };
        var universe = V5CandidateUniverseV1.Build(atoms.Take(2).ToArray(), atoms, V5CandidatePolicyV1.Default);
        var representation = universe.Candidates.First(item => item.Id == "C1");
        var validHeading = universe.Candidates.First(item => item.Id != representation.Id);
        var raw = $$"""{"decisions":[{"candidate":"{{representation.Id}}","kind":"REPRESENTATION"},{"candidate":"{{validHeading.Id}}","kind":"HEADING"},{"candidate":"C999","kind":"HEADING"},{"candidate":"{{validHeading.Id}}","kind":"HEADING","sourceParts":[]}]}""";
        using var payload = JsonDocument.Parse(raw);
        var result = V5CandidateDecisionProtocolV1.Parse(payload.RootElement, Encoding.UTF8.GetByteCount(raw), 49_152, universe);
        Assert.Equal(4, result.RawDecisionCount); Assert.Equal(2, result.Accepted.Count); Assert.Equal(2, result.Quarantined.Count);
        Assert.Contains(result.Accepted, item => item.Candidate.Id == representation.Id && item.Kind == V5CandidateDecisionKind.REPRESENTATION);
        Assert.Contains(result.Accepted, item => item.Candidate.Id == validHeading.Id && item.Kind == V5CandidateDecisionKind.HEADING);
        Assert.Contains(result.Quarantined, item => item.Reason == "candidate-not-issued");
        Assert.Contains(result.Quarantined, item => item.Reason == "decision-field-not-in-contract");

        var forbiddenRelation = $$"""{"decisions":[{"candidate":"{{validHeading.Id}}","kind":"REPRESENTATION","relation":"R1"}]}""";
        using var cross = JsonDocument.Parse(forbiddenRelation);
        Assert.Equal("decision-field-not-in-contract", Assert.Single(V5CandidateDecisionProtocolV1.Parse(cross.RootElement,
            Encoding.UTF8.GetByteCount(forbiddenRelation), 49_152, universe).Quarantined).Reason);

        using var badRoot = JsonDocument.Parse("{\"headings\":[]}");
        Assert.Throws<InvalidOperationException>(() => V5CandidateDecisionProtocolV1.Parse(badRoot.RootElement, 15, 49_152, universe));
    }

    [Fact]
    public void P6S_overlapping_heading_candidates_are_quarantined_as_a_cluster_without_a_winner()
    {
        var atoms = new[] { new SemanticSourceAtom("L0", "source-0", 0, 1, 0, 0, "Chapter I GENERAL PROVISIONS") };
        var universe = V5CandidateUniverseV1.Build(atoms, atoms, V5CandidatePolicyV1.Default);
        var whole = Assert.Single(universe.Candidates.Where(candidate => candidate.Kind == V5CandidateExtentKind.WHOLE));
        var strict = universe.Candidates.First(candidate => candidate.Kind is V5CandidateExtentKind.STRICT_PREFIX or V5CandidateExtentKind.STRICT_SUFFIX);
        var raw = $$"""{"decisions":[{"candidate":"{{whole.Id}}","kind":"HEADING"},{"candidate":"{{strict.Id}}","kind":"HEADING"}]}""";
        using var payload = JsonDocument.Parse(raw);
        var result = V5CandidateDecisionProtocolV1.Parse(payload.RootElement, Encoding.UTF8.GetByteCount(raw), 49_152, universe);
        Assert.Empty(result.Headings);
        Assert.Equal(2, result.Quarantined.Count(item => item.Reason == "candidate-overlap-conflict"));
    }

    private sealed record Row(string DocumentId, int ParentOrdinal, string PackId, int OwnedOccurrences, int Candidates, int Relations,
        int RelationsTruncated, int UserMessageUtf8Bytes, int ProviderRequestBytes, int P05V3ProviderRequestBytes,
        string UniverseFingerprint, string UserMessageSha256, string ProviderRequestSha256);
    private sealed record DocumentRow(string DocumentId, int Packs, int SourceOccurrences, int Candidates, int GoldClaims,
        int GoldExtentCovered, int GoldTargetedByIssuedRelation);
}
