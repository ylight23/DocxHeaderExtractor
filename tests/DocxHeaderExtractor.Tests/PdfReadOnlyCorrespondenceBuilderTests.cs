using System.Text;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfReadOnlyCorrespondenceBuilderTests
{
    [Fact]
    public void Pdf_read_only_builder_matches_frozen_candidate_universe_projection()
    {
        var document = new[]
        {
            Atom("A1", 0, 1, "1. Scope ofHTTP"),
            Atom("A2", 1, 1, " and applicability"),
            Atom("A3", 2, 1, "ordinary body"),
            Atom("A4", 3, 2, "1. Scope of HTTP and applicability"),
            Atom("A5", 4, 2, "TABLE OF CONTENTS"),
            Atom("A6", 5, 2, "1. Scope of HTTP"),
            Atom("A7", 6, 2, "and applicability"),
            Atom("A8", 7, 2, "HTTP"),
            Atom("A9", 8, 2, "3.7"),
            Atom("A10", 9, 2, " "),
        };
        var owned = document.Take(3).ToArray();

        var expected = FromFrozenCandidateUniverse(owned, document);
        var actual = PdfReadOnlyCorrespondenceBuilder.Build(owned, document);

        Assert.Equal(expected.Keys.OrderBy(key => key, StringComparer.Ordinal), actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var alias in expected.Keys)
            Assert.Equal(expected[alias], actual[alias]);
    }

    // The five-document P05 cohort the live PDF function pass was qualified on.
    private static readonly string[] P05Cohort =
    [
        "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf",
        "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf",
        "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf",
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf",
        "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf",
    ];

    // Same envelope as PdfHeadingPipeline.
    private static readonly V5ProviderEnvelope LiveEnvelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    /// <summary>
    /// Provider-free full-P05 parity: for every pack the live adapter would issue, the read-only builder and the
    /// historical candidate-universe projection must agree on aliases, ordered correspondences, F1 user message
    /// bytes, and provider body hash. This is the gate for retiring V5CandidateUniverseV1.
    /// </summary>
    [Fact]
    public void Pdf_read_only_builder_matches_candidate_universe_on_every_p05_pack_of_the_cohort()
    {
        int packs = 0, aliases = 0, correspondences = 0;
        foreach (var pdf in P05Cohort)
        {
            var sourceBuild = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(pdf));
            var authority = sourceBuild.Snapshot;
            var atoms = authority.Atoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            foreach (var pack in SemanticEvidencePackingPolicies.PdfResourceBoundedP05.BuildPacks(authority.Evidence, sourceBuild.Details.LayoutBlockByAtom))
            {
                var ownedAliases = pack.Owned.Select(item => item.SourceAlias).ToArray();
                var owned = ownedAliases.Select(alias => atoms[alias]).ToArray();
                var context = pack.Visible.Select(item => item.SourceAlias)
                    .Where(alias => !ownedAliases.Contains(alias, StringComparer.Ordinal))
                    .Select(alias => (atoms[alias].Page, atoms[alias].Text)).ToArray();
                var where = $"{pdf} {pack.PackId}";

                var expected = FromFrozenCandidateUniverse(owned, authority.Atoms);
                var actual = PdfReadOnlyCorrespondenceBuilder.Build(owned, authority.Atoms);

                Assert.True(expected.Keys.OrderBy(key => key, StringComparer.Ordinal)
                    .SequenceEqual(actual.Keys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal), $"alias set drift: {where}");
                foreach (var alias in expected.Keys)
                    Assert.True(expected[alias].SequenceEqual(actual[alias]), $"correspondence drift: {where} {alias}");

                var expectedF1 = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, expected);
                var actualF1 = OccurrenceFunctionProtocolV1.ComposeWithReadOnlyCorrespondences(owned, context, actual);
                Assert.True(Encoding.UTF8.GetBytes(expectedF1.UserMessage).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(actualF1.UserMessage)), $"F1 user message drift: {where}");
                Assert.Equal(expectedF1.SystemPrompt, actualF1.SystemPrompt);
                Assert.Equal(
                    OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(expectedF1.SystemPrompt, expectedF1.UserMessage, PdfInferenceWireContract.CompletionTokenCeiling, LiveEnvelope).Hash,
                    OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(actualF1.SystemPrompt, actualF1.UserMessage, PdfInferenceWireContract.CompletionTokenCeiling, LiveEnvelope).Hash);

                packs++;
                aliases += actual.Count;
                correspondences += actual.Values.Sum(list => list.Count);
            }
        }

        // The cohort must actually exercise the correspondence path, not pass vacuously.
        Assert.True(packs >= P05Cohort.Length, $"packs={packs}");
        Assert.True(aliases > 0 && correspondences > 0, $"aliases={aliases} correspondences={correspondences}");
    }

    /// <summary>
    /// The read-only builder reproduces the frozen P6T-F1 qualification bodies for SRC-089 and SRC-095 PACK_001
    /// (requestHash, providerRequestHash, providerRequestBytes) from the canonical source snapshots.
    /// </summary>
    [Fact]
    public void Pdf_read_only_builder_reproduces_frozen_p6tf1_qualification_bodies()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6tf1-preflight/two-pack-function-membership-manifest.v1.json")));
        var rows = manifest.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row.GetProperty("documentId").GetString()!, StringComparer.Ordinal);
        foreach (var (documentId, pdf) in new[] { ("SRC-089", P05Cohort[0]), ("SRC-095", P05Cohort[2]) })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(pdf));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
                TestRepository.Path($"eval/a99-closed-loop/pdf-canonical-source-v1/{sourceHash}.json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var atoms = plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var owned = pack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(
                plan, pack, PdfReadOnlyCorrespondenceBuilder.Build(owned, plan.SourceAtoms));

            var frozen = rows[documentId];
            Assert.Equal(frozen.GetProperty("packId").GetString(), pack.PackId);
            Assert.Equal(frozen.GetProperty("issuedOccurrences").GetInt32(), prepared.Request.Occurrences.Count);
            Assert.Equal(frozen.GetProperty("requestHash").GetString(), prepared.Request.UserMessageSha256);
            Assert.Equal(frozen.GetProperty("providerRequestHash").GetString(), prepared.ProviderRequestHash);
            Assert.Equal(frozen.GetProperty("providerRequestBytes").GetInt32(), prepared.ProviderRequestBytes);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> FromFrozenCandidateUniverse(
        IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyList<SemanticSourceAtom> all)
    {
        var universe = V5CandidateUniverseV1.Build(owned, all, V5CandidatePolicyV1.Default);
        var candidates = universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate)) continue;
            var alias = candidate.Endpoint.Parts[0].Alias;
            if (!owned.Any(atom => atom.Alias == alias)) continue;
            var list = result.TryGetValue(alias, out var old) ? old.ToList() : [];
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[alias] = list;
        }
        return result;
    }

    private static SemanticSourceAtom Atom(string alias, int ordinal, int page, string text) =>
        new(alias, $"source-{alias}", ordinal, page, 1, 0, text);
}
