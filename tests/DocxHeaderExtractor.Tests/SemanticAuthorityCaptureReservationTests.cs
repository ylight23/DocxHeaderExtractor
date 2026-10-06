using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticAuthorityCaptureReservationTests
{
    private const string SourceHash =
        "a005f25e3bb9754cd6c8c7000682d00eb68238fb8937d3475fe807ffbbd94b61";
    private const string UniverseHash =
        "2a953bf785ed1af00bc908ff9e5d6a1d988b04c0d980ecd95336bc5a9702f46f";
    private const string PromptHash =
        "6340d1daf507a3d2bf5ce6fbef2b5d7b61735c0e0621a62f8e885ad8b2d8a66e";
    private const string Profile = "STRUCTURED_SOURCE_PARTS_V2";
    private const string Packing = "COHERENT_REGION_SEGMENTATION_V1";
    private const string DocumentId = "DOC-0252";

    [Fact]
    public void Existing_baseline_capture_is_rejected_before_fake_classifier_invocation()
    {
        var directory = TestRepository.Path(
            "eval/a99-closed-loop/structured-v2-target-baseline-v1/DOC-0252/r1");
        Assert.NotEmpty(Directory.GetFiles(directory, "*transport-capture.v1.json"));

        var classifierCalls = 0;
        var request = Request(directory, "r1");
        var error = Assert.Throws<InvalidOperationException>(() =>
            request.Reserve(
                "072_ICP_TAG_Minutes_Mar_2025",
                SourceHash,
                "565bdc87749a1ce1246238cacd1eb7d550a19939dc9e42d23e76ab3a46a8b0ea",
                [CallIdentity()]));

        Assert.Equal("TRANSPORT_CAPTURE_ALREADY_EXISTS", error.Message);
        Assert.Equal(0, classifierCalls);
    }

    [Fact]
    public async Task Concurrent_duplicate_attempts_have_one_reservation_and_no_duplicate_transport()
    {
        var directory = TempDirectory();
        try
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(ignored => Task.Run(() =>
            {
                try
                {
                    _ = Request(directory, "concurrent-r1").Reserve(
                        DocumentId, SourceHash, "v2-contract", [CallIdentity()]);
                    return (Won: true, Error: (string?)null);
                }
                catch (InvalidOperationException error)
                {
                    return (Won: false, Error: error.Message);
                }
            })));

            Assert.Single(attempts, attempt => attempt.Won);
            Assert.Single(attempts, attempt => attempt.Error == "TRANSPORT_CAPTURE_SLOT_RESERVED");
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public void Fresh_reservation_allows_one_fake_call_and_completes_replay_capture()
    {
        var directory = TempDirectory();
        try
        {
            var request = Request(directory, "fresh-r1").Reserve(
                DocumentId, SourceHash, "v2-contract", [CallIdentity()]);
            var classifierCalls = 0;
            const string raw = "{\"headings\":[]}";
            classifierCalls++;

            var bundle = SemanticAuthorityReplayBundleFactory.Create(
                DocumentId, "PDF", SourceHash, UniverseHash,
                [new SemanticSourceAlias("S0001", "p1", 1, "Heading", new(0, 7))],
                "qwen/qwen3.7-flash", "OpenRouter", PromptHash,
                SemanticAuthorityReplayHashing.RawModelResponseHash([raw]), []);
            var result = request.Persist(bundle, [TransportCall(raw)]);

            Assert.Equal(1, classifierCalls);
            Assert.True(result.Persisted);
            Assert.Equal(3, Directory.GetFiles(directory, "*.json").Length);
            var slot = JsonSerializer.Deserialize<SemanticAuthorityCaptureReservation>(
                File.ReadAllText(Path.Combine(directory,
                    SemanticAuthorityCaptureReservationSchema.SlotFileName)))!;
            Assert.Equal(SemanticAuthorityCaptureReservationSchema.TransportCaptureComplete, slot.Status);
            Assert.Equal(SemanticAuthorityTransportCaptureSchema.TransportCaptureComplete,
                JsonSerializer.Deserialize<SemanticAuthorityTransportCapture>(
                    File.ReadAllText(result.TransportArtifactPath!))!.TransportCaptureStatus);
        }
        finally
        {
            Delete(directory);
        }
    }

    private static SemanticAuthorityReplayCaptureRequest Request(string directory, string repeat) =>
        new(
            new SemanticAuthorityCaptureMetadata(
                "PDF", UniverseHash, "qwen/qwen3.7-flash", "OpenRouter", PromptHash,
                Profile: Profile, PackingPolicy: Packing, RepeatIdentity: repeat,
                RunId: $"STRUCTURED_V2_TARGET_BASELINE-{repeat}"),
            directory);

    private static SemanticAuthorityCaptureCallIdentity CallIdentity()
    {
        const string request = "{\"systemPrompt\":\"p\",\"userMessage\":\"u\"}";
        return new(1, "semantic", "PACK_001", SemanticAuthorityTransportCall.Sha256Utf8(request),
            Encoding.UTF8.GetByteCount(request));
    }

    private static SemanticAuthorityTransportCall TransportCall(string raw) =>
        SemanticAuthorityTransportCall.Create(
            1, "semantic", "PACK_001", "{\"systemPrompt\":\"p\",\"userMessage\":\"u\"}", raw);

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "dhx-capture-slot-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
