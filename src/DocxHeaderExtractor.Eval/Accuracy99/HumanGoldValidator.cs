using System.Security.Cryptography;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class HumanGoldValidator
{
    public static HumanGoldValidationResult Validate(
        HumanGoldArtifact artifact,
        SourceDocument source,
        string? sourceSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(source);

        var errors = new List<string>();
        Require(artifact.ArtifactKind, "human_gold", "artifact-kind-not-human-gold", errors);
        Require(artifact.AuthorityClass, "HUMAN_GOLD", "authority-class-not-human-gold", errors);
        RequireText(artifact.ReviewerId, "reviewer-id-missing", errors);
        RequireText(artifact.AdjudicationVersion, "adjudication-version-missing", errors);
        RequireText(artifact.SourceDocumentSha256, "source-sha-missing", errors);
        RequireText(artifact.DocumentId, "document-id-missing", errors);
        RequireText(artifact.MediaType, "media-type-missing", errors);
        if (artifact.CreatedUtc == default)
            errors.Add("created-utc-missing");

        if (artifact.ArtifactKind.Contains("silver", StringComparison.OrdinalIgnoreCase) ||
            artifact.AuthorityClass.Contains("silver", StringComparison.OrdinalIgnoreCase))
            errors.Add("silver-artifact-rejected");

        if (!string.Equals(artifact.DocumentId, source.DocumentId, StringComparison.Ordinal))
            errors.Add("document-id-mismatch");

        if (sourceSha256 is null && File.Exists(source.SourcePath))
            sourceSha256 = ComputeSha256(source.SourcePath);
        if (!string.IsNullOrWhiteSpace(sourceSha256) &&
            !string.Equals(artifact.SourceDocumentSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("source-sha-mismatch");

        var sourceById = source.Paragraphs
            .GroupBy(p => p.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var duplicateSourceIds = source.Paragraphs.GroupBy(p => p.SourceId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key);
        foreach (var id in duplicateSourceIds) errors.Add($"duplicate-source-id:{id}");

        var occurrencesById = new Dictionary<string, HumanGoldSourceOccurrence>(StringComparer.Ordinal);
        foreach (var occurrence in artifact.SourceOccurrences)
        {
            if (!occurrencesById.TryAdd(occurrence.SourceId, occurrence))
                errors.Add($"duplicate-gold-source-identity:{occurrence.SourceId}");
            if (!sourceById.TryGetValue(occurrence.SourceId, out var paragraph))
            {
                errors.Add($"gold-source-not-found:{occurrence.SourceId}");
                continue;
            }
            if (occurrence.SourceOrdinal != paragraph.SourceOrdinal)
                errors.Add($"gold-source-ordinal-mismatch:{occurrence.SourceId}");
            if (!occurrence.Span.IsValidFor(paragraph.Text))
                errors.Add($"gold-source-span-invalid:{occurrence.SourceId}");
        }

        if (artifact.ExhaustiveSourceLabels &&
            sourceById.Keys.Except(occurrencesById.Keys, StringComparer.Ordinal).Any())
            errors.Add("exhaustive-source-labels-incomplete");

        var headingIds = artifact.Headings.Select(heading => heading.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var headingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in artifact.Headings)
        {
            if (!sourceById.TryGetValue(heading.SourceId, out var paragraph))
            {
                errors.Add($"heading-source-not-found:{heading.SourceId}");
                continue;
            }
            if (!heading.HeadingSpan.IsValidFor(paragraph.Text))
                errors.Add($"heading-span-invalid:{heading.SourceId}");
            if (heading.Level is < 1 or > 9)
                errors.Add($"heading-level-invalid:{heading.SourceId}");
            if (!headingKeys.Add($"{heading.SourceId}\u001f{heading.HeadingSpan.Start}\u001f{heading.HeadingSpan.End}"))
                errors.Add($"duplicate-heading:{heading.SourceId}");
            if (!occurrencesById.TryGetValue(heading.SourceId, out var occurrence) ||
                occurrence.Label != GoldSourceLabel.Heading)
                errors.Add($"heading-source-not-labelled-heading:{heading.SourceId}");
            else if (heading.HeadingSpan.Start < occurrence.Span.Start ||
                     heading.HeadingSpan.End > occurrence.Span.End)
                errors.Add($"heading-outside-source-envelope:{heading.SourceId}");
            if (heading.ParentSourceId is not null && !headingIds.Contains(heading.ParentSourceId))
                errors.Add($"heading-parent-not-found:{heading.SourceId}");
            if (string.Equals(heading.ParentSourceId, heading.SourceId, StringComparison.Ordinal))
                errors.Add($"heading-parent-self:{heading.SourceId}");
        }

        var parentById = artifact.Headings
            .GroupBy(h => h.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ParentSourceId, StringComparer.Ordinal);
        foreach (var heading in artifact.Headings)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { heading.SourceId };
            var cursor = heading.ParentSourceId;
            while (cursor is not null && parentById.TryGetValue(cursor, out var next))
            {
                if (!seen.Add(cursor))
                {
                    errors.Add($"heading-hierarchy-cycle:{heading.SourceId}");
                    break;
                }
                cursor = next;
            }
        }

        return new HumanGoldValidationResult(errors.Count == 0, errors);
    }

    public static void EnsureValid(HumanGoldArtifact artifact, SourceDocument source, string? sourceSha256 = null) =>
        Validate(artifact, source, sourceSha256).ThrowIfInvalid();

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Require(string actual, string expected, string error, ICollection<string> errors)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) errors.Add(error);
    }

    private static void RequireText(string? value, string error, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(error);
    }
}
