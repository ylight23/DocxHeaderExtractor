using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public enum A99SourceRepresentabilityStatus
{
    CharacterSpanRepresentable,
    ImageOnlyNotCharacterSpanRepresentable,
    RepresentationIndeterminate,
}

public sealed record A99SourceRepresentabilityResult
{
    [JsonPropertyName("status")] public required A99SourceRepresentabilityStatus Status { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("occurrenceCount")] public int OccurrenceCount { get; init; }
    [JsonPropertyName("nonEmptyOccurrenceCount")] public int NonEmptyOccurrenceCount { get; init; }
    [JsonPropertyName("nonEmptyTextCharacters")] public int NonEmptyTextCharacters { get; init; }
    [JsonPropertyName("nonEmptyOccurrenceRatio")] public double NonEmptyOccurrenceRatio { get; init; }
    [JsonPropertyName("packetTextCorrespondence")] public bool PacketTextCorrespondence { get; init; }
    [JsonPropertyName("providerCalls")] public int ProviderCalls { get; init; }
}

/// <summary>
/// Measurement-only guard for source-first review. It does not inspect gold, predictions, or model output.
/// </summary>
public static class A99SourceRepresentabilityGate
{
    private const int MinimumUsefulTextCharacters = 32;
    private const int MetadataOnlyCharacterLimit = 512;
    private const double MinimumTextPopulationRatio = 0.02;

    public static A99SourceRepresentabilityResult Evaluate(A99ReviewPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var occurrences = packet.Occurrences ?? [];
        var nonEmpty = occurrences.Where(x => !string.IsNullOrWhiteSpace(x.SourceText)).ToArray();
        var textCharacters = nonEmpty.Sum(x => x.SourceText.Length);
        var ratio = occurrences.Count == 0 ? 0d : (double)nonEmpty.Length / occurrences.Count;
        var correspondence = occurrences.All(IsCorrespondingSourceText);

        if (occurrences.Count == 0)
            return Result(A99SourceRepresentabilityStatus.RepresentationIndeterminate,
                "packet-has-no-source-occurrences", occurrences.Count, nonEmpty.Length, textCharacters, ratio, correspondence);

        if (!correspondence)
            return Result(A99SourceRepresentabilityStatus.RepresentationIndeterminate,
                "packet-source-text-correspondence-invalid", occurrences.Count, nonEmpty.Length, textCharacters, ratio, correspondence);

        if (nonEmpty.Length == 0)
            return Result(A99SourceRepresentabilityStatus.ImageOnlyNotCharacterSpanRepresentable,
                "packet-has-no-character-text", occurrences.Count, nonEmpty.Length, textCharacters, ratio, correspondence);

        if (nonEmpty.Length == 1 && textCharacters <= MetadataOnlyCharacterLimit)
            return Result(A99SourceRepresentabilityStatus.ImageOnlyNotCharacterSpanRepresentable,
                "packet-has-only-metadata-scale-text-population", occurrences.Count, nonEmpty.Length, textCharacters, ratio, correspondence);

        if (nonEmpty.Length < 2 || textCharacters < MinimumUsefulTextCharacters || ratio < MinimumTextPopulationRatio)
            return Result(A99SourceRepresentabilityStatus.RepresentationIndeterminate,
                "packet-text-population-too-small-for-safe-span-review", occurrences.Count, nonEmpty.Length, textCharacters, ratio, correspondence);

        return Result(A99SourceRepresentabilityStatus.CharacterSpanRepresentable,
            "packet-has-non-trivial-source-backed-character-text", occurrences.Count, nonEmpty.Length, textCharacters, ratio, correspondence);
    }

    private static bool IsCorrespondingSourceText(A99ReviewOccurrence occurrence) =>
        occurrence.SourceSpan.IsValidFor(occurrence.SourceText) &&
        occurrence.SourceSpan.Start == 0 &&
        occurrence.SourceSpan.End == occurrence.SourceText.Length &&
        string.Equals(occurrence.SourceTextHash, A99ReviewPacketBuilder.TextSha256(occurrence.SourceText), StringComparison.OrdinalIgnoreCase);

    private static A99SourceRepresentabilityResult Result(
        A99SourceRepresentabilityStatus status,
        string reason,
        int occurrenceCount,
        int nonEmptyOccurrenceCount,
        int nonEmptyTextCharacters,
        double nonEmptyOccurrenceRatio,
        bool correspondence) => new()
        {
            Status = status,
            Reason = reason,
            OccurrenceCount = occurrenceCount,
            NonEmptyOccurrenceCount = nonEmptyOccurrenceCount,
            NonEmptyTextCharacters = nonEmptyTextCharacters,
            NonEmptyOccurrenceRatio = nonEmptyOccurrenceRatio,
            PacketTextCorrespondence = correspondence,
            ProviderCalls = 0,
        };
}
