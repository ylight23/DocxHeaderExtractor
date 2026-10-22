using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Sources.Detect;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Scan;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Web;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

[Trait("Category", "Slow")]
public sealed class ScanProductionPipelineTests
{
    static string ScanPath => TestRepository.Path("todo10_8/generated-docx/01_phap_quy/023_ND_23-2015_Chung_thuc.docx");

    static ScanDocumentSelection Prepared(string path)
    {
        ScanSourceLine Line(int n, string primary, string text) => new($"P001-L{n:000}", $"P001-ocr-original-L{n:000}", 1,
            new PixelBox(100, 100 * n, 900, 50), 1500, 2100, text, primary, "vlm-transcribed",
            new Dictionary<string, string> { ["ocr-original"] = primary, ["vlm-transcribed"] = text }, "ACCEPTED", "{}");
        ScanSourceLine[] lines = [Line(1, "Chuong I", "Chương I"), Line(2, "QUY DINH CHUNG", "QUY ĐỊNH CHUNG"),
            Line(3, "Dieu 1. Pham vi. Noi dung", "Điều 1. Phạm vi. Nội dung"), Line(4, "Noi dung than bai", "Nội dung thân bài")];
        var selection = new HeadingSelection(lines.Take(3).Select(l => l.Alias).ToHashSet(), 1, 1, [], [], 2, .01,
            new HeadingPlacementContext("{}", new Dictionary<string, string>(), ScanAgentProtocol.PlacementNote),
            new IReadOnlyList<string>[] { new[] { lines[0].Alias, lines[1].Alias }, new[] { lines[2].Alias } },
            new Dictionary<string, TextOffsetSpan> { [lines[2].Alias] = new(0, "Điều 1. Phạm vi".Length) })
        {
            AliasStates = lines.ToDictionary(l => l.Alias, l => l.Alias == lines[3].Alias
                ? new HeadingAliasState("NOT_SELECT", "NOT_SELECT", null, "NOT_SELECTED")
                : new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"))
        };
        return new(SourceHash.Compute(path), lines, selection,
            new(3, .002, 2, .01, 4, 4, new Dictionary<string, int> { ["ACCEPTED"] = 4 }, [], [], [], new Dictionary<string, double>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_runner_routes_scanned_documents_into_shared_structure_sections_chunks_and_source_evidence(bool pdf)
    {
        var path = pdf ? ScanPath.Replace("generated-docx", "heading_corpus_100").Replace(".docx", ".pdf") : ScanPath;
        var scan = new FixedScan(Prepared(path));
        var factory = new Factory(scan);
        var runner = new DocumentRunner(new PipelineOptions(), factory);
        var run = await runner.ExtractAsync(path);

        Assert.Equal(1, scan.Calls);
        Assert.Equal("test-route-key", scan.Key);
        Assert.Equal("scan-v6-selected-lines", run.Outline.DeterministicRoute);
        Assert.Equal("scan", run.Result.DocumentIdentity.SourceKind);
        Assert.Equal(4, run.Result.SourceCatalog.Units.Count);
        Assert.Equal(new[] { "Chương I QUY ĐỊNH CHUNG", "Điều 1. Phạm vi" }, run.Outline.Headings.Select(h => h.Text));
        Assert.Equal(new int?[] { 1, 2 }, run.Outline.Headings.Select(h => h.Level));
        Assert.NotEmpty(run.Result.Structure.Relations);
        Assert.NotEmpty(run.Result.Sections);
        Assert.Contains(run.Result.Chunks, c => c.Text.Contains("Nội dung thân bài", StringComparison.Ordinal));
        Assert.Equal(2, run.Outline.Headings[0].ScanSources!.Count);
        Assert.Equal("Điều 1. Phạm vi".Length, run.Outline.Headings[1].ScanSources![0].Span.End);
        var evidence = run.Result.SourceCatalog.Units[0].SourceAnchor;
        Assert.Null(evidence.ParagraphId);
        Assert.Null(evidence.BoundingBox);
        Assert.Equal(new PixelBox(100, 100, 900, 50), evidence.Scan!.Box);
        Assert.Equal("Chuong I", evidence.Scan.PrimaryReading);
        Assert.Equal("Chương I", evidence.Scan.Readings["vlm-transcribed"]);
        Assert.Equal(6, run.Result.Provenance.ProviderCalls);
        Assert.Equal(1, run.Outline.RouteAudit!.Scan!.PlacementCalls);
        Assert.Equal(.002, run.Outline.RouteAudit!.Scan!.PreflightCostUsd);
        Assert.Equal("complete", run.Outline.RouteAudit.SemanticLane!.Status);
        Assert.Contains("scanSources", JsonSerializer.Serialize(run.Outline), StringComparison.Ordinal);
        var web = WebPipelineProjection.Project(Guid.NewGuid(), "Completed", run.Outline, run, "scan");
        Assert.Equal("scan", web.SourceKind);
        Assert.Equal(2, web.Headings.Count);
        Assert.NotNull(web.Headings[0].Sources[0].Coordinates!.Scan);
        Assert.Contains("Answered source lines=4/4", web.Stages.Single(s => s.Id == "V6-line-selection").Evidence);
        Assert.Contains("ProposedParentAlias", JsonSerializer.Serialize(web.Audit));
        Assert.Contains("VerificationStatus", JsonSerializer.Serialize(web.Audit));
        Assert.Contains("NOT_SELECT", JsonSerializer.Serialize(web.Audit));
        var safe = WebPipelineProjection.SafeOutline(run.Outline, new JsonSerializerOptions());
        Assert.NotNull(safe["routeAudit"]!["Scan"]);
        Assert.Throws<NotSupportedException>(() => DocumentRunner.WriteOutline(ScanPath, run.Outline, "unused.docx", new PipelineOptions().Extraction));
    }

    [Fact]
    public async Task Scan_runner_keeps_structured_budget_and_terminal_metadata_through_its_actual_lease_wrapper()
    {
        using var handler = new FakeOpenRouter((body, _) =>
        {
            var packet = body["messages"]![1]!["content"]!.Text();
            Assert.Equal("HIERARCHY_PLACE", JsonNode.Parse(packet)!["stage"]!.GetValue<string>());
            Assert.Equal(352, body["max_tokens"]!.GetValue<int>());
            var parents = new Dictionary<string, string> { ["P001-L001"] = "ROOT", ["P001-L003"] = "P001-L001" };
            return (HttpStatusCode.OK, FakeOpenRouter.Stream(new { content = HierarchyTestProtocol.Reply(packet, parents) }, "stop"));
        }, autoVerify: false);
        var runner = new DocumentRunner(new PipelineOptions(),
            new Factory(new FixedScan(Prepared(ScanPath)), new HeadingPlacer(handler)));
        var run = await runner.ExtractAsync(ScanPath);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(new int?[] { 1, 2 }, run.Outline.Headings.Select(h => h.Level));
        var calls = run.Outline.RouteAudit!.PlacementExecution!.Calls;
        Assert.All(calls, c => Assert.Equal("stop", c.FinishReason));
        var proposalCall = Assert.Single(calls, c => c.Phase == "HIERARCHY_PLACE");
        Assert.Equal(352, proposalCall.RequestedMaxOutputTokens);
        Assert.Equal(352, proposalCall.MaxOutputTokens);
        var web = WebPipelineProjection.Project(Guid.NewGuid(), "Completed", run.Outline, run, "scan");
        Assert.Contains("FinishReason", JsonSerializer.Serialize(web.Audit));
        Assert.Contains("MaxOutputTokens", JsonSerializer.Serialize(web.Audit));
    }

    [Fact]
    public async Task Web_grouping_reports_observed_counts_instead_of_guessing_fallback_from_failure_text()
    {
        var prepared = Prepared(ScanPath);
        prepared = prepared with { Selection = prepared.Selection with
        {
            UnitFailures = ["GROUP-REVIEW:SELECTION_DISAGREEMENT_UNRESOLVED:P001-L004"],
            Grouping = new GroupingObservation(ModelDividedRuns: 2, FallbackRuns: 0, DisputedRuns: 1,
                VerificationChecks: 2, VerifiedRuns: 1, UnresolvedAliases: 1,
                Batches: [new GroupingBatchTiming("GROUP", "GROUP-001", 8, 2, 1, 0, 12.5, 12.5),
                    new GroupingBatchTiming("REVIEW-GROUP", "REVIEW-GROUP-001", 1, 1, 0, 0, 1.5, 1.5)],
                FieldDiagnostics: [new GroupingFieldDiagnostic("VERIFY-001", 1, 1, "V1", "verdict", [], ["confidence"], [], true)],
                CoverageDiagnostics: [new GroupingCoverageDiagnostic("VERIFY-001", 3, 3, "V1", 2,
                    ["P001-L001", "P001-L002"], ["P001-L001", "P001-L001"], ["P001-L002"], [], ["P001-L001"], false)],
                Partitions: [new GroupingPartitionObservation("VERIFY-REVERIFY", "VERIFY-001", "V1", "alias-scope-key", 2,
                    ["P001-L001", "P001-L002"], [["P001-L001", "P001-L002"]], [["P001-L001"], ["P001-L002"]], true)],
                VerifierEvidence: [new GroupingVerifierEvidenceObservation("VERIFY-001", 2, [["P001-L001", "P001-L002"]],
                    ["P001-L001", "P001-L002"], [new GroupingEvidenceToolObservation(1, "get_scan_crop", "OK", ["P001-L001"], true, true)],
                    ["P001-L001", "P001-L002"])],
                BoundaryRepairs: [new BoundaryRepairObservation("VERIFY-REGROUP-V1", "VERIFY-001", "V1", "alias-scope-key",
                    ["P001-L001", "P001-L002"], 0, 1, [["P001-L001", "P001-L002"]], [["P001-L001", "P001-L002"]],
                    false, "NO_PROGRESS", "BOUNDARY_REPAIR_NO_PROGRESS")]),
            StageSeconds = new Dictionary<string, double> { ["selection"] = 10, ["grouping"] = 20, ["verification"] = 30, ["review"] = 40 }
        } };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(prepared)));
        var run = await runner.ExtractAsync(ScanPath);
        var web = WebPipelineProjection.Project(Guid.NewGuid(), "Partial", run.Outline, run, "scan");
        var selection = web.Stages.Single(s => s.Id == "V6-line-selection");
        Assert.Equal("completed", selection.Status);
        Assert.Contains("Answered source lines=4/4", selection.Evidence);
        Assert.Contains("downstream semantic units complete=1/1", selection.Evidence);
        var grouping = web.Stages.Single(s => s.Id == "V6-line-grouping");
        Assert.Equal("partial", grouping.Status);
        Assert.Contains("actual fallback runs=0", grouping.Evidence);
        Assert.Contains("unresolved aliases=1", grouping.Evidence);
        Assert.DoesNotContain("layout fallback", grouping.Evidence);
        Assert.Contains("GROUP-001: 8 runs, 2 POST, 1 repairs, 12.5s", grouping.Evidence);
        Assert.Contains("REVIEW-GROUP-001: 1 runs, 1 POST, 0 repairs, 1.5s", grouping.Evidence);
        Assert.Contains("offset within REVIEW-GROUP invocation", grouping.Evidence);
        Assert.Equal(0, run.Outline.RouteAudit!.Grouping!.FallbackRuns);
        Assert.Contains("Grouping", JsonSerializer.Serialize(web.Audit));
        Assert.Contains("FieldDiagnostics", JsonSerializer.Serialize(web.Audit));
        Assert.Contains("confidence", JsonSerializer.Serialize(web.Audit));
        var coverage = JsonSerializer.SerializeToNode(web.Audit)!["Grouping"]!["CoverageDiagnostics"]![0]!;
        Assert.Equal("P001-L002", coverage["MissingAliases"]![0]!.GetValue<string>());
        Assert.Equal("P001-L001", coverage["DuplicateAliases"]![0]!.GetValue<string>());
        Assert.Equal(2, coverage["BoundaryRepairCycle"]!.GetValue<int>());
        var safeGrouping = JsonSerializer.SerializeToNode(web.Audit)!["Grouping"]!;
        Assert.True(safeGrouping["Partitions"]![0]!["ChangedFromInput"]!.GetValue<bool>());
        Assert.Equal("P001-L002", safeGrouping["Partitions"]![0]!["OutputGroups"]![1]![0]!.GetValue<string>());
        Assert.True(safeGrouping["VerifierEvidence"]![0]!["UsedScanCrop"]!.GetValue<bool>());
        Assert.Equal("get_scan_crop", safeGrouping["VerifierEvidence"]![0]!["Requests"]![0]!["ToolName"]!.GetValue<string>());
        Assert.Equal("NO_PROGRESS", safeGrouping["BoundaryRepairs"]![0]!["Result"]!.GetValue<string>());
        Assert.Equal("BOUNDARY_REPAIR_NO_PROGRESS", safeGrouping["BoundaryRepairs"]![0]!["FailureClass"]!.GetValue<string>());
        Assert.False(safeGrouping["BoundaryRepairs"]![0]!["Changed"]!.GetValue<bool>());
        Assert.Contains("verification 30", web.Stages.Single(s => s.Id == "heading-phase-timing").Evidence);
    }

    [Fact]
    public async Task Missing_alias_states_do_not_masquerade_selected_count_as_answered_lines()
    {
        var prepared = Prepared(ScanPath) with { Selection = Prepared(ScanPath).Selection with { AliasStates = null } };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(prepared)));
        var run = await runner.ExtractAsync(ScanPath);
        var stage = WebPipelineProjection.Project(Guid.NewGuid(), "Completed", run.Outline, run, "scan")
            .Stages.Single(s => s.Id == "V6-line-selection");
        Assert.Contains("source-line answer count not recorded", stage.Evidence);
        Assert.DoesNotContain("Answered source lines=", stage.Evidence);
    }

    [Fact]
    public async Task Appendix_uses_the_validated_grouping_partition_without_semantic_verifier_repair()
    {
        var (source, selection, _) = AppendixMembershipTests.RunFixture("reject");
        var prepared = ScanSelector.PrepareSelection(source, selection) with
        { SourceSha256 = SourceHash.Compute(ScanPath) };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(prepared), AppendixPlacement.Placer));
        var run = await runner.ExtractAsync(ScanPath);
        var structured = run.Outline.Headings.Where(h => h.Level is not null).ToArray();
        Assert.Equal("Phụ lục", structured[0].Text);
        Assert.StartsWith("I. LỜI CHỨNG Mẫu lời chứng được đánh máy hoặc khắc", structured[1].Text, StringComparison.Ordinal);
        Assert.Equal(new[] { "1. Lời chứng chứng thực bản sao từ bản chính", "2. Lời chứng chứng thực chữ ký",
            "II. MẪU SỔ CHỨNG THỰC", "1. Bìa sổ", "2. Nội dung sổ" }, structured.Skip(2).Select(h => h.Text));
        Assert.Equal(new int?[] { 1, 2, 3, 3, 2, 3, 3 }, structured.Select(h => h.Level));
        Assert.Equal(9, run.Outline.Headings.Count);
        Assert.Equal(2, run.Outline.Headings.Count(h => h.HierarchyResolution == "model-out-of-hierarchy"));
        Assert.Contains(run.Outline.Headings, h => h.Text.Contains("Mẫu lời chứng được", StringComparison.Ordinal));
        Assert.Equal("SELECT", run.Outline.RouteAudit!.HeadingAliasStates![AppendixMembershipTests.A(3)].FinalSelection);
        Assert.Equal("READY_FOR_BINDING", run.Outline.RouteAudit.HeadingAliasStates[AppendixMembershipTests.A(1)].BindingStatus);
        var safeAudit = JsonSerializer.SerializeToNode(WebPipelineProjection.SafeAudit(run.Outline.RouteAudit))!;
        Assert.Equal(0, selection.Grouping!.VerificationChecks);
        Assert.Null(selection.Grouping.Partitions);
        Assert.Null(selection.Grouping.VerifierEvidence);
        Assert.Null(safeAudit["RawModelResponses"]);
    }

    [Fact]
    public async Task Native_docx_keeps_the_native_selector_and_never_calls_the_scan_processor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhx-scan-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = DocxTestDocument.Create(Path.Combine(dir, "native.docx"));
            var scan = new FixedScan(Prepared(ScanPath));
            var runner = new DocumentRunner(new PipelineOptions(), new Factory(scan));
            var run = await runner.ExtractAsync(path);
            Assert.Equal(0, scan.Calls);
            Assert.Equal("docx-v6-selected-lines", run.Outline.DeterministicRoute);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Offline_scan_has_its_source_catalog_but_no_selection_transcription_or_provider()
    {
        var scan = new FixedScan(Prepared(ScanPath));
        var factory = new Factory(scan);
        var runner = new DocumentRunner(new PipelineOptions { DisableLlm = true }, factory);
        var run = await runner.ExtractAsync(ScanPath);
        Assert.Null(scan.Key);
        Assert.Equal(0, factory.RouteCalls);
        Assert.Equal(4, run.Result.SourceCatalog.Units.Count);
        Assert.Empty(run.Outline.Headings);
        Assert.Equal("not-run", run.Outline.RouteAudit!.SemanticLane!.Status);
        Assert.Equal(0, run.Result.Provenance.ProviderCalls);
    }

    [Theory]
    [InlineData("units")]
    [InlineData("transcription")]
    [InlineData("unreadable")]
    public async Task Incomplete_units_or_preflight_remain_partial_and_keep_the_failure(string gap)
    {
        var p = Prepared(ScanPath);
        p = gap switch
        {
            "transcription" => p with { Diagnostics = p.Diagnostics with { TranscriptionFailures = ["TRANSCRIBE-B001:CALL:FAILED"] } },
            "unreadable" => p with { Diagnostics = p.Diagnostics with { UnreadableLines = ["P001-L009"] } },
            _ => p with { Selection = p.Selection with { UnitsComplete = 1, UnitsTotal = 2, UnitFailures = ["SEG-002:FAILED"] } },
        };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var run = await runner.ExtractAsync(ScanPath);
        Assert.Equal("partial", run.Outline.RouteAudit!.SemanticLane!.Status);
        Assert.Contains(gap == "units" ? "SEG-002:FAILED" : "SCAN_PREFLIGHT_INCOMPLETE", run.Outline.RouteAudit.SemanticLane.FailureClass!, StringComparison.Ordinal);
        Assert.Equal(p.Diagnostics.UnreadableLines, run.Outline.RouteAudit.Scan!.UnreadableLines);
    }

    [Fact]
    public async Task A_source_from_another_file_is_refused_before_binding_or_placement()
    {
        var p = Prepared(ScanPath) with { SourceSha256 = new string('0', 64) };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExtractAsync(ScanPath));
        Assert.Equal("SCAN_SOURCE_HASH_MISMATCH", failure.Message);
    }

    [Fact]
    public async Task A_reading_that_is_not_in_the_prepared_evidence_is_refused()
    {
        var p = Prepared(ScanPath);
        p = p with { Lines = p.Lines.Select((l, i) => i == 0 ? l with { Text = "invented heading" } : l).ToArray() };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExtractAsync(ScanPath));
        Assert.StartsWith("SCAN_SOURCE_READING_MISMATCH", failure.Message);
    }

    [Fact]
    public async Task An_extent_outside_its_reading_is_rejected_and_marks_the_output_partial()
    {
        var p = Prepared(ScanPath);
        p = p with { Selection = p.Selection with { HeadingExtents = new Dictionary<string, TextOffsetSpan> { [p.Lines[2].Alias] = new(0, 9999) } } };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var run = await runner.ExtractAsync(ScanPath);
        Assert.Single(run.Outline.Headings);
        Assert.Equal("partial", run.Outline.RouteAudit!.SemanticLane!.Status);
        Assert.Contains("headingExtentsRejected", run.Outline.RouteAudit.SemanticLane.FailureClass!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_preparation_never_publishes_a_completed_result()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = new FixedScan(Prepared(ScanPath)) { BeforeReturn = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var runner = new DocumentRunner(new PipelineOptions(), new Factory(scan));
        using var cts = new CancellationTokenSource();
        var task = runner.ExtractAsync(ScanPath, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Cancelling_an_inflight_transcription_cancels_http_and_does_not_retry_or_cache_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhx-scan-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var crop = Path.Combine(dir, "crop.png"); File.WriteAllBytes(crop, TestPng.Grey(10, 10));
            using var cts = new CancellationTokenSource();
            using var handler = new BlockingHandler();
            var task = Task.Run(() => ScanTranscription.Transcribe([crop], ["T1"], ["key"], Path.Combine(dir, "cache"), "test-key",
                _ => new AgentBudget(10, 1), () => handler, [], "T", 10, 1, ct: cts.Token));
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(1, handler.Calls);
            Assert.False(Directory.Exists(Path.Combine(dir, "cache")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_cancelled_ocr_run_stops_before_resolving_engines_or_opening_the_document()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new ScanOcrPreflight("unused").Run("missing.docx", ct: cts.Token));
    }

    [Fact]
    public async Task Cancelling_an_external_image_tool_kills_that_process_and_returns_promptly_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(Path.GetTempPath(), "dhx-child-" + Guid.NewGuid().ToString("N") + ".txt");
        using var cts = new CancellationTokenSource();
        var script = "$PID | Set-Content -LiteralPath '" + marker.Replace("'", "''") + "'; Start-Sleep -Seconds 30";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var task = Task.Run(() =>
        {
            using var scope = TesseractOcr.WithCancellation(cts.Token);
            return TesseractOcr.RunRaw("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], null, null);
        });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(20, deadline.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(60)));
        }
        finally { cts.Cancel(); try { await task; } catch (OperationCanceledException) { } File.Delete(marker); }
    }

    sealed class FixedScan(ScanDocumentSelection prepared) : IScanHeadingSelector
    {
        public string SelectorId => "scan-test";
        public int Calls;
        public string? Key;
        public Func<CancellationToken, Task>? BeforeReturn;
        public async Task<ScanDocumentSelection> PrepareAsync(string path, string? apiKey, CancellationToken ct)
        {
            Calls++; Key = apiKey;
            if (BeforeReturn is not null) await BeforeReturn(ct);
            ct.ThrowIfCancellationRequested();
            return apiKey is not null ? prepared : prepared with
            {
                Selection = new HeadingSelection(new HashSet<string>(), 0, 0, [], [], 0, 0),
                Diagnostics = prepared.Diagnostics with { PreflightCalls = 0, PreflightCostUsd = 0, ConversationCalls = 0, ConversationCostUsd = 0 },
            };
        }
    }

    sealed class Factory(FixedScan scan, IHeadingPlacer? placer = null) : IModelProvider
    {
        public int RouteCalls;
        public IScanHeadingSelector CreateScanHeadingSelector() => scan;
        public IHeadingSelector CreateDocxHeadingSelector() => new EmptyNative();
        /// <summary>The scripted placer: P001-L001 is a top-level section and P001-L003 sits under it. No provider is called.</summary>
        public IHeadingPlacer CreateHeadingPlacer() => placer ?? new PlacementScript(packet => HierarchyTestProtocol.Reply(packet.ToJsonString(),
            new Dictionary<string, string> { ["P001-L001"] = "ROOT", ["P001-L003"] = "P001-L001" })).Placer;
        public ModelRoute CreateRoute() { RouteCalls++; return new("test", "test-route-key"); }
    }
    sealed class EmptyNative : IHeadingSelector
    {
        public string SelectorId => "native-test";
        public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct) =>
            Task.FromResult(new HeadingSelection(new HashSet<string>(), 1, 1, [], [], 0, 0));
    }
    static class AppendixPlacement
    {
        /// <summary>Only the scripted model supplies parent relations; production derives levels from these relations.</summary>
        public static IHeadingPlacer Placer => new PlacementScript(packet =>
        {
            var parents = new Dictionary<int, string> { [1] = "ROOT", [2] = AppendixMembershipTests.A(1), [4] = AppendixMembershipTests.A(2),
                [6] = AppendixMembershipTests.A(2), [8] = AppendixMembershipTests.A(1), [9] = AppendixMembershipTests.A(8),
                [11] = AppendixMembershipTests.A(8), [12] = "NONE", [14] = "NONE" };
            return HierarchyTestProtocol.Reply(packet.ToJsonString(), parents.ToDictionary(p => AppendixMembershipTests.A(p.Key), p => p.Value));
        }).Placer;
    }
    sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); }
    }
}
