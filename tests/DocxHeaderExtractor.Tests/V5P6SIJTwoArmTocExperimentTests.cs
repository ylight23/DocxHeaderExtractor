using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free controls for the exact one-dimension P6S-I and P6S-J treatments.</summary>
public sealed class V5P6SIJTwoArmTocExperimentTests
{
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sij-two-arm-experiment-v2";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";

    [Fact]
    public void P6SI_and_P6SJ_frozen_manifest_changes_only_the_authorized_dimension_per_arm()
    {
        var manifestPath = TestRepository.Path($"{Root}/execution-manifest.v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal(2, root.GetProperty("execution").GetProperty("maximumProviderCalls").GetInt32());
        var arms = root.GetProperty("arms").EnumerateArray().ToDictionary(item => item.GetProperty("Id").GetString()!, StringComparer.Ordinal);
        Assert.Equal(2, arms.Count);
        Assert.False(arms["P6S-I"].TryGetProperty("targetCandidateIds", out _));
        Assert.False(arms["P6S-J"].TryGetProperty("targetCandidateIds", out _));
        Assert.False(arms["P6S-I"].TryGetProperty("targetDiagnosisSha256", out _));
        Assert.False(arms["P6S-J"].TryGetProperty("targetDiagnosisSha256", out _));

        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(SourcePdfCorpus.Src095));
        var snapshotPath = TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, "SRC-095");
        var packI = plan.Packs.Single(item => item.PackId == arms["P6S-I"].GetProperty("PackId").GetString());
        var packJ = plan.Packs.Single(item => item.PackId == arms["P6S-J"].GetProperty("PackId").GetString());

        Assert.Equal(packI.ProviderRequestHash, arms["P6S-I"].GetProperty("baselineProviderRequestHash").GetString());
        Assert.Equal(packJ.ProviderRequestHash, arms["P6S-J"].GetProperty("baselineProviderRequestHash").GetString());
        Assert.Equal(Hash(packI.Request.SystemPrompt), arms["P6S-I"].GetProperty("systemPromptSha256").GetString());
        Assert.Equal(packJ.Request.UserMessageSha256, arms["P6S-J"].GetProperty("userMessageSha256").GetString());

        var prepared = Path.Combine(Path.GetDirectoryName(manifestPath)!, "prepared-request-bodies.v1.json");
        using var bodies = JsonDocument.Parse(File.ReadAllText(prepared));
        var treatmentI = bodies.RootElement.GetProperty("arms").EnumerateArray().Single(item => item.GetProperty("Id").GetString() == "P6S-I");
        var treatmentJ = bodies.RootElement.GetProperty("arms").EnumerateArray().Single(item => item.GetProperty("Id").GetString() == "P6S-J");
        using var baselineI = JsonDocument.Parse(packI.Request.UserMessage);
        using var treatedI = JsonDocument.Parse(treatmentI.GetProperty("userMessage").GetString()!);
        Assert.Equal(baselineI.RootElement.GetProperty("candidates").GetRawText(), treatedI.RootElement.GetProperty("candidates").GetRawText());
        Assert.Equal(baselineI.RootElement.GetProperty("relations").GetRawText(), treatedI.RootElement.GetProperty("relations").GetRawText());
        Assert.Equal(Hash(packI.Request.SystemPrompt), arms["P6S-I"].GetProperty("systemPromptSha256").GetString());
        var contextI = treatedI.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray().Select(item => item.GetProperty("text").GetString()!).ToArray();
        Assert.Contains(contextI, text => V5CandidateUniverseV1.Normalize(text).Contains("TABLE OF CONTENTS", StringComparison.Ordinal));
        Assert.Equal(176, contextI.Length); // 64 before + 96 owned + 16 after for this frozen pack.
        Assert.All(treatedI.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray(), item =>
            Assert.Equal(new[] { "page", "text" }, item.EnumerateObject().Select(property => property.Name).ToArray()));
        Assert.Equal("contextOnlyEvidence source-order window only; system prompt, candidates, and relations unchanged",
            arms["P6S-I"].GetProperty("changedDimension").GetString());
        Assert.Equal(arms["P6S-I"].GetProperty("treatmentProviderRequestHash").GetString(), treatmentI.GetProperty("bodySha256").GetString());
        Assert.Equal(arms["P6S-J"].GetProperty("treatmentProviderRequestHash").GetString(), treatmentJ.GetProperty("bodySha256").GetString());
        Assert.Equal(treatmentI.GetProperty("bodySha256").GetString(), Hash(Convert.FromBase64String(treatmentI.GetProperty("bodyBase64").GetString()!)));
        Assert.Equal(treatmentJ.GetProperty("bodySha256").GetString(), Hash(Convert.FromBase64String(treatmentJ.GetProperty("bodyBase64").GetString()!)));
        Assert.Equal(Hash(packI.Request.SystemPrompt), Hash(treatmentI.GetProperty("systemPrompt").GetString()!));

        Assert.NotEqual(packJ.Request.SystemPrompt, treatmentJ.GetProperty("systemPrompt").GetString());
        Assert.Contains("opens or continues structure at its own occurrence location", treatmentJ.GetProperty("systemPrompt").GetString(), StringComparison.Ordinal);
        Assert.Equal(packJ.Request.UserMessageSha256, arms["P6S-J"].GetProperty("userMessageSha256").GetString());
        Assert.Equal("system prompt semantic rule only; user payload, candidates, relations, and context unchanged",
            arms["P6S-J"].GetProperty("changedDimension").GetString());
        Assert.Equal(packI.Universe.Candidates.Count, arms["P6S-I"].GetProperty("candidateCount").GetInt32());
        Assert.Equal(packJ.Universe.Candidates.Count, arms["P6S-J"].GetProperty("candidateCount").GetInt32());
    }

    [Fact]
    public void P6SIJ_raw_and_offline_artifacts_freeze_two_no_retry_observations_without_claiming_cohort_F1()
    {
        var dir = TestRepository.Path(Root);
        using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "raw-result.v1.json")));
        using var score = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "offline-score.v1.json")));
        Assert.Equal(2, raw.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, raw.RootElement.GetProperty("retry").GetInt32());
        Assert.False(raw.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal(2, raw.RootElement.GetProperty("rows").GetArrayLength());
        foreach (var row in raw.RootElement.GetProperty("rows").EnumerateArray())
        {
            Assert.True(row.GetProperty("transportAccepted").GetBoolean());
            Assert.Equal("stop", row.GetProperty("finishReason").GetString());
            Assert.Equal(0, row.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(row.GetProperty("rawResponse").GetString()!), row.GetProperty("rawResponseSha256").GetString());
        }
        Assert.Contains("targeted diagnostic", score.RootElement.GetProperty("measurement").GetString(), StringComparison.Ordinal);
        var scored = score.RootElement.GetProperty("arms").EnumerateArray().ToDictionary(item => item.GetProperty("arm").GetString()!, StringComparer.Ordinal);
        Assert.Equal(34, scored["P6S-I"].GetProperty("targets").GetInt32());
        Assert.Equal(0, scored["P6S-I"].GetProperty("headingToRepresentation").GetInt32());
        Assert.Equal(0, scored["P6S-I"].GetProperty("parserQuarantine").GetInt32());
        Assert.Equal(53, scored["P6S-J"].GetProperty("targets").GetInt32());
        Assert.Equal(0, scored["P6S-J"].GetProperty("headingToRepresentation").GetInt32());
        Assert.Equal(53, scored["P6S-J"].GetProperty("headingToNoProposal").GetInt32());
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
