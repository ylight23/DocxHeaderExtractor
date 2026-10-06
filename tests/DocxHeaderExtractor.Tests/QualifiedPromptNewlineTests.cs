using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Wire reproducibility across checkouts: a code-authored prompt must put the same bytes on the wire
/// whether its source file was checked out LF or CRLF. Only the system prompt is canonicalized; the
/// user message carries document text and is sent as given.
/// </summary>
public sealed class QualifiedPromptNewlineTests
{
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };

    public static TheoryData<string, string> CodeAuthoredPrompts => new()
    {
        { "F1", OccurrenceFunctionProtocolV1.SystemPrompt },
        { "G2A", HeadingAnchorProtocolV1.SystemPrompt },
        { "H2-C", HeadingExtentProtocolV2.SystemPrompt },
    };

    [Theory]
    [MemberData(nameof(CodeAuthoredPrompts))]
    public void Code_authored_prompts_are_lf_only(string name, string prompt)
    {
        Assert.DoesNotContain('\r', prompt);
        Assert.True(prompt.Contains('\n'), $"{name} is multi-line, so the check is not vacuous");
    }

    [Theory]
    [MemberData(nameof(CodeAuthoredPrompts))]
    public void Lf_and_crlf_prompt_build_identical_provider_bodies(string name, string prompt)
    {
        var crlf = prompt.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.NotEqual(prompt, crlf);

        var lfBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(prompt, "user", 1024, Envelope);
        var crlfBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(crlf, "user", 1024, Envelope);
        Assert.Equal(lfBody.PayloadBytes, crlfBody.PayloadBytes);
        Assert.Equal(lfBody.Hash, crlfBody.Hash);

        var lfReasoning = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(prompt, "user", 1024, Envelope);
        var crlfReasoning = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(crlf, "user", 1024, Envelope);
        Assert.Equal(lfReasoning.PayloadBytes, crlfReasoning.PayloadBytes);
        Assert.Equal(lfReasoning.Hash, crlfReasoning.Hash);
        _ = name;
    }

    [Fact]
    public void User_message_newlines_are_source_data_and_are_not_canonicalized()
    {
        var lf = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled("system", "line one\nline two", 1024, Envelope);
        var crlf = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled("system", "line one\r\nline two", 1024, Envelope);

        Assert.NotEqual(lf.Hash, crlf.Hash);
        using var body = JsonDocument.Parse(crlf.PayloadBytes);
        Assert.Equal("line one\r\nline two", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public void Promoted_protocol_system_prompts_match_the_frozen_qualification_hashes()
    {
        Assert.Equal("b44904b62d8807757ba288ee6f8192081f5a2a8742e91e40b1f447974601202a",
            Sha256(OccurrenceFunctionProtocolV1.SystemPrompt));
        Assert.Equal("b231e61785f1793fd94456fad682b851b28778f222a539431ec37b53ef2d2fd6",
            Sha256(HeadingAnchorProtocolV1.SystemPrompt));
        Assert.Equal("c1761a1e1c8d8f6e0502170fcea2290ed476c256f445e37b4094f23136e480c5",
            Sha256(HeadingExtentProtocolV2.SystemPrompt));
    }

    /// <summary>
    /// A CRLF copy of the F1 system prompt still reproduces the frozen P6T-F1 qualification provider bodies.
    /// </summary>
    [Fact]
    public void Crlf_f1_prompt_reproduces_frozen_p6tf1_provider_bodies()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6tf1-preflight/two-pack-function-membership-manifest.v1.json")));
        var rows = manifest.RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row.GetProperty("documentId").GetString()!, StringComparer.Ordinal);
        foreach (var (documentId, pdf) in new[]
                 {
                     ("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf"),
                     ("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf"),
                 })
        {
            var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(pdf));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
                TestRepository.Path($"eval/a99-closed-loop/pdf-canonical-source-v1/{sourceHash}.json"), documentId);
            var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
            var atoms = plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
            var owned = pack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
            var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(
                plan, pack, PdfReadOnlyCorrespondenceBuilder.Build(owned, plan.SourceAtoms));

            var crlfPrompt = prepared.Request.SystemPrompt.Replace("\n", "\r\n", StringComparison.Ordinal);
            var body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(
                crlfPrompt, prepared.Request.UserMessage, pack.MaxCompletionTokens, Envelope);

            var frozen = rows[documentId];
            Assert.Equal(frozen.GetProperty("providerRequestHash").GetString(), body.Hash);
            Assert.Equal(frozen.GetProperty("providerRequestBytes").GetInt32(), body.Bytes);
        }
    }

    [Fact]
    public void G2a_and_h2c_composers_reproduce_frozen_provider_bodies()
    {
        const string documentId = "SRC-089";
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(
            "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf"));
        var snapshotPath = TestRepository.Path($"eval/a99-closed-loop/pdf-canonical-source-v1/{sourceHash}.json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, documentId);
        var snapshot = JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(
            File.ReadAllText(snapshotPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("canonical source snapshot could not be deserialized");
        var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var ownedAliases = pack.OwnedAliases.ToArray();
        var owned = ownedAliases.Select(alias => atoms[alias]).ToArray();
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(
            plan, pack, PdfReadOnlyCorrespondenceBuilder.Build(owned, plan.SourceAtoms));
        using var f1Result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6tf1-preflight/retry-src089-result.v1.json")));
        var functions = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(
            f1, f1Result.RootElement.GetProperty("row").GetProperty("rawResponse").GetString()!);
        var occurrencesByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, StringComparer.Ordinal);
        var establishes = functions.Decisions
            .Where(value => value.Function == OccurrenceFunction.EstablishesStructure)
            .Select(value => occurrencesByAlias.Single(pair => pair.Value.Id == value.OccurrenceId).Value)
            .Select(value => (value.Id, value.Atom))
            .ToArray();
        var idsByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);

        var g2aUser = HeadingAnchorProtocolV1.ComposeUserMessage(owned, idsByAlias, establishes);
        var g2aBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(
            HeadingAnchorProtocolV1.SystemPrompt, g2aUser, pack.MaxCompletionTokens, Envelope);
        using var g2aManifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json")));
        var g2aFrozen = g2aManifest.RootElement.GetProperty("cohort").EnumerateArray()
            .Single(row => row.GetProperty("documentId").GetString() == documentId)
            .GetProperty("g2a");
        Assert.Equal(g2aFrozen.GetProperty("userMessageSha256").GetString(), Sha256(g2aUser));
        Assert.Equal(g2aFrozen.GetProperty("providerBodySha256").GetString(), g2aBody.Hash);
        Assert.Equal(g2aFrozen.GetProperty("providerBodyBytes").GetInt32(), g2aBody.Bytes);

        var anchorAlias = "L0006:S0";
        var tail = ownedAliases.Skip(Array.IndexOf(ownedAliases, anchorAlias)).ToArray();
        var h2User = HeadingExtentProtocolV2.ComposeUserMessage(
            idsByAlias[anchorAlias], tail, idsByAlias, atoms,
            snapshot.Evidence.Select(value => value.Rehydrate())
                .ToDictionary(value => value.SourceAlias, StringComparer.Ordinal));
        var h2Body = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRawReasoningEnabled(
            HeadingExtentProtocolV2.SystemPrompt, h2User, pack.MaxCompletionTokens, Envelope);
        using var h2Manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6th2c-clean-v1-v2-preflight/h2c-clean-v1-v2-preflight.v1.json")));
        var h2Frozen = h2Manifest.RootElement.GetProperty("requests").EnumerateArray()
            .Single(row => row.GetProperty("documentId").GetString() == documentId &&
                           row.GetProperty("anchor").GetString() == idsByAlias[anchorAlias]);
        using var h2Capture = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(
            "artifacts/v5-p6t-function-membership/p6th2c-clean-v2-capture-20261005/raw/SRC-089_O9.raw-capture.v1.json")));
        Assert.Equal(h2Capture.RootElement.GetProperty("userMessageSha256").GetString(), Sha256(h2User));
        Assert.Equal(h2Capture.RootElement.GetProperty("providerBodySha256").GetString(), h2Body.Hash);
        Assert.Equal(h2Frozen.GetProperty("providerBodies").GetProperty("v2").GetString(), h2Body.Hash);
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
