using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99GoldV3Tests
{
    [Fact]
    public void Same_source_with_two_spans_is_valid_and_keeps_both_occurrences()
    {
        var packet = Packet("prefix heading A and heading B suffix");
        var rows = Rows(packet);

        var result = A99HumanGoldV3Validator.Validate(packet, Gold(packet, rows));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(2, rows.Length);
        Assert.NotEqual(rows[0].HeadingOccurrenceId, rows[1].HeadingOccurrenceId);
    }

    [Fact]
    public void Same_source_and_same_span_is_rejected_as_duplicate()
    {
        var packet = Packet("heading");
        var row = Row(packet, 0, 7, "ROOT");

        var result = A99HumanGoldV3Validator.Validate(packet, Gold(packet, [row, row]));

        Assert.False(result.IsValid);
        Assert.Contains("duplicate-heading-occurrence:p4@0:7", result.Errors);
    }

    [Fact]
    public void Parent_and_child_can_share_source_but_self_parent_is_invalid()
    {
        var packet = Packet("Parent and child");
        var parent = Row(packet, 0, 6, "ROOT", level: 1);
        var child = Row(packet, 11, 16, parent.HeadingOccurrenceId, level: 2);

        var valid = A99HumanGoldV3Validator.Validate(packet, Gold(packet, [parent, child]));
        var self = A99HumanGoldV3Validator.Validate(packet, Gold(packet, [parent with
        {
            ParentHeadingOccurrenceId = parent.HeadingOccurrenceId,
        }]));

        Assert.True(valid.IsValid, string.Join("; ", valid.Errors));
        Assert.False(self.IsValid);
        Assert.Contains("parent-self:p4@0:6", self.Errors);
    }

    [Fact]
    public void Cycles_are_checked_by_heading_occurrence_id_not_source_id()
    {
        var packet = Packet("Parent and child");
        var parent = Row(packet, 0, 6, "ROOT", level: 1);
        var child = Row(packet, 11, 16, parent.HeadingOccurrenceId, level: 2);
        var cycle = Gold(packet, [parent with { ParentHeadingOccurrenceId = child.HeadingOccurrenceId }, child]);

        var result = A99HumanGoldV3Validator.Validate(packet, cycle);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.StartsWith("hierarchy-cycle:", StringComparison.Ordinal));
    }

    [Fact]
    public void Exact_evaluator_counts_two_same_source_predictions_as_two_true_positives()
    {
        var packet = Packet("prefix heading A and heading B suffix");
        var rows = Rows(packet);
        var gold = Gold(packet, rows);
        var metrics = A99PositiveSetEvaluatorV3.Evaluate(gold, rows.Select(row =>
            new A99PositivePrediction(row.SourceId, new Accuracy99Span(row.HeadingSpan.Start, row.HeadingSpan.End), row.Level, row.Role, row.ParentHeadingOccurrenceId)));

        Assert.Equal(2, metrics.TruePositives);
        Assert.Equal(0, metrics.FalseNegatives);
        Assert.Equal(2, metrics.ExactSpanMatches);
    }

    [Fact]
    public void One_prediction_cannot_satisfy_two_same_source_gold_occurrences()
    {
        var packet = Packet("prefix heading A and heading B suffix");
        var rows = Rows(packet);
        var metrics = A99PositiveSetEvaluatorV3.Evaluate(Gold(packet, rows), [
            new A99PositivePrediction("p4", new Accuracy99Span(rows[0].HeadingSpan.Start, rows[0].HeadingSpan.End), 1, "heading", "ROOT"),
        ]);

        Assert.Equal(1, metrics.TruePositives);
        Assert.Equal(1, metrics.FalseNegatives);
        Assert.Equal(0.5, metrics.Recall);
    }

    [Fact]
    public void Unsure_span_fails_closed_for_strict_gold_without_collapsing_confirmed_spans()
    {
        var packet = Packet("prefix heading A and heading B suffix");
        var gold = Gold(packet, Rows(packet)) with
        {
            HeadingSetExhaustive = false,
            UnsureSpans = [new A99UnsureSpan { SourceId = "p4", Span = new(17, 20), Reason = "needs-review" }],
        };

        var result = A99HumanGoldV3Validator.Validate(packet, gold);

        Assert.False(result.IsValid);
        Assert.Contains("heading-set-exhaustive-not-declared", result.Errors);
        Assert.Equal(2, gold.Rows.Count);
        Assert.Single(gold.UnsureSpans);
    }

    private static A99HumanGoldV3Document Gold(A99ReviewPacket packet, IReadOnlyList<A99GoldV3Heading> rows) => new()
    {
        ReviewerAlias = "reviewer-test",
        ReviewedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        ReviewVersion = "a99-human-gold-v3-test",
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

    private static A99GoldV3Heading[] Rows(A99ReviewPacket packet) =>
    [
        Row(packet, 7, 18, "ROOT", level: 1),
        Row(packet, 23, 34, A99HeadingOccurrenceIdentity.Create("p4", 7, 18), level: 2),
    ];

    private static A99GoldV3Heading Row(A99ReviewPacket packet, int start, int end, string parent, int level = 1)
    {
        var occurrence = packet.Occurrences.Single();
        return new A99GoldV3Heading
        {
            HeadingOccurrenceId = A99HeadingOccurrenceIdentity.Create(occurrence.SourceId, start, end),
            SourceId = occurrence.SourceId,
            StableId = occurrence.StableId,
            SourceOrdinal = occurrence.SourceOrdinal,
            SourceSpan = occurrence.SourceSpan,
            SourceTextHash = occurrence.SourceTextHash,
            HeadingSpan = new(start, end),
            Role = "heading",
            Level = level,
            ParentHeadingOccurrenceId = parent,
        };
    }

    private static A99ReviewPacket Packet(string text)
    {
        var occurrence = new A99ReviewOccurrence
        {
            SourceId = "p4",
            StableId = "physical-p4",
            SourceOrdinal = 4,
            SourceSpan = new(0, text.Length),
            SourceTextHash = A99ReviewPacketBuilder.TextSha256(text),
            SourceText = text,
            Style = new(),
            Numbering = new(),
            Layout = new(),
        };
        var packet = new A99ReviewPacket
        {
            DocumentId = "DOC-TEST-V3",
            DocumentGroupId = "GROUP-TEST",
            Split = "DEV",
            FamilyId = "TEST",
            FileName = "test.docx",
            SourceKind = "docx",
            SourceDocumentSha256 = "source-sha",
            Occurrences = [occurrence],
        };
        return packet with { PacketSha256 = A99ReviewPacketBuilder.ComputeSha256(packet) };
    }
}
