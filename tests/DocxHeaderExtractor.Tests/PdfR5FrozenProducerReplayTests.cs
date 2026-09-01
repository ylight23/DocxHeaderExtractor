using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Application.Features;
using DocxHeaderExtractor.Core.Application.Policy;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// D2 replay probe. It feeds the current PDF producer the source-keyed baseline cohort through an
/// in-memory classifier. The classifier implements the provider surface but never performs I/O;
/// current candidate construction, validation, grounding, hierarchy, and final projection still run.
/// </summary>
public sealed class PdfR5FrozenProducerReplayTests
{
    [Fact]
    public void ProjectionPreservesValidatedAuthorityOrder()
    {
        var first = Element("first", sourceOrdinal: 20);
        var second = Element("second", sourceOrdinal: 10);

        var headings = HeadingOutlineProjection.Project(new ValidatedStructure([first, second]));

        Assert.Equal(["first", "second"], headings.Select(heading => heading.Text));
    }

    [Fact]
    public async Task ReplayPinnedBaselineCohortWithoutProvider()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("R5_REPLAY_RUN"), "1", StringComparison.Ordinal))
            return;

        var root = RepositoryRoot();
        var fixtureRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("R5_REPLAY_FIXTURE_ROOT") ??
            throw new InvalidOperationException("R5_REPLAY_FIXTURE_ROOT is required."));
        var reportPath = Path.GetFullPath(Environment.GetEnvironmentVariable("R5_REPLAY_REPORT") ??
            throw new InvalidOperationException("R5_REPLAY_REPORT is required."));
        var items = new[]
        {
            (Id: "028-wb-rfb-works-without-prequal-2017", Docx: "todo10_8/generated-docx/02_hop_dong_mua_sam/028_WB_RFB_Works_Without_Prequal_2017.docx"),
            (Id: "056-openstax-business-law-essentials", Docx: "todo10_8/generated-docx/04_giao_trinh/056_OpenStax_Business_Law_I_Essentials.docx"),
            (Id: "091-rfc9110-http-semantics", Docx: "todo10_8/generated-docx/07_system_generated/091_RFC9110_HTTP_Semantics.docx"),
        };
        var rows = new List<object>();
        var errors = new List<string>();
        var replayClassifierInvocations = 0;

        foreach (var item in items)
        {
            var fixturePath = Path.Combine(fixtureRoot, item.Id + ".producer-replay.v1.json");
            var fixture = JsonSerializer.Deserialize<PdfR5ReplayFixture>(File.ReadAllText(fixturePath), JsonOptions)
                ?? throw new InvalidOperationException("FIXTURE_PARSE_FAILED:" + item.Id);
            var docx = Path.Combine(root, item.Docx.Replace('/', Path.DirectorySeparatorChar));
            var source = new OpenXmlDocumentSource().Read(docx);
            var features = NumberingStyleFeatures.FromSourceDocument(source);
            var built = DocxPolicyStateBuilder.Build(source, features,
                new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
            var policy = new DocxPolicyState(source, features, built.DerivedFeatures,
                built.Paragraphs, built.StyleTrust, built.Mode);

            var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
            var candidateIdsBySource = snapshot.CandidateBlocks
                .GroupBy(SourceKey)
                .ToDictionary(group => group.Key, group => group.Select(block => block.Id).ToArray(), StringComparer.Ordinal);
            var currentIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sourceEntry in fixture.SelectedSources)
            {
                if (!candidateIdsBySource.TryGetValue(sourceEntry.Key.Value, out var ids) || ids.Length != 1)
                {
                    errors.Add($"{item.Id}:SELECTED_SOURCE_UNJOINED:{sourceEntry.Key.Value}");
                    continue;
                }
                currentIds[sourceEntry.Key.Value] = ids[0];
            }

            var semantic = fixture.SemanticProposals.ToDictionary(item2 => item2.Source.Value, StringComparer.Ordinal);
            var spans = fixture.SpanProposals.ToDictionary(item2 => item2.Source.Value, StringComparer.Ordinal);
            var hierarchy = fixture.HierarchyProposals.ToDictionary(item2 => item2.Source.Value, StringComparer.Ordinal);
            using var analyst = new FrozenPdfClassifier(currentIds, semantic, spans, hierarchy,
                () => replayClassifierInvocations++);
            var result = await PdfLayoutEvidenceOutline.TryBuildBroadAuditWithAnalystAsync(
                docx, policy, analyst, maximumAnalystBlocks: 160,
                includeAllVisualStyles: true, includeSupplementCandidates: true,
                semanticLaneOptions: new SemanticLaneOptions(
                    TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300), 2),
                includeSemanticHierarchyFallback: true);
            if (result.FinalStructure is null)
            {
                errors.Add($"{item.Id}:CURRENT_AUTHORITY_MISSING");
                continue;
            }

            var currentProduct = PdfProductOutputSerializer.Serialize(result.FinalStructure, result.OutputDecisions);
            var currentHeadings = HeadingOutlineProjection.Project(
                result.Authority.Structure, result.Authority.EmittedElementIds);
            var baselineStructure = fixture.Oracle.FinalStructure?.Deserialize<PdfFinalStructure>(JsonOptions);
            var baselineDecisions = fixture.Oracle.OutputDecisions?.Deserialize<IReadOnlyList<PdfOutputDecision>>(JsonOptions);
            var baselineProduct = fixture.Oracle.ProductOutput?.Deserialize<PdfProductOutput>(JsonOptions);
            var baselineHeadings = fixture.Oracle.FinalHeadingRecords?.Deserialize<IReadOnlyList<HeadingRecord>>(JsonOptions);
            var structureDelta = JsonEqual(baselineStructure, result.FinalStructure);
            var decisionsDelta = JsonEqual(baselineDecisions, result.OutputDecisions);
            var productDelta = JsonEqual(baselineProduct, currentProduct);
            var headingDelta = JsonEqual(baselineHeadings, currentHeadings);
            var currentDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!, item.Id);
            Directory.CreateDirectory(currentDirectory);
            WriteJson(Path.Combine(currentDirectory, "audit.json"),
                result.Audit with { RawAnalystResponses = [], ModelInputContracts = [] });
            WriteJson(Path.Combine(currentDirectory, "output.json"), new
            {
                schemaVersion = 1,
                documentId = item.Id,
                finalStructure = result.FinalStructure,
                outputDecisions = result.OutputDecisions,
                productOutput = currentProduct,
                finalHeadingRecords = currentHeadings,
                providerCalls = 0,
            });
            if (structureDelta) errors.Add($"{item.Id}:FINAL_STRUCTURE_DELTA");
            if (decisionsDelta) errors.Add($"{item.Id}:OUTPUT_DECISION_DELTA");
            if (productDelta) errors.Add($"{item.Id}:PRODUCT_OUTPUT_DELTA");
            if (headingDelta) errors.Add($"{item.Id}:FINAL_HEADING_DELTA");
            rows.Add(new
            {
                item.Id,
                selected = fixture.SelectedSources.Count,
                currentSelected = result.Audit?.SelectedSourceIdentities.Count ?? 0,
                baselineFinalHeadings = baselineHeadings?.Count ?? 0,
                currentFinalHeadings = currentHeadings.Count,
                finalStructureDelta = structureDelta,
                outputDecisionDelta = decisionsDelta,
                productOutputDelta = productDelta,
                finalHeadingDelta = headingDelta,
                headingDifferences = headingDelta
                    ? baselineHeadings!.Zip(currentHeadings)
                        .Select((pair, index) => (pair, index))
                        .Where(item => JsonEqual(item.pair.First, item.pair.Second))
                        .Select(item => new
                        {
                            index = item.index,
                            baseline = item.pair.First,
                            current = item.pair.Second,
                        }).ToArray()
                    : [],
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            artifactKind = "r5_frozen_producer_replay",
            baselineRevision = "0b98ade75f4c5ada46d8af6b4c3fffd3e829d2b8",
            currentBaseRevision = "9afdadacdc383ad400e80f73ad70bda976e37d89",
            currentRevision = Environment.GetEnvironmentVariable("R5_REPLAY_CURRENT_REVISION") ??
                "UNCOMMITTED_WORKTREE",
            productionSourceDelta = new[] { "src/DocxHeaderExtractor.Core/Pipeline/HeadingOutlineProjection.cs" },
            providerCallsDuringReplay = 0,
            replayClassifierInvocations,
            errors,
            rows,
        }, JsonOptions));
        Assert.Empty(errors);
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));

    private static ValidatedStructuralElement Element(string text, int sourceOrdinal) =>
        new()
        {
            Id = "structural:" + text,
            Type = StructuralElementType.Heading,
            Role = ProposedRole.HeadingTopic,
            Sources = [new SourceReference("source:" + text, sourceOrdinal, new StructuralSpan(0, text.Length))],
            Text = text,
            Level = 1,
            Validation = new StructuralValidation(true, true, true, true, 1, true, true, true, null),
            Decision = new StructuralDecision("test", "RequiresReview", 1, "test"),
        };

    private static string SourceKey(PdfSemanticBlock block) =>
        new PdfReplaySourceKey(block.Page, block.Lines.Select(PdfCandidateProvenance.LineId).ToArray()).Value;

    private static bool JsonEqual<T>(T? left, T? right) =>
        JsonSerializer.Serialize(left, JsonOptions) != JsonSerializer.Serialize(right, JsonOptions);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

internal sealed class FrozenPdfClassifier(
    IReadOnlyDictionary<string, string> currentIds,
    IReadOnlyDictionary<string, PdfReplaySemanticProposal> semantic,
    IReadOnlyDictionary<string, PdfReplaySpanProposal> spans,
    IReadOnlyDictionary<string, PdfReplayHierarchyProposal> hierarchy,
    Action countCall) : IHeaderClassifier
{
    public string ModelName => "frozen-baseline-replay";
    public int ContextSize => 32768;
    public string RuntimeDescription => "test-only frozen replay";
    public int SharedPrefixTokens => 0;

    public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
        Task.FromResult(new ChunkResult([], "{}", 0, 0, new HashSet<int>()));

    public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) =>
        Task.FromResult(new ChunkResult([], "{}", 0, 0, new HashSet<int>()));

    public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context,
        IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) =>
        Task.FromResult(new ChunkResult([], "{}", 0, 0, new HashSet<int>()));

    public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        countCall();
        using var json = JsonDocument.Parse(userMessage);
        if (systemPrompt.Contains("source pointer span", StringComparison.OrdinalIgnoreCase))
        {
            var blocks = json.RootElement.GetProperty("blocks").EnumerateArray().Select(item =>
            {
                var id = item.GetProperty("id").GetString()!;
                var key = currentIds.FirstOrDefault(pair => pair.Value == id).Key;
                return spans.TryGetValue(key, out var span)
                    ? (object)new { id, heading_span = new { start = span.Span.Start, end = span.Span.End } }
                    : new { id, heading_span = (object?)null };
            }).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { blocks }));
        }
        if (systemPrompt.Contains("parent links", StringComparison.OrdinalIgnoreCase))
        {
            var items = json.RootElement.GetProperty("items").EnumerateArray().Select(item =>
            {
                var id = item.GetProperty("id").GetString()!;
                var key = currentIds.FirstOrDefault(pair => pair.Value == id).Key;
                var parent = hierarchy.TryGetValue(key, out var proposal) && proposal.ProposedParent is not null
                    ? currentIds.GetValueOrDefault(proposal.ProposedParent.Value)
                    : null;
                return new { id, parent_id = parent };
            });
            return Task.FromResult(JsonSerializer.Serialize(new { items }));
        }

        var decisions = json.RootElement.GetProperty("blocks").EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetString()!;
            var key = currentIds.FirstOrDefault(pair => pair.Value == id).Key;
            if (!semantic.TryGetValue(key, out var proposal))
                return new { id, role = "unknown", confidence = 0d, reason = "replay-missing-baseline-proposal" };
            return new
            {
                id,
                role = Role(proposal.Role),
                confidence = proposal.Confidence,
                reason = proposal.Reason ?? "",
            };
        });
        return Task.FromResult(JsonSerializer.Serialize(new { blocks = decisions }));
    }

    public void Dispose() { }

    private static string Role(string role) => role switch
    {
        "HeadingTopic" => "topic_heading",
        "BodySentence" => "body_text",
        "TableOrChartLabel" => "table_header",
        "DecorativeNoise" => "running_header",
        _ => "unknown",
    };
}
