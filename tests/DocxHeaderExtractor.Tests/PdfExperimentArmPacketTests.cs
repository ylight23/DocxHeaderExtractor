using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The three arms, frozen and compared before any of them is run against a provider.
/// <para>
/// B0 is what ships. B1 changes the context payload only. B2 changes the prompt only. Nothing else
/// may differ between them, because a difference anywhere else would be a second variable and no
/// later measurement could be attributed to the intervention it was supposed to test.
/// </para>
/// <para>
/// There is deliberately no combined arm. If I7 and I8 were applied together and the result
/// improved, the improvement would belong to neither.
/// </para>
/// </summary>
public sealed class PdfExperimentArmPacketTests
{
    private const string Pdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    [Fact]
    public async Task B1_changes_the_context_payload_and_nothing_else()
    {
        var baseline = await CaptureAsync(CanonicalSemanticExperiment.Baseline);
        var arm = await CaptureAsync(CanonicalSemanticExperiment.StructuralAncestorsOnly);

        AssertSameSourceAndOwnership(baseline, arm);
        Assert.All(Enumerable.Range(0, baseline.Count), index =>
            Assert.Equal(baseline[index].SystemPrompt, arm[index].SystemPrompt));
        // The one permitted difference, and it must actually be present.
        Assert.NotEqual(
            baseline.Select(request => Sha256(request.UserMessage)),
            arm.Select(request => Sha256(request.UserMessage)));
        Assert.Contains(arm, request => OpenContextOf(request).Length > 0);
    }

    [Fact]
    public async Task B2_changes_the_prompt_and_nothing_else()
    {
        var baseline = await CaptureAsync(CanonicalSemanticExperiment.Baseline);
        var arm = await CaptureAsync(CanonicalSemanticExperiment.PartialSpanOnly);

        AssertSameSourceAndOwnership(baseline, arm);
        // The payload is the source and the segmentation; neither is what I8 changes.
        Assert.Equal(
            baseline.Select(request => Sha256(request.UserMessage)),
            arm.Select(request => Sha256(request.UserMessage)));
        Assert.NotEqual(baseline[0].SystemPrompt, arm[0].SystemPrompt);
        Assert.StartsWith(baseline[0].SystemPrompt, arm[0].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B1_carries_open_structure_only_where_structure_is_open()
    {
        // Segment 0 begins at the document, so nothing is open yet. A later segment that begins
        // inside a container should say so - that is the whole intervention.
        var arm = await CaptureAsync(CanonicalSemanticExperiment.StructuralAncestorsOnly);

        Assert.Empty(OpenContextOf(arm[0]));
        Assert.Contains(arm.Skip(1), request => OpenContextOf(request).Length > 0);
    }

    [Fact]
    public async Task B1_reports_open_structure_as_context_never_as_a_parent_claim()
    {
        // It must read as "this was already open", not as an assignment. If the harness started
        // naming parents here it would be deciding hierarchy, which belongs to the model.
        var arm = await CaptureAsync(CanonicalSemanticExperiment.StructuralAncestorsOnly);

        var entries = arm.SelectMany(OpenContextOf).ToArray();
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("parent-node:", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("level", entry, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Each_arm_is_deterministic_on_its_own()
    {
        foreach (var experiment in new[]
        {
            CanonicalSemanticExperiment.Baseline,
            CanonicalSemanticExperiment.StructuralAncestorsOnly,
            CanonicalSemanticExperiment.PartialSpanOnly,
        })
        {
            var first = await CaptureAsync(experiment);
            var second = await CaptureAsync(experiment);
            Assert.Equal(
                first.Select(request => Sha256(request.SystemPrompt + request.UserMessage)),
                second.Select(request => Sha256(request.SystemPrompt + request.UserMessage)));
        }
    }

    [Fact]
    public async Task Freeze()
    {
        var arms = new[]
        {
            CanonicalSemanticExperiment.Baseline,
            CanonicalSemanticExperiment.StructuralAncestorsOnly,
            CanonicalSemanticExperiment.PartialSpanOnly,
        };
        var frozen = new List<object>();
        foreach (var experiment in arms)
        {
            var requests = await CaptureAsync(experiment);
            frozen.Add(new
            {
                arm = experiment.Name,
                carryStructuralAncestors = experiment.CarryStructuralAncestors,
                communicatePartialSpan = experiment.CommunicatePartialSpan,
                requests = requests.Count,
                systemPromptSha256 = Sha256(requests[0].SystemPrompt),
                systemPromptChars = requests[0].SystemPrompt.Length,
                payloadSha256 = requests.Select(request => Sha256(request.UserMessage)).ToArray(),
                totalPayloadChars = requests.Sum(request => request.UserMessage.Length),
                segmentsCarryingOpenStructure = requests.Count(request => OpenContextOf(request).Length > 0),
            });
        }

        var root = RepositoryRoot();
        var directory = Path.Combine(root, "eval", "a99-closed-loop", "pdf-canary-072");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "experiment-arms.v1.json"),
            JsonSerializer.Serialize(new
            {
                artifactKind = "PDF_EXPERIMENT_ARM_FREEZE",
                providerCalls = 0,
                document = "DOC-0072",
                note = "Requests per arm, not provider calls made. A full run of one arm sends this " +
                    "many semantic requests, plus a placement request per unresolved heading round.",
                noCombinedArm = "I7 and I8 are never applied together until each has been measured alone.",
                arms = frozen,
            }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(3, frozen.Count);
    }

    private static void AssertSameSourceAndOwnership(
        IReadOnlyList<CapturedRequest> baseline, IReadOnlyList<CapturedRequest> arm)
    {
        Assert.Equal(baseline.Count, arm.Count);
        for (var index = 0; index < baseline.Count; index++)
        {
            var left = Body(baseline[index]);
            var right = Body(arm[index]);
            Assert.Equal(Aliases(left), Aliases(right));
            Assert.Equal(Texts(left), Texts(right));
            Assert.Equal(
                left.GetProperty("ownedSourceAliases").EnumerateArray().Select(item => item.GetString()),
                right.GetProperty("ownedSourceAliases").EnumerateArray().Select(item => item.GetString()));
        }
    }

    private static JsonElement Body(CapturedRequest request)
    {
        var payload = request.UserMessage;
        var schemaAt = payload.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
        return JsonDocument.Parse(schemaAt < 0 ? payload : payload[..schemaAt]).RootElement.Clone();
    }

    private static string[] Aliases(JsonElement body) => body.GetProperty("sourceEvidence")
        .EnumerateArray().Select(item => item.GetProperty("alias").GetString()!).ToArray();

    private static string[] Texts(JsonElement body) => body.GetProperty("sourceEvidence")
        .EnumerateArray().Select(item => item.GetProperty("text").GetString()!).ToArray();

    private static string[] OpenContextOf(CapturedRequest request) =>
        Body(request).TryGetProperty("openStructuralContext", out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : [];

    private static async Task<IReadOnlyList<CapturedRequest>> CaptureAsync(CanonicalSemanticExperiment experiment)
    {
        var path = Path.Combine(RepositoryRoot(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        using var capture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, capture, CancellationToken.None, experiment);
        return capture.Requests;
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
