using System.Security.Cryptography;
using System.Text.Json;

// Source-byte inventory only: no Gold, parser predictions, model output, HTTP, or credentials.
if (args.Length != 3) throw new ArgumentException("Usage: P7CohortInventory <repo> <reference-pdf> <new-source-pool-json>");
var repo = Path.GetFullPath(args[0]);
var reference = Path.GetFullPath(args[1]);
var corpus = Path.Combine(repo, "todo10_8/heading_corpus_100");
if (!Directory.Exists(corpus) || !File.Exists(reference) || File.Exists(args[2])) throw new InvalidOperationException("p7-source-pool-input-or-output-invalid");
static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
var sources = Directory.EnumerateFiles(corpus, "*.pdf", SearchOption.AllDirectories).Select(path => new
{
    sourceKey = Path.GetRelativePath(repo, path).Replace('\\', '/'),
    fileName = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = HashFile(path),
    category = Path.GetRelativePath(corpus, path).Replace('\\', '/').Split('/')[0],
    role = "SOURCE_POOL_NOT_SELECTED_COHORT", priorExposure = "NOT_SCREENED_DO_NOT_CLAIM_HELD_OUT"
}).Append(new { sourceKey = "USER_REFERENCE_PDF", fileName = Path.GetFileName(reference), bytes = new FileInfo(reference).Length,
    sha256 = HashFile(reference), category = "USER_REFERENCE", role = "KNOWN_DEVELOPMENT_DIAGNOSTIC",
    priorExposure = "SOURCE_REVIEW_AND_PROVIDER_PREDICTIONS_ALREADY_OPEN" }).OrderBy(row => row.sourceKey, StringComparer.Ordinal).ToArray();
var canonical = JsonSerializer.SerializeToUtf8Bytes(sources);
var poolHash = Convert.ToHexStringLower(SHA256.HashData(canonical));
var report = new
{
    schemaVersion = "p7-d2-source-pool-inventory-1", status = "SOURCE_BYTES_PINNED_COHORT_FREEZE_OPEN",
    selectionBasis = "COMPLETE_EXISTING_REPO_PDF_POOL_PLUS_REFERENCE_NO_PREDICTION_OR_GOLD_FILTER",
    sourcePoolSha256 = poolHash, files = sources.Length, uniqueContentHashes = sources.Select(row => row.sha256).Distinct().Count(), sources,
    duplicateContentGroups = sources.GroupBy(row => row.sha256).Where(group => group.Count() > 1)
        .Select(group => new { sha256 = group.Key, sourceKeys = group.Select(row => row.sourceKey).ToArray() }).ToArray(),
    requiredShapes = new[] { "ADMINISTRATIVE_DOCUMENT", "MULTICOLUMN_TABLE", "BORDERLESS_TABLE", "MULTILINE_HEADING",
        "TWO_COLUMN_DOCUMENT", "NO_TABLE_DOCUMENT", "TRUE_DOCUMENT_HEADING_INSIDE_TABLE_CELL" }
        .Select(shape => new { shape, sourceOnlyCoverageReview = "OPEN_NO_FILE_NAME_INFERENCE", acceptedSourceCases = Array.Empty<string>() }).ToArray(),
    sourceSnapshots = "NOT_BUILT_FOR_POOL", cohortSelection = "OPEN", titleSubtitlePolicy = "SOURCE_ONLY_ADJUDICATION_REQUIRED",
    scoringAuthority = "NOT_FROZEN", stageRequests = "NOT_FROZEN_FOR_POOL", providerCallBudget = "NOT_ESTABLISHED",
    providerCalls = 0, authorizedProviderCalls = 0, goldRead = false, goldMutation = "NONE",
    productionRequestsChanged = false, runtimeChanged = false, providerExecution = "LOCKED", p7D3 = "LOCKED", p7E = "LOCKED"
};
var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
using (var stream = new FileStream(args[2], FileMode.CreateNew)) { stream.Write(bytes); stream.Flush(true); }
Console.WriteLine(JsonSerializer.Serialize(new { report.status, report.files, report.uniqueContentHashes, report.sourcePoolSha256,
    artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), providerCalls = 0 }));
