using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scale-up evidence for itemising the remaining count-only Gold: the production harness,
/// qwen/qwen3.7-flash, on the lane that matches each (fixed) source - the DOCX adapter for DOCX, the
/// PDF adapter's structured profile for PDF (the legacy profile merges standalone label lines).
/// Gated, real spend, every call through <see cref="BudgetedClassifier"/> with its ledger kept.
/// Evidence for the user's heading decisions, not a Gold write.
/// </summary>
public sealed class ScaleUpRealHarnessRunsTests
{
    private const string Model = "qwen/qwen3.7-flash";
    private const string OutputRoot = "eval/a99-closed-loop/scaleup-real-harness-v1";

    private static readonly Dictionary<string, (string Media, string Path)> Sources = new(StringComparer.Ordinal)
    {
        ["DOC-0092"] = ("PDF", "todo10_8/heading_corpus_100/01_phap_quy/007_Luat_Nha_o_27-2023-QH15.pdf"),
        ["DOC-0264"] = ("DOCX", "todo10_8/heading_corpus_95_word/06_dich_song_ngu/084_Luat_Chung_khoan_2019_EN.docx"),
        ["SRC-003"] = ("DOCX", "todo10_8/heading_corpus_95_word/01_phap_quy/003_Luat_Doanh_nghiep_59-2020-QH14.docx"),
        ["DOC-0123"] = ("DOCX", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx"),
        ["DOC-0133"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf"),
        ["SRC-029"] = ("PDF", "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf"),
        ["SRC-041"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf"),
        ["SRC-042"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/042_IDA_Financial_Statements_June_2025.pdf"),
        ["SRC-044"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/044_IDA_Financial_Statements_June_2024.pdf"),
        ["SRC-053"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/053_IDA_Information_Statement_FY25.pdf"),
        ["SRC-054"] = ("PDF", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/054_IBRD_Information_Statement_FY25.pdf"),
    };

    [Fact]
    public void Sources_are_the_authored_gold_sources()
    {
        foreach (var (id, (media, path)) in Sources)
        {
            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")));
            var source = gold.RootElement.GetProperty("source");
            Assert.Equal(media, source.GetProperty("mediaType").GetString());
            Assert.Equal(path, source.GetProperty("sourcePath").GetString());
        }
    }

    [Fact]
    public void Planned_calls_per_run_are_measured_offline()
    {
        if (Environment.GetEnvironmentVariable("A99_SCALEUP_PLAN") != "1") return;
        var plan = Sources.Select(pair => $"{pair.Key}={(pair.Value.Media == "DOCX"
            ? Math.Ceiling(DocxEvidence(pair.Value.Path) / 120.0)
            : PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pair.Value.Path)).Packs.Count)}");
        Assert.Fail(string.Join(" ", plan));
    }

    [Fact]
    public async Task Run_once()
    {
        if (Environment.GetEnvironmentVariable("A99_SCALEUP_RUN") != "1") return;
        var id = Environment.GetEnvironmentVariable("A99_SCALEUP_ID")!;
        var repeat = Environment.GetEnvironmentVariable("A99_SCALEUP_REPEAT")!;
        var ceiling = int.Parse(Environment.GetEnvironmentVariable("A99_SCALEUP_CEILING")!);
        var (media, path) = Sources[id];
        var lane = media == "DOCX" ? "docx" : "pdf-structured";

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var retry = new RateLimitRetry(new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = apiKey, Model = Model }));
        using var budgeted = new BudgetedClassifier(retry, ceiling)
        {
            DocumentId = id,
            Stage = $"semantic-{lane}",
        };

        string? failure = null;
        string? reason = null;
        IReadOnlyList<object> elements = [];
        try
        {
            if (media == "DOCX")
            {
                var document = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
                var state = DocxPolicyStateBuilder.Build(document, NumberingStyleFeatures.FromSourceDocument(document),
                    new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
                var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
                var result = await CanonicalSemanticDocxAuthorityAdapter.RunAsync(state, mode, budgeted, CancellationToken.None);
                reason = result.Reason;
                elements = result.Structure.Elements.Select(e => (object)new { text = e.Text, sourceIds = e.Sources.Select(s => s.SourceId).ToArray() }).ToArray();
            }
            else
            {
                var result = await CanonicalSemanticPdfAuthorityAdapter.RunAsync(
                    TestRepository.Path(path), budgeted, CancellationToken.None,
                    profile: PdfSemanticAuthorityProfile.StructuredSourceParts, runPlacement: false,
                    // The default five-minute lane deadline fits a pilot-sized PDF, not 40-100 packs;
                    // four hours is the CLI's own ceiling for --pdf-stage-semantic-lane-deadline.
                    semanticLaneOptions: new SemanticLaneOptions(
                        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromHours(4)));
                reason = result.Reason;
                elements = result.Structure.Elements.Select(e => (object)new { text = e.Text, sourceIds = e.Sources.Select(s => s.SourceId).ToArray() }).ToArray();
            }
        }
        catch (Exception error)
        {
            failure = $"{error.GetType().Name}: {error.Message}";
        }

        var directory = TestRepository.Path($"{OutputRoot}/{id}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{lane}-r{repeat}.v1.json"), JsonSerializer.Serialize(new
        {
            artifactKind = "a99_scaleup_real_harness_run",
            documentId = id,
            sourcePath = path,
            sourceSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(path)),
            model = Model,
            lane,
            placement = false,
            repeat,
            providerCallsMade = budgeted.CallsMade,
            rateLimitedAttempts = retry.RefusedAttempts,
            timedOutAttempts = retry.TimedOutAttempts,
            failure,
            reason,
            elementCount = elements.Count,
            elements,
            ledger = budgeted.Ledger,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Fail($"{id} lane={lane} repeat={repeat} calls={budgeted.CallsMade} refused429={retry.RefusedAttempts} timedOut={retry.TimedOutAttempts} elements={elements.Count} failure={failure}");
    }

    /// <summary>
    /// Retries a request the provider refused with 429 (upstream rate limit: no completion, nothing
    /// billed), backing off 30s, 60s, 120s... Sits inside <see cref="BudgetedClassifier"/>, whose
    /// ledger records only completed calls, so the ceiling still bounds real spend.
    /// </summary>
    private sealed class RateLimitRetry(IHeaderClassifier inner) : IHeaderClassifier
    {
        public int RefusedAttempts { get; private set; }
        public int TimedOutAttempts { get; private set; }
        public string ModelName => inner.ModelName;
        public int ContextSize => inner.ContextSize;
        public string RuntimeDescription => inner.RuntimeDescription;
        public int SharedPrefixTokens => inner.SharedPrefixTokens;

        public async Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount); }
                catch (HttpRequestException error) when (attempt < 7 && (error.Message.Contains("429", StringComparison.Ordinal)
                    // A connection that never opened (TLS, socket): no completion came back.
                    || error.InnerException is IOException or System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException))
                {
                    RefusedAttempts++;
                    await Task.Delay(TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(attempt, 3))), ct);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && TimedOutAttempts < 2)
                {
                    // The HTTP timeout, not a cancellation: the request may have been billed, so these
                    // are counted apart from the ledger and capped.
                    TimedOutAttempts++;
                }
            }
        }

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => inner.ClassifyAsync(chunkXml, allowedIndexes, ct);
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => inner.CritiqueAsync(chunkXml, allowedIndexes, ct);
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => inner.ClassifyHierarchyAsync(context, headings, ct);
        public void Dispose() => inner.Dispose();
    }

    private static int DocxEvidence(string path)
    {
        var document = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
        var state = DocxPolicyStateBuilder.Build(document, NumberingStyleFeatures.FromSourceDocument(document),
            new DocumentFeatureDeriver().Derive(document), new ExtractionOptions());
        return DocxAuthorityPipeline.BuildForAudit(state, DocumentModeClassifier.Measure(
            state.Paragraphs.Cast<IPolicyParagraph>().ToArray())).Contexts.Count;
    }
}
