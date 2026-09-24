using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Pilot evidence for itemising count-only Gold: the production harness, qwen/qwen3.7-flash, run on
/// the lane that matches the source - the DOCX adapter for DOCX sources, the PDF adapter for PDF
/// sources (both PDF profiles, since the default is under review). Gated, real spend, every call
/// through <see cref="BudgetedClassifier"/> with its ledger kept. Evidence for a human decision, not
/// a Gold write.
/// </summary>
public sealed class PilotRealHarnessRunsTests
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string OutputRoot = "eval/a99-closed-loop/pilot-real-harness-v1";

    private static readonly Dictionary<string, (string Media, string Path)> Sources = new(StringComparer.Ordinal)
    {
        ["DOC-0255"] = ("DOCX", PilotSourceRegenerationTests.DocxPath("075_FORTIS_GC_Minutes_Nov21_2024")),
        ["DOC-0259"] = ("DOCX", PilotSourceRegenerationTests.DocxPath("079_ICP_TAG_Minutes_Apr_2024")),
        ["SRC-055"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/055_IDA_External_Review_FY25.pdf"),
    };

    [Fact]
    public void Planned_calls_per_run_are_measured_offline()
    {
        var plan = Sources.ToDictionary(pair => pair.Key, pair => pair.Value.Media == "DOCX"
            ? $"docx={Math.Ceiling(DocxEvidence(pair.Value.Path) / 120.0)}"
            : $"pdf-legacy={Math.Ceiling(PdfCanonicalSourceUniverseBuilder.Build(TestRepository.Path(pair.Value.Path)).Evidence.Count / 120.0)} " +
              $"pdf-structured={PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pair.Value.Path)).Packs.Count}");
        Assert.Equal("docx=1", plan["DOC-0255"]);
        Assert.Equal("docx=1", plan["DOC-0259"]);
        Assert.Equal("pdf-legacy=44 pdf-structured=56", plan["SRC-055"]);
    }

    [Fact]
    public async Task Run_once()
    {
        if (Environment.GetEnvironmentVariable("A99_PILOT_RUN") != "1") return;
        var id = Environment.GetEnvironmentVariable("A99_PILOT_ID")!;
        var lane = Environment.GetEnvironmentVariable("A99_PILOT_LANE")!;
        var repeat = Environment.GetEnvironmentVariable("A99_PILOT_REPEAT")!;
        var ceiling = int.Parse(Environment.GetEnvironmentVariable("A99_PILOT_CEILING")!);
        var (media, path) = Sources[id];

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var budgeted = new BudgetedClassifier(
            new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = apiKey, Model = Model }), ceiling)
        {
            DocumentId = id,
            Stage = $"semantic-{lane}",
        };

        string? failure = null;
        string? reason = null;
        IReadOnlyList<object> elements = [];
        try
        {
            StructuralAuthorityResultView view;
            if (media == "DOCX")
            {
                Assert.Equal("docx", lane);
                var document = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
                var state = DocxPolicyStateBuilder.Build(document, NumberingStyleFeatures.FromSourceDocument(document),
                    new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
                var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
                var result = await CanonicalSemanticDocxAuthorityAdapter.RunAsync(state, mode, budgeted, CancellationToken.None);
                view = new(result.Reason, result.Structure.Elements.Select(e => (e.Text, e.Sources.Select(s => s.SourceId).ToArray())).ToArray());
            }
            else
            {
                var profile = lane == "pdf-structured"
                    ? PdfSemanticAuthorityProfile.StructuredSourceParts
                    : PdfSemanticAuthorityProfile.LegacyOccurrence;
                var result = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                    TestRepository.Path(path), budgeted, CancellationToken.None, profile: profile, runPlacement: false);
                view = new(result.Reason, result.Structure.Elements.Select(e => (e.Text, e.Sources.Select(s => s.SourceId).ToArray())).ToArray());
            }
            reason = view.Reason;
            elements = view.Elements.Select(e => (object)new { text = e.Text, sourceIds = e.SourceIds }).ToArray();
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
        }

        var directory = TestRepository.Path($"{OutputRoot}/{id}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{lane}-r{repeat}.v1.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_pilot_real_harness_run",
            documentId = id,
            sourcePath = path,
            sourceSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(path)),
            model = Model,
            lane,
            placement = false,
            repeat,
            providerCallsMade = budgeted.CallsMade,
            failure,
            reason,
            elementCount = elements.Count,
            elements,
            ledger = budgeted.Ledger,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Fail($"{id} lane={lane} repeat={repeat} calls={budgeted.CallsMade} elements={elements.Count} failure={failure}");
    }

    private static int DocxEvidence(string path)
    {
        var document = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
        var state = DocxPolicyStateBuilder.Build(document, NumberingStyleFeatures.FromSourceDocument(document),
            new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
        return DocxAuthorityPipeline.BuildForAudit(state, DocumentModeClassifier.Measure(
            state.Paragraphs.Cast<IPolicyParagraph>().ToArray())).Contexts.Count;
    }

    private sealed record StructuralAuthorityResultView(string Reason, IReadOnlyList<(string Text, string[] SourceIds)> Elements);
}
