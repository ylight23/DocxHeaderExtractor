using System.Net;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

public sealed class StructuredInferenceTransportTests
{
    [Theory]
    [InlineData("stop", 32768, 16896)]
    [InlineData("length", 8192, 8192)]
    [InlineData("content_filter", 32768, 16896)]
    public async Task Explicit_completion_cap_and_finish_reason_survive_the_real_SSE_transport_without_content_retries(
        string finish, int configuredCap, int expectedCap)
    {
        using var handler = new FakeOpenRouter((body, _) =>
        {
            Assert.Equal(expectedCap, body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{}" }, finish));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions
        { ApiKey = "sk-test", MaxOutputTokens = configuredCap });
        var reply = await transport.CompleteStructuredAsync("Return one JSON object.", "{}", 16896);
        Assert.Equal("{}", reply.Content);
        Assert.Equal(finish, reply.FinishReason);
        Assert.Equal(expectedCap, reply.MaxOutputTokens);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task Legacy_boundary_calls_keep_the_existing_per_item_budget()
    {
        using var handler = new FakeOpenRouter((body, _) =>
        {
            Assert.Equal(96 + 16 * 128, body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = "{}" }, "stop"));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions { ApiKey = "sk-test" });
        Assert.Equal("{}", await transport.CompleteAsync("Return JSON.", "{}", expectedItemCount: 16));
    }

    [Fact]
    public async Task Production_agent_route_wrapper_preserves_the_structured_completion_capability()
    {
        var factory = new InferenceTransportFactory(new InferenceProviderSelection
        {
            Backend = InferenceBackend.OpenRouter,
            Remote = new RemoteInferenceOptions { ApiKey = "sk-test", OpenRouterProviderRoute = RemoteInferenceOptions.DefaultProviderRoute }
        });
        using var transport = await factory.CreateAgentRouteAsync();
        Assert.IsAssignableFrom<IStructuredInferenceTransport>(transport);
    }

    [Theory]
    [InlineData("stop", 32768, 2144)]
    [InlineData("stop", 1024, 1024)]
    [InlineData("length", 32768, 2144)]
    [InlineData("length", 1024, 1024)]
    public async Task Hierarchy_proposal_preserves_budget_metadata_through_the_lane_lease_without_verifier_calls(
        string finish, int configuredCap, int expectedCap)
    {
        var lines = Enumerable.Range(0, 16).Select(i => new SemanticSourceAtom("L" + i, "S" + i, i, 1, i, 0, "Section " + i)).ToArray();
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
            "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
        var evidence = new HeadingPlacementEvidence("{}", new Dictionary<string, string>())
        { Source = new(lines, new Dictionary<string, DocxHeaderExtractor.Contracts.Agents.HeadingAliasState>()) };
        using var handler = new FakeOpenRouter((body, _) =>
        {
            var packet = body["messages"]![1]!["content"]!.Text();
            Assert.Equal("HIERARCHY_PLACE", JsonNode.Parse(packet)!["stage"]!.GetValue<string>());
            Assert.Equal(expectedCap, body["max_tokens"]!.GetValue<int>());
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = HierarchyTestProtocol.Reply(packet) }, finish));
        }, autoVerify: false);
        using var http = new HttpClient(handler);
        using var transport = new OpenRouterInferenceTransport(http, new RemoteInferenceOptions { ApiKey = "sk-test", MaxOutputTokens = configuredCap });
        using var leaseBound = LeaseTransport.Bind(transport, new LaneLease());
        var result = await ParentResolver.PlaceAsync(bound, new HeadingPlacer(), leaseBound, evidence, CancellationToken.None);
        var completed = finish == "stop";
        Assert.Equal(0, result.Observation.VerifiedCount);
        Assert.Equal(completed ? 16 : 0, result.Observation.PlacedCount);
        Assert.Equal(completed ? 0 : 16, result.Observation.UnresolvedCount);
        // A reply cut at the length limit comes back the same on the next round, so the rounds stop after one repeat.
        Assert.Equal(completed ? 1 : 2, handler.Posts);
        Assert.All(result.Observation.Calls, c =>
        {
            Assert.Equal(finish, c.FinishReason);
            Assert.Equal(2144, c.RequestedMaxOutputTokens);
            Assert.Equal(expectedCap, c.MaxOutputTokens);
            if (!completed)
            {
                Assert.Empty(c.AcceptedAliases);
                Assert.Contains("FINISH_REASON_LENGTH", c.ProtocolErrors);
            }
        });
        Assert.All(result.Observation.Calls, c => Assert.Equal("HIERARCHY_PLACE", c.Phase));
    }
}
