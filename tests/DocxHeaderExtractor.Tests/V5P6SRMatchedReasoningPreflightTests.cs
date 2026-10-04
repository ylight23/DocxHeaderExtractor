using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free proof that P6S-R differs from frozen P6S-D only in the reasoning carrier object.</summary>
public sealed class V5P6SRMatchedReasoningPreflightTests
{
    private const string Root = "artifacts/v5-p6s-candidate-authority/p6sr-matched-reasoning-full31";
    private const string ControlRoot = "artifacts/v5-p6s-candidate-authority/p6sd-full31";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";

    [Fact]
    public void P6SR_freezes_a_31_pack_matched_reasoning_control_with_no_other_body_delta()
    {
        var manifestPath = TestRepository.Path($"{Root}/execution-manifest.v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal("PREPARED_NOT_AUTHORIZED", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("providerCalls").GetInt32());
        Assert.False(root.GetProperty("goldRead").GetBoolean());
        Assert.Equal("UNCHANGED", root.GetProperty("sharedRuntime").GetString());
        Assert.Equal(31, root.GetProperty("execution").GetProperty("maximumProviderCalls").GetInt32());
        Assert.Equal(0, root.GetProperty("execution").GetProperty("retry").GetInt32());
        Assert.True(root.GetProperty("treatment").GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(root.GetProperty("treatment").GetProperty("reasoning").TryGetProperty("effort", out _));
        Assert.Equal("none", root.GetProperty("control").GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(31, root.GetProperty("rows").GetArrayLength());
        Assert.Equal(2884, root.GetProperty("rows").EnumerateArray().Sum(row => row.GetProperty("ownedAtoms").GetInt32()));

        using var controlCapture = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{ControlRoot}/result.v1.json")));
        foreach (var document in new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) })
        {
            var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(document.Item2));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sha}.json"), document.Item1);
            foreach (var pack in plan.Packs)
            {
                var row = root.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("DocumentId").GetString() == document.Item1 && value.GetProperty("PackId").GetString() == pack.PackId);
                var captured = controlCapture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == document.Item1 && value.GetProperty("PackId").GetString() == pack.PackId);
                var control = PdfCandidateAuthorityQualificationAdapter.BuildProviderBody(pack.Request, pack.MaxCompletionTokens);
                var treatment = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(pack.Request, pack.MaxCompletionTokens);
                Assert.Equal(control.Hash, captured.GetProperty("providerRequestHash").GetString());
                Assert.Equal(control.Hash, row.GetProperty("controlProviderRequestHash").GetString());
                Assert.Equal(treatment.Hash, row.GetProperty("treatmentProviderRequestHash").GetString());
                Assert.Equal(pack.Request.UserMessageSha256, row.GetProperty("semanticRequestHash").GetString());
                Assert.Equal(pack.Universe.Fingerprint, row.GetProperty("candidateUniverseFingerprint").GetString());

                var controlJson = JsonNode.Parse(control.PayloadBytes)!.AsObject();
                var treatmentJson = JsonNode.Parse(treatment.PayloadBytes)!.AsObject();
                Assert.Equal("none", controlJson["reasoning"]!["effort"]!.GetValue<string>());
                Assert.False(controlJson["reasoning"]!.AsObject().ContainsKey("enabled"));
                Assert.True(treatmentJson["reasoning"]!["enabled"]!.GetValue<bool>());
                Assert.False(treatmentJson["reasoning"]!.AsObject().ContainsKey("effort"));
                controlJson.Remove("reasoning"); treatmentJson.Remove("reasoning");
                Assert.True(JsonNode.DeepEquals(controlJson, treatmentJson), $"non-reasoning delta: {document.Item1}/{pack.PackId}");
            }
        }
    }
}
