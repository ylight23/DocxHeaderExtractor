using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.Accuracy99;

/// <summary>
/// Creates the review universe from the frozen split assignment and parser-owned source facts.
/// Prediction, confidence, and historical answer files are never consulted.
/// </summary>
public static class A99ReferenceCampaignBuilder
{
    private const string WordCorpus = "todo10_8/heading_corpus_95_word/";

    public static A99ReferenceCampaign Build(
        string repoRoot,
        string outputPacketRoot,
        string? inventoryPath = null,
        string? splitPath = null,
        string? createdFromRevision = null)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        outputPacketRoot = Path.GetFullPath(outputPacketRoot);
        inventoryPath ??= Path.Combine(repoRoot, "eval", "a99-dataset", "document-inventory.v1.json");
        splitPath ??= Path.Combine(repoRoot, "eval", "a99-dataset", "evaluation-splits.v1.json");

        var inventory = ReadInventory(inventoryPath);
        var splits = ReadSplits(splitPath);
        var selected = inventory
            .Where(item => item.SourcePath.Replace('\\', '/')
                .StartsWith(WordCorpus, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.MediaType.Equals("DOCX", StringComparison.OrdinalIgnoreCase) ||
                          item.MediaType.Equals("DOCM", StringComparison.OrdinalIgnoreCase))
            .Select(item => item with { Split = splits.TryGetValue(item.DocumentGroupId, out var split) ? split : "UNASSIGNED" })
            .Where(item => item.Split is "DEV" or "GENERALIZATION_HOLDOUT")
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToArray();

        if (selected.Length == 0) throw new InvalidDataException("A99 reference campaign has no eligible documents.");
        var missingAssignments = selected.Where(item => item.Split == "UNASSIGNED").ToArray();
        if (missingAssignments.Length > 0)
            throw new InvalidDataException($"A99 split missing for: {string.Join(", ", missingAssignments.Select(x => x.DocumentId))}");

        Directory.CreateDirectory(Path.Combine(outputPacketRoot, "dev"));
        Directory.CreateDirectory(Path.Combine(outputPacketRoot, "holdout-sealed"));
        var campaignDocuments = new List<A99CampaignDocument>();
        foreach (var item in selected)
        {
            var sourcePath = Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("A99 source file missing", sourcePath);
            var actualSha = HumanGoldValidator.ComputeSha256(sourcePath);
            if (!actualSha.Equals(item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"A99 source SHA mismatch: {item.DocumentId}");
            var source = new OpenXmlDocumentSource().Read(sourcePath);
            var packet = A99ReviewPacketBuilder.Create(new A99CampaignDocument
            {
                DocumentId = item.DocumentId,
                DocumentGroupId = item.DocumentGroupId,
                Split = item.Split,
                FamilyId = item.FamilyId,
                SourcePath = item.SourcePath,
                SourceSha256 = item.SourceSha256,
                SourceOccurrenceCount = source.Paragraphs.Count,
                PacketPath = "",
                PacketSha256 = "",
            }, source);
            var splitDirectory = item.Split == "DEV" ? "dev" : "holdout-sealed";
            var packetFileName = item.DocumentId + ".v1.json";
            var packetFullPath = Path.Combine(outputPacketRoot, splitDirectory, packetFileName);
            File.WriteAllText(packetFullPath, A99ReviewJson.Serialize(packet) + Environment.NewLine, new System.Text.UTF8Encoding(false));
            var packetRelativePath = ToPortableRelativePath(outputPacketRoot, packetFullPath);
            campaignDocuments.Add(new A99CampaignDocument
            {
                DocumentId = item.DocumentId,
                DocumentGroupId = item.DocumentGroupId,
                Split = item.Split,
                FamilyId = item.FamilyId,
                SourcePath = item.SourcePath,
                SourceSha256 = item.SourceSha256,
                SourceOccurrenceCount = source.Paragraphs.Count,
                PacketPath = packetRelativePath,
                PacketSha256 = packet.PacketSha256!,
            });
        }

        var existingAudit = AuditExistingPackets(repoRoot, inventory, splits);
        var familySummary = campaignDocuments
            .GroupBy(x => x.FamilyId, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(group => new A99FamilyCampaignSummary
            {
                FamilyId = group.Key,
                Documents = group.Count(),
                Groups = group.Select(x => x.DocumentGroupId).Distinct(StringComparer.Ordinal).Count(),
                SourceOccurrences = group.Sum(x => x.SourceOccurrenceCount),
            }).ToArray();

        return new A99ReferenceCampaign
        {
            CreatedFromRevision = createdFromRevision ?? "UNRESOLVED",
            SourceCorpus = WordCorpus,
            SelectionPolicy = "ALL_ELIGIBLE_DOCX_IN_FROZEN_DEV_AND_GENERALIZATION_HOLDOUT_SPLITS; RESERVED_UNLABELED_EXCLUDED",
            DevDocuments = campaignDocuments.Where(x => x.Split == "DEV").ToArray(),
            HoldoutDocuments = campaignDocuments.Where(x => x.Split == "GENERALIZATION_HOLDOUT").ToArray(),
            FamilySummary = familySummary,
            ExistingPacketAudit = existingAudit,
            ReservedDocumentsExcluded = inventory.Count(item =>
                item.SourcePath.Replace('\\', '/').StartsWith(WordCorpus, StringComparison.OrdinalIgnoreCase) &&
                splits.TryGetValue(item.DocumentGroupId, out var split) && split == "RESERVED_UNLABELED"),
            ProviderCalls = 0,
        };
    }

    public static (A99ReviewManifest Dev, A99ReviewManifest Holdout) BuildReviewManifests(
        A99ReferenceCampaign campaign,
        string packetRoot)
    {
        static A99ReviewManifest Create(string split, IReadOnlyList<A99CampaignDocument> documents, string root) => new()
        {
            Split = split,
            PacketRoot = root,
            PacketCount = documents.Count,
            SourceOccurrences = documents.Sum(x => x.SourceOccurrenceCount),
            Entries = documents.Select(x => new A99ReviewManifestEntry
            {
                DocumentId = x.DocumentId,
                DocumentGroupId = x.DocumentGroupId,
                Split = x.Split,
                FamilyId = x.FamilyId,
                SourcePath = x.SourcePath,
                SourceSha256 = x.SourceSha256,
                PacketPath = x.PacketPath,
                PacketSha256 = x.PacketSha256,
                SourceOccurrenceCount = x.SourceOccurrenceCount,
            }).ToArray(),
            ProviderCalls = 0,
        };
        return (
            Create("DEV", campaign.DevDocuments, ToPortablePath(Path.Combine(packetRoot, "dev"))),
            Create("GENERALIZATION_HOLDOUT", campaign.HoldoutDocuments, ToPortablePath(Path.Combine(packetRoot, "holdout-sealed"))));
    }

    private static IReadOnlyList<A99ExistingPacketAudit> AuditExistingPackets(
        string repoRoot,
        IReadOnlyList<InventoryItem> inventory,
        IReadOnlyDictionary<string, string> splits)
    {
        var packetDirectory = Path.Combine(repoRoot, "eval", "harness-lift", "review-packets-v2");
        if (!Directory.Exists(packetDirectory)) return [];
        var byId = inventory.ToDictionary(x => x.DocumentId, StringComparer.Ordinal);
        var result = new List<A99ExistingPacketAudit>();
        foreach (var path in Directory.EnumerateFiles(packetDirectory, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var id = root.TryGetProperty("documentId", out var idProperty) ? idProperty.GetString() : null;
                var sha = root.TryGetProperty("sourceSha256", out var shaProperty)
                    ? shaProperty.GetString()
                    : root.TryGetProperty("sourceDocumentSha256", out var sourceShaProperty) ? sourceShaProperty.GetString() : null;
                var occurrences = root.TryGetProperty("occurrences", out var rows) && rows.ValueKind == JsonValueKind.Array
                    ? rows.GetArrayLength() : -1;
                if (id is null || !byId.TryGetValue(id, out var item))
                {
                    result.Add(new A99ExistingPacketAudit { PacketPath = RelativeToRepo(repoRoot, path), DocumentId = id, Classification = "REGENERATE_REQUIRED", Reason = "document-not-in-current-campaign" });
                    continue;
                }
                var split = splits.TryGetValue(item.DocumentGroupId, out var assigned) ? assigned : "UNASSIGNED";
                if (!string.Equals(sha, item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    result.Add(new A99ExistingPacketAudit { PacketPath = RelativeToRepo(repoRoot, path), DocumentId = id, Classification = "REGENERATE_REQUIRED", Reason = "source-sha-mismatch" });
                else if (occurrences != -1 && occurrences == CountSourceOccurrences(repoRoot, item))
                    result.Add(new A99ExistingPacketAudit { PacketPath = RelativeToRepo(repoRoot, path), DocumentId = id, Classification = "EXHAUSTIVE_COMPATIBLE", Reason = $"source-first-coverage-confirmed; split={split}" });
                else
                    result.Add(new A99ExistingPacketAudit { PacketPath = RelativeToRepo(repoRoot, path), DocumentId = id, Classification = "PARTIAL_REUSABLE", Reason = $"source-sha-matched-but-occurrence-coverage-not-proven; split={split}" });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                result.Add(new A99ExistingPacketAudit { PacketPath = RelativeToRepo(repoRoot, path), Classification = "REGENERATE_REQUIRED", Reason = $"unreadable:{ex.GetType().Name}" });
            }
        }
        return result;
    }

    private static int CountSourceOccurrences(string repoRoot, InventoryItem item) =>
        new OpenXmlDocumentSource().Read(Path.Combine(repoRoot, item.SourcePath.Replace('/', Path.DirectorySeparatorChar))).Paragraphs.Count;

    private static List<InventoryItem> ReadInventory(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("documents").EnumerateArray().Select(item => new InventoryItem(
            item.GetProperty("documentId").GetString()!,
            item.GetProperty("sourcePath").GetString()!,
            item.GetProperty("sourceSha256").GetString()!,
            item.GetProperty("documentGroupId").GetString()!,
            item.GetProperty("familyId").GetString()!,
            item.GetProperty("mediaType").GetString()!)).ToList();
    }

    private static Dictionary<string, string> ReadSplits(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("splits").EnumerateArray().ToDictionary(
            item => item.GetProperty("documentGroupId").GetString()!,
            item => item.GetProperty("split").GetString()!,
            StringComparer.Ordinal);
    }

    private static string ToPortableRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string RelativeToRepo(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string ToPortablePath(string path) => path.Replace('\\', '/');

    private sealed record InventoryItem(
        string DocumentId,
        string SourcePath,
        string SourceSha256,
        string DocumentGroupId,
        string FamilyId,
        string MediaType)
    {
        public string Split { get; init; } = "UNASSIGNED";
    }
}
