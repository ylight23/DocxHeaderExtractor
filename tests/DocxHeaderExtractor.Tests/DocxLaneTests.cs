using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;
using DocxHeaderExtractor.DocumentProcessing.Lanes.Docx;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The DOCX lane: an <see cref="IHeadingSelector"/> is the only heading authority, as on the PDF lane. These tests use a selector that answers
/// from a fixed alias set and a provider whose placer is a script.
/// </summary>
public sealed class DocxLaneTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-docx-lane-" + Guid.NewGuid().ToString("N"));

    public DocxLaneTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private string Sample() => DocxTestDocument.Create(Path.Combine(dir, "sample.docx"));

    private static string[] Aliases(string path, params int[] ordinals)
    {
        var atoms = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot.Atoms.OrderBy(a => a.Ordinal).ToArray();
        return ordinals.Select(i => atoms[i].Alias).ToArray();
    }

    [Fact]
    public async Task The_lane_builds_its_structure_from_the_selector_answer_and_passes_the_route_key()
    {
        var path = Sample();
        var aliases = Aliases(path, 0, 1, 2);
        var selector = new FixedSelector(aliases.ToHashSet());
        var provider = new ScriptedProvider("route-key");
        var pipeline = new DocxPipeline(new PipelineOptions(), provider, new HeadingAgents(Docx: selector, Placer: provider.Placement.Placer));

        var run = await pipeline.RunDocumentExecutionAsync(path);

        Assert.Equal(1, selector.Calls);
        Assert.Equal("route-key", selector.Key);
        Assert.Equal(path, selector.Path);
        var heading = run.HeadingPipeline!;
        Assert.Equal(DocxLane.RouteId, run.Outline.DeterministicRoute);
        Assert.Equal(DocxLane.RouteId, heading.Reason);
        Assert.Equal(3, heading.Structure.Elements.Count);
        Assert.Contains(FixedSelector.Id, heading.Audit!.ModelInputContracts);
        Assert.Equal("complete", heading.Audit.SemanticLane!.Status);
        Assert.Equal(new[] { "Chapter I", "GENERAL PROVISIONS", "Article 1. Scope" }, run.Outline.Headings.Select(h => h.Text));
    }

    [Fact]
    public async Task The_placement_request_carries_the_docx_measurements_note_when_the_selector_provides_it()
    {
        var path = Sample();
        var aliases = Aliases(path, 0, 1, 2).ToHashSet();
        var context = new HeadingPlacementContext("{\"documentLines\":8,\"styles\":[]}", new Dictionary<string, string>(), DocxAgentProtocol.PlacementNote);
        var provider = new ScriptedProvider("route-key");
        var pipeline = new DocxPipeline(new PipelineOptions(), provider, new HeadingAgents(Docx: new FixedSelector(aliases, context), Placer: provider.Placement.Placer));

        await pipeline.RunDocumentExecutionAsync(path);

        Assert.Equal(1, provider.PlacementCalls);
        Assert.Equal(DocxAgentProtocol.PlacementNote, System.Text.Json.Nodes.JsonNode.Parse(provider.LastPacket!)!["measurementsNote"]!.GetValue<string>());
        Assert.Contains("json", provider.LastPrompt!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_a_selector_a_model_route_or_a_key_the_lane_refuses_before_any_call()
    {
        var path = Sample();
        var keyed = new ScriptedProvider("route-key");
        var noSelector = new DocxPipeline(new PipelineOptions(), keyed, new HeadingAgents(Placer: keyed.Placement.Placer));
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => noSelector.RunDocumentExecutionAsync(path));
        Assert.StartsWith("DOCX_HEADING_SELECTOR_REQUIRED", none.Message);

        var selector = new FixedSelector(new HashSet<string>());
        var keyless = new ScriptedProvider(null);
        var noKey = new DocxPipeline(new PipelineOptions(), keyless, new HeadingAgents(Docx: selector, Placer: keyless.Placement.Placer));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => noKey.RunDocumentExecutionAsync(path));
        Assert.StartsWith("DOCX_HEADING_SELECTOR_KEY_MISSING", missing.Message);

        var noRoute = new DocxPipeline(new PipelineOptions(), new NoRouteProvider(), new HeadingAgents(Docx: selector, Placer: new PlacementScript().Placer));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => noRoute.RunDocumentExecutionAsync(path));
        Assert.Equal("MODEL_ROUTE_REQUIRED", refused.Message);

        Assert.Equal(0, selector.Calls);
        Assert.Equal(0, keyed.PlacementCalls + keyless.PlacementCalls);
    }

    [Fact]
    public async Task With_the_model_disabled_the_selector_is_not_called_and_the_route_says_so()
    {
        var selector = new FixedSelector(new HashSet<string>());
        var pipeline = new DocxPipeline(new PipelineOptions { DisableLlm = true }, new NoRouteProvider(), new HeadingAgents(Docx: selector, Placer: new PlacementScript().Placer));

        var run = await pipeline.RunDocumentExecutionAsync(Sample());

        Assert.Equal(0, selector.Calls);
        Assert.Equal("docx-llm-disabled", run.HeadingPipeline!.Reason);
        Assert.Empty(run.HeadingPipeline.Structure.Elements);
    }

    private sealed class FixedSelector(IReadOnlySet<string> aliases, HeadingPlacementContext? placement = null) : IHeadingSelector
    {
        public const string Id = "fixed-docx-test-selector";
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public string? Path { get; private set; }
        public string SelectorId => Id;

        public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct)
        {
            Calls++; Key = apiKey; Path = path;
            return Task.FromResult(new HeadingSelection(aliases, 1, 1, [], ["{}"], 1, 0.0, placement));
        }
    }

    /// <summary>A provider that issues no model route: a run that needs the model refuses, one with the model disabled never asks.</summary>
    private sealed class NoRouteProvider : IModelProvider;
}
