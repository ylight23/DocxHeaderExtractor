using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class DocumentDiagnosticsOptInTests
{
    [Fact]
    public async Task Normal_canonical_extraction_does_not_run_diagnostics_and_leaves_report_null()
    {
        var path = CreateInput();
        try
        {
            var calls = 0;
            var options = new PipelineOptions
            {
                DisableLlm = true,
                DocumentDiagnosticsAnalyzer = (_, _) =>
                {
                    calls++;
                    throw new InvalidOperationException("diagnostics-must-be-opt-in");
                },
            };

            using var pipeline = new AuthorityExtractionPipeline(options);
            var execution = await pipeline.RunDocumentExecutionAsync(path);

            Assert.Equal(0, calls);
            Assert.Null(execution.CompatibilityOutline.Diagnostics);
            Assert.DoesNotContain("\"diagnostics\"", JsonSerializer.Serialize(execution.CompatibilityOutline));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task Explicit_diagnostics_option_runs_the_existing_runner_and_populates_the_report()
    {
        var path = CreateInput();
        try
        {
            using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions
            {
                DisableLlm = true,
                EnableDocumentDiagnostics = true,
            });

            var execution = await pipeline.RunDocumentExecutionAsync(path);
            Assert.NotNull(execution.CompatibilityOutline.Diagnostics);
            var diagnostics = execution.CompatibilityOutline.Diagnostics!;

            Assert.Equal("normal", diagnostics.Status);
            Assert.Equal("signals_validated", diagnostics.Reason);
            Assert.Equal(
                [
                    "auto:style-declared",
                    "auto:outline-level",
                    "auto:numbering",
                    "auto:typed-numbering",
                    "auto:book-toc-dictionary",
                    "auto:rfc-toc-dictionary",
                ],
                diagnostics.Candidates.Select(candidate => candidate.Route).ToArray());
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task Explicit_diagnostics_hook_is_called_only_when_the_option_is_enabled()
    {
        var path = CreateInput();
        try
        {
            var calls = 0;
            var options = new PipelineOptions
            {
                DisableLlm = true,
                EnableDocumentDiagnostics = true,
                DocumentDiagnosticsAnalyzer = (_, _) =>
                {
                    calls++;
                    return EmptyDiagnostics();
                },
            };

            using var pipeline = new AuthorityExtractionPipeline(options);
            var execution = await pipeline.RunDocumentExecutionAsync(path);

            Assert.Equal(1, calls);
            Assert.Equal("test", execution.CompatibilityOutline.Diagnostics!.Reason);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task Diagnostics_option_does_not_change_canonical_headings_structure_or_provider_count()
    {
        var path = CreateInput();
        try
        {
            var withoutDiagnostics = await RunAsync(path, enableDiagnostics: false);
            var withDiagnostics = await RunAsync(path, enableDiagnostics: true);

            Assert.Equal(HeadingFingerprint(withoutDiagnostics.CompatibilityOutline),
                HeadingFingerprint(withDiagnostics.CompatibilityOutline));
            Assert.Equal(JsonSerializer.Serialize(withoutDiagnostics.Result.Structure),
                JsonSerializer.Serialize(withDiagnostics.Result.Structure));
            Assert.Equal(JsonSerializer.Serialize(withoutDiagnostics.Result.Sections),
                JsonSerializer.Serialize(withDiagnostics.Result.Sections));
            Assert.Equal(JsonSerializer.Serialize(withoutDiagnostics.Result.Chunks),
                JsonSerializer.Serialize(withDiagnostics.Result.Chunks));
            Assert.Equal(withoutDiagnostics.Result.Provenance.ProviderCalls,
                withDiagnostics.Result.Provenance.ProviderCalls);
            Assert.Equal(0, withoutDiagnostics.Result.Provenance.ProviderCalls);
            Assert.Equal(0, withDiagnostics.Result.Provenance.ProviderCalls);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public async Task Explicit_diagnostics_report_is_deterministic_and_keeps_all_candidate_fields()
    {
        var path = CreateInput();
        try
        {
            var first = await RunAsync(path, enableDiagnostics: true);
            var second = await RunAsync(path, enableDiagnostics: true);

            Assert.Equal(
                JsonSerializer.Serialize(first.CompatibilityOutline.Diagnostics),
                JsonSerializer.Serialize(second.CompatibilityOutline.Diagnostics));
            Assert.All(first.CompatibilityOutline.Diagnostics!.Candidates, candidate =>
                Assert.False(string.IsNullOrWhiteSpace(candidate.Route)));
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    private static async Task<AuthorityPipelineExecutionResult> RunAsync(
        string path, bool enableDiagnostics)
    {
        using var pipeline = new AuthorityExtractionPipeline(new PipelineOptions
        {
            DisableLlm = true,
            EnableDocumentDiagnostics = enableDiagnostics,
        });
        return await pipeline.RunDocumentExecutionAsync(path);
    }

    private static string CreateInput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-diagnostics-{Guid.NewGuid():N}.docx");
        SampleDocumentFactory.Create(path);
        return path;
    }

    private static string HeadingFingerprint(DocumentOutline outline) =>
        JsonSerializer.Serialize(outline.Headings.Select(heading => new
        {
            heading.Index,
            heading.StableId,
            heading.SourceId,
            heading.Level,
            heading.Text,
            heading.Source,
            heading.DecisionStatus,
            heading.ConfidenceBasis,
        }));

    private static DocumentDiagnosticReport EmptyDiagnostics() => new(
        "test",
        "test",
        new StyleSignalDiagnostic(0, 0, 0, 0, 0, true, true, false),
        new LayoutSignalDiagnostic(0, 0, 0, 0),
        []);
}
