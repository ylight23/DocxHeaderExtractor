using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free authority for P6R's source-only correspondence/representation lane.</summary>
public sealed class V5P6RStructuralIdentityResolutionTests
{
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();

    [Fact]
    public void P6R_preserves_P6P_source_and_locator_authority_and_adds_only_read_only_correspondence()
    {
        var p6p = PdfHeadingMembershipProductionAdapter.Prepare(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        var p6r = PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        Assert.Equal(24, p6r.Packs.Count);
        Assert.Equal(p6p.SourceSha256, p6r.SourcePlan.SourceSha256);
        Assert.Equal(p6p.SourceUniverseSha256, p6r.SourcePlan.SourceUniverseSha256);

        foreach (var pair in p6p.Packs.Zip(p6r.Packs))
        {
            Assert.Equal(pair.First.PackId, pair.Second.Pack.PackId);
            Assert.Equal(pair.First.OwnedAliases, pair.Second.Pack.OwnedAliases);
            Assert.Equal(pair.First.VisibleAliases, pair.Second.Pack.VisibleAliases);
            Assert.Equal(pair.First.Registry.Fingerprint, pair.Second.Pack.Registry.Fingerprint);
            Assert.NotEqual(pair.First.ProviderRequestHash, pair.Second.Pack.ProviderRequestHash);
            using var request = JsonDocument.Parse(pair.Second.Pack.Request.UserMessage);
            var root = request.RootElement;
            Assert.Equal(V5FreeHeadingCandidateProtocolV1.PdfStructuralIdentityResolutionVersion, root.GetProperty("protocolVersion").GetString());
            Assert.True(root.TryGetProperty("documentContext", out _));
            Assert.True(root.TryGetProperty("correspondenceCandidates", out var candidates));
            Assert.True(root.TryGetProperty("readOnlyContextEvidence", out var evidence));
            Assert.False(root.GetRawText().Contains("layoutFacts", StringComparison.Ordinal));
            Assert.All(candidates.EnumerateArray(), candidate =>
            {
                Assert.Matches("^A[0-9]+$", candidate.GetProperty("subject").GetString()!);
                Assert.All(candidate.GetProperty("candidates").EnumerateArray(), target =>
                {
                    Assert.Matches("^D[0-9]+$", target.GetProperty("target").GetString()!);
                    Assert.Contains(target.GetProperty("matchTier").GetString(), new[] { "NFKC_WHITESPACE", "NFKC_WHITESPACE_INSENSITIVE" });
                    Assert.False(target.TryGetProperty("region", out _));
                    Assert.False(target.TryGetProperty("heading", out _));
                });
            });
            Assert.All(evidence.EnumerateArray(), item =>
            {
                Assert.Matches("^C[0-9]+$", item.GetProperty("handle").GetString()!);
                Assert.False(item.TryGetProperty("atom", out _));
                Assert.False(item.TryGetProperty("boundaryHandles", out _));
            });
        }
        Assert.DoesNotContain("TOC", p6r.Packs[0].Pack.Request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INDEX", p6r.Packs[0].Pack.Request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void P6R_binds_a_valid_heading_and_representation_but_quarantines_only_bad_relation_target()
    {
        var plan = PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(TestRepository.Path(SourcePdfCorpus.Src095), "SRC-095", Contract);
        var pack = plan.Packs.First(item => item.AllowedCorrespondenceTargetsByPrimaryAtom.Count != 0);
        using var request = JsonDocument.Parse(pack.Pack.Request.UserMessage);
        var correspondence = request.RootElement.GetProperty("correspondenceCandidates").EnumerateArray().First();
        var subject = correspondence.GetProperty("subject").GetString();
        var target = correspondence.GetProperty("candidates")[0].GetProperty("target").GetString();
        var valid = $$"""{"headings":[{"sourceParts":[{"atom":"A0"}]}],"representations":[{"sourceParts":[{"atom":"{{subject}}"}],"correspondsTo":["{{target}}"]}]}""";
        var parsed = PdfHeadingMembershipProductionAdapter.ParseStructuralIdentityResolution(pack, valid);
        Assert.Single(parsed.Headings.Response.Occurrences); Assert.Empty(parsed.Headings.Quarantined);
        Assert.Equal(1, parsed.RepresentationsAccepted); Assert.Empty(parsed.RepresentationQuarantine);

        var invalid = $$"""{"headings":[{"sourceParts":[{"atom":"A0"}]}],"representations":[{"sourceParts":[{"atom":"{{subject}}"}],"correspondsTo":["D999999"]}]}""";
        var isolated = PdfHeadingMembershipProductionAdapter.ParseStructuralIdentityResolution(pack, invalid);
        Assert.Single(isolated.Headings.Response.Occurrences); Assert.Empty(isolated.Headings.Quarantined);
        Assert.Equal(0, isolated.RepresentationsAccepted); Assert.Single(isolated.RepresentationQuarantine);

        var wrongSubjectTarget = pack.AllowedCorrespondenceTargetsByPrimaryAtom.First(pair =>
            pack.AllowedCorrespondenceTargetsByPrimaryAtom.Any(other => other.Key != pair.Key && other.Value.Any(target => !pair.Value.Contains(target))));
        var otherTarget = pack.AllowedCorrespondenceTargetsByPrimaryAtom.First(other => other.Key != wrongSubjectTarget.Key && other.Value.Any(target => !wrongSubjectTarget.Value.Contains(target))).Value
            .First(target => !wrongSubjectTarget.Value.Contains(target));
        var crossSubject = $$"""{"headings":[{"sourceParts":[{"atom":"A0"}]}],"representations":[{"sourceParts":[{"atom":"{{wrongSubjectTarget.Key}}"}],"correspondsTo":["{{otherTarget}}"]}]}""";
        var subjectIsolated = PdfHeadingMembershipProductionAdapter.ParseStructuralIdentityResolution(pack, crossSubject);
        Assert.Single(subjectIsolated.Headings.Response.Occurrences); Assert.Equal(0, subjectIsolated.RepresentationsAccepted);
        Assert.Single(subjectIsolated.RepresentationQuarantine);
    }
}
