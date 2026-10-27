using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

namespace DocxHeaderExtractor.Tests;

public sealed class SelectionConcurrencyTests
{
    const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _, sourceBound: true)!);

    [Fact]
    public void Out_of_order_scope_completions_preserve_all_aliases_requests_and_decisions()
    {
        var source = Source.Value;
        var coverage = source.Cores.SelectMany((_, i) => source.Scope(i).Decide).ToArray();
        Assert.Equal(source.Lines.Select(l => l.Alias), coverage);
        Assert.Equal(coverage.Length, coverage.Distinct().Count());
        var expectedSelected = source.Cores.SelectMany((_, i) =>
            new[] { source.Scope(i).Decide[0], source.Scope(i).Decide[^1] }).ToHashSet();
        var expectedDecisions = coverage.ToDictionary(a => a, a => expectedSelected.Contains(a) ? "SELECT" : "NOT_SELECT");
        var results = new List<DocxHeaderExtractor.Contracts.Agents.HeadingSelection>();
        var requests = new List<Dictionary<string, string>>();
        foreach (var jobs in new[] { 1, 6, 8 })
        {
            var sent = new ConcurrentDictionary<string, string>();
            var barrier = jobs == 1 ? null : new CountdownEvent(Math.Min(jobs, source.Cores.Count));
            var release = new ManualResetEventSlim(jobs == 1);
            var active = 0;
            ModelTurn Model(string unit, int turn, PreparedRequest request)
            {
                var packet = JsonNode.Parse(JsonNode.Parse(request.Body)!["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
                if (packet["stage"]!.GetValue<string>() == "GROUP")
                    return new(JsonSerializer.Serialize(new { protocolVersion = "PDF_HEADING_GROUPS_V1",
                        runs = packet["runs"]!.AsArray().Select(r => new { run = r!["run"]!.GetValue<string>(),
                            headings = r["lines"]!.AsArray().Select(l => new[] { l!["alias"]!.GetValue<string>() }) }) }), [], "stop");
                Assert.Equal(1, turn);
                Assert.True(sent.TryAdd(unit, request.Sha256));
                if (barrier is not null)
                {
                    var n = Interlocked.Increment(ref active);
                    if (n <= Math.Min(jobs, source.Cores.Count))
                    {
                        barrier.Signal();
                        if (barrier.CurrentCount == 0) release.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                    }
                }
                Thread.Sleep((source.Cores.Count - int.Parse(unit.AsSpan(4))) % 5 * 3);
                return new(JsonSerializer.Serialize(new { protocolVersion = SourceBoundSelection.Version, status = "COMPLETE",
                    decisions = packet["targets"]!.AsArray().Select(t =>
                    {
                        var a = t!["alias"]!.GetValue<string>();
                        return expectedSelected.Contains(a)
                            ? (object)new { alias = a, decision = "SELECT", sourceText = t["text"]!.GetValue<string>(), evidenceRefs = new[] { a } }
                            : new { alias = a, decision = "NOT_SELECT" };
                    }) }), [], "stop");
            }
            var result = AgentSelector.Select(source, Model, AgentSelector.BudgetFor(source, PdfAgentProtocol.Instruction), jobs,
                CancellationToken.None, 0, () => 0, PdfAgentProtocol.Instruction);
            Assert.Equal(source.Cores.Count, result.UnitsComplete);
            Assert.Empty(result.UnitFailures);
            Assert.True(expectedSelected.SetEquals(result.SelectedAliases));
            Assert.Equal(coverage.Length, result.AliasStates!.Count);
            Assert.All(expectedDecisions, d => Assert.Equal(d.Value, result.AliasStates[d.Key].FinalSelection));
            results.Add(result); requests.Add(sent.ToDictionary());
            barrier?.Dispose(); release.Dispose();
        }
        Assert.All(requests.Skip(1), r => Assert.Equal(requests[0].OrderBy(p => p.Key), r.OrderBy(p => p.Key)));
        Assert.All(results.Skip(1), r => Assert.Equal(results[0].ModelAnswers, r.ModelAnswers));
    }

    [Fact]
    public void Clarification_is_opt_in_has_a_distinct_identity_and_respects_an_explicit_instruction()
    {
        var normal = new PdfSelector(sourceBound: true);
        var candidate = new PdfSelector(sourceBound: true, clarifyMembership: true);
        Assert.NotEqual(normal.SelectorId, candidate.SelectorId);
        Assert.Equal(new PdfSelector(instruction: "User rule").SelectorId,
            new PdfSelector(instruction: "User rule", clarifyMembership: true).SelectorId);
        Assert.StartsWith(PdfAgentProtocol.Instruction, PdfMembershipInstructionV2.Instruction);
        Assert.DoesNotContain("L0513", PdfMembershipInstructionV2.Instruction);
        Assert.DoesNotContain("L0519", PdfMembershipInstructionV2.Instruction);
    }
}
