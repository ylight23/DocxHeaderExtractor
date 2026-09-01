using Accuracy99Baseline;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99PhaseCAdjudicationTests
{
    [Fact]
    public void BlankPacketEnumeratesEveryCatalogOccurrenceExactlyOnce()
    {
        var document = Document(Source("p1", 0, "Heading"), Source("p2", 1, "Body"));
        var packet = PhaseCAdjudication.CreateBlankPacket(document,
            new Dictionary<string, IReadOnlyList<PhaseCHistoricalReference>>());
        var path = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(path, packet);
            Assert.Empty(PhaseCAdjudication.ValidatePacketCompleteness(path, document));
            var roundTrip = PhaseCAdjudication.ReadPacket(path);
            Assert.Equal(2, roundTrip.Manifest.CatalogOccurrenceCount);
            Assert.Equal(["p1", "p2"], roundTrip.Occurrences.Select(item => item.SourceId));
            Assert.All(roundTrip.Occurrences, item => Assert.Null(item.AdjudicatedLabel));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PacketGenerationRejectsDuplicateParserSourceIdentity()
    {
        var document = Document(Source("p1", 0, "One"), Source("p1", 1, "Two"));
        Assert.Throws<InvalidDataException>(() => PhaseCAdjudication.CreateBlankPacket(document,
            new Dictionary<string, IReadOnlyList<PhaseCHistoricalReference>>()));
    }

    [Fact]
    public void HeadingImportValidatesExactSpanAndMaterializesDeterministicIdentity()
    {
        var document = Document(Source("p1", 0, "Heading and body"), Source("p2", 1, "Body"));
        var packet = CompletedPacket(document,
            HeadingRow(document, 0, 0, 7, "Heading", "ROOT", null),
            NonHeadingRow(document, 1));
        var path = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(path, packet);
            var result = PhaseCAdjudication.ImportAndValidate(path, document);
            Assert.True(result.GoldReady, string.Join(Environment.NewLine, result.Errors));
            var heading = result.Occurrences.Single(item => item.AdjudicatedLabel == "HEADING");
            Assert.Equal("Heading", heading.HeadingText);
            Assert.Equal(PhaseCAdjudication.ComputeGoldHeadingId("doc-1", "p1", 0, 7), heading.GoldHeadingId);
            Assert.Equal(heading.GoldHeadingId,
                PhaseCAdjudication.ComputeGoldHeadingId("doc-1", "p1", 0, 7));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidHeadingTextAndContradictoryNonHeadingAreRejected()
    {
        var document = Document(Source("p1", 0, "Heading"), Source("p2", 1, "Body"));
        var badHeading = HeadingRow(document, 0, 0, 7, "Wrong", "ROOT", null);
        var badNonHeading = NonHeadingRow(document, 1) with { HeadingText = "Body" };
        var path = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(path, CompletedPacket(document, badHeading, badNonHeading));
            var result = PhaseCAdjudication.ImportAndValidate(path, document);
            Assert.False(result.GoldReady);
            Assert.Contains("HEADING_SPAN_TEXT_MISMATCH:p1", result.Errors);
            Assert.Contains("NON_HEADING_HAS_HEADING_FIELDS:p2", result.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReviewedParentMustResolveToHeadingInSameDocument()
    {
        var document = Document(Source("p1", 0, "Root"), Source("p2", 1, "Child"));
        var parentId = PhaseCAdjudication.ComputeGoldHeadingId("doc-1", "p1", 0, 4);
        var valid = CompletedPacket(document,
            HeadingRow(document, 0, 0, 4, "Root", "ROOT", null),
            HeadingRow(document, 1, 0, 5, "Child", "PARENT_REVIEWED", parentId));
        var invalid = valid with
        {
            Occurrences = [valid.Occurrences[0], valid.Occurrences[1] with { ParentGoldId = "gold-heading:missing" }],
        };
        var validPath = TempPath();
        var invalidPath = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(validPath, valid);
            PhaseCAdjudication.WritePacket(invalidPath, invalid);
            Assert.True(PhaseCAdjudication.ImportAndValidate(validPath, document).GoldReady);
            var invalidResult = PhaseCAdjudication.ImportAndValidate(invalidPath, document);
            Assert.False(invalidResult.GoldReady);
            Assert.Contains("PARENT_GOLD_ID_NOT_FOUND:p2", invalidResult.Errors);
        }
        finally
        {
            File.Delete(validPath);
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public void BlankOrIncompleteReviewCannotBecomeGoldReady()
    {
        var document = Document(Source("p1", 0, "Heading"));
        var packet = PhaseCAdjudication.CreateBlankPacket(document,
            new Dictionary<string, IReadOnlyList<PhaseCHistoricalReference>>());
        var path = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(path, packet);
            var result = PhaseCAdjudication.ImportAndValidate(path, document);
            Assert.False(result.GoldReady);
            Assert.Contains("UNREVIEWED_OCCURRENCE:p1", result.Errors);
            Assert.Contains("MANIFEST_REVIEW_STATUS_NOT_COMPLETE", result.Errors);
            Assert.Throws<InvalidOperationException>(() =>
                PhaseCAdjudication.FreezeDevelopmentGold([result], "v1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CompletedImportRoundTripsIntoFrozenDevelopmentGold()
    {
        var document = Document(Source("p1", 0, "Heading"), Source("p2", 1, "Body"));
        var packet = CompletedPacket(document,
            HeadingRow(document, 0, 0, 7, "Heading", "ROOT", null),
            NonHeadingRow(document, 1));
        var path = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(path, packet);
            var imported = PhaseCAdjudication.ImportAndValidate(path, document);
            var gold = PhaseCAdjudication.FreezeDevelopmentGold([imported], "development-gold-v1");
            Assert.True(gold.Exhaustive);
            Assert.Equal(2, gold.OccurrenceCount);
            Assert.Equal(1, gold.HeadingCount);
            Assert.Equal(1, gold.NonHeadingCount);
            Assert.Equal(1, gold.ExactSpanReadyHeadingCount);
            Assert.False(gold.Blind);
            Assert.False(gold.Claim99Eligible);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FinalDiscrepancyLabelRequiresInitialLabelAndResolutionReason()
    {
        var document = Document(Source("p1", 0, "Body"));
        var row = NonHeadingRow(document, 0) with
        {
            AdjudicatedLabel = null,
            FinalAdjudicatedLabel = "NON_HEADING",
        };
        var path = TempPath();
        try
        {
            PhaseCAdjudication.WritePacket(path, CompletedPacket(document, row));
            var result = PhaseCAdjudication.ImportAndValidate(path, document);
            Assert.False(result.GoldReady);
            Assert.Contains("DISCREPANCY_REVIEW_PROVENANCE_INCOMPLETE:p1", result.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PhaseCReviewPacket CompletedPacket(
        PhaseCSourceDocument document,
        params PhaseCReviewOccurrence[] occurrences)
    {
        var blank = PhaseCAdjudication.CreateBlankPacket(document,
            new Dictionary<string, IReadOnlyList<PhaseCHistoricalReference>>());
        return new PhaseCReviewPacket(blank.Manifest with { ReviewStatus = "REVIEW_COMPLETE" }, occurrences);
    }

    private static PhaseCReviewOccurrence HeadingRow(
        PhaseCSourceDocument document,
        int index,
        int start,
        int end,
        string text,
        string parentStatus,
        string? parentId)
    {
        var row = BlankRow(document, index);
        return row with
        {
            AdjudicatedLabel = "HEADING",
            HeadingStart = start,
            HeadingEnd = end,
            HeadingText = text,
            StructuralType = "Heading",
            Level = parentStatus == "ROOT" ? 1 : 2,
            LevelReviewStatus = "REVIEWED",
            ParentReviewStatus = parentStatus,
            ParentGoldId = parentId,
            Reviewer = "human-reviewer",
        };
    }

    private static PhaseCReviewOccurrence NonHeadingRow(PhaseCSourceDocument document, int index) =>
        BlankRow(document, index) with
        {
            AdjudicatedLabel = "NON_HEADING",
            Reviewer = "human-reviewer",
        };

    private static PhaseCReviewOccurrence BlankRow(PhaseCSourceDocument document, int index)
    {
        var packet = PhaseCAdjudication.CreateBlankPacket(document,
            new Dictionary<string, IReadOnlyList<PhaseCHistoricalReference>>());
        return packet.Occurrences[index];
    }

    private static PhaseCSourceDocument Document(params PhaseCSourceOccurrence[] sources) => new()
    {
        DatasetId = "test",
        DocumentId = "doc-1",
        SourceCatalogVersion = PhaseCAdjudication.SourceCatalogVersion,
        Occurrences = sources,
    };

    private static PhaseCSourceOccurrence Source(string id, int ordinal, string text) => new()
    {
        SourceId = id,
        SourceOrdinal = ordinal,
        RawSourceText = text,
        RawSourceSpan = new StructuralSpan(0, text.Length),
        SourceType = "docx",
    };

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"accuracy99-{Guid.NewGuid():N}.review.jsonl");
}
