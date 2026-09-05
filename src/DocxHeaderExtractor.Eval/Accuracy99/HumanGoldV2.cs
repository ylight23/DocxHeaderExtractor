namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// Validates the positive-set gold contract. An exhaustive packet is the review surface; it is
/// not required to be copied into the gold file as thousands of explicit negative rows.
/// </summary>
public static class A99HumanGoldV2Validator
{
    private static readonly HashSet<string> RootParents = new(StringComparer.OrdinalIgnoreCase) { "ROOT", "UNKNOWN" };

    public static A99GoldValidationResult Validate(
        A99ReviewPacket packet,
        A99HumanGoldV2Document gold)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(gold);
        var errors = new List<string>();

        Require(gold.ArtifactKind, "a99_human_gold", "artifact-kind-not-human-gold", errors);
        Require(gold.AuthorityClass, "HUMAN_GOLD", "authority-class-not-human-gold", errors);
        Require(gold.GoldSchemaVersion, "a99-human-gold-v2", "gold-schema-version-not-v2", errors);
        Require(gold.GoldVersion, "a99-human-gold-v2", "gold-version-not-v2", errors);
        RequireText(gold.ReviewerAlias, "reviewer-alias-missing", errors);
        RequireText(gold.ReviewVersion, "review-version-missing", errors);
        RequireText(gold.DocumentId, "document-id-missing", errors);
        RequireText(gold.DocumentGroupId, "document-group-id-missing", errors);
        RequireText(gold.Split, "split-missing", errors);
        RequireText(gold.SourceDocumentSha256, "source-sha-missing", errors);
        RequireText(gold.PacketSha256, "packet-sha-missing", errors);
        if (gold.ReviewedAt == default) errors.Add("reviewed-at-missing");
        if (!gold.ReviewedEntireDocument) errors.Add("reviewed-entire-document-not-certified");
        if (!gold.HeadingSetExhaustive) errors.Add("heading-set-not-certified-exhaustive");
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
        var headingById = new Dictionary<string, A99GoldV2Heading>(StringComparer.Ordinal);
        foreach (var heading in gold.Rows)
        {
            if (!headingById.TryAdd(heading.SourceId, heading))
            {
                errors.Add($"duplicate-gold-heading-identity:{heading.SourceId}");
                continue;
            }

            if (!packetById.TryGetValue(heading.SourceId, out var occurrence))
            {
                errors.Add($"gold-source-not-found:{heading.SourceId}");
                continue;
            }

            if (!string.Equals(heading.StableId, occurrence.StableId, StringComparison.Ordinal)) errors.Add($"stable-id-mismatch:{heading.SourceId}");
            if (heading.SourceOrdinal != occurrence.SourceOrdinal) errors.Add($"source-ordinal-mismatch:{heading.SourceId}");
            if (heading.SourceSpan != occurrence.SourceSpan) errors.Add($"source-span-mismatch:{heading.SourceId}");
            if (!string.Equals(heading.SourceTextHash, occurrence.SourceTextHash, StringComparison.OrdinalIgnoreCase)) errors.Add($"source-text-hash-mismatch:{heading.SourceId}");
            ValidateHeading(heading, occurrence, errors);
        }

        var unsureIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceId in gold.UnsureSourceIds)
        {
            if (!unsureIds.Add(sourceId)) errors.Add($"duplicate-unsure-source-identity:{sourceId}");
            if (!packetById.ContainsKey(sourceId)) errors.Add($"unsure-source-not-found:{sourceId}");
            if (headingById.ContainsKey(sourceId)) errors.Add($"heading-and-unsure-overlap:{sourceId}");
        }
        if (unsureIds.Count > 0) errors.Add("unsure-prevents-exhaustive-certification");

        ValidateParents(gold.Rows, headingById, errors);
        return new A99GoldValidationResult(errors.Count == 0, errors);
    }

    public static void EnsureValid(A99ReviewPacket packet, A99HumanGoldV2Document gold) =>
        Validate(packet, gold).ThrowIfInvalid();

    private static void ValidateHeading(
        A99GoldV2Heading heading,
        A99ReviewOccurrence occurrence,
        ICollection<string> errors)
    {
        if (!heading.HeadingSpan.IsValidFor(occurrence.SourceText)) errors.Add($"heading-span-invalid:{heading.SourceId}");
        else if (heading.HeadingSpan.Start < heading.SourceSpan.Start || heading.HeadingSpan.End > heading.SourceSpan.End)
            errors.Add($"heading-outside-source-envelope:{heading.SourceId}");
        if (!heading.SourceSpan.IsValidFor(occurrence.SourceText)) errors.Add($"source-span-invalid:{heading.SourceId}");
        if (string.IsNullOrWhiteSpace(heading.Role) || !A99ReviewRoles.HeadingRoles.Contains(heading.Role)) errors.Add($"heading-role-invalid:{heading.SourceId}");
        if (heading.Level is < 1 or > 9) errors.Add($"heading-level-invalid:{heading.SourceId}");
        if (string.IsNullOrWhiteSpace(heading.ParentOccurrenceId)) errors.Add($"heading-parent-missing:{heading.SourceId}");
    }

    private static void ValidateParents(
        IReadOnlyList<A99GoldV2Heading> headings,
        IReadOnlyDictionary<string, A99GoldV2Heading> headingById,
        ICollection<string> errors)
    {
        foreach (var heading in headings)
        {
            var parentId = heading.ParentOccurrenceId;
            if (parentId is null || RootParents.Contains(parentId)) continue;
            if (!headingById.ContainsKey(parentId)) errors.Add($"parent-not-heading:{heading.SourceId}");
            if (string.Equals(parentId, heading.SourceId, StringComparison.Ordinal)) errors.Add($"parent-self:{heading.SourceId}");

            var seen = new HashSet<string>(StringComparer.Ordinal) { heading.SourceId };
            var cursor = parentId;
            while (cursor is not null && !RootParents.Contains(cursor))
            {
                if (!seen.Add(cursor)) { errors.Add($"hierarchy-cycle:{heading.SourceId}"); break; }
                if (!headingById.TryGetValue(cursor, out var parent)) break;
                cursor = parent.ParentOccurrenceId;
            }
        }
    }

    private static void Require(string? actual, string expected, string error, ICollection<string> errors)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) errors.Add(error);
    }

    private static void RequireText(string? value, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(error);
    }
}
