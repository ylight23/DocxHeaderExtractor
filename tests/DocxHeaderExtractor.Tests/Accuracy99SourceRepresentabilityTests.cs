using DocxHeaderExtractor.Eval.Accuracy99;

namespace DocxHeaderExtractor.Tests;

public sealed class Accuracy99SourceRepresentabilityTests
{
    [Fact]
    public void Drawing_only_packet_is_not_character_span_representable()
    {
        var result = A99SourceRepresentabilityGate.Evaluate(Packet(EmptyOccurrence("image-page")));

        Assert.Equal(A99SourceRepresentabilityStatus.ImageOnlyNotCharacterSpanRepresentable, result.Status);
        Assert.Equal(0, result.NonEmptyOccurrenceCount);
        Assert.Equal(0, result.ProviderCalls);
    }

    [Fact]
    public void Normal_text_packet_is_character_span_representable()
    {
        var result = A99SourceRepresentabilityGate.Evaluate(Packet(
            TextOccurrence("A substantive heading with enough source-backed text"),
            TextOccurrence("A normal body paragraph with enough source-backed text")));

        Assert.Equal(A99SourceRepresentabilityStatus.CharacterSpanRepresentable, result.Status);
        Assert.True(result.PacketTextCorrespondence);
    }

    [Fact]
    public void Mixed_text_and_image_packet_remains_representable()
    {
        var result = A99SourceRepresentabilityGate.Evaluate(Packet(
            TextOccurrence("A substantive heading with enough source-backed text"),
            EmptyOccurrence("decorative-image"),
            TextOccurrence("A normal body paragraph with enough source-backed text")));

        Assert.Equal(A99SourceRepresentabilityStatus.CharacterSpanRepresentable, result.Status);
    }

    [Fact]
    public void Empty_decorative_image_page_fails_closed_as_image_only()
    {
        var result = A99SourceRepresentabilityGate.Evaluate(Packet(EmptyOccurrence("decorative-image")));

        Assert.Equal(A99SourceRepresentabilityStatus.ImageOnlyNotCharacterSpanRepresentable, result.Status);
    }

    [Fact]
    public void Metadata_only_text_population_is_not_promoted_to_strict_gold()
    {
        var occurrences = Enumerable.Range(0, 170)
            .Select(index => index == 0 ? TextOccurrence(new string('m', 148)) : EmptyOccurrence($"image-{index}"))
            .ToArray();

        var result = A99SourceRepresentabilityGate.Evaluate(Packet(occurrences));

        Assert.Equal(A99SourceRepresentabilityStatus.ImageOnlyNotCharacterSpanRepresentable, result.Status);
        Assert.False(A99StrictGoldAuthorityRules.IsEligible(
            "VALID",
            A99StrictGoldAuthorityRules.HumanReviewedModelAssisted,
            eligibleForStrictA99Claim: false));
    }

    [Fact]
    public void Invalid_source_span_correspondence_is_indeterminate()
    {
        var result = A99SourceRepresentabilityGate.Evaluate(Packet(TextOccurrence("heading") with
        {
            SourceSpan = new A99ReviewSpan(0, 1),
        }));

        Assert.Equal(A99SourceRepresentabilityStatus.RepresentationIndeterminate, result.Status);
        Assert.False(result.PacketTextCorrespondence);
    }

    private static A99ReviewPacket Packet(params A99ReviewOccurrence[] occurrences) => new()
    {
        DocumentId = "TEST",
        DocumentGroupId = "GROUP-TEST",
        Split = "DEV",
        FamilyId = "TEST",
        FileName = "test.docx",
        SourceKind = "docx",
        SourceDocumentSha256 = "source",
        Occurrences = occurrences,
    };

    private static A99ReviewOccurrence TextOccurrence(string text) => new()
    {
        SourceId = $"p-{Guid.NewGuid():N}",
        StableId = "stable",
        SourceSpan = new A99ReviewSpan(0, text.Length),
        SourceTextHash = A99ReviewPacketBuilder.TextSha256(text),
        SourceText = text,
        Style = new A99ReviewStyleFacts(),
        Numbering = new A99ReviewNumberingFacts(),
        Layout = new A99ReviewLayoutFacts(),
    };

    private static A99ReviewOccurrence EmptyOccurrence(string suffix) => TextOccurrence(string.Empty) with
    {
        SourceId = suffix,
    };
}
