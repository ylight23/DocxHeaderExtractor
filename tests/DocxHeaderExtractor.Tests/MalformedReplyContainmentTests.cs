using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Blast radius of a malformed reply. A provider can emit any shape at any time, so the question is
/// never whether malformed output appears but how much it costs when it does.
/// <para>
/// The contract is fail-closed and local: a bad entry costs that entry, a bad reply costs that
/// segment, and neither ends the document. Reads used to go through <c>GetString</c>/<c>GetInt32</c>
/// directly, which throw on type confusion; the throw escaped the entry loop, the segment loop and
/// the route, so a single <c>"occurrence": "1"</c> produced no headings and no recorded reason.
/// </para>
/// <para>
/// These drive the real route with the real DOC-0256 recording and corrupt one entry of it, so a
/// surviving count is measured against a known-good replay rather than asserted in the abstract.
/// </para>
/// </summary>
public sealed class MalformedReplyContainmentTests
{
    [Fact]
    public async Task AHealthyRecordingIsTheBaselineTheseTestsDegradeFrom()
    {
        Assert.Equal(23, (await RunAsync(Frozen())).Structure.Elements.Count);
    }

    [Theory]
    // Type confusion on each field the parser reads. Every one of these threw out of the route
    // before; each must now cost exactly the entry it was injected into.
    [InlineData("occurrence", "\"1\"")]
    [InlineData("occurrence", "1.5")]
    [InlineData("sourceAliases", "\"S0123\"")]
    [InlineData("sourceAliases", "[null]")]
    [InlineData("sourceAliases", "[1, 2]")]
    [InlineData("verbatimText", "42")]
    [InlineData("verbatimParts", "\"DAY 2\"")]
    [InlineData("relationHints", "{\"a\": 1}")]
    [InlineData("scope", "[\"table\"]")]
    [InlineData("semanticRole", "true")]
    [InlineData("selectionMode", "7")]
    // Missing or unusable required fields, so the whole family is covered in one place.
    [InlineData("sourceAlias", "null")]
    [InlineData("isHeading", "\"yes\"")]
    public async Task OneMalformedEntryCostsThatEntryAndNothingElse(string field, string json)
    {
        var replies = Frozen();
        // Entry 0 of reply 1 is the DAY 2 proposal - a heading that reaches the output when healthy,
        // so its disappearance is visible in the count.
        var corrupted = new[] { replies[0], Corrupt(replies[1], field, json) };
        Assert.NotEqual(replies[1], corrupted[1]);

        var authority = await RunAsync(corrupted);

        Assert.Equal(22, authority.Structure.Elements.Count);
    }

    [Fact]
    public async Task AReplyThatIsNotJsonCostsThatSegmentAndNotTheDocument()
    {
        var replies = Frozen();
        var authority = await RunAsync([replies[0], "I'm sorry, I can't produce that. {\"headings\": ["]);

        // Segment two is gone; segment one survives intact rather than the route throwing.
        Assert.NotEmpty(authority.Structure.Elements);
        Assert.True(authority.Structure.Elements.Count < 23);
    }

    [Fact]
    public async Task AnEmptyReplyIsContainedTheSameWay()
    {
        var authority = await RunAsync([Frozen()[0], string.Empty]);

        Assert.NotEmpty(authority.Structure.Elements);
    }

    /// <summary>Replaces one field of the first heading entry with an arbitrary JSON value.</summary>
    private static string Corrupt(string reply, string field, string json)
    {
        var node = JsonNode.Parse(reply)!;
        node["headings"]![0]![field] = JsonNode.Parse(json);
        return node.ToJsonString();
    }

    private static async Task<StructuralAuthorityResult> RunAsync(IReadOnlyList<string> replies)
    {
        var state = State();
        var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
        using var replay = new FrozenReplyClassifier(replies);
        return await DocxAuthorityPipeline.RunAsync(state, mode, replay);
    }

    private static string[] Frozen()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "tests", "DocxHeaderExtractor.Tests", "Assets", "DOC-0256.frozen-responses.json")));
        return document.RootElement.GetProperty("responses")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static DocxPolicyState State()
    {
        var docx = Path.Combine(RepositoryRoot(), "todo10_8", "heading_corpus_95_word",
            "05_bien_ban_hop", "076_ICP_IACG08_Minutes_2023.docx");
        var source = new OpenXmlDocumentSource().Read(docx);
        var features = NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocumentFeatureDeriver().Derive(source);
        var policy = DocxPolicyStateBuilder.Build(source, features, derived, new ExtractionOptions());
        return new DocxPolicyState(source, features, derived, policy.Paragraphs, policy.StyleTrust);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
