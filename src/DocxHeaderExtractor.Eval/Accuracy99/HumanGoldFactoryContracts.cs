using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public enum A99ReviewLabel
{
    [JsonStringEnumMemberName("YES")] Yes,
    [JsonStringEnumMemberName("NO")] No,
    [JsonStringEnumMemberName("UNSURE")] Unsure,
}

public static class A99ReviewRoles
{
    public static readonly IReadOnlySet<string> HeadingRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "heading", "title", "caption", "label", "other" };

    public static readonly IReadOnlySet<string> NonHeadingRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "body", "non-heading", "other" };
}

public sealed record A99ReviewSpan(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End)
{
    public bool IsValidFor(string text) => Start >= 0 && End >= Start && End <= text.Length;
}

public sealed record A99ReviewStyleFacts
{
    [JsonPropertyName("styleId")] public string? StyleId { get; init; }
    [JsonPropertyName("styleName")] public string? StyleName { get; init; }
    [JsonPropertyName("builtInHeadingStyleLevel")] public int? BuiltInHeadingStyleLevel { get; init; }
    [JsonPropertyName("outlineLevel")] public int? OutlineLevel { get; init; }
    [JsonPropertyName("bold")] public bool Bold { get; init; }
    [JsonPropertyName("italic")] public bool Italic { get; init; }
    [JsonPropertyName("underline")] public bool Underline { get; init; }
    [JsonPropertyName("allCaps")] public bool AllCaps { get; init; }
    [JsonPropertyName("fontSizePt")] public double? FontSizePt { get; init; }
    [JsonPropertyName("alignment")] public string? Alignment { get; init; }
}

public sealed record A99ReviewNumberingFacts
{
    [JsonPropertyName("numberingId")] public int? NumberingId { get; init; }
    [JsonPropertyName("ilvl")] public int? LevelReference { get; init; }
    [JsonPropertyName("numberLabel")] public string? NumberLabel { get; init; }
    [JsonPropertyName("numberingFormat")] public string? NumberingFormat { get; init; }
    [JsonPropertyName("styleLinkedLevel")] public int? StyleLinkedLevel { get; init; }
}

public sealed record A99ReviewLayoutFacts
{
    [JsonPropertyName("inContentControl")] public bool InContentControl { get; init; }
    [JsonPropertyName("keepNext")] public bool KeepNext { get; init; }
    [JsonPropertyName("pageBreakBefore")] public bool PageBreakBefore { get; init; }
    [JsonPropertyName("tableDepth")] public int TableDepth { get; init; }
    [JsonPropertyName("sectionIndex")] public int SectionIndex { get; init; }
    [JsonPropertyName("inTableOfContents")] public bool InTableOfContents { get; init; }
}

/// <summary>Source-first review material. It intentionally has no prediction or decision fields.</summary>
public sealed record A99ReviewOccurrence
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("stableId")] public required string StableId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public required A99ReviewSpan SourceSpan { get; init; }
    [JsonPropertyName("sourceTextHash")] public required string SourceTextHash { get; init; }
    [JsonPropertyName("sourceText")] public required string SourceText { get; init; }
    [JsonPropertyName("previousSourceId")] public string? PreviousSourceId { get; init; }
    [JsonPropertyName("previousText")] public string? PreviousText { get; init; }
    [JsonPropertyName("nextSourceId")] public string? NextSourceId { get; init; }
    [JsonPropertyName("nextText")] public string? NextText { get; init; }
    [JsonPropertyName("style")] public required A99ReviewStyleFacts Style { get; init; }
    [JsonPropertyName("numbering")] public required A99ReviewNumberingFacts Numbering { get; init; }
    [JsonPropertyName("layout")] public required A99ReviewLayoutFacts Layout { get; init; }
}

public sealed record A99ReviewPacket
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_exhaustive_source_first_review_packet";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-review-packet-v1";
    [JsonPropertyName("packetSha256")] public string? PacketSha256 { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("familyId")] public required string FamilyId { get; init; }
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("sourceKind")] public required string SourceKind { get; init; }
    [JsonPropertyName("sourceDocumentSha256")] public required string SourceDocumentSha256 { get; init; }
    [JsonPropertyName("occurrences")] public required IReadOnlyList<A99ReviewOccurrence> Occurrences { get; init; }
}

public sealed record A99GoldOccurrence
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("stableId")] public required string StableId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public required A99ReviewSpan SourceSpan { get; init; }
    [JsonPropertyName("sourceTextHash")] public required string SourceTextHash { get; init; }
    [JsonPropertyName("isHeading")] public required A99ReviewLabel IsHeading { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("headingSpan")] public A99ReviewSpan? HeadingSpan { get; init; }
    [JsonPropertyName("level")] public int? Level { get; init; }
    [JsonPropertyName("parentOccurrenceId")] public string? ParentOccurrenceId { get; init; }
}

public sealed record A99HumanGoldDocument
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_human_gold";
    [JsonPropertyName("authorityClass")] public string AuthorityClass { get; init; } = "HUMAN_GOLD";
    [JsonPropertyName("goldSchemaVersion")] public string GoldSchemaVersion { get; init; } = "a99-human-gold-v1";
    [JsonPropertyName("reviewerAlias")] public required string ReviewerAlias { get; init; }
    [JsonPropertyName("reviewedAt")] public required DateTimeOffset ReviewedAt { get; init; }
    [JsonPropertyName("reviewVersion")] public required string ReviewVersion { get; init; }
    [JsonPropertyName("independentOfModelPrediction")] public bool IndependentOfModelPrediction { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("sourceDocumentSha256")] public required string SourceDocumentSha256 { get; init; }
    [JsonPropertyName("packetSha256")] public required string PacketSha256 { get; init; }
    [JsonPropertyName("rows")] public required IReadOnlyList<A99GoldOccurrence> Rows { get; init; }
}

/// <summary>
/// Version 2 keeps the review packet exhaustive but stores only the positive heading set.
/// Completeness is a signed human assertion, not an implicit label for every body paragraph.
/// </summary>
public sealed record A99GoldV2Heading
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("stableId")] public required string StableId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public required A99ReviewSpan SourceSpan { get; init; }
    [JsonPropertyName("sourceTextHash")] public required string SourceTextHash { get; init; }
    [JsonPropertyName("headingSpan")] public required A99ReviewSpan HeadingSpan { get; init; }
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("level")] public required int Level { get; init; }
    [JsonPropertyName("parentOccurrenceId")] public string? ParentOccurrenceId { get; init; }
}

public sealed record A99HumanGoldV2Document
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_human_gold";
    [JsonPropertyName("authorityClass")] public string AuthorityClass { get; init; } = "HUMAN_GOLD";
    [JsonPropertyName("goldSchemaVersion")] public string GoldSchemaVersion { get; init; } = "a99-human-gold-v2";
    [JsonPropertyName("reviewerAlias")] public required string ReviewerAlias { get; init; }
    [JsonPropertyName("reviewedAt")] public required DateTimeOffset ReviewedAt { get; init; }
    [JsonPropertyName("reviewVersion")] public required string ReviewVersion { get; init; }
    [JsonPropertyName("reviewedEntireDocument")] public bool ReviewedEntireDocument { get; init; }
    [JsonPropertyName("headingSetExhaustive")] public bool HeadingSetExhaustive { get; init; }
    [JsonPropertyName("independentOfModelPrediction")] public bool IndependentOfModelPrediction { get; init; }
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("sourceDocumentSha256")] public required string SourceDocumentSha256 { get; init; }
    [JsonPropertyName("packetSha256")] public required string PacketSha256 { get; init; }
    [JsonPropertyName("goldVersion")] public string GoldVersion { get; init; } = "a99-human-gold-v2";
    [JsonPropertyName("derivedFromHumanKey")] public bool DerivedFromHumanKey { get; init; }
    [JsonPropertyName("unsureSourceIds")] public IReadOnlyList<string> UnsureSourceIds { get; init; } = [];
    [JsonPropertyName("rows")] public required IReadOnlyList<A99GoldV2Heading> Rows { get; init; }
}

public sealed record A99EarlyDevDocument
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("familyId")] public required string FamilyId { get; init; }
    [JsonPropertyName("sourceKind")] public required string SourceKind { get; init; }
    [JsonPropertyName("sizeBand")] public required string SizeBand { get; init; }
    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("sourceOccurrenceCount")] public int SourceOccurrenceCount { get; init; }
    [JsonPropertyName("packetPath")] public required string PacketPath { get; init; }
    [JsonPropertyName("packetSha256")] public required string PacketSha256 { get; init; }
}

public sealed record A99EarlyDevCampaign
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_early_dev_campaign";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-early-dev-campaign-v1";
    [JsonPropertyName("createdFromRevision")] public required string CreatedFromRevision { get; init; }
    [JsonPropertyName("selectionPolicy")] public required string SelectionPolicy { get; init; }
    [JsonPropertyName("targetDocuments")] public int TargetDocuments { get; init; }
    [JsonPropertyName("documents")] public required IReadOnlyList<A99EarlyDevDocument> Documents { get; init; }
    [JsonPropertyName("sourceOccurrences")] public int SourceOccurrences { get; init; }
    [JsonPropertyName("familiesCovered")] public required IReadOnlyList<string> FamiliesCovered { get; init; }
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

public sealed record A99EarlyGoldImportCoverage
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_early_dev_gold_coverage";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-early-dev-gold-coverage-v2";
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("documentsExpected")] public int DocumentsExpected { get; init; }
    [JsonPropertyName("documentsValidated")] public int DocumentsValidated { get; init; }
    [JsonPropertyName("sourceOccurrencesExpected")] public int SourceOccurrencesExpected { get; init; }
    [JsonPropertyName("sourceOccurrencesValidated")] public int SourceOccurrencesValidated { get; init; }
    [JsonPropertyName("headingPositives")] public int HeadingPositives { get; init; }
    [JsonPropertyName("unsureDocuments")] public int UnsureDocuments { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string> Errors { get; init; } = [];
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

public sealed record A99GoldValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new InvalidDataException(string.Join("; ", Errors));
    }
}

public sealed record A99CampaignDocument
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("familyId")] public required string FamilyId { get; init; }
    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("sourceOccurrenceCount")] public int SourceOccurrenceCount { get; init; }
    [JsonPropertyName("packetPath")] public required string PacketPath { get; init; }
    [JsonPropertyName("packetSha256")] public required string PacketSha256 { get; init; }
    [JsonPropertyName("packetStatus")] public string PacketStatus { get; init; } = "READY_FOR_HUMAN_REVIEW";
}

public sealed record A99FamilyCampaignSummary
{
    [JsonPropertyName("familyId")] public required string FamilyId { get; init; }
    [JsonPropertyName("documents")] public int Documents { get; init; }
    [JsonPropertyName("groups")] public int Groups { get; init; }
    [JsonPropertyName("sourceOccurrences")] public int SourceOccurrences { get; init; }
}

public sealed record A99ExistingPacketAudit
{
    [JsonPropertyName("packetPath")] public required string PacketPath { get; init; }
    [JsonPropertyName("documentId")] public string? DocumentId { get; init; }
    [JsonPropertyName("classification")] public required string Classification { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

public sealed record A99ReferenceCampaign
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_reference_campaign";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-reference-campaign-v1";
    [JsonPropertyName("createdFromRevision")] public required string CreatedFromRevision { get; init; }
    [JsonPropertyName("sourceCorpus")] public required string SourceCorpus { get; init; }
    [JsonPropertyName("selectionPolicy")] public required string SelectionPolicy { get; init; }
    [JsonPropertyName("devDocuments")] public required IReadOnlyList<A99CampaignDocument> DevDocuments { get; init; }
    [JsonPropertyName("holdoutDocuments")] public required IReadOnlyList<A99CampaignDocument> HoldoutDocuments { get; init; }
    [JsonPropertyName("familySummary")] public required IReadOnlyList<A99FamilyCampaignSummary> FamilySummary { get; init; }
    [JsonPropertyName("existingPacketAudit")] public required IReadOnlyList<A99ExistingPacketAudit> ExistingPacketAudit { get; init; }
    [JsonPropertyName("reservedDocumentsExcluded")] public int ReservedDocumentsExcluded { get; init; }
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

public sealed record A99ReviewManifestEntry
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("familyId")] public required string FamilyId { get; init; }
    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("packetPath")] public required string PacketPath { get; init; }
    [JsonPropertyName("packetSha256")] public required string PacketSha256 { get; init; }
    [JsonPropertyName("sourceOccurrenceCount")] public int SourceOccurrenceCount { get; init; }
}

public sealed record A99ReviewManifest
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_review_manifest";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-review-manifest-v1";
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("packetRoot")] public required string PacketRoot { get; init; }
    [JsonPropertyName("packetCount")] public int PacketCount { get; init; }
    [JsonPropertyName("sourceOccurrences")] public int SourceOccurrences { get; init; }
    [JsonPropertyName("entries")] public required IReadOnlyList<A99ReviewManifestEntry> Entries { get; init; }
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

public sealed record A99GoldImportCoverage
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_dev_gold_coverage";
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("devDocumentsExpected")] public int DevDocumentsExpected { get; init; }
    [JsonPropertyName("devDocumentsValidated")] public int DevDocumentsValidated { get; init; }
    [JsonPropertyName("sourceOccurrencesExpected")] public int SourceOccurrencesExpected { get; init; }
    [JsonPropertyName("sourceOccurrencesValidated")] public int SourceOccurrencesValidated { get; init; }
    [JsonPropertyName("headingYes")] public int HeadingYes { get; init; }
    [JsonPropertyName("headingNo")] public int HeadingNo { get; init; }
    [JsonPropertyName("headingUnsure")] public int HeadingUnsure { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string> Errors { get; init; } = [];
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}
