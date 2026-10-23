using System.Net;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Problems;
using DocxHeaderExtractor.Entities;

namespace DocxHeaderExtractor.Tests;

/// <summary>The entity problem on the shared frame, with the model answered by a fake: nothing reaches a provider.</summary>
public sealed class EntityExtractionTests : IDisposable
{
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dhx-entities-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    string Sample() => DocxTestDocument.Create(Path.Combine(dir, "sample.docx"));

    static DocumentSourceUnit[] Units(string path) =>
        ProblemRunner.ReadText(path, ProblemRunner.SourceKind.Docx, new PipelineOptions(), default).Catalog.Units.ToArray();

    /// <summary>A fake that answers every POST with <paramref name="content"/>, and keeps the request bodies it was sent.</summary>
    static FakeOpenRouter Reply(string content, List<JsonObject>? bodies = null) => new((body, _) =>
    {
        bodies?.Add(body);
        return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content }, "stop"));
    });

    [Fact]
    public async Task The_checker_keeps_spans_inside_the_text_and_refuses_the_rest_with_a_reason()
    {
        var path = Sample();
        var u = Units(path)[0];
        var end = Math.Min(5, u.Text.Length);
        var json = "{\"entities\":[" +
            $"{{\"sourceId\":\"{u.SourceId}\",\"start\":0,\"end\":{end},\"type\":\"PERSON\"}}," +
            $"{{\"sourceId\":\"{u.SourceId}\",\"start\":0,\"end\":{end},\"type\":\"PERSON\"}}," +
            $"{{\"sourceId\":\"{u.SourceId}\",\"start\":0,\"end\":9999,\"type\":\"PERSON\"}}," +
            "{\"sourceId\":\"nowhere\",\"start\":0,\"end\":1,\"type\":\"PERSON\"}," +
            $"{{\"sourceId\":\"{u.SourceId}\",\"start\":0,\"end\":3,\"type\":\"WRONG\"}}]}}";
        using var handler = Reply(json);

        var run = await ProblemRunner.RunAsync(path, new PipelineOptions(), new ScriptedProvider("sk-entities-test-key"), "entities",
            new EntityProposer(() => handler), new EntityChecker());

        var output = run.Output!;
        Assert.Equal(u.Text[..end], Assert.Single(output.Entities).Text);
        Assert.Equal(["DUPLICATE", "SPAN_OUTSIDE_TEXT", "UNKNOWN_SOURCE", "TYPE_UNKNOWN"], output.Refused.Select(r => r.Reason));
        Assert.Empty(output.BatchFailures);
        Assert.Equal(("entities", "entities-v1"), (run.Envelope.Problem, run.Envelope.Contract));
    }

    [Fact]
    public async Task A_reply_that_is_not_json_fails_its_batch_and_the_run_still_completes()
    {
        var path = Sample();
        using var handler = Reply("this is not json");

        var run = await ProblemRunner.RunAsync(path, new PipelineOptions(), new ScriptedProvider("sk-entities-test-key"), "entities",
            new EntityProposer(() => handler), new EntityChecker());

        Assert.Empty(run.Output!.Entities);
        Assert.Equal(["batch-001:REPLY_NOT_JSON"], run.Output.BatchFailures);
    }

    [Fact]
    public async Task The_request_carries_the_types_and_every_unit_of_the_document()
    {
        var path = Sample();
        var bodies = new List<JsonObject>();
        using var handler = Reply("{\"entities\":[]}", bodies);

        await ProblemRunner.RunAsync(path, new PipelineOptions(), new ScriptedProvider("sk-entities-test-key"), "entities",
            new EntityProposer(() => handler), new EntityChecker());

        var payload = JsonNode.Parse(bodies.Single()["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(EntityTypes.All, payload["types"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(Units(path).Select(u => u.SourceId), payload["units"]!.AsArray().Select(x => x!["sourceId"]!.GetValue<string>()));
    }

    [Fact]
    public void Units_are_packed_into_batches_by_characters_and_by_count()
    {
        static DocumentSourceUnit Unit(int i, int length) => new($"U{i:000}", i, new string('a', length),
            new SourceAnchor { SourceType = "docx" }, new StructuralSpan(0, length));

        // Two of 2500 characters fit in one batch of 6000; the third starts another.
        var bySize = EntityProposer.Batches(Enumerable.Range(0, 3).Select(i => Unit(i, 2500)));
        Assert.Equal([2, 1], bySize.Select(b => b.Count));

        // Forty-five short units: the count limit makes a batch of 40 and one of 5.
        var byCount = EntityProposer.Batches(Enumerable.Range(0, 45).Select(i => Unit(i, 10)));
        Assert.Equal([40, 5], byCount.Select(b => b.Count));
    }
}
