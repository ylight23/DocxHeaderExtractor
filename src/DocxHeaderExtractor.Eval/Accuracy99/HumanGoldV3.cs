using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// Corrected positive-only human Gold contract. A row identifies a logical heading occurrence,
/// not a physical paragraph, so several rows may legitimately share SourceId.
/// </summary>
public sealed record A99GoldV3Heading
{
    [JsonPropertyName("headingOccurrenceId")] public required string HeadingOccurrenceId { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("stableId")] public required string StableId { get; init; }
    [JsonPropertyName("sourceOrdinal")] public int SourceOrdinal { get; init; }
    [JsonPropertyName("sourceSpan")] public required A99ReviewSpan SourceSpan { get; init; }
    [JsonPropertyName("sourceTextHash")] public required string SourceTextHash { get; init; }
    [JsonPropertyName("headingSpan")] public required A99ReviewSpan HeadingSpan { get; init; }
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("level")] public required int Level { get; init; }
    [JsonPropertyName("parentHeadingOccurrenceId")] public required string ParentHeadingOccurrenceId { get; init; }
}
public sealed record A99UnsureSpan
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("span")] public required A99ReviewSpan Span { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed record A99HumanGoldV3Document
{
    [JsonPropertyName("artifactKind")] public string ArtifactKind { get; init; } = "a99_human_gold";
    [JsonPropertyName("authorityClass")] public string AuthorityClass { get; init; } = "HUMAN_GOLD";
    [JsonPropertyName("goldSchemaVersion")] public string GoldSchemaVersion { get; init; } = "a99-human-gold-v3";
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
    [JsonPropertyName("goldVersion")] public string GoldVersion { get; init; } = "a99-human-gold-v3";
    [JsonPropertyName("unsureSpans")] public IReadOnlyList<A99UnsureSpan> UnsureSpans { get; init; } = [];
    [JsonPropertyName("rows")] public required IReadOnlyList<A99GoldV3Heading> Rows { get; init; }
}

public static class A99HumanGoldV3Validator
{
    private const string Root = "ROOT";

    public static A99GoldValidationResult Validate(A99ReviewPacket packet, A99HumanGoldV3Document gold)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(gold);
        var errors = new List<string>();
        Require(gold.ArtifactKind, "a99_human_gold", "artifact-kind-not-human-gold", errors);
        Require(gold.AuthorityClass, "HUMAN_GOLD", "authority-class-not-human-gold", errors);
        Require(gold.GoldSchemaVersion, "a99-human-gold-v3", "gold-schema-version-mismatch", errors);
        Require(gold.GoldVersion, "a99-human-gold-v3", "gold-version-mismatch", errors);
        RequireText(gold.ReviewerAlias, "reviewer-alias-missing", errors);
        RequireText(gold.ReviewVersion, "review-version-missing", errors);
        RequireText(gold.DocumentId, "document-id-missing", errors);
        RequireText(gold.DocumentGroupId, "document-group-id-missing", errors);
        RequireText(gold.Split, "split-missing", errors);
        RequireText(gold.SourceDocumentSha256, "source-sha-missing", errors);
        RequireText(gold.PacketSha256, "packet-sha-missing", errors);
        if (gold.ReviewedAt == default) errors.Add("reviewed-at-missing");
        if (!gold.ReviewedEntireDocument) errors.Add("reviewed-entire-document-not-declared");
        if (!gold.IndependentOfModelPrediction) errors.Add("reviewer-independence-not-declared");
        if (gold.ArtifactKind.Contains("silver", StringComparison.OrdinalIgnoreCase) ||
            gold.AuthorityClass.Contains("silver", StringComparison.OrdinalIgnoreCase))
            errors.Add("silver-artifact-rejected");

        if (!string.Equals(gold.DocumentId, packet.DocumentId, StringComparison.Ordinal)) errors.Add("document-id-mismatch");
        if (!string.Equals(gold.DocumentGroupId, packet.DocumentGroupId, StringComparison.Ordinal)) errors.Add("document-group-id-mismatch");
        if (!string.Equals(gold.Split, packet.Split, StringComparison.OrdinalIgnoreCase)) errors.Add("split-mismatch");
        if (!string.Equals(gold.SourceDocumentSha256, packet.SourceDocumentSha256, StringComparison.OrdinalIgnoreCase)) errors.Add("source-sha-mismatch");
        if (!string.Equals(gold.PacketSha256, packet.PacketSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.PacketSha256, A99ReviewPacketBuilder.ComputeSha256(packet), StringComparison.OrdinalIgnoreCase))
            errors.Add("packet-sha-mismatch");

        var packetById = packet.Occurrences.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var rowsByOccurrenceId = new Dictionary<string, A99GoldV3Heading>(StringComparer.Ordinal);
        foreach (var row in gold.Rows)
        {
            if (!packetById.TryGetValue(row.SourceId, out var occurrence))
            {
                errors.Add($"gold-source-not-found:{row.SourceId}");
                continue;
            }

            var expectedId = A99HeadingOccurrenceIdentity.Create(row.SourceId, row.HeadingSpan);
            if (!string.Equals(row.HeadingOccurrenceId, expectedId, StringComparison.Ordinal))
                errors.Add($"heading-occurrence-id-mismatch:{row.SourceId}");
            if (!rowsByOccurrenceId.TryAdd(row.HeadingOccurrenceId, row))
                errors.Add($"duplicate-heading-occurrence:{row.HeadingOccurrenceId}");
            if (!string.Equals(row.StableId, occurrence.StableId, StringComparison.Ordinal)) errors.Add($"stable-id-mismatch:{row.HeadingOccurrenceId}");
            if (row.SourceOrdinal != occurrence.SourceOrdinal) errors.Add($"source-ordinal-mismatch:{row.HeadingOccurrenceId}");
            if (row.SourceSpan != occurrence.SourceSpan) errors.Add($"source-span-mismatch:{row.HeadingOccurrenceId}");
            if (!string.Equals(row.SourceTextHash, occurrence.SourceTextHash, StringComparison.OrdinalIgnoreCase)) errors.Add($"source-text-hash-mismatch:{row.HeadingOccurrenceId}");
            if (!row.SourceSpan.IsValidFor(occurrence.SourceText)) errors.Add($"source-span-invalid:{row.HeadingOccurrenceId}");
            if (!row.HeadingSpan.IsValidFor(occurrence.SourceText)) errors.Add($"heading-span-invalid:{row.HeadingOccurrenceId}");
            else if (row.HeadingSpan.Start < row.SourceSpan.Start || row.HeadingSpan.End > row.SourceSpan.End)
                errors.Add($"heading-outside-source-envelope:{row.HeadingOccurrenceId}");
            if (string.IsNullOrWhiteSpace(row.Role) || !A99ReviewRoles.HeadingRoles.Contains(row.Role)) errors.Add($"heading-role-invalid:{row.HeadingOccurrenceId}");
            if (row.Level is < 1 or > 9) errors.Add($"heading-level-invalid:{row.HeadingOccurrenceId}");
            if (string.IsNullOrWhiteSpace(row.ParentHeadingOccurrenceId)) errors.Add($"heading-parent-missing:{row.HeadingOccurrenceId}");
        }

        foreach (var unsure in gold.UnsureSpans)
        {
            if (!packetById.TryGetValue(unsure.SourceId, out var occurrence))
            {
                errors.Add($"unsure-source-not-found:{unsure.SourceId}");
                continue;
            }
            if (!unsure.Span.IsValidFor(occurrence.SourceText) ||
                unsure.Span.Start < occurrence.SourceSpan.Start || unsure.Span.End > occurrence.SourceSpan.End)
                errors.Add($"unsure-span-invalid:{unsure.SourceId}");
        }
        if (gold.HeadingSetExhaustive && gold.UnsureSpans.Count > 0)
            errors.Add("heading-set-exhaustive-with-unsure-span");

        foreach (var row in gold.Rows)
        {
            var parent = row.ParentHeadingOccurrenceId;
            if (string.Equals(parent, row.HeadingOccurrenceId, StringComparison.Ordinal))
                errors.Add($"parent-self:{row.HeadingOccurrenceId}");
            if (!string.Equals(parent, Root, StringComparison.OrdinalIgnoreCase) &&
                !rowsByOccurrenceId.ContainsKey(parent))
                errors.Add($"parent-not-found:{row.HeadingOccurrenceId}");
        }

        foreach (var row in gold.Rows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { row.HeadingOccurrenceId };
            var cursor = row.ParentHeadingOccurrenceId;
            while (!string.Equals(cursor, Root, StringComparison.OrdinalIgnoreCase) &&
                   cursor is not null && rowsByOccurrenceId.TryGetValue(cursor, out var parent))
            {
                if (!seen.Add(cursor))
                {
                    errors.Add($"hierarchy-cycle:{row.HeadingOccurrenceId}");
                    break;
                }
                cursor = parent.ParentHeadingOccurrenceId;
            }
        }

        return new A99GoldValidationResult(errors.Count == 0, errors);
    }

    public static void EnsureValid(A99ReviewPacket packet, A99HumanGoldV3Document gold) =>
        Validate(packet, gold).ThrowIfInvalid();

    private static void Require(string actual, string expected, string error, ICollection<string> errors)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) errors.Add(error);
    }

    private static void RequireText(string? value, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(error);
    }
}
