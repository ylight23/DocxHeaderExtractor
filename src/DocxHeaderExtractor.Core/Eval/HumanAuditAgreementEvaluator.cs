using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Validates human labels against a frozen blind source packet and computes agreement with a separate
/// silver binding. It never mutates silver labels and deliberately calls the result an agreement,
/// never human-gold accuracy.
/// </summary>
public static class HumanAuditAgreementEvaluator
{
    public const string Heading = "REVIEWED_HEADING";
    public const string NonHeading = "REVIEWED_NON_HEADING";
    public const string Uncertain = "UNCERTAIN";
    private static readonly HashSet<string> Labels = [Heading, NonHeading, Uncertain];

    public static JsonElement CreateTemplate(string sourcePath, string bindingPath)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
        using var binding = JsonDocument.Parse(File.ReadAllText(bindingPath));
        var sourceItems = source.RootElement.GetProperty("items").EnumerateArray().ToArray();
        ValidatePacketPair(source.RootElement, binding.RootElement, sourceItems);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            artifactKind = "n1_4_human_audit_result",
            sourcePacketSha256 = Sha256(sourcePath),
            bindingSha256 = Sha256(bindingPath),
            reviewItems = sourceItems.Select(item => new
            {
                auditItemId = item.GetProperty("AuditItemId").GetString(),
                humanLabel = (string?)null,
                reviewerNote = (string?)null,
            }),
        }, JsonOptions));
        return document.RootElement.Clone();
    }

    public static HumanAuditAgreementReport Evaluate(string sourcePath, string bindingPath, string resultPath)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
        using var binding = JsonDocument.Parse(File.ReadAllText(bindingPath));
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        var sourceItems = source.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var bindingById = ValidatePacketPair(source.RootElement, binding.RootElement, sourceItems);
        var root = result.RootElement;
        if (root.GetProperty("sourcePacketSha256").GetString() != Sha256(sourcePath)) throw new InvalidDataException("Source packet hash mismatch.");
        if (root.GetProperty("bindingSha256").GetString() != Sha256(bindingPath)) throw new InvalidDataException("Binding hash mismatch.");

        var resultItems = root.GetProperty("reviewItems").EnumerateArray().Select(ReadResult).ToArray();
        var sourceById = sourceItems.ToDictionary(item => item.GetProperty("AuditItemId").GetString()!, StringComparer.Ordinal);
        if (resultItems.Length != sourceItems.Length) throw new InvalidDataException("Human result must retain the exact frozen review population.");
        if (resultItems.GroupBy(item => item.AuditItemId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Duplicate audit occurrence identity.");
        var resultById = resultItems.ToDictionary(item => item.AuditItemId, StringComparer.Ordinal);
        if (resultById.Keys.Except(sourceById.Keys, StringComparer.Ordinal).Any()) throw new InvalidDataException("Human result contains extra/unknown audit occurrence.");
        if (sourceById.Keys.Except(resultById.Keys, StringComparer.Ordinal).Any()) throw new InvalidDataException("Human result is missing frozen audit occurrence.");

        var complete = resultItems.All(item => item.HumanLabel is not null);
        var claim = complete ? "HUMAN_AUDITED_SILVER" : resultItems.All(item => item.HumanLabel is null) ? "NO_HUMAN_AUDIT" : "PARTIAL_HUMAN_AUDIT";
        var joined = resultItems.Select(item => Join(item, sourceById[item.AuditItemId], bindingById[item.AuditItemId])).ToArray();
        var report = complete ? BuildMetrics(joined) : null;
        var disagreements = complete ? joined.Where(item => item.HumanLabel != Uncertain && item.HumanLabel != item.SilverLabel)
            .Select(item => new AuditDisagreement(item.AuditItemId, item.Identity, item.HumanLabel!, item.SilverLabel,
                item.SilverConfidence, item.SelectionReason, "REVIEW_REQUIRED")).ToArray() : [];
        return new HumanAuditAgreementReport(1, "n1_4_human_audited_silver_agreement", claim,
            "HUMAN_AUDITED_SILVER_AGREEMENT", sourceItems.Length, complete, report, disagreements);
    }

    private static IReadOnlyDictionary<string, JsonElement> ValidatePacketPair(JsonElement source, JsonElement binding, JsonElement[] sourceItems)
    {
        if (source.GetProperty("parentManifestSha256").GetString() != binding.GetProperty("parentManifestSha256").GetString())
            throw new InvalidDataException("Source/binding parent manifest mismatch.");
        var sourceById = sourceItems.ToDictionary(item => item.GetProperty("AuditItemId").GetString()!, StringComparer.Ordinal);
        var bindingItems = binding.GetProperty("items").EnumerateArray().ToArray();
        if (bindingItems.Length != sourceItems.Length) throw new InvalidDataException("Binding population size mismatch.");
        var bindingById = bindingItems.ToDictionary(item => item.GetProperty("AuditItemId").GetString()!, StringComparer.Ordinal);
        if (bindingById.Count != bindingItems.Length || sourceById.Count != sourceItems.Length) throw new InvalidDataException("Duplicate frozen audit identity.");
        if (!sourceById.Keys.OrderBy(x => x).SequenceEqual(bindingById.Keys.OrderBy(x => x))) throw new InvalidDataException("Source/binding occurrence identity mismatch.");
        foreach (var id in sourceById.Keys)
        {
            var sourceItem = sourceById[id]; var bindingItem = bindingById[id];
            if (sourceItem.GetProperty("documentSha256").GetString() != bindingItem.GetProperty("documentSha256").GetString())
                throw new InvalidDataException($"Source/binding document identity mismatch: {id}");
            var sourceLines = sourceItem.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()).ToArray();
            var bindingLines = bindingItem.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()).ToArray();
            if (!sourceLines.SequenceEqual(bindingLines)) throw new InvalidDataException($"Source/binding source-line identity mismatch: {id}");
        }
        return bindingById;
    }

    private static HumanResultItem ReadResult(JsonElement item)
    {
        var allowed = new HashSet<string>(["auditItemId", "humanLabel", "reviewerNote"], StringComparer.Ordinal);
        if (item.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new InvalidDataException("Reviewer result leaks a non-review field.");
        var label = item.TryGetProperty("humanLabel", out var raw) && raw.ValueKind != JsonValueKind.Null ? raw.GetString() : null;
        if (label is not null && !Labels.Contains(label)) throw new InvalidDataException($"Unknown human label: {label}");
        return new HumanResultItem(item.GetProperty("auditItemId").GetString()!, label,
            item.TryGetProperty("reviewerNote", out var note) && note.ValueKind != JsonValueKind.Null ? note.GetString() : null);
    }

    private static JoinedItem Join(HumanResultItem result, JsonElement source, JsonElement binding) => new(
        result.AuditItemId,
        new AuditIdentity(source.GetProperty("documentSha256").GetString()!, source.GetProperty("page").GetInt32(),
            source.GetProperty("sourceLineIds").EnumerateArray().Select(x => x.GetString()!).ToArray(), source.GetProperty("sourceSpan").Clone()),
        result.HumanLabel, binding.GetProperty("silverLabel").GetString()!, binding.GetProperty("silverConfidence").GetString()!,
        binding.GetProperty("selectionReason").GetString()!, binding.GetProperty("documentId").GetString()!);

    private static AgreementMetrics BuildMetrics(IReadOnlyList<JoinedItem> rows) => new(
        Overall: Metrics(rows),
        BySilverLabel: rows.GroupBy(row => row.SilverLabel).OrderBy(group => group.Key).Select(group => new NamedMetrics(group.Key, Metrics(group.ToArray()))).ToArray(),
        MediumSubset: Metrics(rows.Where(row => row.SelectionReason == "all_medium_heading").ToArray()),
        RandomStratifiedByDocument: rows.Where(row => row.SelectionReason.StartsWith("stratified_", StringComparison.Ordinal))
            .GroupBy(row => row.DocumentId).OrderBy(group => group.Key).Select(group => new DocumentStratumMetrics(group.Key,
                Metrics(group.Where(row => row.SelectionReason == "stratified_heading").ToArray()),
                Metrics(group.Where(row => row.SelectionReason == "stratified_non_heading").ToArray()))).ToArray());

    private static LabelMetrics Metrics(IReadOnlyList<JoinedItem> rows)
    {
        var determinate = rows.Where(row => row.HumanLabel != Uncertain).ToArray();
        return new LabelMetrics(rows.Count, determinate.Length, rows.Count(row => row.HumanLabel == Heading),
            rows.Count(row => row.HumanLabel == NonHeading), rows.Count(row => row.HumanLabel == Uncertain),
            determinate.Length == 0 ? null : determinate.Count(row => row.HumanLabel == row.SilverLabel) / (double)determinate.Length);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record HumanResultItem(string AuditItemId, string? HumanLabel, string? ReviewerNote);
    private sealed record JoinedItem(string AuditItemId, AuditIdentity Identity, string? HumanLabel, string SilverLabel,
        string SilverConfidence, string SelectionReason, string DocumentId);
}

public sealed record HumanAuditAgreementReport(int SchemaVersion, string ArtifactKind, string ClaimStatus, string MetricName,
    int FrozenPopulation, bool Complete, AgreementMetrics? Metrics, IReadOnlyList<AuditDisagreement> Disagreements);
public sealed record AuditIdentity(string DocumentSha256, int Page, IReadOnlyList<string> SourceLineIds, JsonElement SourceSpan);
public sealed record AuditDisagreement(string AuditItemId, AuditIdentity Identity, string HumanLabel, string SilverLabel,
    string SilverConfidence, string AuditStratum, string Status);
public sealed record AgreementMetrics(LabelMetrics Overall, IReadOnlyList<NamedMetrics> BySilverLabel, LabelMetrics MediumSubset,
    IReadOnlyList<DocumentStratumMetrics> RandomStratifiedByDocument);
public sealed record LabelMetrics(int Total, int Determinate, int HumanHeading, int HumanNonHeading, int Uncertain, double? Agreement);
public sealed record NamedMetrics(string SilverLabel, LabelMetrics Metrics);
public sealed record DocumentStratumMetrics(string DocumentId, LabelMetrics Heading, LabelMetrics NonHeading);
