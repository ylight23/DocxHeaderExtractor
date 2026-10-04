using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freeze the first F1 request for the cross-PDF candidate; no Gold is in model input.</summary>
public sealed class V5P6TEChallengeFunctionMembershipPreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership/p6te-doc0256-e-challenge";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string GoldPath = "eval/a99-closed-loop/gold-current/documents/DOC-0256.gold.v1.json";
    private const string GoldRegistryPath = "eval/a99-closed-loop/gold-current/registry.v1.json";
    private const string ChallengeContinuationAlias = "L0001:S0";

    [Fact]
    public void P6TE_DOC0256_F1_request_is_frozen_before_provider_and_excludes_Gold()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldRegistryPath)));
        var registryEntry = registry.RootElement.GetProperty("authorities").EnumerateArray()
            .Single(value => value.GetProperty("authorityId").GetString() == "DOC-0256");
        Assert.True(registryEntry.GetProperty("occurrenceEvaluable").GetBoolean());
        var goldSha256 = registryEntry.GetProperty("goldSha256").GetString()!;
        var goldClaims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().ToArray();
        var challengeClaim = Assert.Single(goldClaims, claim => claim.GetProperty("sourceParts").EnumerateArray()
            .Any(part => part.GetProperty("sourceAlias").GetString() == ChallengeContinuationAlias));
        Assert.Equal(2, challengeClaim.GetProperty("sourceParts").GetArrayLength());

        var sourcePath = gold.RootElement.GetProperty("source").GetProperty("sourcePath").GetString()!;
        var sourceSha256 = CanonicalSemanticSourceHash.Compute(TestRepository.Path(sourcePath));
        Assert.Equal(gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString(), sourceSha256);
        var snapshotPath = TestRepository.Path($"{SnapshotRoot}/{sourceSha256}.json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, "DOC-0256");
        Assert.Equal(sourceSha256, plan.SourceSha256);
        Assert.Equal(546, plan.SourceOccurrenceTotal);

        var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        Assert.Equal(96, prepared.Request.Occurrences.Count);
        var target = Assert.Single(prepared.Request.Occurrences, occurrence => occurrence.Atom.Alias == ChallengeContinuationAlias);

        FreezeArtifact.AssertJson(Root, "f1-request-manifest.v1.json", new
        {
            schemaVersion = "v5-p6te-doc0256-f1-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            purpose = "F1 function-membership authority for a pre-registered strict-Gold multipart continuation; G2A will only be prepared if this exact continuation receives ESTABLISHES_STRUCTURE.",
            challenge = new
            {
                sourceAuthority = "DOC-0256 canonical strict Gold; selection registered before provider call",
                goldSha256,
                sourceSha256,
                sourceAliasUniverseSha256 = plan.SourceUniverseSha256,
                headingParts = challengeClaim.GetProperty("sourceParts").EnumerateArray()
                    .Select(part => part.GetProperty("sourceAlias").GetString()).ToArray(),
                continuationAlias = ChallengeContinuationAlias,
                continuationOccurrence = target.Id,
            },
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                temperature = 0,
                reasoning = new { enabled = true, effort = "OMITTED" },
                contract = "P6T-F1 TOTAL FUNCTION MEMBERSHIP",
            },
            execution = new { providerCalls = 0, maximumProviderCalls = 2, retries = 0, repair = false, fallback = false, goldReadDuringCapture = false, goldMutation = "NONE", runtimeChanged = false },
            sourcePack = new
            {
                documentId = "DOC-0256",
                packId = pack.PackId,
                sourceOccurrences = plan.SourceOccurrenceTotal,
                ownedAliases = pack.OwnedAliases,
                visibleAliases = pack.VisibleAliases,
                issuedOccurrences = prepared.Request.Occurrences.Count,
            },
            request = new
            {
                protocolVersion = prepared.Request.ProtocolVersion,
                systemPromptSha256 = Hash(prepared.Request.SystemPrompt),
                userMessageSha256 = prepared.Request.UserMessageSha256,
                providerBodySha256 = prepared.ProviderRequestHash,
                providerBodyBytes = prepared.ProviderRequestBytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
            },
        });
    }

    [Fact]
    public void P6TE_DOC0252_F1_request_is_frozen_before_provider_and_excludes_Gold()
    {
        const string documentId = "DOC-0252";
        const string targetAlias = "L0514:S0";
        const string goldPath = "eval/a99-closed-loop/gold-current/documents/DOC-0252.gold.v1.json";
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(goldPath)));
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldRegistryPath)));
        var registryEntry = registry.RootElement.GetProperty("authorities").EnumerateArray()
            .Single(value => value.GetProperty("authorityId").GetString() == documentId);
        Assert.True(registryEntry.GetProperty("occurrenceEvaluable").GetBoolean());
        var goldSha256 = registryEntry.GetProperty("goldSha256").GetString()!;
        var goldClaims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().ToArray();
        var challengeClaim = Assert.Single(goldClaims, claim => claim.GetProperty("sourceParts").EnumerateArray()
            .Any(part => part.GetProperty("sourceAlias").GetString() == targetAlias));
        Assert.Equal(2, challengeClaim.GetProperty("sourceParts").GetArrayLength());

        var sourcePath = gold.RootElement.GetProperty("source").GetProperty("sourcePath").GetString()!;
        var sourceSha256 = CanonicalSemanticSourceHash.Compute(TestRepository.Path(sourcePath));
        Assert.Equal(gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString(), sourceSha256);
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            TestRepository.Path($"{SnapshotRoot}/{sourceSha256}.json"), documentId);
        Assert.Equal(650, plan.SourceOccurrenceTotal);
        var pack = plan.Packs.Single(value => value.OwnedAliases.Contains(targetAlias, StringComparer.Ordinal));
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        Assert.Equal(96, prepared.Request.Occurrences.Count);
        var target = Assert.Single(prepared.Request.Occurrences, occurrence => occurrence.Atom.Alias == targetAlias);

        FreezeArtifact.AssertJson("artifacts/v5-p6t-function-membership/p6te-doc0252-e-challenge", "f1-request-manifest.v1.json", new
        {
            schemaVersion = "v5-p6te-doc0252-f1-manifest-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            purpose = "F1 function-membership authority for a pre-registered strict-Gold multipart continuation; G2A is composed only from accepted F1 ESTABLISHES_STRUCTURE decisions.",
            challenge = new
            {
                sourceAuthority = "DOC-0252 canonical strict Gold; selection registered before provider call",
                goldSha256, sourceSha256, sourceAliasUniverseSha256 = plan.SourceUniverseSha256,
                headingParts = challengeClaim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()).ToArray(),
                continuationAlias = targetAlias, continuationOccurrence = target.Id,
            },
            treatment = new { model = "qwen/qwen3.7-flash", provider = "alibaba", temperature = 0, reasoning = new { enabled = true, effort = "OMITTED" }, contract = "P6T-F1 TOTAL FUNCTION MEMBERSHIP" },
            execution = new { providerCalls = 0, maximumProviderCalls = 2, retries = 0, repair = false, fallback = false, goldReadDuringCapture = false, goldMutation = "NONE", runtimeChanged = false },
            sourcePack = new { documentId, packId = pack.PackId, sourceOccurrences = plan.SourceOccurrenceTotal, ownedAliases = pack.OwnedAliases, visibleAliases = pack.VisibleAliases, issuedOccurrences = prepared.Request.Occurrences.Count },
            request = new { protocolVersion = prepared.Request.ProtocolVersion, systemPromptSha256 = Hash(prepared.Request.SystemPrompt), userMessageSha256 = prepared.Request.UserMessageSha256, providerBodySha256 = prepared.ProviderRequestHash, providerBodyBytes = prepared.ProviderRequestBytes, maxCompletionTokens = pack.MaxCompletionTokens },
        });
    }

    [Fact]
    public void P6TE_SRC041_F1_request_is_frozen_before_provider_and_excludes_Gold()
    {
        const string documentId = "SRC-041";
        const string targetAlias = "L3527:S0";
        const string goldPath = "eval/a99-closed-loop/gold-current/documents/SRC-041.gold.v1.json";
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(goldPath)));
        using var registry = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldRegistryPath)));
        var registryEntry = registry.RootElement.GetProperty("authorities").EnumerateArray().Single(value => value.GetProperty("authorityId").GetString() == documentId);
        Assert.True(registryEntry.GetProperty("occurrenceEvaluable").GetBoolean());
        var goldSha256 = registryEntry.GetProperty("goldSha256").GetString()!;
        var goldClaims = gold.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().ToArray();
        var challengeClaim = Assert.Single(goldClaims, claim => claim.GetProperty("sourceParts").EnumerateArray().Any(part => part.GetProperty("sourceAlias").GetString() == targetAlias));
        Assert.Equal(2, challengeClaim.GetProperty("sourceParts").GetArrayLength());
        var sourcePath = gold.RootElement.GetProperty("source").GetProperty("sourcePath").GetString()!;
        var sourceSha256 = CanonicalSemanticSourceHash.Compute(TestRepository.Path(sourcePath));
        Assert.Equal(gold.RootElement.GetProperty("source").GetProperty("sourceSha256").GetString(), sourceSha256);
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceSha256}.json"), documentId);
        Assert.Equal(10_443, plan.SourceOccurrenceTotal);
        var pack = plan.Packs.Single(value => value.OwnedAliases.Contains(targetAlias, StringComparer.Ordinal));
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        Assert.Equal(96, prepared.Request.Occurrences.Count);
        var target = Assert.Single(prepared.Request.Occurrences, occurrence => occurrence.Atom.Alias == targetAlias);
        FreezeArtifact.AssertJson("artifacts/v5-p6t-function-membership/p6te-src041-e-challenge", "f1-request-manifest.v1.json", new
        {
            schemaVersion = "v5-p6te-src041-f1-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            purpose = "F1 function-membership authority for a pre-registered strict-Gold multipart continuation candidate; G2A is composed only from accepted F1 ESTABLISHES_STRUCTURE decisions.",
            challenge = new { sourceAuthority = "SRC-041 canonical strict Gold; continuation is independently heading-shaped by source presentation, not yet an E false anchor", goldSha256, sourceSha256, sourceAliasUniverseSha256 = plan.SourceUniverseSha256,
                headingParts = challengeClaim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()).ToArray(), continuationAlias = targetAlias, continuationOccurrence = target.Id },
            treatment = new { model = "qwen/qwen3.7-flash", provider = "alibaba", temperature = 0, reasoning = new { enabled = true, effort = "OMITTED" }, contract = "P6T-F1 TOTAL FUNCTION MEMBERSHIP" },
            execution = new { providerCalls = 0, maximumProviderCalls = 2, retries = 0, repair = false, fallback = false, goldReadDuringCapture = false, goldMutation = "NONE", runtimeChanged = false },
            sourcePack = new { documentId, packId = pack.PackId, sourceOccurrences = plan.SourceOccurrenceTotal, ownedAliases = pack.OwnedAliases, visibleAliases = pack.VisibleAliases, issuedOccurrences = prepared.Request.Occurrences.Count },
            request = new { protocolVersion = prepared.Request.ProtocolVersion, systemPromptSha256 = Hash(prepared.Request.SystemPrompt), userMessageSha256 = prepared.Request.UserMessageSha256, providerBodySha256 = prepared.ProviderRequestHash, providerBodyBytes = prepared.ProviderRequestBytes, maxCompletionTokens = pack.MaxCompletionTokens },
        });
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
