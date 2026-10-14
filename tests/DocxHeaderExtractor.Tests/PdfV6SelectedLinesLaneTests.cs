using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.PdfHeadingAgent;
using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF lane after the V6 promotion (2026-10-14): an <see cref="IPdfHeadingSelector"/> is the only heading authority. These tests use a
/// selector that answers from a fixed alias set and a transport that makes no provider call.
/// </summary>
public sealed class PdfV6SelectedLinesLaneTests
{
    private const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private static (PdfSourceBuildResult Built, SemanticSourceAtom A, SemanticSourceAtom B, SemanticSourceAtom Other) Atoms()
    {
        var built = PdfSourceAdapter.BuildWithDetails(TestRepository.Path(Pdf));
        var atoms = built.Snapshot.Atoms.OrderBy(a => a.Ordinal).ToArray();
        var blocks = built.Details.LayoutBlockByAtom;
        // Two consecutive atoms of one layout block (a heading over two lines), and an atom of a later block.
        var i = Enumerable.Range(0, atoms.Length - 1).First(k => atoms[k].Page == atoms[k + 1].Page
            && blocks.TryGetValue(atoms[k].SourceId, out var x) && blocks.TryGetValue(atoms[k + 1].SourceId, out var y) && x == y);
        var other = atoms.Skip(i + 2).First(a => blocks.TryGetValue(a.SourceId, out var z) && z != blocks[atoms[i].SourceId]);
        return (built, atoms[i], atoms[i + 1], other);
    }

    [Fact]
    public void Consecutive_selected_lines_of_one_block_are_one_heading_and_a_gap_or_another_block_starts_a_new_one()
    {
        var (built, a, b, other) = Atoms();
        var atoms = built.Snapshot.Atoms;
        var blocks = built.Details.LayoutBlockByAtom;

        var one = SelectedLinesHeadingAuthority.Group(atoms, new HashSet<string> { a.Alias, b.Alias }, blocks);
        Assert.Single(one);
        Assert.Equal(new[] { a.Alias, b.Alias }, one[0].Parts!.Select(p => p.Alias));
        Assert.Equal(a.SourceId, one[0].Id);
        Assert.Equal(SelectedLinesHeadingAuthority.Reason, one[0].Reason);

        var two = SelectedLinesHeadingAuthority.Group(atoms, new HashSet<string> { a.Alias, other.Alias }, blocks);
        Assert.Equal(2, two.Count);

        var unknown = SelectedLinesHeadingAuthority.Group(atoms, new HashSet<string> { "NOT-AN-ALIAS" }, blocks);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task The_lane_builds_its_structure_from_the_selector_answer_and_passes_the_route_key()
    {
        var (_, a, b, other) = Atoms();
        var selector = new FixedSelector(new HashSet<string> { a.Alias, b.Alias, other.Alias });
        using var transport = new NoCallTransport("route-key");
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));

        var run = await PdfExtractionPipeline.RunWithSelectorAsync(file, new PipelineOptions(), transport, selector);

        Assert.Equal(1, selector.Calls);
        Assert.Equal("route-key", selector.Key);
        Assert.Equal(file.LocalPath, selector.Path);
        var heading = run.HeadingPipeline!;
        Assert.Equal(SelectedLinesHeadingAuthority.AuthorityId, heading.Reason);
        Assert.Equal(2, heading.Structure.Elements.Count);
        Assert.Equal(new[] { FixedSelector.Id }, heading.Audit!.ModelInputContracts);
        Assert.Equal("complete", heading.Audit.SemanticLane!.Status);
        Assert.Equal(0, transport.FrozenCalls);   // the selector, not the frozen transport, decides membership
        // Hierarchy: the selector gives no parent relation, so the shared placement pass asks once for the headings it left unplaced.
        Assert.Equal(1, transport.PlacementCalls);
        Assert.Equal(2, heading.Structure.Elements.Count);
    }

    [Fact]
    public async Task Without_a_selector_or_a_route_key_the_lane_refuses_before_any_call()
    {
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));
        using var keyed = new NoCallTransport("route-key");
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => PdfExtractionPipeline.RunExecutionAsync(file, new PipelineOptions(), keyed));
        Assert.StartsWith("PDF_HEADING_SELECTOR_REQUIRED", none.Message);

        var selector = new FixedSelector(new HashSet<string>());
        using var keyless = new NoCallTransport(null);
        var noKey = await Assert.ThrowsAsync<InvalidOperationException>(() => PdfExtractionPipeline.RunWithSelectorAsync(file, new PipelineOptions(), keyless, selector));
        Assert.StartsWith("PDF_HEADING_SELECTOR_KEY_MISSING", noKey.Message);
        Assert.Equal(0, selector.Calls);
        Assert.Equal(0, keyed.FrozenCalls + keyed.PlacementCalls + keyless.FrozenCalls + keyless.PlacementCalls);
    }

    [Fact]
    public async Task With_the_model_disabled_the_selector_is_not_called()
    {
        var selector = new FixedSelector(new HashSet<string>());
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));
        var run = await PdfExtractionPipeline.RunWithSelectorAsync(file, new PipelineOptions { DisableLlm = true }, null, selector);
        Assert.Equal(0, selector.Calls);
        Assert.Empty(run.HeadingPipeline!.Structure.Elements);
    }

    [Fact]
    public void The_production_selector_runs_the_d412_method_with_the_revised_instruction()
    {
        var selector = new PdfHeadingAgentSelector();
        // The frozen D4.12 instruction is kept for parity; production sends the owner's revised instruction (2026-10-14).
        Assert.Equal(P7F1V6DirectD45.InstructionEnglishMainRepresentativeLines, AgentProtocol.InstructionD412);
        Assert.Equal("2d057c62aaf69297afa46f3dec8d8b88720893491c5620ecfcc51562e9e03884", P7F1V6Core.InstructionSha256(AgentProtocol.InstructionD412));
        Assert.Equal(AgentProtocol.InstructionOutlineMeaning, AgentProtocol.Instruction);
        Assert.Equal("813e49c6", P7F1V6Core.InstructionSha256(AgentProtocol.Instruction)[..8]);
        Assert.Equal("p7-f1-v6-d4.12:TOOLFACTS_ANALYSIS_V3:instruction-813e49c6", selector.SelectorId);
        Assert.Contains("Apply this the same way everywhere in the document", AgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("not by how it looks", AgentProtocol.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_authorized_openrouter_route_carries_its_key_and_other_routes_issue_none()
    {
        var selection = new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = new RemoteInferenceOptions { ApiKey = "sk-test", OpenRouterProviderRoute = RemoteInferenceOptions.DefaultProviderRoute },
        };
        using var transport = await new InferenceTransportFactory(selection).CreatePdfProductionAsync();
        Assert.Equal("sk-test", ((IPdfProductionAuthorizedInferenceTransport)transport).OpenRouterApiKey);
        Assert.Null(((IPdfProductionAuthorizedInferenceTransport)new DefaultKeyTransport()).OpenRouterApiKey);
    }

    [Fact]
    public async Task The_placement_request_asks_for_json_and_carries_the_style_table_only_when_the_selector_provides_it()
    {
        var (_, a, b, other) = Atoms();
        var aliases = new HashSet<string> { a.Alias, b.Alias, other.Alias };
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));

        using var plain = new NoCallTransport("route-key");
        await PdfExtractionPipeline.RunWithSelectorAsync(file, new PipelineOptions(), plain, new FixedSelector(aliases));
        Assert.Equal(1, plain.PlacementCalls);
        Assert.Equal(DocxHeaderExtractor.DocumentProcessing.Materialization.HeadingParentResolver.PlacementPrompt, plain.LastPrompt);
        Assert.DoesNotContain("documentStyles", plain.LastPacket!, StringComparison.Ordinal);

        using var withStyles = new NoCallTransport("route-key");
        var context = PdfHeadingAgentSelector.PlacementContextOf(TestRepository.Path(Pdf));
        await PdfExtractionPipeline.RunWithSelectorAsync(file, new PipelineOptions(), withStyles, new FixedSelector(aliases, context));
        Assert.Equal(DocxHeaderExtractor.DocumentProcessing.Materialization.HeadingParentResolver.PlacementPromptWithEvidence, withStyles.LastPrompt);
        using var packet = JsonDocument.Parse(withStyles.LastPacket!);
        Assert.Equal(new[] { "documentStyles", "headings", "toPlace" }, packet.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(packet.RootElement.GetProperty("documentStyles").GetProperty("styles").GetArrayLength() > 0);
        Assert.All(packet.RootElement.GetProperty("headings").EnumerateArray(), h => Assert.True(h.TryGetProperty("alias", out _) && h.TryGetProperty("text", out _)));
        Assert.Contains(packet.RootElement.GetProperty("headings").EnumerateArray(), h => h.TryGetProperty("style", out var s) && s.TryGetProperty("rankInDocument", out _));

        // Both prompts must pass the provider's rule for response_format json_object: some message mentions JSON.
        Assert.Contains("json", plain.LastPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", withStyles.LastPrompt!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Groups_given_by_the_selector_decide_the_headings_and_no_selected_line_is_dropped()
    {
        var (built, a, b, other) = Atoms();
        var atoms = built.Snapshot.Atoms;
        var blocks = built.Details.LayoutBlockByAtom;
        var selected = new HashSet<string> { a.Alias, b.Alias, other.Alias };

        // Two lines the layout rule would keep apart (different blocks) are one heading when the selector says so.
        var far = atoms.OrderBy(x => x.Ordinal).First(x => x.Ordinal > a.Ordinal && x.Page != a.Page);
        var together = SelectedLinesHeadingAuthority.FromGroups(atoms, new HashSet<string> { a.Alias, far.Alias }, [[a.Alias, far.Alias]], blocks);
        Assert.Equal(new[] { a.Alias, far.Alias }, together.Single().Parts!.Select(p => p.Alias));

        // A selected line no group names is a heading of its own; an alias the source lacks, or that was not selected, is ignored.
        var decided = SelectedLinesHeadingAuthority.FromGroups(atoms, selected, [[a.Alias, "L9999:S0", "NOT-SELECTED"]], blocks);
        Assert.Equal(new[] { new[] { a.Alias }, new[] { b.Alias }, new[] { other.Alias } }.OrderBy(g => atoms.First(x => x.Alias == g[0]).Ordinal),
            decided.Select(d => d.Parts!.Select(p => p.Alias).ToArray()));
        // A line named by two groups belongs to the first.
        var twice = SelectedLinesHeadingAuthority.FromGroups(atoms, selected, [[a.Alias, b.Alias], [b.Alias, other.Alias]], blocks);
        Assert.Equal(new[] { a.Alias, b.Alias }, twice[0].Parts!.Select(p => p.Alias));
        Assert.Equal(selected.Order(), twice.SelectMany(d => d.Parts!.Select(p => p.Alias)).Order());
    }

    [Fact]
    public void Without_evidence_the_placement_packet_is_the_one_the_stage_always_sent()
    {
        var headings = new[]
        {
            new CanonicalSemanticBoundHeading("L0001:S0", "id-1", 0, "Chapter I", "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, 9),
            new CanonicalSemanticBoundHeading("L0002:S0", "id-2", 1, "GENERAL", "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, 7),
        };
        var packet = DocxHeaderExtractor.DocumentProcessing.Materialization.HeadingParentResolver.PlacementPacket(
            headings, new HashSet<string> { "id-2" }, evidence: null);
        Assert.Equal("{\"headings\":[{\"alias\":\"L0001:S0\",\"text\":\"Chapter I\"},{\"alias\":\"L0002:S0\",\"text\":\"GENERAL\"}],\"toPlace\":[\"L0002:S0\"]}", packet);
    }

    private sealed class FixedSelector(IReadOnlySet<string> aliases, PdfPlacementContext? placement = null) : IPdfHeadingSelector
    {
        public const string Id = "fixed-test-selector";
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public string? Path { get; private set; }
        public string SelectorId => Id;

        public Task<PdfHeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct)
        {
            Calls++; Key = apiKey; Path = pdfPath;
            return Task.FromResult(new PdfHeadingSelection(aliases, 3, 3, [], ["{}"], 3, 0.0, placement));
        }
    }

    private class NoCallTransport(string? key) : IPdfProductionAuthorizedInferenceTransport
    {
        public int FrozenCalls { get; private set; }
        public int PlacementCalls { get; private set; }
        public string? LastPrompt { get; private set; }
        public string? LastPacket { get; private set; }
        public string? OpenRouterApiKey => key;
        public IFrozenInferenceRequestComposer RequestComposer => new OpenRouterQwen37InferenceRequestComposer();
        public string PdfProductionProvider => "test";
        public string PdfProductionModel => "test";
        public string ModelName => "test";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "V6 lane test; no provider";
        public int SharedPrefixTokens => 0;

        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            PlacementCalls++;
            LastPrompt = systemPrompt; LastPacket = userMessage;
            return Task.FromResult("{\"placements\":[]}");
        }

        public Task<FrozenInferenceResponse> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default)
        {
            FrozenCalls++;
            throw new InvalidOperationException("the V6 lane must not send a frozen request");
        }

        public void Dispose() { }
    }

    /// <summary>A transport that does not implement the key member: the interface default answers null.</summary>
    private sealed class DefaultKeyTransport : IPdfProductionAuthorizedInferenceTransport
    {
        public IFrozenInferenceRequestComposer RequestComposer => new OpenRouterQwen37InferenceRequestComposer();
        public string PdfProductionProvider => "test";
        public string PdfProductionModel => "test";
        public string ModelName => "test";
        public int ContextSize => 1;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) => throw new NotSupportedException();
        public Task<FrozenInferenceResponse> ExecuteFrozenRequestAsync(byte[] providerBody, int maxTokens, string systemPrompt, string userMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
