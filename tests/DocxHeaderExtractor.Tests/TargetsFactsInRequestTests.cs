using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.Tests;

[Trait("Category", "Slow")]
public sealed class TargetsFactsInRequestTests : IDisposable
{
    const string Pdf = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    static readonly Lazy<AgentSource> PdfSource = new(() => AgentSource.Load(TestRepository.Path(Pdf), out var error)
        ?? throw new InvalidOperationException(error));
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dhx-request-facts-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    IAgentSource Source(bool pdf)
    {
        if (pdf) return PdfSource.Value;
        var path = DocxTestDocument.CreateLong(Path.Combine(dir, "facts.docx"), 130);
        using (var doc = WordprocessingDocument.Open(path, true))
        {
            var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToArray();
            paragraphs[100].RemoveAllChildren();
            paragraphs[100].Append(new Run(new Text(paragraphs[2].InnerText)));
        }
        return DocxAgentSource.Load(path, null, out var error) ?? throw new InvalidOperationException(error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_target_has_exactly_the_existing_tool_fact_in_order_and_first_requests_fit_the_cap(bool pdf)
    {
        var source = Source(pdf);
        var flavor = source.Flavor;
        var styles = flavor.CreateTools(source).DocumentStyles();
        foreach (var segment in Enumerable.Range(0, source.Cores.Count))
        {
            var (targets, shown) = source.Scope(segment);
            var supplied = flavor.PayloadFacts(source, targets)!;
            var tools = flavor.CreateTools(source);
            var expected = new JsonArray();
            for (var offset = 0; offset < targets.Length;)
            {
                var response = tools.Execute("E1.01", "get_line_facts", JsonSerializer.Serialize(new { first = targets[0], last = targets[^1], offset }));
                Assert.Equal("OK", response.Status);
                var result = JsonNode.Parse(response.Content)!["result"]!;
                foreach (var fact in result["facts"]!.AsArray()) expected.Add(fact!.DeepClone());
                if (!result["hasMore"]!.GetValue<bool>()) break;
                offset = result["nextOffset"]!.GetValue<int>();
            }
            Assert.Equal(expected.ToJsonString(), supplied.ToJsonString());
            Assert.Equal(targets, supplied.Select(f => f!["alias"]!.GetValue<string>()));
            var payload = AgentProtocol.Payload(source.SourceSha256, AgentSource.SegmentId(segment), targets,
                shown.Select(i => source.Lines[i]).ToArray(), styles, flavor: flavor, targetFacts: supplied);
            var request = AgentProtocol.Build(payload, new JsonArray(), "auto", flavor);
            Assert.True(request.Bytes <= AgentBudget.MaxRequestBytes, $"segment {segment}: {request.Bytes}");
            Assert.Contains("the request carries targetFacts", flavor.SystemPrompt);
            Assert.DoesNotContain("Your first reply must be tool calls", flavor.SystemPrompt);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_model_can_select_a_body_line_on_the_first_post_and_the_program_keeps_its_decision(bool pdf)
    {
        var source = Source(pdf);
        var target = source.Scope(0).Decide[2];
        using var handler = new FakeOpenRouter((body, _) =>
        {
            var messages = body["messages"]!.AsArray();
            Assert.Equal(2, messages.Count); // no tool round or repair
            var payload = JsonNode.Parse(messages[1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Equal(payload["targetCount"]!.GetValue<int>(), payload["targetFacts"]!.AsArray().Count);
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = JsonSerializer.Serialize(new
            {
                protocolVersion = FakeOpenRouter.AnswerVersion(body), status = "COMPLETE",
                decisions = payload["targets"]!.AsArray().Select(t => t!["alias"]!.GetValue<string>()).Select(a => a == target
                    ? (object)new { alias = a, decision = "SELECT", evidenceRefs = new[] { a } }
                    : new { alias = a, decision = "NOT_SELECT" }),
            }) }, "stop"));
        });
        var budget = AgentSelector.BudgetFor(source);
        using var model = new AgentModel(handler, "sk-test", budget);
        var outcome = AgentUnit.Run(source, 0, model.Turn, budget, retryDelayMs: 0);
        Assert.Equal("COMPLETE", outcome.Status);
        Assert.Equal(1, handler.Posts);
        Assert.Empty(outcome.TargetsWithoutFacts!);
        Assert.Equal("SELECT", outcome.Decisions[target]);
        Assert.Empty(outcome.DetailEvidenceAliases!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void References_in_the_supplied_facts_are_citable_without_claiming_their_own_core_facts_were_read(bool pdf)
    {
        var source = Source(pdf);
        var flavor = source.Flavor;
        var entries = source.Cores.SelectMany((_, segment) => flavor.PayloadFacts(source, source.Scope(segment).Decide)!).ToArray();
        var entry = entries.First(f => flavor.FactReferences(f!).Any());
        var alias = entry!["alias"]!.GetValue<string>();
        var refs = flavor.FactReferences(entry).Distinct().ToArray();
        var tools = flavor.CreateTools(source);
        tools.NoteRequestFacts([alias], refs);
        Assert.True(tools.FactsWereRead);
        Assert.Equal(new[] { alias }, tools.FactsReturnedFor);
        Assert.All(refs, a => Assert.Contains(a, tools.ObservedAliases));
        Assert.Empty(tools.DetailEvidenceAliases);

        // Context facts remain available on demand and acquire their own coverage only when actually returned.
        var other = source.Lines.First(l => l.Alias != alias).Alias;
        Assert.DoesNotContain(other, tools.FactsReturnedFor);
        var response = tools.Execute("E1.01", "get_line_facts", JsonSerializer.Serialize(new { first = other, last = other, offset = 0 }));
        Assert.Equal("OK", response.Status);
        Assert.Contains(other, tools.FactsReturnedFor);
    }
}
