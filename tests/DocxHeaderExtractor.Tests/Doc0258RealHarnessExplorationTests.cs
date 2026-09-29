using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EXPLORATORY, gated behind an env var, real spend: runs the actual production DOCX harness
/// (CanonicalSemanticDocxAuthorityAdapter, same route/model as OccurrenceBaselineTransportTests -
/// qwen/qwen3.7-flash via OpenRouterHeaderExtractor) against DOC-0258 once, with no pre-existing
/// occurrence Gold, to discover what the real model+harness considers the document's heading set to
/// be. Output is written to a scratch location, not a canonical/frozen one - this is evidence for a
/// human decision, not a Gold write.
/// </summary>
public sealed class Doc0258RealHarnessExplorationTests
{
    private const string RunVariable = "A99_DOC0258_EXPLORE_RUN";
    private const string RepeatVariable = "A99_DOC0258_EXPLORE_REPEAT";
    private const string DocPath =
        "todo10_8/heading_corpus_95_word/05_bien_ban_hop/078_ICP_IACG07_Minutes_May_2023.docx";
    private const string Model = "qwen/qwen3.7-flash";

    [Fact]
    public async Task Run_the_real_harness_once_and_report_the_heading_set()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) is not ("1" or "true"))
            return;
        var repeat = Environment.GetEnvironmentVariable(RepeatVariable) ?? "1";

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        var path = TestRepository.Path(DocPath);
        var document = new OpenXmlDocumentSource().Read(path);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
        {
            ApiKey = apiKey,
            Model = Model,
        });

        var authority = await CanonicalSemanticDocxAuthorityAdapter.RunAsync(
            document, provider, CancellationToken.None);

        var elements = authority.Structure.Elements
            .OrderBy(e => e.Sources.FirstOrDefault()?.SourceId, StringComparer.Ordinal)
            .Select(e => new
            {
                e.Id,
                type = e.Type.ToString(),
                role = e.Role.ToString(),
                e.Text,
                e.Level,
                e.ParentId,
                sourceIds = e.Sources.Select(s => s.SourceId).ToArray(),
            })
            .ToArray();

        var outputDir = TestRepository.Path("eval/a99-closed-loop/doc0258-real-harness-exploration-v1");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, $"run-r{repeat}.v1.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_doc0258_real_harness_exploration",
            documentId = "DOC-0258",
            model = Model,
            repeat,
            reason = authority.Reason,
            elementCount = elements.Length,
            elements,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Fail($"repeat={repeat} elementCount={elements.Length} reason={authority.Reason} - see run-r{repeat}.v1.json for the full list");
    }
}
