extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Review;
using DocxHeaderExtractor.Infrastructure.Review;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AuthorityTextOffsetSpan = DocxHeaderExtractor.DocumentProcessing.Authority.TextOffsetSpan;
using ReviewSpan = DocxHeaderExtractor.Application.Review.TextOffsetSpan;

namespace DocxHeaderExtractor.Tests;

public sealed class HumanReviewVerticalSliceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Projection_uses_parser_text_and_rejects_unresolved_headings()
    {
        var source = SourceDocument("doc-1",
            Paragraph("source-1", 7, "prefix Heading suffix"));
        var outline = new DocumentOutline
        {
            File = "sample.docx",
            Headings =
            [
                new HeadingRecord
                {
                    Index = 7,
                    StableId = "source-1",
                    SourceId = "source-1",
                    Level = 2,
                    Text = "Heading",
                    HeadingSpan = new AuthorityTextOffsetSpan(7, 14),
                    Confidence = 1.4,
                    DecisionStatus = HeadingDecisionStatus.RequiresReview,
                },
                new HeadingRecord
                {
                    Index = 8,
                    StableId = "missing",
                    Level = 1,
                    Text = "Missing",
                },
            ],
        };

        var result = AuthorityOutlineReviewProjection.Project(outline, source);

        var heading = Assert.Single(result.Headings);
        Assert.Equal("prefix Heading suffix"[7..14], heading.Text);
        Assert.Equal(new ReviewSpan(7, 14), heading.Span);
        Assert.Equal(1d, heading.Confidence);
        Assert.Equal(1, result.Summary.PendingCount);
        Assert.Contains(result.Diagnostics, item => item.Code == "review.heading-source-unresolved");
    }

    [Fact]
    public void Projection_falls_back_only_to_a_unique_exact_text_match()
    {
        var source = SourceDocument("doc-2",
            Paragraph("source-2", 0, "before Unique after"));
        var outline = new DocumentOutline
        {
            File = "sample.docx",
            Headings =
            [
                new HeadingRecord
                {
                    Index = 0,
                    StableId = "source-2",
                    Level = 1,
                    Text = "Unique",
                    HeadingSpan = new AuthorityTextOffsetSpan(-1, 4),
                },
            ],
        };

        var result = AuthorityOutlineReviewProjection.Project(outline, source);

        Assert.Equal(new ReviewSpan(7, 13), Assert.Single(result.Headings).Span);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Json_store_is_write_once_idempotent_and_decisions_are_append_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dhx-review-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonFileHumanReviewStore(root);
            var review = Review("doc-3", "h-1");
            await store.PublishAsync(review);
            await store.PublishAsync(review);

            var record = new HumanReviewRecord(
                "doc-3",
                new HumanReviewDecision("h-1", HumanReviewAction.Accept, null, null, null),
                ReviewState.Accepted,
                DateTimeOffset.Parse("2026-09-04T00:00:00Z"));
            await store.AppendAsync(record);

            AssertReviewEquivalent(review, await store.GetReviewAsync("doc-3"));
            Assert.Single(await store.GetRecordsAsync("doc-3"));
            Assert.Contains("\"action\":\"Accept\"", File.ReadAllText(DecisionPath(root, "doc-3")));

            var conflict = review with
            {
                Summary = review.Summary with { PendingCount = 99 },
            };
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PublishAsync(conflict));
            Assert.Equal("review-snapshot-conflict", exception.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Review_endpoints_read_and_append_without_mutating_snapshot()
    {
        var store = new TestReviewStore();
        await store.PublishAsync(Review("doc-4", "h-1"));
        await using var factory = new WebApplicationFactory<WebApp.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHumanReviewStore>();
                services.RemoveAll<HumanReviewService>();
                services.AddSingleton<IHumanReviewStore>(store);
                services.AddSingleton<HumanReviewService>();
            }));
        using var client = factory.CreateClient();

        var get = await client.GetAsync("/api/review/doc-4");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var before = await get.Content.ReadFromJsonAsync<DocumentReviewResult>(JsonOptions);

        var post = await client.PostAsJsonAsync(
            "/api/review/doc-4/decisions",
            new HumanReviewDecision("h-1", HumanReviewAction.Correct, "Updated", 2, "reviewed"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var decisions = await client.GetFromJsonAsync<HumanReviewRecord[]>(
            "/api/review/doc-4/decisions", JsonOptions);
        Assert.NotNull(decisions);
        Assert.Single(decisions!);
        Assert.Equal(ReviewState.Corrected, decisions![0].State);
        AssertReviewEquivalent(before, await store.GetReviewAsync("doc-4"));

        var missing = await client.GetAsync("/api/review/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static DocumentReviewResult Review(string documentId, string headingId) =>
        new(
            documentId,
            [new ReviewHeadingDto(
                headingId,
                "Heading",
                1,
                new ReviewSpan(0, 7),
                .8,
                "RequiresReview",
                [],
                new HeadingProvenanceDto("source-1", "docx", 0, null, "test"))],
            [],
            new ReviewSummaryDto(1, 1, 0));

    private static SourceDocument SourceDocument(string id, params SourceParagraph[] paragraphs) =>
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

    private static string DecisionPath(string root, string documentId)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(documentId))).ToLowerInvariant();
        return Path.Combine(root, hash, "decisions.jsonl");
    }

    private static void AssertReviewEquivalent(
        DocumentReviewResult? expected,
        DocumentReviewResult? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.DocumentId, actual!.DocumentId);
        using var expectedJson = JsonDocument.Parse(JsonSerializer.Serialize(expected, JsonOptions));
        using var actualJson = JsonDocument.Parse(JsonSerializer.Serialize(actual, JsonOptions));
        Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement));
        Assert.Equal(expected.Summary, actual.Summary);
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
            if (!_records.TryGetValue(record.DocumentId, out var records))
                _records[record.DocumentId] = records = [];
            records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HumanReviewRecord>> GetRecordsAsync(
            string documentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HumanReviewRecord>>(
                _records.GetValueOrDefault(documentId) ?? []);
    }
}
