using System.Text.Json;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Pdf;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The PDF lane after the V6 promotion (2026-10-14): an <see cref="IHeadingSelector"/> is the only heading authority. These tests use a
/// selector that answers from a fixed alias set and a provider whose placer is a script.
/// </summary>
[Trait("Category", "Slow")]
public sealed class PdfLaneTests
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

        var one = SelectionAuthority.Group(atoms, new HashSet<string> { a.Alias, b.Alias }, blocks);
        Assert.Single(one);
        Assert.Equal(new[] { a.Alias, b.Alias }, one[0].Parts!.Select(p => p.Alias));
        Assert.Equal(a.SourceId, one[0].Id);
        Assert.Equal(SelectionAuthority.Reason, one[0].Reason);

        var two = SelectionAuthority.Group(atoms, new HashSet<string> { a.Alias, other.Alias }, blocks);
        Assert.Equal(2, two.Count);

        var unknown = SelectionAuthority.Group(atoms, new HashSet<string> { "NOT-AN-ALIAS" }, blocks);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task The_lane_builds_its_structure_from_the_selector_answer_and_passes_the_route_key()
    {
        var (_, a, b, other) = Atoms();
        var selector = new FixedSelector(new HashSet<string> { a.Alias, b.Alias, other.Alias });
        var provider = new ScriptedProvider("route-key");
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));

        var run = await PdfPipeline.RunAsync(file, new PipelineOptions(), provider.CreateRoute(), new HeadingAgents(Pdf: selector, Placer: provider.Placement.Placer));

        Assert.Equal(1, selector.Calls);
        Assert.Equal("route-key", selector.Key);
        Assert.Equal(file.LocalPath, selector.Path);
        var heading = run.HeadingPipeline!;
        Assert.Equal(SelectionAuthority.AuthorityId, heading.Reason);
        Assert.Equal(2, heading.Structure.Elements.Count);
        Assert.Contains(FixedSelector.Id, heading.Audit!.ModelInputContracts);
        Assert.Contains(HeadingPlacer.Version, heading.Audit.ModelInputContracts);
        Assert.Equal("complete", heading.Audit.SemanticLane!.Status);
        // One parent-proposal pass; no semantic verifier gate follows it.
        Assert.Equal(1, provider.PlacementCalls);
        Assert.Equal(4, run.Result.Provenance.ProviderCalls); // 3 selector attempts + 1 hierarchy proposal call
        Assert.Equal(2, heading.Structure.Elements.Count);
    }

    [Fact]
    public async Task Without_a_selector_or_a_route_key_the_lane_refuses_before_any_call()
    {
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));
        var keyed = new ScriptedProvider("route-key");
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => PdfPipeline.RunAsync(file, new PipelineOptions(), keyed.CreateRoute(), new HeadingAgents(Placer: keyed.Placement.Placer)));
        Assert.StartsWith("PDF_HEADING_SELECTOR_REQUIRED", none.Message);

        var selector = new FixedSelector(new HashSet<string>());
        var keyless = new ScriptedProvider(null);
        var noKey = await Assert.ThrowsAsync<InvalidOperationException>(() => PdfPipeline.RunAsync(file, new PipelineOptions(), keyless.CreateRoute(), new HeadingAgents(Pdf: selector, Placer: keyless.Placement.Placer)));
        Assert.StartsWith("PDF_HEADING_SELECTOR_KEY_MISSING", noKey.Message);
        Assert.Equal(0, selector.Calls);
        Assert.Equal(0, keyed.PlacementCalls + keyless.PlacementCalls);
    }

    [Fact]
    public async Task With_the_model_disabled_the_selector_is_not_called()
    {
        var selector = new FixedSelector(new HashSet<string>());
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));
        var run = await PdfPipeline.RunAsync(file, new PipelineOptions { DisableLlm = true }, null, new HeadingAgents(Pdf: selector, Placer: new PlacementScript().Placer));
        Assert.Equal(0, selector.Calls);
        Assert.Empty(run.HeadingPipeline!.Structure.Elements);
    }

    [Fact]
    public void The_production_selector_runs_the_d412_method_with_the_structural_instruction()
    {
        var selector = new PdfSelector();
        // The frozen D4.12 instruction is kept for parity; production sends the owner's structural-heading instruction (2026-10-14),
        // which replaced the earlier "main headings by meaning" instruction after the three-document comparison.
        Assert.Equal("2d057c62aaf69297afa46f3dec8d8b88720893491c5620ecfcc51562e9e03884", Sha256Hex(D412Prompt.Text));
        Assert.Equal(AgentProtocol.InstructionStructuralV1, AgentProtocol.Instruction);
        Assert.Equal("c5c5b6939b99d4cb907a59b2da472d14e1ffc7e35067c1ae48ca0f0ccabdb793", Sha256Hex(AgentProtocol.Instruction));
        Assert.Equal("p7-f1-v6-d4.12:TOOLFACTS_ANALYSIS_V3:facts-in-request:instruction-" + Sha256Hex(PdfAgentProtocol.Instruction)[..8] + ":" + AgentSelector.BindingPolicyVersion + ":group-selected-text-v1:ordered-page-budget-v1:" + AgentProtocol.Sha(System.Text.Json.JsonSerializer.Serialize(new DocxHeaderExtractor.Agents.Budget.RequestPolicy()))[..8] + ":wait-for-complete-response", selector.SelectorId);
        Assert.Contains("at every level of its outline", AgentProtocol.Instruction, StringComparison.Ordinal);
        Assert.Contains("It does not need to summarize that content", AgentProtocol.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_placement_request_asks_for_json_and_carries_the_style_table_only_when_the_selector_provides_it()
    {
        var (_, a, b, other) = Atoms();
        var aliases = new HashSet<string> { a.Alias, b.Alias, other.Alias };
        var file = UploadedFile.FromLocalPath(TestRepository.Path(Pdf));

        var plain = new ScriptedProvider("route-key");
        await PdfPipeline.RunAsync(file, new PipelineOptions(), plain.CreateRoute(), new HeadingAgents(Pdf: new FixedSelector(aliases), Placer: plain.Placement.Placer));
        Assert.Equal(1, plain.PlacementCalls);
        Assert.Contains("propose its", plain.LastPrompt);   // the parent definition of the placement prompt
        Assert.Contains("sourceContext", plain.LastPacket!, StringComparison.Ordinal);

        var withStyles = new ScriptedProvider("route-key");
        var agentSource = AgentSource.Load(TestRepository.Path(Pdf), out _)!;
        var context = agentSource.Flavor.PlacementContext(agentSource);
        await PdfPipeline.RunAsync(file, new PipelineOptions(), withStyles.CreateRoute(), new HeadingAgents(Pdf: new FixedSelector(aliases, context), Placer: withStyles.Placement.Placer));
        Assert.Contains("propose its", withStyles.LastPrompt);
        using var packet = JsonDocument.Parse(withStyles.LastPacket!);
        Assert.True(packet.RootElement.TryGetProperty("upstreamWithheld", out _));
        Assert.True(packet.RootElement.TryGetProperty("sourceContext", out _));
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
        var together = SelectionAuthority.FromGroups(atoms, new HashSet<string> { a.Alias, far.Alias }, [[a.Alias, far.Alias]], blocks);
        Assert.Equal(new[] { a.Alias, far.Alias }, together.Single().Parts!.Select(p => p.Alias));

        // A selected line no group names is a heading of its own; an alias the source lacks, or that was not selected, is ignored.
        var decided = SelectionAuthority.FromGroups(atoms, selected, [[a.Alias, "L9999:S0", "NOT-SELECTED"]], blocks);
        Assert.Equal(new[] { new[] { a.Alias }, new[] { b.Alias }, new[] { other.Alias } }.OrderBy(g => atoms.First(x => x.Alias == g[0]).Ordinal),
            decided.Select(d => d.Parts!.Select(p => p.Alias).ToArray()));
        // A line named by two groups belongs to the first.
        var twice = SelectionAuthority.FromGroups(atoms, selected, [[a.Alias, b.Alias], [b.Alias, other.Alias]], blocks);
        Assert.Equal(new[] { a.Alias, b.Alias }, twice[0].Parts!.Select(p => p.Alias));
        Assert.Equal(selected.Order(), twice.SelectMany(d => d.Parts!.Select(p => p.Alias)).Order());
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private sealed class FixedSelector(IReadOnlySet<string> aliases, HeadingPlacementContext? placement = null) : IHeadingSelector
    {
        public const string Id = "fixed-test-selector";
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public string? Path { get; private set; }
        public string SelectorId => Id;

        public Task<HeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct)
        {
            Calls++; Key = apiKey; Path = pdfPath;
            return Task.FromResult(new HeadingSelection(aliases, 3, 3, [], ["{}"], 3, 0.0, placement));
        }
    }
}
