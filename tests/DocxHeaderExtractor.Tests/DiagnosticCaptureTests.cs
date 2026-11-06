using System.Net;
using System.Text;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.Tests;

public sealed class DiagnosticCaptureTests
{
    [Fact]
    public async Task Placement_repair_has_its_own_captured_wire_attempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-repair-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lines = Enumerable.Range(0, 3).Select(i => new SemanticSourceAtom("H" + i, "id:" + i, i, 1, i, 0, "Heading " + i)).ToArray();
            var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text,
                "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, l.Text.Length)).ToArray();
            var evidence = new HeadingPlacementEvidence("{}", new Dictionary<string, string>())
                { Source = new(lines, new Dictionary<string, HeadingAliasState>()) };
            using var fake = new FakeOpenRouter((body, _) =>
            {
                var packet = JsonNode.Parse(body["messages"]![1]!["content"]!.Text())!;
                var repair = packet["stage"]!.GetValue<string>() == "HIERARCHY_CHAIN_REPAIR";
                var content = JsonSerializer.Serialize(new { protocolVersion = HeadingPlacer.Version,
                    placements = packet["toPlace"]!.AsArray().Select(a => new { alias = a!.GetValue<string>(),
                        parent = a.GetValue<string>() == "H0" ? (repair ? "ROOT" : "NONE") : "H0" }) });
                return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content }, "stop").Replace("\"cost\":0.001", "\"cost\":0.0000001"));
            }, autoVerify: false);
            var capture = new CaptureRun(root, "sk-route");
            using var handler = capture.Handler("test", "PLACE", fake);
            var result = await ParentResolver.PlaceAsync(bound, new HeadingPlacer(handler, repairParentChains: true),
                new ModelAccess("sk-route", () => { }), evidence, CancellationToken.None);
            Assert.True(result.Observation.ChainRepair?.LogicalCalls > 0);
            var indices = Directory.GetFiles(root, "*.index.json", SearchOption.AllDirectories)
                .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone()).ToArray();
            Assert.Contains(indices, i => i.GetProperty("stage").GetString() == "HIERARCHY_CHAIN_REPAIR" &&
                !string.IsNullOrWhiteSpace(i.GetProperty("unit").GetString()) && i.GetProperty("responseAvailable").GetBoolean());
            Assert.True(capture.Summary().PlacementWireCalls >= 2);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Selector_and_group_wire_are_recorded_with_model_reply_units()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-selector-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var doc = DocxTestDocument.CreateLong(Path.Combine(root, "input.docx"), 20);
            var capture = new CaptureRun(Path.Combine(root, "evidence"), "sk-test");
            var replies = new List<ModelReply>();
            var selector = new DocxSelector(() => capture.Handler("doc", "SELECT_GROUP",
                new FakeOpenRouter(FakeOpenRouter.FactsThenFirstTwoTargetsThenGroup)), parallelism: 1,
                onModelReply: r => replies.Add(r));
            var result = await selector.SelectAsync(doc, "sk-test", CancellationToken.None);
            var indices = Directory.GetFiles(Path.Combine(root, "evidence"), "*.index.json", SearchOption.AllDirectories)
                .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone()).ToArray();
            Assert.NotEmpty(result.SelectedAliases);
            Assert.Contains(indices, i => i.GetProperty("stage").GetString() == "SEGMENT");
            Assert.Contains(indices, i => i.GetProperty("stage").GetString() == "GROUP");
            Assert.All(indices, i =>
            {
                Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("unit").GetString()));
                Assert.Equal(JsonValueKind.Number, i.GetProperty("turn").ValueKind);
                Assert.Equal(JsonValueKind.Number, i.GetProperty("retry").ValueKind);
            });
            Assert.NotEmpty(replies);
            Assert.All(replies, r => Assert.False(string.IsNullOrWhiteSpace(r.Unit)));
            Assert.Equal(0, capture.Summary().MissingResponses);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Placement_wire_and_protocol_errors_are_available_without_a_provider(bool malformed)
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-placement-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (bound, evidence, _) = HeadingPlacerTests.Fixture();
            using var fake = new FakeOpenRouter((body, _) =>
            {
                var packet = body["messages"]![1]!["content"]!.Text();
                var content = malformed ? "{\"invalid\":true}" : HierarchyTestProtocol.Reply(packet);
                return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content }, "stop").Replace("\"cost\":0.001", "\"cost\":0.0000001"));
            }, autoVerify: false);
            var capture = new CaptureRun(root, "sk-route");
            using var handler = capture.Handler("test", "PLACE", fake);
            var result = await ParentResolver.PlaceAsync(bound, new HeadingPlacer(handler), new ModelAccess("sk-route", () => { }),
                evidence, CancellationToken.None);
            var indices = Directory.GetFiles(root, "*.index.json", SearchOption.AllDirectories);
            Assert.NotEmpty(indices);
            Assert.All(indices, f =>
            {
                using var index = JsonDocument.Parse(File.ReadAllText(f));
                Assert.True(index.RootElement.GetProperty("responseAvailable").GetBoolean());
                Assert.Equal("stop", index.RootElement.GetProperty("finishReason").GetString());
                Assert.False(string.IsNullOrWhiteSpace(index.RootElement.GetProperty("unit").GetString()));
                Assert.Equal(JsonValueKind.Number, index.RootElement.GetProperty("turn").ValueKind);
                Assert.Equal(JsonValueKind.Number, index.RootElement.GetProperty("retry").ValueKind);
            });
            Assert.Equal(indices.Length, Directory.GetFiles(root, "*.response.json", SearchOption.AllDirectories).Length);
            if (malformed) Assert.Contains(result.Observation.Calls.SelectMany(c => c.ProtocolErrors), _ => true);
            else Assert.DoesNotContain(result.Observation.Calls.SelectMany(c => c.ProtocolErrors), _ => true);
            Assert.DoesNotContain(Directory.GetFiles(root, "*", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("sk-route"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    sealed class StreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n") });
    }

    [Fact]
    public async Task Streaming_response_finish_reason_is_recorded()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-capture-sse-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CaptureRun(root, "secret-key-example");
            using var client = new HttpClient(capture.Handler("test", "SELECT", new StreamHandler()));
            using var response = await client.PostAsync("https://example.invalid", new StringContent("{}"));
            var index = Directory.GetFiles(root, "*.index.json", SearchOption.AllDirectories).Single();
            Assert.Contains("\"finishReason\":\"stop\"", File.ReadAllText(index));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    sealed class FakeHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => fail
            ? throw new HttpRequestException("test failure")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\"}]}") });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_records_wire_or_missing_response_without_headers(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-capture-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CaptureRun(root, "secret-key-example");
            using var client = new HttpClient(capture.Handler("test", "SELECT", new FakeHandler(fail)));
            client.DefaultRequestHeaders.Add("Authorization", "Bearer secret-key-example");
            using var body = new StringContent("{}", Encoding.UTF8, "application/json");
            if (fail) await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("https://example.invalid", body));
            else using (await client.PostAsync("https://example.invalid", body)) { }
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("secret-key-example"));
            Assert.Contains(files, f => f.EndsWith(".request.json"));
            Assert.Equal(!fail, files.Any(f => f.EndsWith(".response.json")));
            Assert.Contains(files, f => f.EndsWith(".index.json") && File.ReadAllText(f).Contains(fail ? "\"responseAvailable\":false" : "\"responseAvailable\":true"));
            Assert.Equal(fail ? 1 : 0, capture.Summary().MissingResponses);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Capture_rejects_credential_in_body_before_writing()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-capture-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CaptureRun(root, "secret-key-example");
            Assert.Throws<InvalidDataException>(() => capture.Record("unsafe.json", new { text = "secret-key-example" }));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void The_capture_cost_ceiling_applies_only_when_capture_is_on_and_must_be_positive()
    {
        var outside = Path.Combine(Path.GetTempPath(), "dhx-capture-cap-" + Guid.NewGuid().ToString("N"));
        Assert.Null(new DiagnosticCaptureSettings(null, Path.GetTempPath(), "0.5").MaxRunUsd);
        Assert.Null(new DiagnosticCaptureSettings(outside, Path.GetTempPath()).MaxRunUsd);
        Assert.Equal(0.5, new DiagnosticCaptureSettings(outside, Path.GetTempPath(), "0.5").MaxRunUsd);
        Assert.Throws<InvalidOperationException>(() => new DiagnosticCaptureSettings(outside, Path.GetTempPath(), "0"));
        Assert.Throws<InvalidOperationException>(() => new DiagnosticCaptureSettings(outside, Path.GetTempPath(), "abc"));
    }
}
