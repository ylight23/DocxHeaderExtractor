using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Source-owned evidence supplied to a semantic-node resolver.</summary>
public sealed record HdsaSemanticNodeSourceOccurrence(
    [property: JsonPropertyName("occurrenceId")] string OccurrenceId,
    [property: JsonPropertyName("documentOrder")] int DocumentOrder,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("styleEvidence")] string? StyleEvidence = null,
    [property: JsonPropertyName("layoutEvidence")] string? LayoutEvidence = null,
    // Compatibility/poison field only. The resolver must not consume legacy hierarchy metadata.
    [property: JsonPropertyName("legacyHints")] IReadOnlyList<string>? LegacyHints = null);

public sealed record HdsaSemanticNodeResolutionInput(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("occurrences")] IReadOnlyList<HdsaSemanticNodeSourceOccurrence> Occurrences,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed = false);

public sealed record HdsaSemanticNodePrediction(
    [property: JsonPropertyName("predictedSemanticNodeId")] string PredictedSemanticNodeId,
    [property: JsonPropertyName("memberOccurrenceIds")] IReadOnlyList<string> MemberOccurrenceIds,
    [property: JsonPropertyName("canonicalText")] string CanonicalText,
    [property: JsonPropertyName("mergeEvidence")] string MergeEvidence,
    [property: JsonPropertyName("resolverVersion")] string ResolverVersion,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed);

public sealed record HdsaSemanticNodeResolutionResult(
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("preprocessingSnapshotHash")] string PreprocessingSnapshotHash,
    [property: JsonPropertyName("predictions")] IReadOnlyList<HdsaSemanticNodePrediction> Predictions,
    [property: JsonPropertyName("resolverVersion")] string ResolverVersion,
    [property: JsonPropertyName("goldUsed")] bool GoldUsed);

/// <summary>
/// Deterministic no-merge baseline for the resolver boundary. It intentionally does not infer
/// repeat/continuation identity; a future semantic resolver may replace it behind this contract.
/// Legacy role/level/parent hints are never read.
/// </summary>
public static class HdsaSemanticNodeResolver
{
    public const string Version = "identity-no-merge-v1";

    public static HdsaSemanticNodeResolutionResult Resolve(HdsaSemanticNodeResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceSha256)) throw new ArgumentException("Source hash is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PreprocessingSnapshotHash)) throw new ArgumentException("Preprocessing snapshot hash is required.", nameof(input));
        if (input.GoldUsed) throw new InvalidOperationException("GOLD_FIREWALL: semantic-node resolver input is marked as Gold-derived.");

        var occurrences = input.Occurrences
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId)) throw new InvalidDataException("EMPTY_OCCURRENCE_ID");
            if (!seen.Add(occurrence.OccurrenceId)) throw new InvalidDataException($"DUPLICATE_OCCURRENCE_ID:{occurrence.OccurrenceId}");
            if (occurrence.DocumentOrder < 0) throw new InvalidDataException($"INVALID_DOCUMENT_ORDER:{occurrence.OccurrenceId}");
            if (occurrence.Text is null) throw new InvalidDataException($"NULL_OCCURRENCE_TEXT:{occurrence.OccurrenceId}");
        }

        var predictions = occurrences.Select(occurrence =>
        {
            var nodeId = "SN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.OccurrenceId))).ToLowerInvariant()[..16];
            return new HdsaSemanticNodePrediction(
                nodeId,
                [occurrence.OccurrenceId],
                occurrence.Text,
                "IDENTITY_ONLY_NO_MERGE",
                Version,
                false);
        }).ToArray();

        return new(input.SourceSha256, input.PreprocessingSnapshotHash, predictions, Version, false);
    }
}

/// <summary>
/// Conservative evidence-based resolver. It only merges adjacent source occurrences when
/// normalized text and non-empty parser-owned style/layout evidence are exactly compatible.
/// Ambiguous or weakly evidenced occurrences remain separate.
/// </summary>
public static class HdsaSemanticNodeResolverV2
{
    public const string Version = "exact-safe-equivalence-v2";

    public static HdsaSemanticNodeResolutionResult Resolve(HdsaSemanticNodeResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceSha256)) throw new ArgumentException("Source hash is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.PreprocessingSnapshotHash)) throw new ArgumentException("Preprocessing snapshot hash is required.", nameof(input));
        if (input.GoldUsed) throw new InvalidOperationException("GOLD_FIREWALL: semantic-node resolver input is marked as Gold-derived.");
        ArgumentNullException.ThrowIfNull(input.Occurrences);

        var occurrences = input.Occurrences
            .OrderBy(item => item.DocumentOrder)
            .ThenBy(item => item.OccurrenceId, StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in occurrences)
        {
            if (string.IsNullOrWhiteSpace(occurrence.OccurrenceId)) throw new InvalidDataException("EMPTY_OCCURRENCE_ID");
            if (!seen.Add(occurrence.OccurrenceId)) throw new InvalidDataException($"DUPLICATE_OCCURRENCE_ID:{occurrence.OccurrenceId}");
            if (occurrence.DocumentOrder < 0) throw new InvalidDataException($"INVALID_DOCUMENT_ORDER:{occurrence.OccurrenceId}");
            if (occurrence.Text is null) throw new InvalidDataException($"NULL_OCCURRENCE_TEXT:{occurrence.OccurrenceId}");
        }

        var predictions = new List<HdsaSemanticNodePrediction>();
        var index = 0;
        while (index < occurrences.Length)
        {
            var first = occurrences[index];
            var members = new List<HdsaSemanticNodeSourceOccurrence> { first };
            var next = index + 1;
            while (next < occurrences.Length && IsSafeEquivalent(members[^1], occurrences[next]))
            {
                members.Add(occurrences[next]);
                next++;
            }

            var normalizedText = NormalizeText(first.Text);
            var nodeKey = string.Join('\u001f', first.OccurrenceId, normalizedText,
                first.StyleEvidence ?? string.Empty, first.LayoutEvidence ?? string.Empty);
            var nodeId = "SN-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nodeKey))).ToLowerInvariant()[..16];
            var evidence = members.Count == 1
                ? "IDENTITY_ONLY_NO_SAFE_MERGE"
                : "NORMALIZED_TEXT+ADJACENT+EXACT_STYLE+EXACT_LAYOUT";

            predictions.Add(new HdsaSemanticNodePrediction(
                nodeId,
                members.Select(item => item.OccurrenceId).ToArray(),
                first.Text,
                evidence,
                Version,
                false));
            index = next;
        }

        return new(input.SourceSha256, input.PreprocessingSnapshotHash, predictions, Version, false);
    }

    private static bool IsSafeEquivalent(
        HdsaSemanticNodeSourceOccurrence previous,
        HdsaSemanticNodeSourceOccurrence current)
    {
        return current.DocumentOrder == previous.DocumentOrder + 1
            && string.Equals(NormalizeText(previous.Text), NormalizeText(current.Text), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previous.StyleEvidence)
            && string.Equals(previous.StyleEvidence, current.StyleEvidence, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previous.LayoutEvidence)
            && string.Equals(previous.LayoutEvidence, current.LayoutEvidence, StringComparison.Ordinal);
    }

    private static string NormalizeText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }

        return builder.ToString();
    }
}
