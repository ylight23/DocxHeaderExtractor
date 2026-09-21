using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline replay of DOC-0256: frozen source, frozen provider replies, current harness.
/// <para>
/// The model said DAY 2, DAY 3 and DAY 4 were headings. The harness deleted them. This drives the
/// model's own recorded words through the whole canonical route so the recovery is attributable to
/// the harness change and nothing else — a fresh provider call would move the model and the harness
/// together and could not separate them.
/// </para>
/// </summary>
public sealed class FrozenReplayDoc0256Tests
{
    private static readonly string[] DayAliases = ["S0123", "S0138", "S0159"];

    private static readonly string[] DaySourceIds =
    [
        "body[1]/tbl[2]/tr[1]/tc[1]/p[1]",
        "body[1]/tbl[3]/tr[1]/tc[1]/p[1]",
        "body[1]/tbl[4]/tr[1]/tc[1]/p[1]",
    ];

    [Fact]
    public async Task TheThreeDayHeadingsSurviveEveryStageToCanonicalOutput()
    {
        var (authority, replay) = await ReplayAsync();

        // Stage by stage, so a future regression names the stage that lost them.
        var proposed = authority.Audit!.BlockDecisions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var validated = authority.Audit.GroundedBlockIds.ToHashSet(StringComparer.Ordinal);
        var canonical = authority.Structure.Elements
            .SelectMany(element => element.Sources.Select(source => source.StableId ?? source.SourceId))
            .ToHashSet(StringComparer.Ordinal);
        var output = HeadingOutlineProjection.Project(authority.Structure, authority.EmittedElementIds)
            .Select(heading => heading.StableId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(DaySourceIds, id => Assert.Contains(id, proposed));
        Assert.All(DaySourceIds, id => Assert.Contains(id, validated));
        Assert.All(DaySourceIds, id => Assert.Contains(id, canonical));
        Assert.All(DaySourceIds, id => Assert.Contains(id, output));

        // Nothing outside the recording was consulted. The placement pass did not fire at all -
        // every heading carried a model relation - so the whole replay is two recorded replies and
        // no invented answer.
        Assert.Equal(0, replay.CallsBeyondRecording);
    }

    [Fact]
    public async Task ReplayIsAlignedToTheRecordingItClaimsToReplay()
    {
        // A segmentation change would silently shift reply 2 onto segment 1, whose aliases it does
        // not own; every proposal would then be filtered out and the replay would report a clean
        // zero instead of a mismatch. Pinning the model's own aliases to the ids they resolve to
        // makes that failure loud.
        var (authority, _) = await ReplayAsync();
        var aliases = SemanticSourceAliasCatalog
            .FromCatalog(DocumentSourceCatalogBuilder.FromSourceDocument(State().Source))
            .ToDictionary(item => item.Alias, item => item.SourceId, StringComparer.Ordinal);

        Assert.Equal(DaySourceIds, DayAliases.Select(alias => aliases[alias]).ToArray());
        Assert.NotEmpty(authority.Structure.Elements);
    }

    [Fact]
    public async Task ReplayReportsTheStageCountsRatherThanAssumingThem()
    {
        var (authority, _) = await ReplayAsync();

        var proposals = authority.Audit!.BlockDecisions.Count;
        var validated = authority.Audit.GroundedBlockIds.Count;
        var canonical = HeadingOutlineProjection
            .Project(authority.Structure, authority.EmittedElementIds).Count;

        // The measured shape of the fix: nothing is lost between a bound proposal and the output.
        var counts = $"proposals={proposals} validated={validated} canonical={canonical}";
        Assert.True(proposals == validated, counts);
        Assert.True(proposals == canonical, counts);
        Assert.True(canonical == 23, counts);
    }

    private static async Task<(StructuralAuthorityResult Authority, FrozenReplyClassifier Replay)> ReplayAsync()
    {
        var state = State();
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        using var replay = new FrozenReplyClassifier(FrozenReplies());
        var authority = await DocxAuthorityPipeline.RunAsync(state, mode, replay);
        return (authority, replay);
    }

    private static IReadOnlyList<string> FrozenReplies()
    {
        var path = Path.Combine(TestRepository.Root(), "tests", "DocxHeaderExtractor.Tests", "Assets",
            "DOC-0256.frozen-responses.json");
        Assert.True(File.Exists(path), $"Missing recording: {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("responses")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static DocxPolicyState State()
    {
        var docx = Path.Combine(TestRepository.Root(), "todo10_8", "heading_corpus_95_word",
            "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.docx");
        Assert.True(File.Exists(docx), $"Missing fixture: {docx}");

        var source = new OpenXmlDocumentSource().Read(docx);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        return new DocxPolicyState(source, features, derived, policy.Paragraphs, policy.StyleTrust);
    }

}
