using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One producer of final request bytes, proven to be the only one.
/// <para>
/// A routing preflight built a real request through
/// <see cref="CanonicalSemanticEngine.CanonicalTextInferenceModel.InferAsync"/> and found it
/// did not match the hash a measurement helper had frozen for the same six calls. Same evidence,
/// same partition, different bytes: the helper embedded a contract's schema as a JSON field: the
/// engine appends it as text after a literal <c>\nSCHEMA=</c>. Two implementations of one step,
/// discovered only because something checked, at zero provider calls.
/// </para>
/// <para>
/// <see cref="CanonicalSemanticRequestComposer"/> is now that one step. This file pins its exact
/// output, proves <c>InferAsync</c> and the dry-run <c>ComposeRequests</c> path produce identical
/// bytes for the same input, for the DOCX and the PDF lane, and proves DOCX requests did not move.
/// </para>
/// </summary>
public sealed class CanonicalSemanticRequestComposerTests
{
    private const string DocxContractHash =
        "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e";

    [Fact]
    public void Hash_is_plain_sha256_of_the_exact_string()
    {
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))),
            CanonicalSemanticRequestComposer.Hash("abc"));
    }

    // ---- InferAsync and the dry-run producer agree, for every lane ------------------------------

    [Fact]
    public async Task InferAsync_and_ComposeRequests_produce_identical_bytes_for_docx()
    {
        await AssertComposerMatchesInferAsync(SemanticCoordinateContract.DocxAliasSpan);
    }

    private static async Task AssertComposerMatchesInferAsync(SemanticCoordinateContract contract)
    {
        var input = SampleInput();
        var composed = new CanonicalSemanticEngine.CanonicalTextInferenceModel(
            new UnreachableClassifier(), contract, SemanticEvidencePackingPolicies.FixedOwnedCount120)
            .ComposeRequests(input)
            .Select(segment => segment.RequestBytes)
            .ToArray();

        using var recording = new RecordingClassifier();
        var model = new CanonicalSemanticEngine.CanonicalTextInferenceModel(
            recording, contract, SemanticEvidencePackingPolicies.FixedOwnedCount120);
        await model.InferAsync(input, new SemanticContextPacket([], [], []), "composer-parity");

        Assert.NotEmpty(composed);
        Assert.Equal(composed, recording.Requests);
    }

    // ---- non-regression: no lane's bytes moved because this seam exists -------------------------

    [Fact]
    public async Task DOCX_request_bytes_are_unchanged()
    {
        var request = await SingleRequest(SemanticCoordinateContract.DocxAliasSpan);
        Assert.Equal(DocxContractHash, SemanticCoordinateContract.DocxAliasSpan.SchemaHash());
        Assert.Contains("\nSCHEMA=", request, StringComparison.Ordinal);
        Assert.EndsWith(
            System.Text.Json.JsonSerializer.Serialize(SemanticCoordinateContract.DocxAliasSpan.Schema()),
            request, StringComparison.Ordinal);
    }

    private static async Task<string> SingleRequest(SemanticCoordinateContract contract)
    {
        using var recording = new RecordingClassifier();
        var model = new CanonicalSemanticEngine.CanonicalTextInferenceModel(
            recording, contract, SemanticEvidencePackingPolicies.FixedOwnedCount120);
        await model.InferAsync(SampleInput(), new SemanticContextPacket([], [], []), "non-regression");
        return Assert.Single(recording.Requests);
    }

    // ---- discovery: exactly one final-request producer -------------------------------------------

    [Fact]
    public void The_structured_PDF_authority_builder_no_longer_serializes_requests()
    {
        // The authority carries atoms and their evidence only: no request bytes, and no partition of
        // its own - packing belongs to the lane's packing policy, requests to the one composer.
        var fieldNames = typeof(DocumentSourceSnapshot).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("RequestPayload", fieldNames);
        Assert.DoesNotContain("RequestSha256", fieldNames);
        Assert.DoesNotContain("Packs", fieldNames);
        Assert.DoesNotContain("CallPlanHash", fieldNames);
        Assert.Contains("Atoms", fieldNames);
        Assert.Contains("Evidence", fieldNames);
    }

    private static CanonicalSemanticTextInferenceInput SampleInput()
    {
        var evidence = new[]
        {
            new CanonicalSemanticSourceEvidence(
                "S0001", "p1", 1, "Heading One", "document_body",
                ["test"], new { }, new { }, [], [], [], []),
            new CanonicalSemanticSourceEvidence(
                "S0002", "p2", 2, "Heading Two", "document_body",
                ["test"], new { }, new { }, [], [], [], []),
        };
        return new CanonicalSemanticTextInferenceInput(evidence);
    }

    private sealed class UnreachableClassifier : IInferenceTransport
    {
        public string ModelName => throw new InvalidOperationException();
        public int ContextSize => throw new InvalidOperationException();
        public string RuntimeDescription => throw new InvalidOperationException();
        public int SharedPrefixTokens => throw new InvalidOperationException();
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            throw new InvalidOperationException("PROVIDER_CALLS must remain 0: composing a request must never transport.");
        public void Dispose() { }
    }

    private sealed class RecordingClassifier : IInferenceTransport
    {
        public List<string> Requests { get; } = [];
        public string ModelName => "recording";
        public int ContextSize => 8192;
        public string RuntimeDescription => "recording";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Requests.Add(userMessage);
            return Task.FromResult("{\"headings\":[]}");
        }
        public void Dispose() { }
    }
}
