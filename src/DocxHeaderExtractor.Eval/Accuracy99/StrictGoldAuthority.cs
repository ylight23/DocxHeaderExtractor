using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class A99StrictGoldAuthorityRules
{
    public const string StrictHumanGold = "STRICT_HUMAN_GOLD";
    public const string HumanReviewedModelAssisted = "HUMAN_REVIEWED_MODEL_ASSISTED";
    public const string NotReviewed = "NOT_REVIEWED";

    public static bool IsEligible(
        string? validatorStatus,
        string? referenceAuthority,
        bool eligibleForStrictA99Claim) =>
        eligibleForStrictA99Claim &&
        string.Equals(validatorStatus, "VALID", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(referenceAuthority, StrictHumanGold, StringComparison.Ordinal);
}

public sealed record A99StrictGoldAuthorityEntry
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("validatorStatus")] public required string ValidatorStatus { get; init; }
    [JsonPropertyName("referenceAuthority")] public required string ReferenceAuthority { get; init; }
    [JsonPropertyName("eligibleForStrictA99Claim")] public bool EligibleForStrictA99Claim { get; init; }
    [JsonPropertyName("exposureStatus")] public required string ExposureStatus { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}

public sealed record A99StrictGoldAuthorityArtifact
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_strict_gold_authority";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-strict-gold-authority-v1";
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("strictCohortDocumentCount")] public int StrictCohortDocumentCount { get; init; }
    [JsonPropertyName("documents")] public required IReadOnlyList<A99StrictGoldAuthorityEntry> Documents { get; init; }
    [JsonPropertyName("assistedPilotDocumentIds")] public IReadOnlyList<string> AssistedPilotDocumentIds { get; init; } = [];
    [JsonPropertyName("holdoutStatus")] public string HoldoutStatus { get; init; } = "SEALED";
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

public sealed record A99StrictCohortDocument
{
    [JsonPropertyName("documentId")] public required string DocumentId { get; init; }
    [JsonPropertyName("documentGroupId")] public required string DocumentGroupId { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("familyId")] public required string FamilyId { get; init; }
    [JsonPropertyName("familyAssignmentAuthority")] public required string FamilyAssignmentAuthority { get; init; }
    [JsonPropertyName("sourcePath")] public required string SourcePath { get; init; }
    [JsonPropertyName("sourceSha256")] public required string SourceSha256 { get; init; }
    [JsonPropertyName("packetPath")] public string? PacketPath { get; init; }
    [JsonPropertyName("packetSha256")] public string? PacketSha256 { get; init; }
    [JsonPropertyName("selectionRole")] public required string SelectionRole { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("exposureStatus")] public string ExposureStatus { get; init; } = "NOT_MODEL_ASSISTED";
}

public sealed record A99StrictDevGoldCohortArtifact
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_strict_dev_gold_cohort";
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "a99-strict-dev-gold-cohort-v1";
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("strictCohortDocumentCount")] public int StrictCohortDocumentCount { get; init; }
    [JsonPropertyName("strictCohort")] public required IReadOnlyList<A99StrictCohortDocument> StrictCohort { get; init; }
    [JsonPropertyName("assistedPilotDocumentIds")] public IReadOnlyList<string> AssistedPilotDocumentIds { get; init; } = [];
    [JsonPropertyName("assistedPilotNote")] public required string AssistedPilotNote { get; init; }
    [JsonPropertyName("holdout")] public required string Holdout { get; init; }
    [JsonPropertyName("selectionPolicy")] public required string SelectionPolicy { get; init; }
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}
