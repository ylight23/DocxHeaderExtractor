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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task An_arm_with_I7_off_does_not_carry_the_field_at_all(
        bool carryAncestors, bool partialSpan)
    {
        // Absent, not empty. Serialising openStructuralContext as [] when the arm is off still
        // changes every payload byte-for-byte, which silently moves the baseline that every arm is
        // measured against. The hash assertions elsewhere prove identity; this one names the cause,
        // so a regression says what broke rather than only that something did.
        var requests = await CaptureAsync(new CanonicalSemanticExperiment(carryAncestors, partialSpan));

        Assert.All(requests, request =>
            Assert.False(Body(request).TryGetProperty("openStructuralContext", out _)));
    }

    [Fact]
    public async Task The_baseline_payload_matches_the_frozen_hashes()
    {
        // If an arm ever leaks into the default, these move and the B0 column stops meaning "what
        // ships today".
        //
        // They moved once, deliberately: tableDepth left the PDF request, because a PDF has no
        // nested-table depth and zero read as a measurement. The move was verified to be that
        // change and nothing else - re-inserting "tableDepth":0 where it used to sit reproduced
        // the previous values 9461b418..., 3b673d8f... and 3e1e1045... byte for byte. No PDF
        // provider run had been taken against the old baseline, so nothing frozen depends on it.
        var baseline = await CaptureAsync(CanonicalSemanticExperiment.Baseline);

        Assert.Equal(
            [
                "e3bddbe9b074fe203249a52e8497a215c75d9a044dcae420107b53aa57dc7010",
                "8468fb223a10e015ddddebd45b3ae073e5aaa62c1f1b5ea0d8d4ece55086c1f2",
                "acc0f9f04c56f2ecb2e723e56273e985f6c1b6843112a060763a48d4a4fd7f01",
            ],
            baseline.Take(3).Select(request => Sha256(request.UserMessage)));
    }

    [Fact]
    public async Task No_pdf_request_states_a_table_depth_it_could_not_have_measured()
    {
        // Absent, not zero - the same rule the openStructuralContext test above holds. A PDF has
        // no nested-table structure, so there is nothing for an occurrence to be at depth zero of,
        // and a number on the wire cannot be distinguished from one that was measured.
        var requests = await CaptureAsync(CanonicalSemanticExperiment.Baseline);

        Assert.NotEmpty(requests);
        Assert.All(requests, request =>
            Assert.All(Body(request).GetProperty("sourceEvidence").EnumerateArray(), entry =>
                Assert.False(entry.TryGetProperty("tableDepth", out _))));
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
    public async Task The_frozen_arm_comparison_still_describes_this_code()
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

        FreezeArtifact.AssertJson("eval/a99-closed-loop/pdf-canary-072", "experiment-arms.v1.json",
            new
            {
                artifactKind = "PDF_EXPERIMENT_ARM_FREEZE",
                providerCalls = 0,
                document = "DOC-0072",
                note = "Requests per arm, not provider calls made. A full run of one arm sends this " +
                    "many semantic requests, plus a placement request per unresolved heading round.",
                noCombinedArm = "I7 and I8 are never applied together until each has been measured alone.",
                arms = frozen,
            });

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
        var path = Path.Combine(TestRepository.Root(), Pdf.Replace('/', Path.DirectorySeparatorChar));
        using var capture = new RequestCapturingClassifier();
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(path, capture, CancellationToken.None, experiment);
        return capture.Requests;
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

}
