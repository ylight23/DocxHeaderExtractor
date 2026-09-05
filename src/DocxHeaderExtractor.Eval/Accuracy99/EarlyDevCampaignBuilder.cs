namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// Selects a small, frozen DEV review set without looking at predictions, confidence, errors, or
/// historical labels. Per-family occurrence quantiles keep the early set useful across sizes.
/// </summary>
public static class A99EarlyDevCampaignBuilder
{
    public static A99EarlyDevCampaign Build(
        A99ReferenceCampaign campaign,
        string createdFromRevision,
        int targetDocuments = 15)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (string.IsNullOrWhiteSpace(createdFromRevision)) throw new ArgumentException("Revision is required.", nameof(createdFromRevision));
        if (targetDocuments is < 12 or > 20) throw new ArgumentOutOfRangeException(nameof(targetDocuments));

        var groups = campaign.DevDocuments
            .GroupBy(x => x.FamilyId, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(x => x.SourceOccurrenceCount)
                .ThenBy(x => x.DocumentId, StringComparer.Ordinal)
                .ToArray())
            .ToArray();
        if (groups.Length == 0) throw new InvalidDataException("Early DEV campaign requires DEV documents.");

        var selected = new List<A99CampaignDocument>();
        var perFamily = Math.Min(5, Math.Max(1, targetDocuments / groups.Length));
        foreach (var group in groups)
            selected.AddRange(Quantiles(group, perFamily));

        if (selected.Count < targetDocuments)
        {
            foreach (var item in campaign.DevDocuments
                         .OrderBy(x => x.FamilyId, StringComparer.Ordinal)
                         .ThenBy(x => x.SourceOccurrenceCount)
                         .ThenBy(x => x.DocumentId, StringComparer.Ordinal))
            {
                if (selected.Any(x => x.DocumentId == item.DocumentId)) continue;
                selected.Add(item);
                if (selected.Count == targetDocuments) break;
            }
        }
        selected = selected
            .DistinctBy(x => x.DocumentId, StringComparer.Ordinal)
            .OrderBy(x => x.FamilyId, StringComparer.Ordinal)
            .ThenBy(x => x.SourceOccurrenceCount)
            .ThenBy(x => x.DocumentId, StringComparer.Ordinal)
            .Take(targetDocuments)
            .ToList();

        var documents = selected.Select(item =>
        {
            var family = groups.First(group => group.Any(x => x.DocumentId == item.DocumentId));
            var rank = Array.IndexOf(family, item);
            var band = rank * 3 < family.Length ? "small" : rank * 3 < family.Length * 2 ? "medium" : "large";
            return new A99EarlyDevDocument
            {
                DocumentId = item.DocumentId,
                DocumentGroupId = item.DocumentGroupId,
                Split = item.Split,
                FamilyId = item.FamilyId,
                SourceKind = Path.GetExtension(item.SourcePath).TrimStart('.').ToUpperInvariant(),
                SizeBand = band,
                SourcePath = item.SourcePath,
                SourceSha256 = item.SourceSha256,
                SourceOccurrenceCount = item.SourceOccurrenceCount,
                PacketPath = item.PacketPath,
                PacketSha256 = item.PacketSha256,
            };
        }).ToArray();

        return new A99EarlyDevCampaign
        {
            CreatedFromRevision = createdFromRevision,
            SelectionPolicy = "STRATIFIED_DEV_ONLY_BY_FAMILY_SOURCE_KIND_AND_OCCURRENCE_QUANTILES; PREDICTIONS_CONFIDENCE_AND_LABELS_EXCLUDED",
            TargetDocuments = targetDocuments,
            Documents = documents,
            SourceOccurrences = documents.Sum(x => x.SourceOccurrenceCount),
            FamiliesCovered = documents.Select(x => x.FamilyId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ProviderCalls = 0,
        };
    }

    private static IEnumerable<A99CampaignDocument> Quantiles(
        IReadOnlyList<A99CampaignDocument> documents,
        int count)
    {
        if (documents.Count == 0) yield break;
        if (count == 1) { yield return documents[documents.Count / 2]; yield break; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var position = (int)Math.Round(index * (documents.Count - 1d) / (count - 1d), MidpointRounding.AwayFromZero);
            if (seen.Add(documents[position].DocumentId)) yield return documents[position];
        }
    }
}
