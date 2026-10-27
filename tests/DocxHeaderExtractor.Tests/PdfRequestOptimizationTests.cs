using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Sources.Pdf;
using SkiaSharp;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfRequestOptimizationTests
{
    sealed class Provider : IModelProvider { public T? Create<T>(string role) where T : class => null; }
    const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _,
        instruction: PdfMembershipInstructionV12.Instruction, sourceBound: true, unitSelection: true,
        dynamicCompletionBudget: true)!);

    [Fact]
    public void Column_geometry_roundtrips_every_measurement_null_peer_and_unavailable_page()
    {
        var evidence = JsonNode.Parse("""
            {"version":"V1","pages":[
              {"page":1,"semanticLabels":"NONE","status":"AVAILABLE","blankHorizontalBandsPx":[[4,8]],
               "occurrences":[{"alias":"A","pixelBox":[1,2,3,4],"horizontalInkRunsPx":[[1,2,1,4],[3,2,1,4]],"verticalOverlapPeers":["B"],"enclosingDrawnFrame":null},
                              {"alias":"B","pixelBox":[4,2,3,4],"horizontalInkRunsPx":[[4,2,3,4]],"verticalOverlapPeers":["A"],"enclosingDrawnFrame":null}]},
              {"page":2,"semanticLabels":"NONE","status":"UNAVAILABLE","reason":"RENDER_FAILED"}]}
            """)!.AsObject();
        var original = evidence.DeepClone();
        PdfRasterEvidence.Compact(evidence);
        Assert.Equal("PDF_GEOMETRY_COLUMNS_V1", evidence["encoding"]!.GetValue<string>());
        var shared = evidence["sharedPageFacts"]!.AsObject();
        foreach (var page in evidence["pages"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var field in shared) page[field.Key] = field.Value?.DeepClone();
            if (page["occurrenceColumns"] is not JsonArray columns) continue;
            page["occurrences"] = new JsonArray(page["occurrences"]!.AsArray().Select(row =>
            {
                var obj = new JsonObject();
                for (var i = 0; i < columns.Count; i++) obj[columns[i]!.GetValue<string>()] = row![i]?.DeepClone();
                return (JsonNode)obj;
            }).ToArray());
            page.Remove("occurrenceColumns");
        }
        evidence.Remove("encoding"); evidence.Remove("encodingNote"); evidence.Remove("sharedPageFacts");
        Assert.True(JsonNode.DeepEquals(original, evidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Truncated_reply_rebuilds_only_budget_and_accepts_only_complete_source_bound_answer(bool audited)
    {
        var source = Source.Value;
        var bodies = new List<JsonObject>();
        ModelTurn Model(string id, int turn, PreparedRequest request)
        {
            var wire = JsonNode.Parse(request.Body)!.AsObject(); bodies.Add(wire);
            if (bodies.Count == 1)
            {
                if (audited) throw new ReplyFailure("AUDIT:FINISH_LENGTH,REASONING_EXHAUSTED");
                return new("{\"partial\":true}", [], "length");
            }
            return RejectAll(wire);
        }
        var result = PdfUnitSelectionTask.Run(source, 0, Model, AgentSelector.BudgetFor(source, unitSelection: true), 0, PdfMembershipInstructionV12.Instruction);
        Assert.Equal("COMPLETE", result.Status);
        Assert.Equal(2, result.Posts);
        Assert.Equal(2, result.RequestSha256s!.Count);
        Assert.True(bodies[1]["max_tokens"]!.GetValue<int>() > bodies[0]["max_tokens"]!.GetValue<int>());
        foreach (var body in bodies) body.Remove("max_tokens");
        Assert.True(JsonNode.DeepEquals(bodies[0], bodies[1]));
        Assert.All(result.Decisions, d => Assert.Equal("NOT_SELECT", d.Value));
    }

    [Fact]
    public void Exhausted_completion_recovers_at_most_twice_and_never_emits_partial_decisions()
    {
        var source = Source.Value;
        var caps = new List<int>();
        var result = PdfUnitSelectionTask.Run(source, 0, (_, _, request) =>
        { caps.Add(JsonNode.Parse(request.Body)!["max_tokens"]!.GetValue<int>()); return new("{}", [], "length"); },
            AgentSelector.BudgetFor(source, unitSelection: true), 0, PdfMembershipInstructionV12.Instruction);
        Assert.NotEqual("COMPLETE", result.Status);
        Assert.InRange(caps.Count, 2, 3);
        Assert.Equal(caps.Order(), caps);
        Assert.Equal(source.ResourcePolicy.MaxCompletionTokens, caps[^1]);
        Assert.All(result.Decisions, d => Assert.Equal("UNRESOLVED", d.Value));
    }

    [Fact]
    public void Unrelated_audit_failure_is_not_retried_by_completion_recovery()
    {
        var source = Source.Value; var posts = 0;
        var result = PdfUnitSelectionTask.Run(source, 0, (_, _, _) =>
        { posts++; throw new ReplyFailure("AUDIT:FINISH_LENGTH,USAGE_INCONSISTENT"); },
            AgentSelector.BudgetFor(source, unitSelection: true), 0, PdfMembershipInstructionV12.Instruction);
        Assert.Equal(1, posts); Assert.NotEqual("COMPLETE", result.Status);
    }

    [Fact]
    public void Smaller_image_uses_same_full_page_and_aspect_ratio_with_lower_byte_cost()
    {
        var path = TestRepository.Path(Pdf);
        var baseline = PdfVisualSelectionImages.RenderJpegOverviews(path, [14]);
        var smaller = PdfVisualSelectionImages.RenderJpegOverviews(path, [14], maxWidth: 960, quality: 50);
        using var a = SKBitmap.Decode(baseline[14]); using var b = SKBitmap.Decode(smaller[14]);
        Assert.InRange(b.Width, 1, 960);
        Assert.True(b.Width < a.Width);
        Assert.InRange(Math.Abs((double)a.Height / a.Width - (double)b.Height / b.Width), 0, .002);
        Assert.True(smaller[14].Length < baseline[14].Length);
    }

    [Fact]
    public void Grayscale_image_preserves_page_dimensions_and_is_not_larger_than_color_at_same_encoding()
    {
        var path = TestRepository.Path(Pdf);
        var color = PdfVisualSelectionImages.RenderJpegOverviews(path, [14], maxWidth: 960, quality: 50);
        var gray = PdfVisualSelectionImages.RenderJpegOverviews(path, [14], maxWidth: 960, quality: 50, grayscale: true);
        using var a = SKBitmap.Decode(color[14]); using var b = SKBitmap.Decode(gray[14]);
        Assert.Equal(a.Width, b.Width); Assert.Equal(a.Height, b.Height);
        Assert.True(gray[14].Length <= color[14].Length);
    }

    internal static ModelTurn RejectAll(JsonObject wire)
    {
        var packet = JsonNode.Parse(wire["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
        return new(JsonSerializer.Serialize(new { protocolVersion = PdfUnitSelectionProtocol.Version, status = "COMPLETE",
            decisions = packet["targetUnits"]!.AsArray().Select(u => new { unitId = u!["unitId"]!.GetValue<string>(),
                decision = "NOT_SELECT", selected = Array.Empty<object>(), reason = "Synthetic protocol test; no semantic quality claim." }) }), [], "stop");
    }

    [Fact]
    public void Frozen_pdf_profile_is_pdf_only_and_versioned_with_unchanged_response_and_instruction()
    {
        var baseline = HeadingAgentSetup.Create(new PipelineOptions(), new Provider());
        var tuned = HeadingAgentSetup.Create(new PipelineOptions { PdfMembershipInstructionVersion = 14,
            PdfAdaptiveVisualSelection = true, PdfVisualSelectionMaxRequestBytes = 786432, PdfCompactGeometry = true,
            PdfVisualMaxWidth = 960, PdfVisualQuality = 50, PdfVisualGrayscale = true, PdfDynamicCompletionBudget = true, PdfJobs = 8 }, new Provider());
        Assert.Equal(baseline.Docx!.SelectorId, tuned.Docx!.SelectorId);
        Assert.Equal(baseline.Scan!.SelectorId, tuned.Scan!.SelectorId);
        Assert.Contains("geometry-columns-v1:visual-w960-q50:visual-gray-v1:unit-completion-adaptive-v1", baseline.Pdf!.SelectorId);
        Assert.Equal(8, new PipelineOptions().PdfJobs);
        Assert.Contains("geometry-columns-v1:visual-w960-q50:visual-gray-v1:unit-completion-adaptive-v1", tuned.Pdf!.SelectorId);
        Assert.Contains("instruction-" + AgentProtocol.Sha(PdfMembershipInstructionV12.Instruction)[..8], tuned.Pdf.SelectorId);
        Assert.Equal("PDF_UNIT_SELECT_SOURCE_BOUND_V2", PdfUnitSelectionProtocol.Version);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task Transport_gate_admits_configured_count_and_blocks_next_request_until_release(int concurrency)
    {
        var gate = new ProviderRequestGate(concurrency);
        var held = Enumerable.Range(0, concurrency).Select(_ => gate.Enter(TimeSpan.FromSeconds(5), default)).ToArray();
        using var entered = new ManualResetEventSlim();
        using var attempting = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = Task.Run(() => { attempting.Set(); using var lease = gate.Enter(TimeSpan.FromSeconds(5), stop.Token); entered.Set(); });
        try
        {
            Assert.True(attempting.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(entered.Wait(TimeSpan.FromMilliseconds(100)));
            held[0].Dispose();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await next;
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    }
}
