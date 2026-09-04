using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Evaluator_joins_by_source_and_span_and_measures_hierarchy()
    {
        var source = Source(
            ("p0", "Root"),
            ("p1", "Child"),
            ("p2", "body"),
            ("p3", "noise"));
        var gold = Gold(source,
            [("p0", 0, 4, 1, null), ("p1", 0, 5, 2, "p0")]);
        var predictions = new[]
        {
            new Accuracy99Prediction("p0", new(0, 4), 1, "Root", HasParent: true),
            new Accuracy99Prediction("p1", new(0, 5), 2, "Child", ParentSourceId: "p0", HasParent: true),
            new Accuracy99Prediction("p2", new(0, 4), 1, "body"),
        };

        var metrics = Accuracy99Evaluator.Evaluate(source, gold, predictions);

        Assert.Equal(2, metrics.TruePositives);
        Assert.Equal(1, metrics.FalsePositives);
        Assert.Equal(0, metrics.FalseNegatives);
        Assert.Equal(2, metrics.ExactSpanMatches);
        Assert.Equal(2, metrics.LevelCorrect);
        Assert.Equal(2, metrics.ParentCorrect);
        Assert.Equal(2, metrics.HierarchyCorrect);
        Assert.Equal(2.0 / 3.0, metrics.Precision);
        Assert.Equal(1.0, metrics.Recall);
        Assert.True(metrics.DocumentExactMatch is false);
    }

    [Fact]
    public void Evaluator_does_not_text_match_without_source_identity()
    {
        var source = Source(("p0", "Same text"));
        var gold = Gold(source, [("p0", 0, 9, 1, null)]);

        var metrics = Accuracy99Evaluator.Evaluate(source, gold, [
            new Accuracy99Prediction("other", new(0, 9), 1, "Same text"),
        ]);

        Assert.Equal(0, metrics.TruePositives);
        Assert.Equal(1, metrics.FalseNegatives);
        Assert.Equal(1, metrics.SourceUnjoined);
        Assert.Equal(0, metrics.FalsePositives);
    }

    [Fact]
    public void Evaluator_separates_covering_span_from_exact_span()
    {
        var source = Source(("p0", "prefix Root suffix"));
        var gold = Gold(source, [("p0", 7, 11, 1, null)]);

        var metrics = Accuracy99Evaluator.Evaluate(source, gold, [
            new Accuracy99Prediction("p0", new(0, source.Paragraphs[0].Text.Length), 1, source.Paragraphs[0].Text),
        ]);

        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(0, metrics.ExactSpanMatches);
        Assert.Equal(1, metrics.SpanEvaluated);
    }

    [Fact]
    public void Aggregate_reports_micro_and_macro_metrics_per_document()
    {
        var first = new Accuracy99DocumentMetrics
        {
            DocumentId = "a", TruePositives = 1, FalsePositives = 0, FalseNegatives = 0,
            Precision = 1, Recall = 1, F1 = 1,
        };
        var second = new Accuracy99DocumentMetrics
        {
            DocumentId = "b", TruePositives = 0, FalsePositives = 1, FalseNegatives = 1,
            Precision = 0, Recall = 0, F1 = 0,
        };

        var aggregate = Accuracy99Evaluator.Aggregate([first, second]);

        Assert.Equal(2, aggregate.DocumentCount);
        Assert.Equal(1, aggregate.Micro.TruePositives);
        Assert.Equal(0.5, aggregate.MacroPrecision);
        Assert.Equal(0.5, aggregate.MacroRecall);
        Assert.Equal(0.5, aggregate.MacroF1);
    }

    [Fact]
    public void Human_gold_validator_rejects_invalid_metadata_span_level_parent_and_cycle()
    {
        var source = Source(("p0", "Root"), ("p1", "Child"));
        var valid = Gold(source, [("p0", 0, 4, 1, "p1"), ("p1", 0, 5, 10, "p0")]);
        var result = HumanGoldValidator.Validate(valid with
        {
            ArtifactKind = "silver",
            SourceOccurrences =
            [
                .. valid.SourceOccurrences,
                valid.SourceOccurrences[0],
            ],
        }, source);

        Assert.False(result.IsValid);
        Assert.Contains("artifact-kind-not-human-gold", result.Errors);
        Assert.Contains("silver-artifact-rejected", result.Errors);
        Assert.Contains("duplicate-gold-source-identity:p0", result.Errors);
        Assert.Contains("heading-level-invalid:p1", result.Errors);
        Assert.Contains("heading-hierarchy-cycle:p0", result.Errors);
    }

    [Fact]
    public void Human_gold_validator_rejects_invalid_source_span_and_missing_parent()
    {
        var source = Source(("p0", "Root"));
        var result = HumanGoldValidator.Validate(Gold(source, [
            ("p0", 0, 99, 1, "missing"),
        ]) with
        {
            SourceOccurrences =
            [
                new HumanGoldSourceOccurrence
                {
                    SourceId = "p0",
                    SourceOrdinal = 0,
                    Span = new Accuracy99Span(0, 99),
                    Label = GoldSourceLabel.Heading,
                },
            ],
        }, source);

        Assert.False(result.IsValid);
        Assert.Contains("gold-source-span-invalid:p0", result.Errors);
        Assert.Contains("heading-span-invalid:p0", result.Errors);
        Assert.Contains("heading-parent-not-found:p0", result.Errors);
    }

    [Fact]
    public void Blind_packet_contains_source_facts_but_no_prediction_fields()
    {
        var source = Source(("p0", "prefix Figure 3 caption suffix"));
        var packet = BlindSourcePacketBuilder.Create(source, "sha256");
        var json = JsonSerializer.Serialize(packet, JsonOptions);

        Assert.Contains("prefix Figure 3 caption suffix", json);
        Assert.Contains("\"start\":0", json);
        Assert.Contains("\"end\":30", json);
        Assert.DoesNotContain("headingSpan", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("predicted", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(BlindSourcePacketLeakageValidator.FindLeaks(json));
    }

    [Fact]
    public void Blind_packet_leakage_validator_rejects_injected_prediction_fields()
    {
        var leaks = BlindSourcePacketLeakageValidator.FindLeaks(
            """{"sourceId":"p0","prediction":{"level":1},"selected":true}""");

        Assert.Contains(leaks, leak => leak.EndsWith(".prediction", StringComparison.Ordinal));
        Assert.Contains(leaks, leak => leak.EndsWith(".selected", StringComparison.Ordinal));
    }

    [Fact]
    public void Human_gold_round_trips_with_string_enums()
    {
        var source = Source(("p0", "Root"));
        var gold = Gold(source, [("p0", 0, 4, 1, null)]);
        var json = JsonSerializer.Serialize(gold, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<HumanGoldArtifact>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal("human_gold", roundTrip!.ArtifactKind);
        Assert.Equal(GoldSourceLabel.Heading, Assert.Single(roundTrip.SourceOccurrences).Label);
        Assert.Equal(1, Assert.Single(roundTrip.Headings).Level);
    }

    [Fact]
    public void Inventory_classifies_key_sidecar_as_silver_only()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dhx-a99-inventory-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "sample.docx");
        try
        {
            Directory.CreateDirectory(root);
            SampleDocumentFactory.Create(sourcePath);
            File.WriteAllText(Path.ChangeExtension(sourcePath, ".key"), "0 1 Root");

            var inventory = Accuracy99DatasetInventoryBuilder.Discover(root);

            var entry = Assert.Single(inventory.Entries);
            Assert.Equal(Accuracy99DatasetClassification.SilverOnly, entry.Classification);
            Assert.Equal("NOT_READY_HUMAN_GOLD_REQUIRED", inventory.FreezeStatus);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static HumanGoldArtifact Gold(
        SourceDocument source,
        IEnumerable<(string Id, int Start, int End, int Level, string? Parent)> headings)
    {
        var headingArray = headings.ToArray();
        var headingIds = headingArray.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return new HumanGoldArtifact
        {
            ArtifactKind = "human_gold",
            AuthorityClass = "HUMAN_GOLD",
            ReviewerId = "reviewer-test",
            AdjudicationVersion = "a99-test-v1",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            SourceDocumentSha256 = "sha256",
            DocumentId = source.DocumentId,
            MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Split = "DEV",
            ExhaustiveSourceLabels = true,
            SourceOccurrences = source.Paragraphs.Select(paragraph =>
            {
                var heading = headingArray.FirstOrDefault(x => x.Id == paragraph.SourceId);
                var label = heading.Id is not null
                    ? GoldSourceLabel.Heading
                    : GoldSourceLabel.NonHeading;
                return new HumanGoldSourceOccurrence
                {
                    SourceId = paragraph.SourceId,
                    SourceOrdinal = paragraph.SourceOrdinal,
                    Span = new Accuracy99Span(0, paragraph.Text.Length),
                    Label = label,
                };
            }).ToArray(),
            Headings = headingArray.Select(x => new HumanGoldHeading
            {
                SourceId = x.Id,
                HeadingSpan = new Accuracy99Span(x.Start, x.End),
                Level = x.Level,
                ParentSourceId = x.Parent,
            }).ToArray(),
        };
    }

    private static SourceDocument Source(params (string Id, string Text)[] paragraphs) =>
        new()
        {
            DocumentId = "doc-1",
            FileName = "doc.docx",
            SourcePath = "missing-doc.docx",
            SourceKind = "docx",
            Paragraphs = paragraphs.Select((item, index) => new SourceParagraph
            {
                SourceId = item.Id,
                SourceOrdinal = index,
                Text = item.Text,
                Style = new SourceStyleFacts(),
                Numbering = new SourceNumberingFacts(),
                Layout = new SourceLayoutFacts(),
            }).ToArray(),
        };
}
