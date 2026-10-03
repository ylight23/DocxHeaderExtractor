using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Proves P6S-C uses the shared candidate adapter and frozen snapshot, never a duplicate request composer.</summary>
public sealed class V5P6SCandidateQualificationAdapterTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private static readonly (string Id, string Pdf)[] Documents =
    [ ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) ];

    [Fact]
    public void P6SC_adapter_replays_canonical_snapshot_into_the_exact_31_candidate_bodies()
    {
        var packs = 0; var owned = 0;
        foreach (var document in Documents)
        {
            var pdf = TestRepository.Path(document.Pdf);
            var sha = CanonicalSemanticSourceHash.Compute(pdf);
            var snapshot = TestRepository.Path($"{SnapshotRoot}/{sha}.json");
            Assert.True(File.Exists(snapshot), $"missing P6S-A snapshot {sha}");
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshot, document.Id);
            Assert.Equal(sha, plan.SourceSha256);
            Assert.All(plan.Packs, pack =>
            {
                Assert.Equal(V5CandidateDecisionProtocolV1.Version, pack.Request.ProtocolVersion);
                Assert.Equal(pack.ProviderRequestHash, OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(
                    pack.Request.SystemPrompt, pack.Request.UserMessage, pack.MaxCompletionTokens,
                    new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
                    { UsageInclude = true, OpenRouterResponseCacheDisabled = true }).Hash);
                Assert.DoesNotContain("sourceAlias", pack.Request.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("sourceParts", pack.Request.UserMessage, StringComparison.Ordinal);
            });
            packs += plan.Packs.Count; owned += plan.Packs.Sum(pack => pack.OwnedAliases.Count);
        }
        Assert.Equal(31, packs); Assert.Equal(2_884, owned);
    }
}
