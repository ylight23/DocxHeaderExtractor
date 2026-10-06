using System.Text.Json;
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
        { "F1", V5TotalOccurrenceFunctionProtocolF1.SystemPrompt },
        { "G2A", PdfFunctionConditionedHeadingAuthorityAdapter.G2APrompt },
        { "H2-C", PdfFunctionConditionedHeadingAuthorityAdapter.BoundaryPromptV2 },
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
}
