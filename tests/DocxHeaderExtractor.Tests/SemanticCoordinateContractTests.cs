using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// One semantic core, one coordinate contract per lane.
/// <para>
/// The engine used to read its response schema off a static, which made every source format share
/// one answer to a question only the source can answer. A DOCX paragraph is addressed by alias and
/// an exact span; a PDF by a run of visual-segment atoms. Neither is wrong, and forcing them into
/// one payload so that a contract could be counted once is what hid the difference.
/// </para>
/// <para>
/// This file pins the seam and nothing more. Both lanes still hand over the same schema, byte for
/// byte, and the hash every frozen artifact names is still the hash both of them send.
/// </para>
/// </summary>
public sealed class SemanticCoordinateContractTests
{
    /// <summary>The schema both lanes send today, named by every frozen preflight artifact.</summary>
    private const string ActiveSchemaHash =
        "91005fabc2e978d5ab4d900bc66ebeb27e563628056b3073cef22896687ac72e";

    [Fact]
    public void Both_lanes_still_send_the_schema_they_have_always_sent()
    {
        // The whole point of doing this as a separate step: the seam moves, the bytes do not.
        Assert.Equal(ActiveSchemaHash, SemanticCoordinateContract.DocxAliasSpan.SchemaHash());
        Assert.Equal(ActiveSchemaHash, SemanticCoordinateContract.PdfAliasSelection.SchemaHash());
        Assert.Equal(
            JsonSerializer.Serialize(SemanticCoordinateContract.DocxAliasSpan.Schema()),
            JsonSerializer.Serialize(SemanticCoordinateContract.PdfAliasSelection.Schema()));
        Assert.Equal(
            JsonSerializer.Serialize(CanonicalSemanticContract.Schema()),
            JsonSerializer.Serialize(SemanticCoordinateContract.DocxAliasSpan.Schema()));
    }

    [Fact]
    public void The_lanes_declare_different_coordinate_systems_over_that_one_schema()
    {
        // The difference that already existed in Gold and had nowhere to live in the code.
        Assert.Equal("SOURCE_ALIAS_PLUS_UTF16_SPAN", SemanticCoordinateContract.DocxAliasSpan.CoordinateSystem);
        Assert.Equal("SOURCE_ALIAS_PLUS_SELECTION_MODE", SemanticCoordinateContract.PdfAliasSelection.CoordinateSystem);
        Assert.NotEqual(
            SemanticCoordinateContract.DocxAliasSpan.CoordinateSystem,
            SemanticCoordinateContract.PdfAliasSelection.CoordinateSystem);

        // One concrete contract is still active. The architecture allows divergence; nothing has
        // diverged yet, and claiming two active contract hashes here would be claiming a migration
        // that has not happened.
        Assert.Equal(
            SemanticCoordinateContract.DocxAliasSpan.ProtocolVersion,
            SemanticCoordinateContract.PdfAliasSelection.ProtocolVersion);
    }

    [Fact]
    public async Task The_engine_sends_the_schema_it_was_given_and_not_a_global_one()
    {
        // A lane that hands over a different contract gets a different request, without the engine
        // consulting a file extension, a runtime type or a flag to notice.
        var probe = new SemanticCoordinateContract(
            "PROBE_COORDINATES",
            "a99-probe-contract-v1",
            () => new { type = "object", probe = true },
            CanonicalSemanticContractValidator.ValidateJson,
            SemanticProposalDecoder.DecodeAliasScalar);

        var active = await RequestFor(SemanticCoordinateContract.PdfAliasSelection);
        var probed = await RequestFor(probe);

        Assert.Contains(JsonSerializer.Serialize(CanonicalSemanticContract.Schema()), active, StringComparison.Ordinal);
        Assert.Contains("\"probe\":true", probed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"probe\":true", active, StringComparison.Ordinal);
        Assert.Contains("a99-probe-contract-v1", probed, StringComparison.Ordinal);
        Assert.NotEqual(active, probed);
    }

    [Fact]
    public async Task The_semantic_core_is_the_same_whichever_contract_the_lane_supplies()
    {
        // Only the coordinate half moves. The instructions, the ontology and the evidence the model
        // reasons over are identical, which is what keeps this one engine rather than two.
        // The probe keeps the protocol id and changes only the schema, because the protocol id is
        // itself part of the coordinate contract and travels in the packet. Holding it still is
        // what leaves the evidence as the only thing being compared.
        var probe = new SemanticCoordinateContract(
            "PROBE_COORDINATES", CanonicalSemanticContract.ProtocolVersion,
            () => new { type = "object", probe = true },
            CanonicalSemanticContractValidator.ValidateJson,
            SemanticProposalDecoder.DecodeAliasScalar);

        var activePrompt = await PromptFor(SemanticCoordinateContract.PdfAliasSelection);
        var probedPrompt = await PromptFor(probe);
        Assert.Equal(activePrompt, probedPrompt);

        var active = await RequestFor(SemanticCoordinateContract.PdfAliasSelection);
        var probed = await RequestFor(probe);
        Assert.Equal(Evidence(active), Evidence(probed));
    }

    [Fact]
    public async Task A_reply_is_checked_against_the_contract_that_offered_the_schema()
    {
        // Request and validator come from one descriptor. Validating against a different one would
        // accept coordinates the model was never offered, and neither side could see the mismatch.
        var refusing = new SemanticCoordinateContract(
            "PROBE_COORDINATES", "a99-probe-contract-v1",
            CanonicalSemanticContract.Schema,
            _ => [new SemanticContractIssue("PROBE_REFUSED", null, "this contract refuses every reply")],
            SemanticProposalDecoder.DecodeAliasScalar);

        var result = await RunWith(refusing);

        Assert.Contains(result.ContractIssues, issue => issue.Code == "PROBE_REFUSED");
        Assert.DoesNotContain(
            (await RunWith(SemanticCoordinateContract.PdfAliasSelection)).ContractIssues,
            issue => issue.Code == "PROBE_REFUSED");
    }

    [Fact]
    public void Neither_lane_can_reach_the_other_contract()
    {
        Assert.NotSame(SemanticCoordinateContract.DocxAliasSpan, SemanticCoordinateContract.PdfAliasSelection);
        Assert.NotEqual(SemanticCoordinateContract.DocxAliasSpan, SemanticCoordinateContract.PdfAliasSelection);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<string> RequestFor(SemanticCoordinateContract contract) =>
        (await Capture(contract)).Request;

    private static async Task<string> PromptFor(SemanticCoordinateContract contract) =>
        (await Capture(contract)).Prompt;

    private static async Task<CanonicalSemanticTextInferenceResult> RunWith(SemanticCoordinateContract contract)
    {
        using var classifier = new RecordingClassifier();
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(classifier, contract);
        return await model.InferAsync(Input(), new SemanticContextPacket([], [], []), "contract-seam");
    }

    private static async Task<(string Prompt, string Request)> Capture(SemanticCoordinateContract contract)
    {
        using var classifier = new RecordingClassifier();
        var model = new CanonicalSemanticEngine.HeaderClassifierCanonicalTextModel(classifier, contract);
        await CanonicalSemanticProductionEntryPoint.RunAsync(Input(), model);
        return (classifier.Prompt!, classifier.Request!);
    }

    /// <summary>The request with its trailing schema removed: what the model reasons over.</summary>
    private static string Evidence(string request)
    {
        var at = request.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
        return at < 0 ? request : request[..at];
    }

    private static CanonicalSemanticProductionInput Input()
    {
        var catalog = new DocumentSourceCatalog([
            new DocumentSourceUnit("p1", 1, "Heading", new SourceAnchor { SourceType = "DOCX", ParagraphId = "p1" }, new(0, 7))]);
        return new CanonicalSemanticProductionInput(
            catalog, null, "source-hash", [new CanonicalSemanticPageEvidence("P0001", true, 0, "test")],
            [], [], [], [], DocumentId: "DOC-CONTRACT-SEAM",
            SourceEvidence: [new CanonicalSemanticSourceEvidence(
                "S0001", "p1", 1, "Heading", "document_body", null, 1, false, false,
                ["test"], new { }, new { }, [], [], [], [], [],
                new SemanticCandidateAttentionHint("S0001", true, "test"))]);
    }

    private sealed class RecordingClassifier : IHeaderClassifier
    {
        public string? Prompt { get; private set; }
        public string? Request { get; private set; }

        public string ModelName => "recording";
        public int ContextSize => 8192;
        public string RuntimeDescription => "recording";
        public int SharedPrefixTokens => 0;

        public Task<string> BoundaryCutAsync(
            string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Prompt = systemPrompt;
            Request = userMessage;
            return Task.FromResult("{\"headings\":[]}");
        }

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChunkResult> ClassifyHierarchyAsync(
            IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }
}
