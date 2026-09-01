using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace Accuracy99Baseline;

public sealed record PhaseCSourceDocument
{
    public required string DatasetId { get; init; }
    public required string DocumentId { get; init; }
    public required string SourceCatalogVersion { get; init; }
    public required IReadOnlyList<PhaseCSourceOccurrence> Occurrences { get; init; }
}

public sealed record PhaseCSourceOccurrence
{
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string RawSourceText { get; init; }
    public required StructuralSpan RawSourceSpan { get; init; }
    public required string SourceType { get; init; }
    public string? PreviousSourceText { get; init; }
    public string? NextSourceText { get; init; }
    public int? Page { get; init; }
}

public sealed record PhaseCHistoricalReference
{
    public required int HistoricalOrdinal { get; init; }
    public string? HistoricalSourceId { get; init; }
    public int? HistoricalSourceOrdinal { get; init; }
    public string? HistoricalText { get; init; }
    public int? HistoricalLevel { get; init; }
    public required string Status { get; init; }
}

public sealed record PhaseCReviewManifest
{
    public string RecordType { get; init; } = "manifest";
    public int SchemaVersion { get; init; } = 1;
    public string ArtifactKind { get; init; } = "accuracy99_development_source_first_review";
    public required string DatasetId { get; init; }
    public required string DocumentId { get; init; }
    public required string ProductionBaselineRevision { get; init; }
    public required string SourceCatalogVersion { get; init; }
    public required string SourceCatalogHash { get; init; }
    public required int CatalogOccurrenceCount { get; init; }
    public bool Blind { get; init; }
    public bool PredictionsIncluded { get; init; }
    public string ReviewStatus { get; init; } = "READY_FOR_REVIEW";
    public IReadOnlyList<string> RequiredLabels { get; init; } =
        ["HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED"];
}

public sealed record PhaseCReviewOccurrence
{
    public string RecordType { get; init; } = "occurrence";
    public required string DatasetId { get; init; }
    public required string DocumentId { get; init; }
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string RawSourceText { get; init; }
    public required StructuralSpan RawSourceSpan { get; init; }
    public required int RawTextLength { get; init; }
    public required string SourceType { get; init; }
    public string? PreviousSourceText { get; init; }
    public string? NextSourceText { get; init; }
    public int? Page { get; init; }
    public required string HistoricalProvenanceStatus { get; init; }
    public IReadOnlyList<PhaseCHistoricalReference> HistoricalPositiveReferences { get; init; } = [];

    public string? AdjudicatedLabel { get; init; }
    public string? InitialAdjudicatedLabel { get; init; }
    public string? FinalAdjudicatedLabel { get; init; }
    public string? ResolutionReason { get; init; }
    public int? HeadingStart { get; init; }
    public int? HeadingEnd { get; init; }
    public string? HeadingText { get; init; }
    public string? StructuralType { get; init; }
    public int? Level { get; init; }
    public string? LevelReviewStatus { get; init; }
    public string? ParentGoldId { get; init; }
    public string? ParentReviewStatus { get; init; }
    public string? GoldHeadingId { get; init; }
    public string? Reviewer { get; init; }
    public string? ReviewNotes { get; init; }
}

public sealed record PhaseCReviewPacket(
    PhaseCReviewManifest Manifest,
    IReadOnlyList<PhaseCReviewOccurrence> Occurrences);

public sealed record PhaseCImportedOccurrence
{
    public required string DatasetId { get; init; }
    public required string DocumentId { get; init; }
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string RawSourceText { get; init; }
    public required StructuralSpan RawSourceSpan { get; init; }
    public required string AdjudicatedLabel { get; init; }
    public int? HeadingStart { get; init; }
    public int? HeadingEnd { get; init; }
    public string? HeadingText { get; init; }
    public string? StructuralType { get; init; }
    public int? Level { get; init; }
    public string? LevelReviewStatus { get; init; }
    public string? ParentGoldId { get; init; }
    public string? ParentReviewStatus { get; init; }
    public string? GoldHeadingId { get; init; }
    public required string Reviewer { get; init; }
    public string? ReviewNotes { get; init; }
}

public sealed record PhaseCImportResult
{
    public required string DatasetId { get; init; }
    public required string SourceCatalogHash { get; init; }
    public required string ReviewPacketHash { get; init; }
    public required bool GoldReady { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<PhaseCImportedOccurrence> Occurrences { get; init; }
}

public sealed record PhaseCDevelopmentGold
{
    public int SchemaVersion { get; init; } = 1;
    public string ArtifactKind { get; init; } = "accuracy99_frozen_development_gold";
    public required string DatasetVersion { get; init; }
    public required string ProductionBaselineRevision { get; init; }
    public required IReadOnlyList<string> SourceCatalogHashes { get; init; }
    public required IReadOnlyList<string> ReviewPacketHashes { get; init; }
    public required IReadOnlyList<string> Documents { get; init; }
    public required int OccurrenceCount { get; init; }
    public required int HeadingCount { get; init; }
    public required int NonHeadingCount { get; init; }
    public required int UncertainCount { get; init; }
    public required int ExcludedCount { get; init; }
    public required int ExactSpanReadyHeadingCount { get; init; }
    public required int LevelReadyCount { get; init; }
    public required int ParentReadyCount { get; init; }
    public bool Exhaustive { get; init; } = true;
    public bool Blind { get; init; }
    public bool TuningAllowed { get; init; } = true;
    public bool Claim99Eligible { get; init; }
    public required IReadOnlyList<PhaseCImportedOccurrence> Occurrences { get; init; }
}

public static class PhaseCAdjudication
{
    public const string SourceCatalogVersion = "accuracy99-parser-source-catalog-v1";
    public const string ProductionBaselineRevision = "732c3505afc5dd312423ed0fa58056192fb39608";

    private static readonly HashSet<string> Labels =
        new(["HEADING", "NON_HEADING", "UNCERTAIN", "EXCLUDED"], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static PhaseCReviewPacket CreateBlankPacket(
        PhaseCSourceDocument document,
        IReadOnlyDictionary<string, IReadOnlyList<PhaseCHistoricalReference>> historicalBySourceId)
    {
        EnsureSourceDocumentIntegrity(document);
        var catalogHash = ComputeSourceCatalogHash(document);
        var occurrences = document.Occurrences.Select(source =>
        {
            historicalBySourceId.TryGetValue(source.SourceId, out var historical);
            historical ??= [];
            return new PhaseCReviewOccurrence
            {
                DatasetId = document.DatasetId,
                DocumentId = document.DocumentId,
                SourceId = source.SourceId,
                SourceOrdinal = source.SourceOrdinal,
                RawSourceText = source.RawSourceText,
                RawSourceSpan = source.RawSourceSpan,
                RawTextLength = source.RawSourceText.Length,
                SourceType = source.SourceType,
                PreviousSourceText = source.PreviousSourceText,
                NextSourceText = source.NextSourceText,
                Page = source.Page,
                HistoricalProvenanceStatus = HistoricalStatus(historical),
                HistoricalPositiveReferences = historical,
            };
        }).ToArray();

        return new PhaseCReviewPacket(new PhaseCReviewManifest
        {
            DatasetId = document.DatasetId,
            DocumentId = document.DocumentId,
            ProductionBaselineRevision = ProductionBaselineRevision,
            SourceCatalogVersion = document.SourceCatalogVersion,
            SourceCatalogHash = catalogHash,
            CatalogOccurrenceCount = occurrences.Length,
        }, occurrences);
    }

    public static void WritePacket(string path, PhaseCReviewPacket packet)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(JsonSerializer.Serialize(packet.Manifest, JsonOptions));
        foreach (var occurrence in packet.Occurrences)
            writer.WriteLine(JsonSerializer.Serialize(occurrence, JsonOptions));
    }

    public static PhaseCReviewPacket ReadPacket(string path)
    {
        var lines = File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length == 0) throw new InvalidDataException("Review packet is empty.");
        var manifest = JsonSerializer.Deserialize<PhaseCReviewManifest>(lines[0], JsonOptions)
            ?? throw new InvalidDataException("Review packet manifest is invalid.");
        var occurrences = lines.Skip(1).Select((line, index) =>
            JsonSerializer.Deserialize<PhaseCReviewOccurrence>(line, JsonOptions)
            ?? throw new InvalidDataException($"Review occurrence line {index + 2} is invalid.")).ToArray();
        return new PhaseCReviewPacket(manifest, occurrences);
    }

    public static IReadOnlyList<string> ValidatePacketCompleteness(string path, PhaseCSourceDocument expected)
    {
        var packet = ReadPacket(path);
        var errors = new List<string>();
        var expectedHash = ComputeSourceCatalogHash(expected);
        if (packet.Manifest.DatasetId != expected.DatasetId) errors.Add("DATASET_ID_MISMATCH");
        if (packet.Manifest.DocumentId != expected.DocumentId) errors.Add("DOCUMENT_ID_MISMATCH");
        if (packet.Manifest.SourceCatalogVersion != expected.SourceCatalogVersion) errors.Add("SOURCE_CATALOG_VERSION_MISMATCH");
        if (packet.Manifest.SourceCatalogHash != expectedHash) errors.Add("SOURCE_CATALOG_HASH_MISMATCH");
        if (packet.Manifest.CatalogOccurrenceCount != expected.Occurrences.Count) errors.Add("MANIFEST_OCCURRENCE_COUNT_MISMATCH");
        if (packet.Manifest.PredictionsIncluded) errors.Add("PRODUCTION_PREDICTION_EXPOSED");
        if (packet.Occurrences.Count != expected.Occurrences.Count) errors.Add("PACKET_OCCURRENCE_COUNT_MISMATCH");
        if (packet.Occurrences.GroupBy(item => item.SourceId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            errors.Add("DUPLICATE_SOURCE_ID");

        var expectedById = expected.Occurrences.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        foreach (var row in packet.Occurrences)
        {
            if (!expectedById.TryGetValue(row.SourceId, out var source))
            {
                errors.Add($"SOURCE_NOT_IN_CATALOG:{row.SourceId}");
                continue;
            }
            if (row.DatasetId != expected.DatasetId || row.DocumentId != expected.DocumentId ||
                row.SourceOrdinal != source.SourceOrdinal || row.RawSourceText != source.RawSourceText ||
                row.RawSourceSpan != source.RawSourceSpan || row.RawTextLength != source.RawSourceText.Length ||
                row.SourceType != source.SourceType)
            {
                errors.Add($"PARSER_SOURCE_FIELDS_CHANGED:{row.SourceId}");
            }
        }
        var actualIds = packet.Occurrences.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in expected.Occurrences.Where(item => !actualIds.Contains(item.SourceId)))
            errors.Add($"SOURCE_MISSING_FROM_PACKET:{source.SourceId}");
        return errors.Distinct(StringComparer.Ordinal).OrderBy(error => error, StringComparer.Ordinal).ToArray();
    }

    public static PhaseCImportResult ImportAndValidate(string path, PhaseCSourceDocument expected)
    {
        var packet = ReadPacket(path);
        var errors = new List<string>();
        var expectedHash = ComputeSourceCatalogHash(expected);
        if (packet.Manifest.RecordType != "manifest") errors.Add("MANIFEST_RECORD_TYPE_INVALID");
        if (packet.Manifest.DatasetId != expected.DatasetId) errors.Add("DATASET_ID_MISMATCH");
        if (packet.Manifest.DocumentId != expected.DocumentId) errors.Add("DOCUMENT_ID_MISMATCH");
        if (packet.Manifest.SourceCatalogVersion != expected.SourceCatalogVersion) errors.Add("SOURCE_CATALOG_VERSION_MISMATCH");
        if (packet.Manifest.SourceCatalogHash != expectedHash) errors.Add("SOURCE_CATALOG_HASH_MISMATCH");
        if (packet.Manifest.CatalogOccurrenceCount != expected.Occurrences.Count) errors.Add("MANIFEST_OCCURRENCE_COUNT_MISMATCH");
        if (packet.Manifest.PredictionsIncluded) errors.Add("PRODUCTION_PREDICTION_EXPOSED");
        if (packet.Occurrences.Count != expected.Occurrences.Count) errors.Add("PACKET_OCCURRENCE_COUNT_MISMATCH");
        if (packet.Occurrences.GroupBy(item => item.SourceId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            errors.Add("DUPLICATE_SOURCE_ID");

        var imported = new List<PhaseCImportedOccurrence>();
        var expectedById = expected.Occurrences.ToDictionary(item => item.SourceId, StringComparer.Ordinal);
        foreach (var row in packet.Occurrences)
        {
            if (row.RecordType != "occurrence") errors.Add($"OCCURRENCE_RECORD_TYPE_INVALID:{row.SourceId}");
            if (!expectedById.TryGetValue(row.SourceId, out var source))
            {
                errors.Add($"SOURCE_NOT_IN_CATALOG:{row.SourceId}");
                continue;
            }
            if (row.DatasetId != expected.DatasetId || row.DocumentId != expected.DocumentId ||
                row.SourceOrdinal != source.SourceOrdinal || row.RawSourceText != source.RawSourceText ||
                row.RawSourceSpan != source.RawSourceSpan || row.RawTextLength != source.RawSourceText.Length ||
                row.SourceType != source.SourceType)
            {
                errors.Add($"PARSER_SOURCE_FIELDS_CHANGED:{row.SourceId}");
                continue;
            }

            var label = EffectiveLabel(row, errors);
            if (label is null)
            {
                errors.Add($"UNREVIEWED_OCCURRENCE:{row.SourceId}");
                continue;
            }
            if (!Labels.Contains(label))
            {
                errors.Add($"INVALID_LABEL:{row.SourceId}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Reviewer)) errors.Add($"REVIEWER_REQUIRED:{row.SourceId}");

            string? goldHeadingId = null;
            if (label == "HEADING")
            {
                ValidateHeading(row, errors);
                if (row.HeadingStart is not null && row.HeadingEnd is not null)
                    goldHeadingId = ComputeGoldHeadingId(row.DocumentId, row.SourceId, row.HeadingStart.Value, row.HeadingEnd.Value);
                if (row.GoldHeadingId is not null && row.GoldHeadingId != goldHeadingId)
                    errors.Add($"GOLD_HEADING_ID_MISMATCH:{row.SourceId}");
            }
            else
            {
                ValidateNonHeading(row, errors);
            }

            imported.Add(new PhaseCImportedOccurrence
            {
                DatasetId = row.DatasetId,
                DocumentId = row.DocumentId,
                SourceId = row.SourceId,
                SourceOrdinal = row.SourceOrdinal,
                RawSourceText = row.RawSourceText,
                RawSourceSpan = row.RawSourceSpan,
                AdjudicatedLabel = label,
                HeadingStart = row.HeadingStart,
                HeadingEnd = row.HeadingEnd,
                HeadingText = row.HeadingText,
                StructuralType = row.StructuralType,
                Level = row.Level,
                LevelReviewStatus = row.LevelReviewStatus,
                ParentGoldId = row.ParentGoldId,
                ParentReviewStatus = row.ParentReviewStatus,
                GoldHeadingId = goldHeadingId,
                Reviewer = row.Reviewer ?? string.Empty,
                ReviewNotes = row.ReviewNotes,
            });
        }

        var importedSourceIds = imported.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in expected.Occurrences.Where(item => !importedSourceIds.Contains(item.SourceId)))
            errors.Add($"SOURCE_NOT_IMPORTED:{missing.SourceId}");
        ValidateParents(imported, errors);
        if (!string.Equals(packet.Manifest.ReviewStatus, "REVIEW_COMPLETE", StringComparison.Ordinal))
            errors.Add("MANIFEST_REVIEW_STATUS_NOT_COMPLETE");

        return new PhaseCImportResult
        {
            DatasetId = expected.DatasetId,
            SourceCatalogHash = expectedHash,
            ReviewPacketHash = ComputeFileHash(path),
            GoldReady = errors.Count == 0 && imported.Count == expected.Occurrences.Count,
            Errors = errors.Distinct(StringComparer.Ordinal).OrderBy(error => error, StringComparer.Ordinal).ToArray(),
            Occurrences = imported,
        };
    }

    public static PhaseCDevelopmentGold FreezeDevelopmentGold(
        IReadOnlyList<PhaseCImportResult> imports,
        string datasetVersion)
    {
        if (imports.Count == 0 || imports.Any(item => !item.GoldReady))
            throw new InvalidOperationException("Development gold cannot be frozen until every review packet is GOLD_READY.");
        if (imports.GroupBy(item => item.DatasetId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Development gold contains duplicate document identities.");

        var occurrences = imports.SelectMany(item => item.Occurrences).ToArray();
        var headings = occurrences.Where(item => item.AdjudicatedLabel == "HEADING").ToArray();
        return new PhaseCDevelopmentGold
        {
            DatasetVersion = datasetVersion,
            ProductionBaselineRevision = ProductionBaselineRevision,
            SourceCatalogHashes = imports.Select(item => item.SourceCatalogHash).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            ReviewPacketHashes = imports.Select(item => item.ReviewPacketHash).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Documents = imports.Select(item => item.DatasetId).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            OccurrenceCount = occurrences.Length,
            HeadingCount = headings.Length,
            NonHeadingCount = occurrences.Count(item => item.AdjudicatedLabel == "NON_HEADING"),
            UncertainCount = occurrences.Count(item => item.AdjudicatedLabel == "UNCERTAIN"),
            ExcludedCount = occurrences.Count(item => item.AdjudicatedLabel == "EXCLUDED"),
            ExactSpanReadyHeadingCount = headings.Count(item => item.HeadingStart is not null && item.HeadingEnd is not null),
            LevelReadyCount = headings.Count(item => item.LevelReviewStatus == "REVIEWED"),
            ParentReadyCount = headings.Count(item => item.ParentReviewStatus is "ROOT" or "PARENT_REVIEWED"),
            Occurrences = occurrences,
        };
    }

    public static string ComputeSourceCatalogHash(PhaseCSourceDocument document)
    {
        var builder = new StringBuilder();
        Append(builder, document.DatasetId);
        Append(builder, document.DocumentId);
        Append(builder, document.SourceCatalogVersion);
        foreach (var source in document.Occurrences.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.SourceId, StringComparer.Ordinal))
        {
            Append(builder, source.SourceId);
            Append(builder, source.SourceOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, source.RawSourceText);
            Append(builder, source.RawSourceSpan.Start.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, source.RawSourceSpan.End.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, source.SourceType);
            Append(builder, source.Page?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Sha256(builder.ToString());
    }

    public static string ComputeGoldHeadingId(string documentId, string sourceId, int start, int end)
    {
        var builder = new StringBuilder();
        Append(builder, documentId);
        Append(builder, sourceId);
        Append(builder, start.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, end.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "gold-heading:" + Sha256(builder.ToString());
    }

    private static void EnsureSourceDocumentIntegrity(PhaseCSourceDocument document)
    {
        if (document.Occurrences.GroupBy(item => item.SourceId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException($"Source catalog {document.DatasetId} contains duplicate SourceId values.");
        if (document.Occurrences.Any(item => !PhaseBContracts.IsValidSourceSpan(item.RawSourceText, item.RawSourceSpan)))
            throw new InvalidDataException($"Source catalog {document.DatasetId} contains an invalid raw source span.");
    }

    private static string HistoricalStatus(IReadOnlyList<PhaseCHistoricalReference> historical)
    {
        if (historical.Count == 0) return "NONE";
        if (historical.Any(item => item.Status == "AMBIGUOUS")) return "AMBIGUOUS";
        if (historical.Any(item => item.Status == "REVIEW_REQUIRED")) return "REVIEW_REQUIRED";
        return historical.All(item => item.Status == "EXACT_REBOUND") ? "EXACT_REBOUND" : "REVIEW_REQUIRED";
    }

    private static string? EffectiveLabel(PhaseCReviewOccurrence row, ICollection<string> errors)
    {
        if (row.FinalAdjudicatedLabel is not null)
        {
            if (row.InitialAdjudicatedLabel is null || string.IsNullOrWhiteSpace(row.ResolutionReason))
                errors.Add($"DISCREPANCY_REVIEW_PROVENANCE_INCOMPLETE:{row.SourceId}");
            return row.FinalAdjudicatedLabel;
        }
        if (row.InitialAdjudicatedLabel is not null) return row.InitialAdjudicatedLabel;
        return row.AdjudicatedLabel;
    }

    private static void ValidateHeading(PhaseCReviewOccurrence row, ICollection<string> errors)
    {
        if (row.HeadingStart is null || row.HeadingEnd is null || row.HeadingText is null)
        {
            errors.Add($"HEADING_SPAN_REQUIRED:{row.SourceId}");
        }
        else
        {
            var span = new StructuralSpan(row.HeadingStart.Value, row.HeadingEnd.Value);
            if (!PhaseBContracts.IsHeadingSpanTextConsistent(row.RawSourceText, span, row.HeadingText))
                errors.Add($"HEADING_SPAN_TEXT_MISMATCH:{row.SourceId}");
        }
        if (string.IsNullOrWhiteSpace(row.StructuralType)) errors.Add($"STRUCTURAL_TYPE_REQUIRED:{row.SourceId}");
        if (row.LevelReviewStatus == "REVIEWED")
        {
            if (row.Level is null || row.Level < 1) errors.Add($"REVIEWED_LEVEL_INVALID:{row.SourceId}");
        }
        else if (row.LevelReviewStatus == "LEVEL_NOT_REVIEWED")
        {
            if (row.Level is not null) errors.Add($"UNREVIEWED_LEVEL_MUST_BE_NULL:{row.SourceId}");
        }
        else
        {
            errors.Add($"LEVEL_REVIEW_STATUS_REQUIRED:{row.SourceId}");
        }
        if (row.ParentReviewStatus is not ("ROOT" or "PARENT_REVIEWED" or "PARENT_UNKNOWN"))
            errors.Add($"PARENT_REVIEW_STATUS_REQUIRED:{row.SourceId}");
        if (row.ParentReviewStatus == "PARENT_REVIEWED" && string.IsNullOrWhiteSpace(row.ParentGoldId))
            errors.Add($"PARENT_GOLD_ID_REQUIRED:{row.SourceId}");
        if (row.ParentReviewStatus is "ROOT" or "PARENT_UNKNOWN" && row.ParentGoldId is not null)
            errors.Add($"PARENT_GOLD_ID_CONTRADICTS_STATUS:{row.SourceId}");
    }

    private static void ValidateNonHeading(PhaseCReviewOccurrence row, ICollection<string> errors)
    {
        if (row.HeadingStart is not null || row.HeadingEnd is not null || row.HeadingText is not null ||
            row.StructuralType is not null || row.Level is not null || row.LevelReviewStatus is not null ||
            row.ParentGoldId is not null || row.ParentReviewStatus is not null || row.GoldHeadingId is not null)
        {
            errors.Add($"NON_HEADING_HAS_HEADING_FIELDS:{row.SourceId}");
        }
    }

    private static void ValidateParents(IReadOnlyList<PhaseCImportedOccurrence> imported, ICollection<string> errors)
    {
        var headings = imported.Where(item => item.AdjudicatedLabel == "HEADING" && item.GoldHeadingId is not null)
            .ToDictionary(item => item.GoldHeadingId!, StringComparer.Ordinal);
        foreach (var child in imported.Where(item => item.ParentReviewStatus == "PARENT_REVIEWED"))
        {
            if (child.ParentGoldId is null || !headings.TryGetValue(child.ParentGoldId, out var parent))
                errors.Add($"PARENT_GOLD_ID_NOT_FOUND:{child.SourceId}");
            else if (parent.DocumentId != child.DocumentId)
                errors.Add($"PARENT_DOCUMENT_MISMATCH:{child.SourceId}");
            else if (parent.GoldHeadingId == child.GoldHeadingId)
                errors.Add($"HEADING_CANNOT_PARENT_ITSELF:{child.SourceId}");
        }
    }

    public static string ComputeFileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }
}
