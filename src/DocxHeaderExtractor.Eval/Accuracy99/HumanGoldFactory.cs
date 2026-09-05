using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class A99ReviewJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value, bool indented = true)
    {
        if (indented) return JsonSerializer.Serialize(value, Options);
        var compact = new JsonSerializerOptions(Options) { WriteIndented = false };
        return JsonSerializer.Serialize(value, compact);
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException($"Không đọc được JSON {typeof(T).Name}.");

    public static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

public static class A99ReviewPacketBuilder
{
    public static A99ReviewPacket Create(
        A99CampaignDocument campaign,
        SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(campaign.SourceSha256, HumanGoldValidator.ComputeSha256(source.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"source-sha-mismatch:{campaign.DocumentId}");

        var paragraphs = source.Paragraphs.ToArray();
        var occurrences = paragraphs.Select((paragraph, index) => new A99ReviewOccurrence
        {
            SourceId = paragraph.SourceId,
            StableId = paragraph.StableId,
            SourceOrdinal = paragraph.SourceOrdinal,
            SourceSpan = new A99ReviewSpan(0, paragraph.Text.Length),
            SourceTextHash = TextSha256(paragraph.Text),
            SourceText = paragraph.Text,
            PreviousSourceId = index == 0 ? null : paragraphs[index - 1].SourceId,
            PreviousText = index == 0 ? null : paragraphs[index - 1].Text,
            NextSourceId = index + 1 == paragraphs.Length ? null : paragraphs[index + 1].SourceId,
            NextText = index + 1 == paragraphs.Length ? null : paragraphs[index + 1].Text,
            Style = new A99ReviewStyleFacts
            {
                StyleId = paragraph.Style.StyleId,
                StyleName = paragraph.Style.StyleName,
                BuiltInHeadingStyleLevel = paragraph.Style.BuiltInHeadingStyleLevel,
                OutlineLevel = paragraph.Style.OutlineLevel,
                Bold = paragraph.Style.Bold,
                Italic = paragraph.Style.Italic,
                Underline = paragraph.Style.Underline,
                AllCaps = paragraph.Style.AllCaps,
                FontSizePt = paragraph.Style.FontSizePt,
                Alignment = paragraph.Style.Alignment,
            },
            Numbering = new A99ReviewNumberingFacts
            {
                NumberingId = paragraph.Numbering.NumberingId,
                LevelReference = paragraph.Numbering.NumberingLevel,
                NumberLabel = paragraph.Numbering.NumberLabel,
                NumberingFormat = paragraph.Numbering.NumberingFormat,
                StyleLinkedLevel = paragraph.Numbering.NumberingStyleHeadingLevel,
            },
            Layout = new A99ReviewLayoutFacts
            {
                InContentControl = paragraph.Layout.InContentControl,
                KeepNext = paragraph.Layout.KeepNext,
                PageBreakBefore = paragraph.Layout.PageBreakBefore,
                TableDepth = paragraph.Layout.TableDepth,
                SectionIndex = paragraph.Layout.SectionIndex,
                InTableOfContents = paragraph.InTableOfContents,
            },
        }).ToArray();

        var packet = new A99ReviewPacket
        {
            DocumentId = campaign.DocumentId,
            DocumentGroupId = campaign.DocumentGroupId,
            Split = campaign.Split,
            FamilyId = campaign.FamilyId,
            FileName = source.FileName,
            SourceKind = source.SourceKind,
            SourceDocumentSha256 = campaign.SourceSha256,
            Occurrences = occurrences,
        };
        return packet with { PacketSha256 = ComputeSha256(packet) };
    }

    public static string ComputeSha256(A99ReviewPacket packet)
    {
        var unsigned = packet with { PacketSha256 = null };
        return A99ReviewJson.Sha256(A99ReviewJson.Serialize(unsigned, indented: false));
    }

    public static string TextSha256(string text) => A99ReviewJson.Sha256(text);
}

public static class A99HumanGoldValidator
{
    private static readonly HashSet<string> YesParents = new(StringComparer.OrdinalIgnoreCase) { "ROOT", "UNKNOWN" };

    public static A99GoldValidationResult Validate(
        A99ReviewPacket packet,
        A99HumanGoldDocument gold)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(gold);
        var errors = new List<string>();
        Require(gold.ArtifactKind, "a99_human_gold", "artifact-kind-not-human-gold", errors);
        Require(gold.AuthorityClass, "HUMAN_GOLD", "authority-class-not-human-gold", errors);
        RequireText(gold.GoldSchemaVersion, "gold-schema-version-missing", errors);
        RequireText(gold.ReviewerAlias, "reviewer-alias-missing", errors);
        RequireText(gold.ReviewVersion, "review-version-missing", errors);
        RequireText(gold.DocumentId, "document-id-missing", errors);
        RequireText(gold.DocumentGroupId, "document-group-id-missing", errors);
        RequireText(gold.Split, "split-missing", errors);
        RequireText(gold.SourceDocumentSha256, "source-sha-missing", errors);
        RequireText(gold.PacketSha256, "packet-sha-missing", errors);
        if (gold.ReviewedAt == default) errors.Add("reviewed-at-missing");
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
        var rowsById = new Dictionary<string, A99GoldOccurrence>(StringComparer.Ordinal);
        foreach (var row in gold.Rows)
        {
            if (!rowsById.TryAdd(row.SourceId, row))
            {
                errors.Add($"duplicate-gold-source-identity:{row.SourceId}");
                continue;
            }
            if (!packetById.TryGetValue(row.SourceId, out var occurrence))
            {
                errors.Add($"gold-source-not-found:{row.SourceId}");
                continue;
            }
            if (!string.Equals(row.StableId, occurrence.StableId, StringComparison.Ordinal)) errors.Add($"stable-id-mismatch:{row.SourceId}");
            if (row.SourceOrdinal != occurrence.SourceOrdinal) errors.Add($"source-ordinal-mismatch:{row.SourceId}");
            if (row.SourceSpan != occurrence.SourceSpan) errors.Add($"source-span-mismatch:{row.SourceId}");
            if (!string.Equals(row.SourceTextHash, occurrence.SourceTextHash, StringComparison.OrdinalIgnoreCase)) errors.Add($"source-text-hash-mismatch:{row.SourceId}");
            ValidateRow(row, occurrence, rowsById, packetById, errors);
        }

        foreach (var occurrence in packet.Occurrences)
            if (!rowsById.ContainsKey(occurrence.SourceId)) errors.Add($"missing-gold-source-identity:{occurrence.SourceId}");

        ValidateParents(gold.Rows, rowsById, packetById, errors);
        return new A99GoldValidationResult(errors.Count == 0, errors);
    }

    public static void EnsureValid(A99ReviewPacket packet, A99HumanGoldDocument gold) =>
        Validate(packet, gold).ThrowIfInvalid();

    public static int OfficialDenominatorCount(IEnumerable<A99GoldOccurrence> rows) =>
        rows.Count(row => row.IsHeading is A99ReviewLabel.Yes or A99ReviewLabel.No);

    private static void ValidateRow(
        A99GoldOccurrence row,
        A99ReviewOccurrence occurrence,
        IReadOnlyDictionary<string, A99GoldOccurrence> rowsById,
        IReadOnlyDictionary<string, A99ReviewOccurrence> packetById,
        ICollection<string> errors)
    {
        if (!row.SourceSpan.IsValidFor(occurrence.SourceText)) errors.Add($"source-span-invalid:{row.SourceId}");
        switch (row.IsHeading)
        {
            case A99ReviewLabel.Yes:
                if (string.IsNullOrWhiteSpace(row.Role) || !A99ReviewRoles.HeadingRoles.Contains(row.Role)) errors.Add($"heading-role-invalid:{row.SourceId}");
                if (row.HeadingSpan is null || !row.HeadingSpan.IsValidFor(occurrence.SourceText)) errors.Add($"heading-span-invalid:{row.SourceId}");
                else if (row.HeadingSpan.Start < row.SourceSpan.Start || row.HeadingSpan.End > row.SourceSpan.End) errors.Add($"heading-outside-source-envelope:{row.SourceId}");
                if (row.Level is < 1 or > 9) errors.Add($"heading-level-invalid:{row.SourceId}");
                if (string.IsNullOrWhiteSpace(row.ParentOccurrenceId)) errors.Add($"heading-parent-missing:{row.SourceId}");
                break;
            case A99ReviewLabel.No:
                if (string.IsNullOrWhiteSpace(row.Role) || !A99ReviewRoles.NonHeadingRoles.Contains(row.Role)) errors.Add($"non-heading-role-invalid:{row.SourceId}");
                if (row.HeadingSpan is not null) errors.Add($"non-heading-has-heading-span:{row.SourceId}");
                if (row.Level is not null) errors.Add($"non-heading-has-level:{row.SourceId}");
                if (row.ParentOccurrenceId is not null) errors.Add($"non-heading-has-parent:{row.SourceId}");
                break;
            case A99ReviewLabel.Unsure:
                if (row.HeadingSpan is not null) errors.Add($"unsure-has-heading-span:{row.SourceId}");
                if (row.Level is not null) errors.Add($"unsure-has-level:{row.SourceId}");
                if (row.ParentOccurrenceId is not null) errors.Add($"unsure-has-parent:{row.SourceId}");
                break;
            default:
                errors.Add($"label-invalid:{row.SourceId}");
                break;
        }
    }

    private static void ValidateParents(
        IReadOnlyList<A99GoldOccurrence> rows,
        IReadOnlyDictionary<string, A99GoldOccurrence> rowsById,
        IReadOnlyDictionary<string, A99ReviewOccurrence> packetById,
        ICollection<string> errors)
    {
        foreach (var row in rows.Where(x => x.IsHeading == A99ReviewLabel.Yes))
        {
            if (row.ParentOccurrenceId is null || YesParents.Contains(row.ParentOccurrenceId)) continue;
            if (!rowsById.TryGetValue(row.ParentOccurrenceId, out var parent)) errors.Add($"parent-not-found:{row.SourceId}");
            else if (parent.IsHeading != A99ReviewLabel.Yes) errors.Add($"parent-not-heading:{row.SourceId}");
            if (string.Equals(row.ParentOccurrenceId, row.SourceId, StringComparison.Ordinal)) errors.Add($"parent-self:{row.SourceId}");

            var seen = new HashSet<string>(StringComparer.Ordinal) { row.SourceId };
            var cursor = row.ParentOccurrenceId;
            while (cursor is not null && !YesParents.Contains(cursor))
            {
                if (!seen.Add(cursor)) { errors.Add($"hierarchy-cycle:{row.SourceId}"); break; }
                if (!rowsById.TryGetValue(cursor, out var parentInChain) || parentInChain.IsHeading != A99ReviewLabel.Yes) break;
                cursor = parentInChain.ParentOccurrenceId;
            }
        }
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
