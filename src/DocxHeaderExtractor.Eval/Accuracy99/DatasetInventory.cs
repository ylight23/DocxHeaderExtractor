using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

namespace DocxHeaderExtractor.Eval.Accuracy99;

public static class Accuracy99DatasetInventoryBuilder
{
    private static readonly string[] SourceExtensions = [".docx", ".docm", ".pdf"];

    public static Accuracy99DatasetInventory Discover(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !IsGeneratedPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entries = files.Select(Classify).ToArray();
        var duplicateGroups = entries.GroupBy(entry => entry.Sha256, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        var mixedSplit = duplicateGroups.Any(group =>
        {
            var splits = group.Select(entry => entry.Split).Where(split => split != "UNASSIGNED")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return splits.Length > 1;
        });

        return new Accuracy99DatasetInventory
        {
            Root = Path.GetFullPath(root),
            FreezeStatus = entries.Length == 0
                ? "NOT_READY_NO_SOURCE"
                : entries.Any(entry => entry.Classification == Accuracy99DatasetClassification.InvalidSource)
                    ? "NOT_READY_INVALID_SOURCE"
                    : entries.All(entry => entry.Classification == Accuracy99DatasetClassification.HumanGold) &&
                      entries.Any(entry => entry.Split.Equals("DEV", StringComparison.OrdinalIgnoreCase)) &&
                      entries.Any(entry => entry.Split.Equals("BLIND_HOLDOUT", StringComparison.OrdinalIgnoreCase)) &&
                      !mixedSplit
                        ? "READY"
                        : "NOT_READY_HUMAN_GOLD_REQUIRED",
            HumanGoldCount = entries.Count(entry => entry.Classification == Accuracy99DatasetClassification.HumanGold),
            SilverOnlyCount = entries.Count(entry => entry.Classification == Accuracy99DatasetClassification.SilverOnly),
            UnlabeledCount = entries.Count(entry => entry.Classification == Accuracy99DatasetClassification.Unlabeled),
            InvalidSourceCount = entries.Count(entry => entry.Classification == Accuracy99DatasetClassification.InvalidSource),
            DuplicateContentGroups = duplicateGroups.Length,
            Entries = entries,
        };
    }

    private static Accuracy99DatasetEntry Classify(string path)
    {
        string sha;
        try
        {
            sha = HumanGoldValidator.ComputeSha256(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Accuracy99DatasetEntry
            {
                DocumentId = path,
                Path = path,
                Sha256 = string.Empty,
                Classification = Accuracy99DatasetClassification.InvalidSource,
                ContentGroup = "invalid",
                Reason = $"source-unreadable:{ex.GetType().Name}",
            };
        }

        var goldPath = FindGoldPath(path);
        var silverPath = FindSilverPath(path);
        if (goldPath is null)
        {
            var hasSilver = silverPath is not null ||
                            Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*.json")
                                .Any(IsNonGoldSidecar);
            return new Accuracy99DatasetEntry
            {
                DocumentId = path,
                Path = path,
                Sha256 = sha,
                Classification = hasSilver
                    ? Accuracy99DatasetClassification.SilverOnly
                    : Accuracy99DatasetClassification.Unlabeled,
                ContentGroup = sha,
                Reason = hasSilver ? "non-human-gold-sidecar-present" : "no-human-gold-sidecar",
            };
        }

        try
        {
            var artifact = JsonSerializer.Deserialize<HumanGoldArtifact>(
                File.ReadAllText(goldPath), JsonOptions);
            if (artifact is null)
                throw new InvalidDataException("gold-json-null");
            var metadataValid = string.Equals(artifact.ArtifactKind, "human_gold", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(artifact.AuthorityClass, "HUMAN_GOLD", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(artifact.SourceDocumentSha256, sha, StringComparison.OrdinalIgnoreCase);
            var reason = metadataValid ? null : "human-gold-metadata-or-sha-invalid";
            if (metadataValid && IsOpenXml(path))
            {
                var source = new OpenXmlDocumentSource().Read(path);
                var validation = HumanGoldValidator.Validate(artifact, source, sha);
                if (!validation.IsValid)
                {
                    metadataValid = false;
                    reason = $"human-gold-contract-invalid:{string.Join(",", validation.Errors)}";
                }
            }
            else if (metadataValid && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // PDF source facts are intentionally not reconstructed from DOCX here. The
                // metadata classification is retained, while the evaluator still requires a
                // parser-owned source representation before it can score a PDF document.
                reason = "pdf-source-validation-deferred-to-pdf-reader";
            }
            return new Accuracy99DatasetEntry
            {
                DocumentId = path,
                Path = path,
                Sha256 = sha,
                GoldPath = goldPath,
                Classification = metadataValid
                    ? Accuracy99DatasetClassification.HumanGold
                    : Accuracy99DatasetClassification.InvalidSource,
                Split = metadataValid ? artifact.Split : "UNASSIGNED",
                ContentGroup = sha,
                Reason = reason,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Accuracy99DatasetEntry
            {
                DocumentId = path,
                Path = path,
                Sha256 = sha,
                GoldPath = goldPath,
                Classification = Accuracy99DatasetClassification.InvalidSource,
                ContentGroup = sha,
                Reason = $"human-gold-unreadable:{ex.GetType().Name}",
            };
        }
    }

    private static string? FindGoldPath(string sourcePath)
    {
        var stem = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            Path.GetFileNameWithoutExtension(sourcePath));
        var candidates = new[]
        {
            stem + ".human-gold.json",
            stem + ".gold.json",
            sourcePath + ".human-gold.json",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindSilverPath(string sourcePath)
    {
        var stem = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            Path.GetFileNameWithoutExtension(sourcePath));
        var candidates = new[] { stem + ".key", stem + ".silver.json", sourcePath + ".key" };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsNonGoldSidecar(string path) =>
        !Path.GetFileName(path).Contains(".human-gold.", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenXml(string path) =>
        path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".docm", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
