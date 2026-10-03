using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Projection;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Guards the P6S correction for the concrete P6R failures observed against frozen SRC-089/SRC-095
/// Gold: local heading role wins over recurrence, and multipart heading extent stays atomic.
/// </summary>
public sealed class V5P6SLocalHeadingPrecedenceTests
{
    private static readonly DocumentTaskContract Contract = DocumentStructureTaskContract.Create();

    [Fact]
    public void P6R_frozen_request_hashes_remain_unchanged_after_P6S_is_added()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6r-structural-identity-resolution/execution-manifest.v1.json")));
        var frozen = manifest.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}",
            StringComparer.Ordinal);

        var plans = new[]
        {
            PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(
                TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", Contract),
            PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(
                TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract),
        };

        var packs = plans.SelectMany(plan => plan.Packs).ToArray();
        Assert.Equal(31, packs.Length);
        foreach (var prepared in packs)
        {
            var key = $"{prepared.Pack.DocumentId}|{prepared.Pack.PackId}";
            var row = frozen[key];
            Assert.Equal(row.GetProperty("providerRequestHash").GetString(), prepared.Pack.ProviderRequestHash);
            Assert.Equal(row.GetProperty("userMessageSha256").GetString(), prepared.Pack.Request.UserMessageSha256);
            Assert.Equal(row.GetProperty("locatorRegistryFingerprint").GetString(), prepared.Pack.Registry.Fingerprint);
        }
    }

    [Fact]
    public void P6S_changes_only_task_semantics_not_source_packing_or_correspondence_evidence()
    {
        var p6r = PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(
            TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        var p6s = PdfHeadingMembershipProductionAdapter.PrepareLocalHeadingPrecedence(
            TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);

        Assert.Equal(p6r.SourcePlan.SourceSha256, p6s.SourcePlan.SourceSha256);
        Assert.Equal(p6r.SourcePlan.SourceUniverseSha256, p6s.SourcePlan.SourceUniverseSha256);
        Assert.Equal(p6r.Packs.Count, p6s.Packs.Count);

        foreach (var pair in p6r.Packs.Zip(p6s.Packs))
        {
            Assert.Equal(pair.First.Pack.OwnedAliases, pair.Second.Pack.OwnedAliases);
            Assert.Equal(pair.First.Pack.VisibleAliases, pair.Second.Pack.VisibleAliases);
            Assert.Equal(pair.First.Pack.Registry.Fingerprint, pair.Second.Pack.Registry.Fingerprint);
            Assert.Equal(pair.First.AllowedContextEvidence, pair.Second.AllowedContextEvidence);
            Assert.Equal(
                pair.First.AllowedCorrespondenceTargetsByPrimaryAtom.OrderBy(item => item.Key)
                    .Select(item => (item.Key, Values: item.Value.Order().ToArray())),
                pair.Second.AllowedCorrespondenceTargetsByPrimaryAtom.OrderBy(item => item.Key)
                    .Select(item => (item.Key, Values: item.Value.Order().ToArray())));

            var oldRequest = JsonNode.Parse(pair.First.Pack.Request.UserMessage)!.AsObject();
            var newRequest = JsonNode.Parse(pair.Second.Pack.Request.UserMessage)!.AsObject();
            oldRequest.Remove("protocolVersion");
            newRequest.Remove("protocolVersion");
            Assert.Equal(oldRequest.ToJsonString(), newRequest.ToJsonString());
            Assert.NotEqual(pair.First.Pack.ProviderRequestHash, pair.Second.Pack.ProviderRequestHash);
        }

        var prompt = p6s.Packs[0].Pack.Request.SystemPrompt;
        Assert.Contains("Judge each occurrence by what it does at its own source location", prompt, StringComparison.Ordinal);
        Assert.Contains("Correspondence with repeated or related text elsewhere is secondary evidence only", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not split one local heading into separate heading items", prompt, StringComparison.Ordinal);
        Assert.Contains("A source occurrence must not appear in both headings and representations", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("TOC", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INDEX", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SRC-089", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SRC-095", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void P6S_preserves_multipart_heading_and_fails_closed_on_heading_representation_overlap()
    {
        var plan = PdfHeadingMembershipProductionAdapter.PrepareLocalHeadingPrecedence(
            TestRepository.Path(SourcePdfCorpus.Src089), "SRC-089", Contract);
        var pack = plan.Packs[0];

        const string multipart = """
            {"headings":[{"sourceParts":[{"atom":"A16"},{"atom":"A17"}]}],"representations":[]}
            """;
        var parsed = PdfHeadingMembershipProductionAdapter.ParseLocalHeadingPrecedence(pack, multipart);
        var heading = Assert.Single(parsed.Headings.Response.Occurrences);
        Assert.Equal("A16", heading.Primary.Atom);
        Assert.Single(heading.AdditionalParts);
        Assert.Equal("A17", heading.AdditionalParts[0].Atom);
        Assert.Empty(parsed.Headings.Quarantined);
        Assert.Empty(parsed.RepresentationQuarantine);

        const string overlap = """
            {"headings":[{"sourceParts":[{"atom":"A16"},{"atom":"A17"}]}],"representations":[{"sourceParts":[{"atom":"A17"}],"evidenceParts":["C0"]}]}
            """;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PdfHeadingMembershipProductionAdapter.ParseLocalHeadingPrecedence(pack, overlap));
        Assert.Equal("p6s-heading-representation-source-overlap", exception.Message);
    }
}
