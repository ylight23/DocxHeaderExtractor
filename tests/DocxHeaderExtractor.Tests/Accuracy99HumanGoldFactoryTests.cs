using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99HumanGoldFactoryTests
{
    [Fact]
    public void Validator_requires_exhaustive_source_coverage()
    {
        var packet = Packet();
        var gold = Gold(packet, Rows(packet).Take(1).ToArray());
        var result = A99HumanGoldValidator.Validate(packet, gold);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.StartsWith("missing-gold-source-identity:", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duplicate_occurrence_wrong_source_sha_and_wrong_packet_sha()
    {
        var packet = Packet();
        var rows = Rows(packet).ToArray();
        var duplicate = rows[0];
        var gold = Gold(packet, [.. rows, duplicate]) with
        {
            SourceDocumentSha256 = "wrong-source",
            PacketSha256 = "wrong-packet",
        };
        var result = A99HumanGoldValidator.Validate(packet, gold);

        Assert.False(result.IsValid);
        Assert.Contains("source-sha-mismatch", result.Errors);
        Assert.Contains("packet-sha-mismatch", result.Errors);
        Assert.Contains(result.Errors, error => error.StartsWith("duplicate-gold-source-identity:", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_invalid_span_level_self_parent_and_cycles()
    {
        var packet = Packet();
        var rows = Rows(packet).ToArray();
        rows[0] = rows[0] with { HeadingSpan = new(0, 99), Level = 10, ParentOccurrenceId = "p0" };
        rows[1] = rows[1] with { ParentOccurrenceId = "p0" };
        var result = A99HumanGoldValidator.Validate(packet, Gold(packet, rows));

        Assert.False(result.IsValid);
        Assert.Contains("heading-span-invalid:p0", result.Errors);
        Assert.Contains("heading-level-invalid:p0", result.Errors);
        Assert.Contains("parent-self:p0", result.Errors);
        Assert.Contains("hierarchy-cycle:p0", result.Errors);
    }

    [Fact]
    public void Validator_rejects_no_row_with_heading_fields_and_yes_without_role()
    {
        var packet = Packet();
        var rows = Rows(packet).ToArray();
        rows[0] = rows[0] with { IsHeading = A99ReviewLabel.No, Role = "body", HeadingSpan = new(0, 4), Level = 1 };
        rows[1] = rows[1] with { IsHeading = A99ReviewLabel.Yes, Role = null, HeadingSpan = new(0, 5), Level = 2, ParentOccurrenceId = "ROOT" };
        var result = A99HumanGoldValidator.Validate(packet, Gold(packet, rows));

        Assert.False(result.IsValid);
        Assert.Contains("non-heading-has-heading-span:p0", result.Errors);
        Assert.Contains("non-heading-has-level:p0", result.Errors);
        Assert.Contains("heading-role-invalid:p1", result.Errors);
    }

    [Fact]
    public void Unsure_is_explicit_but_excluded_from_official_denominator()
    {
        var packet = Packet();
        var rows = Rows(packet).ToArray();
        rows[0] = rows[0] with { IsHeading = A99ReviewLabel.Unsure, Role = null, HeadingSpan = null, Level = null, ParentOccurrenceId = null };
        rows[1] = rows[1] with { IsHeading = A99ReviewLabel.No, Role = "body", HeadingSpan = null, Level = null, ParentOccurrenceId = null };
        var result = A99HumanGoldValidator.Validate(packet, Gold(packet, rows));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(1, A99HumanGoldValidator.OfficialDenominatorCount(rows));
    }

    [Fact]
    public void Unknown_parent_is_explicit_and_valid_without_inventing_a_relation()
    {
        var packet = Packet();
        var rows = Rows(packet).ToArray();
        rows[0] = rows[0] with { ParentOccurrenceId = "UNKNOWN" };
        rows[1] = rows[1] with { IsHeading = A99ReviewLabel.No, Role = "body", HeadingSpan = null, Level = null, ParentOccurrenceId = null };
        var result = A99HumanGoldValidator.Validate(packet, Gold(packet, rows));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Silver_and_non_independent_artifacts_are_rejected()
    {
        var packet = Packet();
        var result = A99HumanGoldValidator.Validate(packet, Gold(packet, Rows(packet)) with
        {
            ArtifactKind = "model_assisted_silver",
            IndependentOfModelPrediction = false,
        });

        Assert.False(result.IsValid);
        Assert.Contains("silver-artifact-rejected", result.Errors);
        Assert.Contains("reviewer-independence-not-declared", result.Errors);
    }

    [Fact]
    public void Dev_guard_refuses_to_open_sealed_holdout()
    {
        Assert.Throws<InvalidOperationException>(() => A99GoldStoreGuard.EnsureDevPath("C:\\A99-Gold\\holdout-sealed"));
        A99GoldStoreGuard.EnsureDevPath("C:\\A99-Gold\\dev");
    }

    private static A99ReviewPacket Packet()
    {
        var occurrences = new[]
        {
            new A99ReviewOccurrence { SourceId = "p0", StableId = "p0", SourceOrdinal = 0, SourceSpan = new(0, 4), SourceTextHash = A99ReviewPacketBuilder.TextSha256("Root"), SourceText = "Root", Style = new(), Numbering = new(), Layout = new() },
            new A99ReviewOccurrence { SourceId = "p1", StableId = "p1", SourceOrdinal = 1, SourceSpan = new(0, 5), SourceTextHash = A99ReviewPacketBuilder.TextSha256("Child"), SourceText = "Child", PreviousSourceId = "p0", PreviousText = "Root", Style = new(), Numbering = new(), Layout = new() },
        };
        var packet = new A99ReviewPacket
        {
            DocumentId = "DOC-TEST",
            DocumentGroupId = "GROUP-TEST",
            Split = "DEV",
            FamilyId = "TEST",
            FileName = "test.docx",
            SourceKind = "docx",
            SourceDocumentSha256 = "source-sha",
            Occurrences = occurrences,
        };
        return packet with { PacketSha256 = A99ReviewPacketBuilder.ComputeSha256(packet) };
    }

    private static A99GoldOccurrence[] Rows(A99ReviewPacket packet) =>
    [
        new() { SourceId = "p0", StableId = "p0", SourceOrdinal = 0, SourceSpan = new(0, 4), SourceTextHash = packet.Occurrences[0].SourceTextHash, IsHeading = A99ReviewLabel.Yes, Role = "heading", HeadingSpan = new(0, 4), Level = 1, ParentOccurrenceId = "ROOT" },
        new() { SourceId = "p1", StableId = "p1", SourceOrdinal = 1, SourceSpan = new(0, 5), SourceTextHash = packet.Occurrences[1].SourceTextHash, IsHeading = A99ReviewLabel.Yes, Role = "heading", HeadingSpan = new(0, 5), Level = 2, ParentOccurrenceId = "p0" },
    ];

    private static A99HumanGoldDocument Gold(A99ReviewPacket packet, IReadOnlyList<A99GoldOccurrence> rows) => new()
    {
        ReviewerAlias = "test-reviewer",
        ReviewedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        ReviewVersion = "a99-human-gold-v1",
        IndependentOfModelPrediction = true,
        DocumentId = packet.DocumentId,
        DocumentGroupId = packet.DocumentGroupId,
        Split = packet.Split,
        SourceDocumentSha256 = packet.SourceDocumentSha256,
        PacketSha256 = packet.PacketSha256!,
        Rows = rows,
    };
}
