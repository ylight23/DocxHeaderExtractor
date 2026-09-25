using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Pipeline.HistoricalContracts;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// REMOVE_MODEL_VISIBLE_ATTENTION_V1, version gates. V2_ATTENTION_FREE is what production sends; the
/// V1 legacy request survives only for historical experiments and replays, which must select it by
/// name through <see cref="AttentionLegacyV1.Select"/>. No default, null or zero value reaches V1, and
/// a version the code does not know is refused rather than mapped.
/// </summary>
public sealed partial class SemanticRequestVersionGateTests
{
    [Fact]
    public void The_production_default_is_the_attention_free_request()
    {
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, new CanonicalSemanticExperiment(false, false).RequestVersion);
        foreach (var preset in new[]
                 {
                     CanonicalSemanticExperiment.Baseline, CanonicalSemanticExperiment.StructuralAncestorsOnly,
                     CanonicalSemanticExperiment.PartialSpanOnly, CanonicalSemanticExperiment.NonStructuralMetadataConstrained,
                     CanonicalSemanticExperiment.MembershipBeforePlacement,
                 })
            Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, preset.RequestVersion);
        Assert.Equal(CanonicalSemanticEngine.SystemPrompt, CanonicalSemanticEngine.SystemPromptFor(CanonicalSemanticExperiment.Baseline));
    }

    [Fact]
    public void An_unknown_or_unspecified_version_fails_closed()
    {
        foreach (var version in new[] { SemanticRequestVersion.Unspecified, (SemanticRequestVersion)99 })
        {
            var experiment = CanonicalSemanticExperiment.Baseline with { RequestVersion = version };
            Assert.Throws<InvalidOperationException>(() => CanonicalSemanticEngine.SystemPromptFor(experiment));
            Assert.Throws<InvalidOperationException>(() => CanonicalSemanticEngine.SystemPromptFor(
                SemanticCoordinateContract.PdfStructuredSourcePartsV2, experiment));
            Assert.Throws<InvalidOperationException>(() => CanonicalSemanticEngine.MembershipPromptFor(
                SemanticCoordinateContract.PdfSemanticMembershipV1, version));
        }
    }

    [Fact]
    public void V1_is_selected_only_through_the_historical_contract()
    {
        var historical = AttentionLegacyV1.Select(CanonicalSemanticExperiment.PartialSpanOnly);
        Assert.Equal(SemanticRequestVersion.V1_ATTENTION_LEGACY, historical.RequestVersion);
        Assert.True(historical.CommunicatePartialSpan); // only the version changes

        // No code outside HistoricalContracts assigns V1, and nothing forges it from a number.
        var root = TestRepository.Root();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests", "DocxHeaderExtractor.Tests"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("SemanticRequestVersionGateTests.cs", StringComparison.Ordinal));
        var offenders = files
            .Where(f => !f.EndsWith(Path.Combine("HistoricalContracts", "AttentionLegacyV1.cs"), StringComparison.Ordinal))
            .Where(f => AssignsV1().IsMatch(File.ReadAllText(f)) || ForgedVersion().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_two_versions_differ_only_by_the_attention_judgement()
    {
        Assert.Contains("\"attention\" flag", AttentionLegacyV1.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("attention", CanonicalSemanticEngine.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attention", CanonicalSemanticEngine.Stage1MembershipPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\r', CanonicalSemanticEngine.SystemPrompt);
        static string Words(string s) => Regex.Replace(s, @"\s+", " ");
        Assert.Equal(
            Words(AttentionLegacyV1.SystemPrompt).Replace(
                "The \"attention\" flag is a hint, not the set of allowed headings: any owned occurrence may be a heading.",
                "Any owned occurrence may be a heading."),
            Words(CanonicalSemanticEngine.SystemPrompt));
        Assert.Equal(
            Words(AttentionLegacyV1.Stage1MembershipPrompt).Replace(
                "The \"attention\" flag is a hint, not the set of allowed claims: any owned occurrence may be one.",
                "Any owned occurrence may be one."),
            Words(CanonicalSemanticEngine.Stage1MembershipPrompt));
    }

    [Fact]
    public void A_versioned_run_records_its_request_version_and_an_unversioned_one_hashes_as_before()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"request-version-{Guid.NewGuid():N}");
        var metadata = new SemanticAuthorityCaptureMetadata("PDF", new string('a', 64), "model", "route", new string('b', 64));
        var calls = new[] { new SemanticAuthorityCaptureCallIdentity(1, "semantic", "PACK_001", new string('c', 64), 10) };
        SemanticAuthorityCaptureReservation Reserve(SemanticAuthorityCaptureMetadata m, string dir) =>
            new SemanticAuthorityReplayCaptureRequest(m, dir)
                .Reserve("DOC", new string('d', 64), new string('e', 64), calls).Reservation!;

        var unversioned = Reserve(metadata, directory + "-a");
        var versioned = Reserve(metadata with { RequestVersion = SemanticRequestVersion.V2_ATTENTION_FREE.ToString() }, directory + "-b");
        Assert.Null(unversioned.RequestVersion);
        Assert.Equal("V2_ATTENTION_FREE", versioned.RequestVersion);
        Assert.NotEqual(unversioned.IdentityHash, versioned.IdentityHash);
        Assert.Equal(unversioned.PromptHash, versioned.PromptHash);
        Assert.Equal(unversioned.SemanticContractHash, versioned.SemanticContractHash);
    }

    [GeneratedRegex(@"RequestVersion\s*=\s*SemanticRequestVersion\.V1_ATTENTION_LEGACY")] private static partial Regex AssignsV1();
    [GeneratedRegex(@"\(\s*SemanticRequestVersion\s*\)\s*1\b")] private static partial Regex ForgedVersion();
}
