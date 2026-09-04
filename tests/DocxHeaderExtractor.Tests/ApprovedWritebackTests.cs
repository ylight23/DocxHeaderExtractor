extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReviewOffsetSpan = DocxHeaderExtractor.Application.Review.TextOffsetSpan;

namespace DocxHeaderExtractor.Tests;

public sealed class ApprovedWritebackTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Plan_uses_latest_decision_without_mutating_review_and_rejects_heading()
    {
        var source = Source("doc-1",
            Paragraph("p-1", 0, "First heading"),
            Paragraph("p-2", 1, "Second heading"),
            Paragraph("p-3", 2, "Third heading"));
        var review = Review("doc-1",
            Heading("p-1", "First heading", 1, "RequiresReview"),
            Heading("p-2", "Second heading", 2, "RequiresReview"),
            Heading("p-3", "Third heading", 2, "RequiresReview"));
        var records = new[]
        {
            Record("doc-1", "p-1", HumanReviewAction.Accept),
            Record("doc-1", "p-2", HumanReviewAction.Reject),
            new HumanReviewRecord(
                "doc-1",
                new HumanReviewDecision("p-3", HumanReviewAction.Correct, null, 3, "raise"),
                ReviewState.Corrected,
                DateTimeOffset.Parse("2026-09-04T00:00:00Z")),
        };
        var before = JsonSerializer.Serialize(review, JsonOptions);

        var plan = ApprovedWritebackPlanProjector.Build(review, records, source);

        Assert.True(plan.IsReady);
        Assert.Equal(2, plan.Headings.Count(item => item.IncludeInWriteback));
        Assert.False(plan.Headings.Single(item => item.HeadingId == "p-2").IncludeInWriteback);
        Assert.Equal(3, plan.Headings.Single(item => item.HeadingId == "p-3").Level);
        Assert.Equal(before, JsonSerializer.Serialize(review, JsonOptions));
    }

    [Fact]
    public void Plan_defers_when_review_is_missing_or_corrected_text_is_not_source_backed()
    {
        var source = Source("doc-2", Paragraph("p-1", 0, "Original heading"));
        var review = Review("doc-2", Heading("p-1", "Original heading", 1, "RequiresReview"));

        var pending = ApprovedWritebackPlanProjector.Build(review, [], source);
        Assert.Equal(ApprovedWritebackPlanStatus.DeferredToHuman, pending.Status);

        var unsafeCorrection = new HumanReviewRecord(
            "doc-2",
            new HumanReviewDecision("p-1", HumanReviewAction.Correct, "Invented heading", null, null),
            ReviewState.Corrected,
            DateTimeOffset.UtcNow);
        var rejected = ApprovedWritebackPlanProjector.Build(review, [unsafeCorrection], source);
        Assert.Equal(ApprovedWritebackPlanStatus.DeferredToHuman, rejected.Status);
        Assert.Contains("not-source-backed", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Executor_requires_explicit_approval_and_writes_a_copy_only()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"dhx-r17-{Guid.NewGuid():N}.docx");
        var targetPath = Path.Combine(Path.GetTempPath(), $"dhx-r17-target-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(sourcePath);
            var source = new OpenXmlDocumentSource(new ExtractionOptions()).Read(sourcePath);
            var paragraph = source.Paragraphs.First(item => !string.IsNullOrWhiteSpace(item.Text));
            var plan = new ApprovedWritebackPlan(
                source.DocumentId,
                ApprovedWritebackPlanStatus.Ready,
                "test",
                [new ReviewedHeadingDecision(
                    paragraph.SourceId,
                    paragraph.SourceId,
                    paragraph.SourceOrdinal,
                    paragraph.Text,
                    paragraph.Text,
                    1,
                    new ReviewOffsetSpan(0, paragraph.Text.Length),
                    HumanReviewAction.Accept,
                    ReviewState.Accepted,
                    true,
                    null)]);

            Assert.Throws<InvalidOperationException>(() => ApprovedWritebackExecutor.Apply(
                sourcePath, targetPath, plan, new ExtractionOptions(), explicitApproval: false));
            var sourceBytes = File.ReadAllBytes(sourcePath);
            var result = ApprovedWritebackExecutor.Apply(
                sourcePath, targetPath, plan, new ExtractionOptions(), explicitApproval: true);

            Assert.Equal(1, result.Applied);
            Assert.True(File.Exists(targetPath));
            Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(targetPath);
        }
    }

    [Fact]
    public async Task Web_plan_and_apply_are_separate_explicit_actions()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"dhx-r17-api-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(sourcePath);
            var source = new OpenXmlDocumentSource(new ExtractionOptions()).Read(sourcePath);
            var paragraph = source.Paragraphs.First(item => !string.IsNullOrWhiteSpace(item.Text));
            var store = new TestReviewStore();
            await store.PublishAsync(Review(source.DocumentId,
                Heading(paragraph.SourceId, paragraph.Text, 1, "RequiresReview")));
            await store.AppendAsync(Record(source.DocumentId, paragraph.SourceId, HumanReviewAction.Accept));
            Assert.NotNull(await store.GetReviewAsync(source.DocumentId));

            await using var factory = new WebApplicationFactory<WebApp.Program>()
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHumanReviewStore>();
                    services.RemoveAll<HumanReviewService>();
                    services.AddSingleton<IHumanReviewStore>(store);
                    services.AddSingleton<HumanReviewService>();
                }));
            using var client = factory.CreateClient();

            var reviewUrlId = Uri.EscapeDataString(source.DocumentId);
            var reviewResponse = await client.GetAsync("/api/review/" + reviewUrlId);
            Assert.True(reviewResponse.IsSuccessStatusCode,
                $"review lookup {(int)reviewResponse.StatusCode} {reviewResponse.StatusCode} for '{source.DocumentId}'");
            var planResponse = await client.PostAsync(
                "/api/review/" + reviewUrlId + "/writeback-plan", content: null);
            var planBody = await planResponse.Content.ReadAsStringAsync();
            Assert.True(planResponse.IsSuccessStatusCode,
                $"{(int)planResponse.StatusCode} {planResponse.StatusCode}: {planBody}");
            var plan = await planResponse.Content.ReadFromJsonAsync<ApprovedWritebackPlan>(JsonOptions);
            Assert.NotNull(plan);
            Assert.True(plan!.IsReady);

            await using var stream = File.OpenRead(sourcePath);
            using var file = new StreamContent(stream);
            using var form = new MultipartFormDataContent();
            form.Add(file, "file", "source.docx");
            var apply = await client.PostAsync(
                "/api/review/" + reviewUrlId + "/writeback", form);
            Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                apply.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            TryDelete(sourcePath);
        }
    }

    private static HumanReviewRecord Record(string documentId, string headingId, HumanReviewAction action) =>
        new(documentId, new HumanReviewDecision(headingId, action, null, null, null),
            action == HumanReviewAction.Accept ? ReviewState.Accepted : ReviewState.Rejected,
            DateTimeOffset.UtcNow);

    private static DocumentReviewResult Review(string documentId, params ReviewHeadingDto[] headings) =>
        new(documentId, headings, [], new ReviewSummaryDto(
            headings.Length,
            headings.Count(item => item.Status == "RequiresReview"),
            0));

    private static ReviewHeadingDto Heading(
        string id, string text, int level, string status) =>
        new(id, text, level, new ReviewOffsetSpan(0, text.Length), .9, status, [],
            new HeadingProvenanceDto(id, "docx", 0, null, "test"));

    private static SourceDocument Source(string id, params SourceParagraph[] paragraphs) =>
        new()
        {
            DocumentId = id,
            FileName = "sample.docx",
            SourcePath = "sample.docx",
            SourceKind = "docx",
            Paragraphs = paragraphs,
        };

    private static SourceParagraph Paragraph(string id, int ordinal, string text) =>
        new()
        {
            SourceId = id,
            SourceOrdinal = ordinal,
            Text = text,
            Style = new SourceStyleFacts(),
            Numbering = new SourceNumberingFacts(),
            Layout = new SourceLayoutFacts(),
        };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private sealed class TestReviewStore : IHumanReviewStore
    {
        private readonly Dictionary<string, DocumentReviewResult> _reviews = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<HumanReviewRecord>> _records = new(StringComparer.Ordinal);

        public Task PublishAsync(DocumentReviewResult review, CancellationToken cancellationToken = default)
        {
            _reviews[review.DocumentId] = review;
            return Task.CompletedTask;
        }

        public Task<DocumentReviewResult?> GetReviewAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_reviews.GetValueOrDefault(documentId));

        public Task AppendAsync(HumanReviewRecord record, CancellationToken cancellationToken = default)
        {
            if (!_records.TryGetValue(record.DocumentId, out var list))
                _records[record.DocumentId] = list = [];
            list.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HumanReviewRecord>> GetRecordsAsync(
            string documentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HumanReviewRecord>>(
                _records.GetValueOrDefault(documentId) ?? []);
    }
}
