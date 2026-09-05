using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99GoldV2Tests
{
    [Fact]
    public void Positive_set_is_valid_without_negative_rows()
    {
        var packet = Packet();
        var gold = Gold(packet, [Heading(packet, "p0")]);

        var result = A99HumanGoldV2Validator.Validate(packet, gold);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Unsure_blocks_exhaustive_certification_without_becoming_NO()
    {
        var packet = Packet();
        var gold = Gold(packet, [Heading(packet, "p0")]) with { UnsureSourceIds = ["p1"] };

        var result = A99HumanGoldV2Validator.Validate(packet, gold);

        Assert.False(result.IsValid);
        Assert.Contains("unsure-prevents-exhaustive-certification", result.Errors);
    }

    [Fact]
    public void Positive_set_metrics_count_unmatched_prediction_as_fp()
    {
        var packet = Packet();
        var gold = Gold(packet, [Heading(packet, "p0")]);
        var metrics = A99PositiveSetEvaluator.Evaluate(gold,
        [
            new("p0", new(0, 4), 1, "heading", "ROOT"),
            new("p1", new(0, 4), 1, "heading", "ROOT"),
        ]);

        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(1, metrics.FalsePositives);
        Assert.Equal(0, metrics.FalseNegatives);
        Assert.Equal(0.5, metrics.Precision);
    }

    [Fact]
    public void Positive_set_metrics_count_unmatched_gold_as_fn()
    {
        var packet = Packet();
        var gold = Gold(packet, [Heading(packet, "p0"), Heading(packet, "p2")]);
        var metrics = A99PositiveSetEvaluator.Evaluate(gold,
        [new("p0", new(0, 4), 1, "heading", "ROOT")]);

        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(0, metrics.FalsePositives);
        Assert.Equal(1, metrics.FalseNegatives);
        Assert.Equal(0.5, metrics.Recall);
    }

    [Fact]
    public void V2_parent_graph_rejects_self_parent_and_cycle()
    {
        var packet = Packet();
        var rows = new[]
        {
            Heading(packet, "p0") with { ParentOccurrenceId = "p1" },
            Heading(packet, "p1") with { ParentOccurrenceId = "p0" },
            Heading(packet, "p2") with { ParentOccurrenceId = "p2" },
        };

        var result = A99HumanGoldV2Validator.Validate(packet, Gold(packet, rows));

        Assert.False(result.IsValid);
        Assert.Contains("hierarchy-cycle:p0", result.Errors);
        Assert.Contains("parent-self:p2", result.Errors);
    }

    [Fact]
    public void Early_campaign_selection_is_deterministic_and_covers_families()
    {
        var documents = new[] { "A", "B", "C" }.SelectMany(family => Enumerable.Range(1, 5).Select(index => new A99CampaignDocument
        {
            DocumentId = family + index,
            DocumentGroupId = "G-" + family + index,
            Split = "DEV",
            FamilyId = family,
            SourcePath = "todo10_8/" + family + index + ".docx",
            SourceSha256 = "sha-" + family + index,
            SourceOccurrenceCount = index * 10,
            PacketPath = "dev/" + family + index + ".v1.json",
            PacketSha256 = "packet-" + family + index,
        })).ToArray();
        var campaign = new A99ReferenceCampaign
        {
            CreatedFromRevision = "base",
            SourceCorpus = "todo10_8/heading_corpus_95_word/",
            SelectionPolicy = "frozen",
            DevDocuments = documents,
            HoldoutDocuments = [],
            FamilySummary = [],
            ExistingPacketAudit = [],
        };

        var first = A99EarlyDevCampaignBuilder.Build(campaign, "revision");
        var second = A99EarlyDevCampaignBuilder.Build(campaign, "revision");

        Assert.Equal(15, first.Documents.Count);
        Assert.Equal(first.Documents.Select(x => x.DocumentId), second.Documents.Select(x => x.DocumentId));
        Assert.Equal(new[] { "A", "B", "C" }, first.FamiliesCovered);
        Assert.All(first.Documents, x => Assert.Equal("DEV", x.Split));
    }

    [Fact]
    public void Autonomy_rates_use_documents_and_gold_headings_as_denominators()
    {
        var packet = Packet();
        var gold = Gold(packet, [Heading(packet, "p0")]);
        var result = A99PositiveSetEvaluator.ComputeAutonomy(
            [(true, false, false, 0), (false, true, true, 1)],
            [(gold, (IReadOnlyList<A99PositivePrediction>)[new("p0", new(0, 4), 1, "heading", "ROOT")])]);

        Assert.Equal(0.5, result.DocumentAutoCompletionRate);
        Assert.Equal(1, result.GoldHeadingsResolvedWithoutHuman);
        Assert.Equal(1, result.HeadingAutoCoverage);
        Assert.Equal(1, result.AbstainedDocuments);
        Assert.Equal(1, result.ReviewEscalatedDocuments);
    }

    private static A99ReviewPacket Packet()
    {
        var occurrences = new[]
        {
            Occurrence("p0", "Root", 0),
            Occurrence("p1", "Body", 1),
            Occurrence("p2", "Child", 2),
        };
        var packet = new A99ReviewPacket
        {
            DocumentId = "DOC-V2",
            DocumentGroupId = "GROUP-V2",
            Split = "DEV",
            FamilyId = "TEST",
            FileName = "test.docx",
            SourceKind = "DOCX",
            SourceDocumentSha256 = "source-sha",
            Occurrences = occurrences,
        };
        return packet with { PacketSha256 = A99ReviewPacketBuilder.ComputeSha256(packet) };
    }

    private static A99ReviewOccurrence Occurrence(string id, string text, int ordinal) => new()
    {
        SourceId = id,
        StableId = id,
        SourceOrdinal = ordinal,
        SourceSpan = new(0, text.Length),
        SourceTextHash = A99ReviewPacketBuilder.TextSha256(text),
        SourceText = text,
        Style = new(),
        Numbering = new(),
        Layout = new(),
    };

    private static A99GoldV2Heading Heading(A99ReviewPacket packet, string sourceId) =>
        new()
        {
            SourceId = sourceId,
            StableId = sourceId,
            SourceOrdinal = packet.Occurrences.Single(x => x.SourceId == sourceId).SourceOrdinal,
            SourceSpan = packet.Occurrences.Single(x => x.SourceId == sourceId).SourceSpan,
            SourceTextHash = packet.Occurrences.Single(x => x.SourceId == sourceId).SourceTextHash,
            HeadingSpan = packet.Occurrences.Single(x => x.SourceId == sourceId).SourceSpan,
            Role = "heading",
            Level = sourceId == "p0" ? 1 : 2,
            ParentOccurrenceId = sourceId == "p0" ? "ROOT" : "p0",
        };

    private static A99HumanGoldV2Document Gold(A99ReviewPacket packet, IReadOnlyList<A99GoldV2Heading> rows) => new()
    {
        ReviewerAlias = "reviewer-v2",
        ReviewedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        ReviewVersion = "a99-human-gold-v2",
        ReviewedEntireDocument = true,
        HeadingSetExhaustive = true,
        IndependentOfModelPrediction = true,
        DocumentId = packet.DocumentId,
        DocumentGroupId = packet.DocumentGroupId,
        Split = packet.Split,
        SourceDocumentSha256 = packet.SourceDocumentSha256,
        PacketSha256 = packet.PacketSha256!,
        Rows = rows,
    };
}
