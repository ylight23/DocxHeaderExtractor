using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freeze one H2 continuation request from SRC-041's already-frozen F1/G2A authorities.</summary>
public sealed class V5P6TEH2ContinuationPreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership/p6te-src041-h2-challenge";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string SourcePdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf";
    private const string F1Root = "artifacts/v5-p6t-function-membership/p6te-src041-e-challenge";
    private const string Primary = "O4";
    private const string PrimaryAlias = "L3526:S0";
    private const string ContinuationAlias = "L3527:S0";

    [Fact]
    public void P6TE_H2_continuation_request_is_frozen_from_raw_G2A_anchor_without_Gold()
    {
        var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(SourcePdf));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json"), "SRC-041");
        var pack = plan.Packs.Single(value => value.OwnedAliases.Contains(PrimaryAlias, StringComparer.Ordinal));
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        Assert.Equal(96, f1.Request.Occurrences.Count);
        Assert.Equal(Primary, Assert.Single(f1.Request.Occurrences, value => value.Atom.Alias == PrimaryAlias).Id);
        Assert.Equal("O5", Assert.Single(f1.Request.Occurrences, value => value.Atom.Alias == ContinuationAlias).Id);

        using var g2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/g2a.raw-capture.v1.json")));
        Assert.Equal("G2A_TOTAL_LEDGER_ACCEPTED", g2.RootElement.GetProperty("classification").GetString());
        var g2Anchor = g2.RootElement.GetProperty("parsed").GetProperty("decisions").EnumerateArray()
            .Single(value => value.GetProperty("alias").GetString() == PrimaryAlias);
        Assert.Equal("HAS_STRUCTURAL_EXTENT", g2Anchor.GetProperty("anchor").GetString());
        var owned = pack.OwnedAliases;
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var byAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var index = Array.IndexOf(owned.ToArray(), PrimaryAlias);
        Assert.True(index >= 0 && index + 3 < owned.Count);
        var aliases = owned.Skip(index).Take(4).ToArray();
        var occurrences = aliases.Select(alias => new { occurrence = byAlias[alias], page = atoms[alias].Page, text = atoms[alias].Text, selectable = false }).ToArray();
        var edges = Enumerable.Range(0, 3).Select(i => new { anchor = Primary, left = byAlias[aliases[i]], right = byAlias[aliases[i + 1]], ordinal = i }).ToArray();
        const string systemPrompt = """
            Judge only source-order continuation boundaries for an already-qualified structural anchor. For each issued edge, decide whether right continues the same local structural unit begun at anchor, or whether the unit stops before right.

            CONTINUES_STRUCTURAL_UNIT means left and right belong to the same exact local structural unit. STOPS_STRUCTURAL_UNIT means the unit begun at anchor ends before right. The edges are consecutive source occurrences; do not use similarity, hierarchy, candidates, spans, locators, or hypothetical text not issued in the request. Once an anchor stops, every later issued edge for that anchor must also be STOPS_STRUCTURAL_UNIT.

            Return exactly one JSON object: {"decisions":[{"anchor":"O4","left":"O4","right":"O5","boundary":"CONTINUES_STRUCTURAL_UNIT"}]}. Return exactly one decision for every issued edge. Echo only issued O# values. Do not output candidate IDs, source text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
            """;
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = "v5-function-conditioned-continuation-boundary-1",
            anchors = new[] { new { anchor = Primary, occurrences, edges } },
        });
        var request = new V5FreeHeadingRequestV1("v5-function-conditioned-continuation-boundary-1", systemPrompt, user,
            Hash(user), Encoding.UTF8.GetByteCount(systemPrompt), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
        using var f1Raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{F1Root}/f1.raw-capture.v1.json")));
        FreezeArtifact.AssertJson(Root, "h2-request-manifest.v1.json", new
        {
            schemaVersion = "v5-p6te-src041-h2-preflight-v1", status = "PREPARED_NOT_AUTHORIZED",
            authority = new
            {
                sourceSha256 = plan.SourceSha256,
                sourceUniverseSha256 = plan.SourceUniverseSha256,
                packId = pack.PackId,
                primary = new { occurrence = Primary, alias = PrimaryAlias, anchorDecision = "HAS_STRUCTURAL_EXTENT" },
                target = new { occurrence = "O5", alias = ContinuationAlias },
                f1RawCaptureSha256 = Hash(File.ReadAllText(TestRepository.Path($"{F1Root}/f1.raw-capture.v1.json"))),
                g2aRawCaptureSha256 = Hash(File.ReadAllText(TestRepository.Path($"{F1Root}/g2a.raw-capture.v1.json"))),
            },
            issuedEdges = edges,
            request = new { protocolVersion = request.ProtocolVersion, systemPromptSha256 = Hash(systemPrompt), userMessageSha256 = request.UserMessageSha256, providerBodySha256 = body.Hash, providerBodyBytes = body.Bytes },
            execution = new { providerCalls = 0, maximumProviderCalls = 1, retry = 0, repair = false, fallback = false, goldRead = false, goldMutation = "NONE", runtimeChanged = false },
        });
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
