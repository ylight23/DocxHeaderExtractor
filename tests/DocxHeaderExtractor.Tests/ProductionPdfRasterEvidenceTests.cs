using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.HeadingAgent.Grouping;

namespace DocxHeaderExtractor.Tests;

public sealed class ProductionPdfRasterEvidenceTests
{
    const string Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    static readonly Lazy<AgentSource> Source = new(() => AgentSource.Load(TestRepository.Path(Pdf), out _,
        instruction: PdfMembershipInstructionV12.Instruction, sourceBound: true, rasterGeometry: true)!);
    sealed class Provider : IModelProvider { public T? Create<T>(string role) where T : class => null; }

    [Fact]
    public void Default_application_pdf_route_enables_geometry_and_explicit_opt_out_restores_legacy_identity()
    {
        var routes = HeadingAgentSetup.CreateRoutes(new PipelineOptions(), new Provider());
        var legacy = HeadingAgentSetup.CreateRoutes(new PipelineOptions { PdfRasterGeometryEvidence = false }, new Provider());
        Assert.True(((PdfSelector)routes.NativePdf.Pdf!).RasterGeometryEnabled);
        Assert.False(((PdfSelector)legacy.NativePdf.Pdf!).RasterGeometryEnabled);
        Assert.Contains("pdf-raster-geometry-v1:", routes.NativePdf.Pdf.SelectorId);
        Assert.DoesNotContain("pdf-raster-geometry-v1:", legacy.NativePdf.Pdf.SelectorId);
        Assert.Equal(routes.Shared.Docx!.SelectorId, legacy.Shared.Docx!.SelectorId);
        Assert.Equal(routes.Shared.Scan!.SelectorId, legacy.Shared.Scan!.SelectorId);
        Assert.Equal(14, new PipelineOptions().PdfMembershipInstructionVersion);
    }

    [Fact]
    public void All_production_first_packets_include_geometry_before_planning_and_match_actual_conversation_bytes()
    {
        var source = Source.Value;
        var ordinary = AgentSource.Load(TestRepository.Path(Pdf), out _, instruction: PdfMembershipInstructionV12.Instruction, sourceBound: true)!;
        Assert.Equal(ordinary.Lines.Select(l => (l.Alias, l.Text)), source.Lines.Select(l => (l.Alias, l.Text)));
        var owned = source.Cores.SelectMany(c => source.Lines.Skip(c.Start).Take(c.End - c.Start).Select(l => l.Alias)).ToArray();
        Assert.Equal(source.Lines.Select(l => l.Alias), owned);
        foreach (var i in Enumerable.Range(0, source.Cores.Count))
        {
            var planned = AgentUnit.PrepareFirstRequest(source, i, PdfMembershipInstructionV12.Instruction);
            var actual = new SelectionTask(source, i, PdfMembershipInstructionV12.Instruction, null, null).Build([], "auto");
            Assert.Equal(planned.Sha256, actual.Sha256);
            Assert.Equal(source.Plan[i].RequestSha256, actual.Sha256);
            Assert.Equal(source.Plan[i].FirstRequestBytes, actual.Bytes);
            Assert.Null(source.ResourcePolicy.Refusal(RequestResources.Measure(actual, source.ResourcePolicy), reserveContinuation: true));
            var wire = JsonNode.Parse(actual.Body)!;
            var packet = JsonNode.Parse(wire["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.Equal("NONE", packet["pageGeometryEvidence"]!["semanticLabels"]!.GetValue<string>());
            Assert.Equal(SourceBoundSelection.Version, source.Flavor.AnswerVersion);
        }
        Assert.False(source.Flavor.RequiresPageLayoutEvidence);
        Assert.Equal("AVAILABLE", source.RasterGeometry[14]["status"]!.GetValue<string>());
        Assert.Equal(33, source.RasterGeometry[14]["occurrences"]!.AsArray().Count);
    }

    [Fact]
    public void Adaptive_visual_pdf_packets_are_budgeted_before_scheduling_and_carry_one_page_image()
    {
        var policy = new RequestPolicy { MaxRequestBytes = 786432, ContinuationReserveBytes = 0 };
        var source = AgentSource.Load(TestRepository.Path(Pdf), out var failure, policy,
            PdfMembershipInstructionV12.Instruction, sourceBound: true, rasterGeometry: true,
            adaptiveVisualSelection: true);
        Assert.Equal("OK", failure);
        Assert.NotNull(source);
        foreach (var i in Enumerable.Range(0, source!.Cores.Count))
        {
            var request = AgentUnit.PrepareFirstRequest(source, i, PdfMembershipInstructionV12.Instruction);
            var actual = new SelectionTask(source, i, PdfMembershipInstructionV12.Instruction, null, null).Build([], "auto");
            Assert.Equal(request.Sha256, actual.Sha256);
            Assert.Equal(source.Plan[i].RequestSha256, actual.Sha256);
            Assert.Equal(source.Plan[i].FirstRequestBytes, actual.Bytes);
            Assert.True(actual.Bytes <= policy.MaxRequestBytes);
            var content = JsonNode.Parse(actual.Body)!["messages"]![1]!["content"]!.AsArray();
            var images = content.Where(x => x!["type"]!.GetValue<string>() == "image_url").ToArray();
            Assert.Single(images);
            Assert.StartsWith("data:image/jpeg;base64,", images[0]!["image_url"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scoped_geometry_contains_only_shown_aliases_and_does_not_mutate_input()
    {
        var source = Source.Value;
        var alias = source.Lines.First(l => l.Page == 14).Alias;
        var packet = new JsonObject { ["targets"] = new JsonArray(new JsonObject { ["alias"] = alias }) };
        var scoped = JsonNode.Parse(PdfRasterEvidence.Attach(packet.ToJsonString(), source))!;
        var page = scoped["pageGeometryEvidence"]!["pages"]!.AsArray().Single()!;
        Assert.Equal(alias, page["occurrences"]!.AsArray().Single()!["alias"]!.GetValue<string>());
        Assert.All(page["occurrences"]!.AsArray(), o =>
        {
            Assert.Empty(o!["verticalOverlapPeers"]!.AsArray());
            Assert.Empty(o["nearestLeftAlignedPeers"]!.AsArray());
        });
        Assert.Null(JsonNode.Parse(packet.ToJsonString())!["pageGeometryEvidence"]);
    }

    [Fact]
    public void Rendering_failure_is_explicitly_unavailable_and_contains_no_fabricated_occurrences()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        var data = DocxHeaderExtractor.Sources.Pdf.PdfGeometryMeasurements.MeasureDocument(missing,
            [new("A", 1, 10, 20, 30, 40)]);
        Assert.Equal("UNAVAILABLE", data[1]["status"]!.GetValue<string>());
        Assert.StartsWith("RENDER_FAILED:", data[1]["reason"]!.GetValue<string>());
        Assert.Null(data[1]["occurrences"]);
    }

    [Fact]
    public void Pairwise_grouping_receives_geometry_and_keeps_all_selected_aliases()
    {
        var source = Source.Value;
        var selected = source.Lines.Where(l => l.Page == 14).Take(2).Select(l => l.Alias).ToHashSet();
        var calls = 0;
        ModelTurn Model(string unit, int turn, PreparedRequest request)
        {
            calls++;
            var wire = JsonNode.Parse(request.Body)!;
            var packet = JsonNode.Parse(wire["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!;
            Assert.NotEmpty(packet["pageGeometryEvidence"]!["pages"]!.AsArray());
            return new(JsonSerializer.Serialize(new { protocolVersion = PdfBoundaryDecisions.Version,
                runs = packet["runs"]!.AsArray().Select(run => new { run = run!["run"]!.GetValue<string>(),
                    boundaries = run["boundaryTargets"]!.AsArray().Select(pair => new {
                        leftAlias = pair!["leftAlias"]!.GetValue<string>(), rightAlias = pair["rightAlias"]!.GetValue<string>(),
                        leftText = source.Lines.Single(l => l.Alias == pair["leftAlias"]!.GetValue<string>()).Text,
                        rightText = source.Lines.Single(l => l.Alias == pair["rightAlias"]!.GetValue<string>()).Text,
                        decision = "SPLIT", reason = "Synthetic boundary integration check" }) }) }), [], "stop");
        }
        var result = AgentGrouping.Group(source, selected, Model, AgentSelector.BudgetFor(source), 1,
            CancellationToken.None, 0, PdfBoundaryInstruction.Instruction, pairwisePdf: true);
        Assert.Equal(1, calls);
        Assert.Empty(result.Failures);
        Assert.Equal(selected.Order(), result.Groups.SelectMany(g => g).Order());
    }
}
