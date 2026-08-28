using System.Text.Json;
using System.Reflection;
using System.Collections;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using Xunit.Abstractions;

namespace DocxHeaderExtractor.Tests;

public sealed class RfcTocFirstRejectDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public RfcTocFirstRejectDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Rfc_092_first_reject_diagnostic_is_offline_and_deterministic()
    {
        var failingDocument = Extract("092_RFC9111_HTTP_Caching.docx");
        var failing = RfcTocDictionaryOutline.Analyze(failingDocument);
        var nearby = new[]
        {
            Analyze("091_RFC9110_HTTP_Semantics.docx"),
            Analyze("093_RFC9112_HTTP_1_1.docx"),
            Analyze("094_RFC9113_HTTP_2.docx"),
            Analyze("095_RFC9114_HTTP_3.docx")
        };
        var passingControl = AnalyzePassingControl();

        _output.WriteLine(JsonSerializer.Serialize(new
        {
            failing = Snapshot(failing),
            failingInput = InputSnapshot(failingDocument),
            tableSamples = failingDocument.Paragraphs
                .Where(p => p.TableDepth > 0 && !string.IsNullOrWhiteSpace(p.Text))
                .Take(12)
                .Select(p => new { p.Index, p.StableId, p.TableDepth, p.Text }),
            tocTextSamples = failingDocument.Paragraphs
                .Where(p => p.Text.Contains("Contents", StringComparison.OrdinalIgnoreCase)
                    || p.Text.Contains("Introduction", StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .Select(p => new { p.Index, p.StableId, p.TableDepth, p.Text }),
            densitySamples = failingDocument.Paragraphs
                .Where(p => p.TableDepth == 0 && !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => new { p.Index, density = MarkerCount(p.Text), p.Text })
                .OrderByDescending(x => x.density)
                .Take(12),
            nearby = nearby.Select(Snapshot),
            passingControl = Snapshot(passingControl),
            sourceLocations = new
            {
                paragraphFilter = "RfcTocDictionaryOutline.cs:61-67",
                tocCluster = "RfcTocDictionaryOutline.cs:69-73, 108-135",
                dictionary = "RfcTocDictionaryOutline.cs:75-77, 138-177",
                bodyAnchor = "RfcTocDictionaryOutline.cs:80-105",
                headingConstruction = "RfcTocDictionaryOutline.cs:82-101"
            }
        }));

        Assert.True(failing.Accepted, JsonSerializer.Serialize(failing.Diagnostics));
        Assert.True(passingControl.Accepted);
    }

    private static SlimDocument Extract(string fileName)
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "07_system_generated", fileName);
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return new DocxSlimExtractor(new ExtractionOptions()).Extract(path);
    }

    private static RfcTocDictionaryResult Analyze(string fileName) =>
        RfcTocDictionaryOutline.Analyze(Extract(fileName));

    private static object InputSnapshot(SlimDocument document) => new
    {
        documentIdentity = document.SourcePath,
        totalParagraphs = document.Paragraphs.Count,
        nonEmptyParagraphs = document.Paragraphs.Count(p => !string.IsNullOrWhiteSpace(p.Text)),
        tableDepthDistribution = document.Paragraphs
            .GroupBy(p => p.TableDepth)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => g.Count()),
        eligibleParagraphs = document.Paragraphs.Count(p =>
            !p.Corrupt && p.TableDepth == 0 && !string.IsNullOrWhiteSpace(p.Text))
    };

    private static int MarkerCount(string text)
    {
        var marks = typeof(RfcTocDictionaryOutline).GetMethod(
            "Marks", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [text]);
        return ((ICollection)marks!).Count;
    }

    private static object Snapshot(RfcTocDictionaryResult result) => new
    {
        accepted = result.Accepted,
        headings = result.Headings.Count,
        diagnostics = result.Diagnostics
    };

    private static RfcTocDictionaryResult AnalyzePassingControl()
    {
        const int count = 20;
        var toc = string.Join(" ", Enumerable.Range(1, count).Select(i => $"{i}. Control heading {i}"));
        var paragraphs = new List<SlimParagraph>
        {
            new() { Index = 0, StableId = "control-toc", Text = toc }
        };
        paragraphs.AddRange(Enumerable.Range(1, count).Select(i => new SlimParagraph
        {
            Index = i,
            StableId = $"control-body-{i}",
            Text = $"{i}. Control heading {i}"
        }));

        var result = RfcTocDictionaryOutline.Analyze(new SlimDocument
        {
            FileName = "rfc-control.docx",
            SourcePath = "rfc-control.docx",
            Paragraphs = paragraphs
        }.Build());
        return result;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }
}
