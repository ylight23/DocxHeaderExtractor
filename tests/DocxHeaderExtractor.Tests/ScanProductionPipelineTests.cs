using System.Net;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Provenance;
using DocxHeaderExtractor.DocumentProcessing.Routing;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.V6Runner;
using DocxHeaderExtractor.Web;

namespace DocxHeaderExtractor.Tests;

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
            new Dictionary<string, TextOffsetSpan> { [lines[2].Alias] = new(0, "Điều 1. Phạm vi".Length) });
        return new(CanonicalSemanticSourceHash.Compute(path), lines, selection,
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
        using var runner = new V6DocumentRunner(new PipelineOptions(), factory);
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
        var safe = WebPipelineProjection.SafeOutline(run.Outline, new JsonSerializerOptions());
        Assert.NotNull(safe["routeAudit"]!["Scan"]);
        Assert.Throws<NotSupportedException>(() => V6DocumentRunner.WriteOutline(ScanPath, run.Outline, "unused.docx", new PipelineOptions().Extraction));
    }

    [Fact]
    public async Task Web_grouping_reports_observed_counts_instead_of_guessing_fallback_from_failure_text()
    {
        var prepared = Prepared(ScanPath);
        prepared = prepared with { Selection = prepared.Selection with
        {
            UnitFailures = ["GROUP-REVIEW:SELECTION_DISAGREEMENT_UNRESOLVED:P001-L004"],
            Grouping = new GroupingObservation(ModelDividedRuns: 2, FallbackRuns: 0, DisputedRuns: 1,
                VerificationChecks: 2, VerifiedRuns: 1, UnresolvedAliases: 1),
            StageSeconds = new Dictionary<string, double> { ["selection"] = 10, ["grouping"] = 20, ["verification"] = 30, ["review"] = 40 }
        } };
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(prepared)));
        var run = await runner.ExtractAsync(ScanPath);
        var web = WebPipelineProjection.Project(Guid.NewGuid(), "Partial", run.Outline, run, "scan");
        var grouping = web.Stages.Single(s => s.Id == "V6-line-grouping");
        Assert.Equal("partial", grouping.Status);
        Assert.Contains("actual fallback runs=0", grouping.Evidence);
        Assert.Contains("unresolved aliases=1", grouping.Evidence);
        Assert.DoesNotContain("layout fallback", grouping.Evidence);
        Assert.Equal(0, run.Outline.RouteAudit!.Grouping!.FallbackRuns);
        Assert.Contains("Grouping", JsonSerializer.Serialize(web.Audit));
        Assert.Contains("verification 30", web.Stages.Single(s => s.Id == "heading-phase-timing").Evidence);
    }

    [Fact]
    public async Task Appendix_mixed_run_reaches_model_placement_without_losing_parents_or_outside_tree_headings()
    {
        var (source, selection, _) = AppendixMembershipTests.RunFixture("reject");
        var prepared = ScanHeadingAgentSelector.PrepareSelection(source, selection) with
        { SourceSha256 = CanonicalSemanticSourceHash.Compute(ScanPath) };
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(prepared), new AppendixPlacement()));
        var run = await runner.ExtractAsync(ScanPath);
        var structured = run.Outline.Headings.Where(h => h.Level is not null).ToArray();
        Assert.Equal(new[] { "Phụ lục", "I. LỜI CHỨNG", "1. Lời chứng chứng thực bản sao từ bản chính", "2. Lời chứng chứng thực chữ ký",
            "II. MẪU SỔ CHỨNG THỰC", "1. Bìa sổ", "2. Nội dung sổ" }, structured.Select(h => h.Text));
        Assert.Equal(new int?[] { 1, 2, 3, 3, 2, 3, 3 }, structured.Select(h => h.Level));
        Assert.Equal(9, run.Outline.Headings.Count);
        Assert.Equal(2, run.Outline.Headings.Count(h => h.HierarchyResolution == "model-out-of-hierarchy"));
        Assert.DoesNotContain(run.Outline.Headings, h => h.Text.StartsWith("Mẫu lời chứng được", StringComparison.Ordinal));
        Assert.Equal("NOT_SELECT", run.Outline.RouteAudit!.HeadingAliasStates![AppendixMembershipTests.A(3)].FinalSelection);
        Assert.Equal("READY_FOR_BINDING", run.Outline.RouteAudit.HeadingAliasStates[AppendixMembershipTests.A(1)].BindingStatus);
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
            using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(scan));
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
        using var runner = new V6DocumentRunner(new PipelineOptions { DisableLlm = true }, factory);
        var run = await runner.ExtractAsync(ScanPath);
        Assert.Null(scan.Key);
        Assert.Equal(0, factory.TransportCalls);
        Assert.Equal(4, run.Result.SourceCatalog.Units.Count);
        Assert.Empty(run.Outline.Headings);
        Assert.Equal("not-run", run.Outline.RouteAudit!.SemanticLane!.Status);
        Assert.Equal(0, run.Result.Provenance.ProviderCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_units_or_preflight_remain_partial_and_keep_the_failure(bool preflight)
    {
        var p = Prepared(ScanPath);
        p = preflight ? p with { Diagnostics = p.Diagnostics with { TranscriptionFailures = ["TRANSCRIBE-B001:CALL:FAILED"] } }
            : p with { Selection = p.Selection with { UnitsComplete = 1, UnitsTotal = 2, UnitFailures = ["SEG-002:FAILED"] } };
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var run = await runner.ExtractAsync(ScanPath);
        Assert.Equal("partial", run.Outline.RouteAudit!.SemanticLane!.Status);
        Assert.Contains(preflight ? "SCAN_PREFLIGHT_INCOMPLETE" : "SEG-002:FAILED", run.Outline.RouteAudit.SemanticLane.FailureClass!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_from_another_file_is_refused_before_binding_or_placement()
    {
        var p = Prepared(ScanPath) with { SourceSha256 = new string('0', 64) };
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExtractAsync(ScanPath));
        Assert.Equal("SCAN_SOURCE_HASH_MISMATCH", failure.Message);
    }

    [Fact]
    public async Task A_reading_that_is_not_in_the_prepared_evidence_is_refused()
    {
        var p = Prepared(ScanPath);
        p = p with { Lines = p.Lines.Select((l, i) => i == 0 ? l with { Text = "invented heading" } : l).ToArray() };
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExtractAsync(ScanPath));
        Assert.StartsWith("SCAN_SOURCE_READING_MISMATCH", failure.Message);
    }

    [Fact]
    public async Task An_extent_outside_its_reading_is_rejected_and_marks_the_output_partial()
    {
        var p = Prepared(ScanPath);
        p = p with { Selection = p.Selection with { HeadingExtents = new Dictionary<string, TextOffsetSpan> { [p.Lines[2].Alias] = new(0, 9999) } } };
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(new FixedScan(p)));
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
        using var runner = new V6DocumentRunner(new PipelineOptions(), new Factory(scan));
        using var cts = new CancellationTokenSource();
        var task = runner.ExtractAsync(ScanPath, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
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

    sealed class Factory(FixedScan scan, IInferenceTransport? placement = null) : IInferenceTransportFactory
    {
        public int TransportCalls;
        public bool SendsDataExternally => true;
        public IScanHeadingSelector CreateScanHeadingSelector() => scan;
        public IHeadingSelector CreateDocxHeadingSelector() => new EmptyNative();
        public Task<IInferenceTransport> CreateAgentRouteAsync(CancellationToken ct = default)
        { TransportCalls++; return Task.FromResult(placement ?? new PlacementTransport()); }
    }
    sealed class EmptyNative : IHeadingSelector
    {
        public string SelectorId => "native-test";
        public Task<HeadingSelection> SelectAsync(string path, string apiKey, CancellationToken ct) =>
            Task.FromResult(new HeadingSelection(new HashSet<string>(), 1, 1, [], [], 0, 0));
    }
    sealed class PlacementTransport : IAgentRouteInferenceTransport
    {
        public string? OpenRouterApiKey => "test-route-key";
        public string RouteProvider => "test";
        public string RouteModel => "test";
        public string ModelName => "test";
        public int ContextSize => 100_000;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            Task.FromResult("{\"placements\":[{\"alias\":\"P001-L001\",\"parent\":\"ROOT\"},{\"alias\":\"P001-L003\",\"parent\":\"P001-L001\"}]}");
        public void Dispose() { }
    }
    sealed class AppendixPlacement : IAgentRouteInferenceTransport
    {
        public string? OpenRouterApiKey => "test-route-key";
        public string RouteProvider => "test";
        public string RouteModel => "test";
        public string ModelName => "test";
        public int ContextSize => 100_000;
        public string RuntimeDescription => "scripted appendix parent relations, not a live placement result";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            // Only the scripted model supplies parent relations; production derives levels from these relations.
            var parents = new Dictionary<int, string> { [1] = "ROOT", [2] = AppendixMembershipTests.A(1), [4] = AppendixMembershipTests.A(2),
                [6] = AppendixMembershipTests.A(2), [8] = AppendixMembershipTests.A(1), [9] = AppendixMembershipTests.A(8),
                [11] = AppendixMembershipTests.A(8), [12] = "NONE", [14] = "NONE" };
            return Task.FromResult(JsonSerializer.Serialize(new { placements = parents.Select(p => new { alias = AppendixMembershipTests.A(p.Key), parent = p.Value }) }));
        }
        public void Dispose() { }
    }
    sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); }
    }
}
