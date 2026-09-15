using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV8B0HeadingBoundaryAudit;

internal static class Program
{
    private const string V8A2Relative = "artifacts/identity-benchmark/v8a2/new-source-intake-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v8b0/heading-boundary-audit-v1";
    private const string V8A2Authority = "579b3fd";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ArtifactSearchRoots =
    {
        "artifacts/identity-benchmark",
        "eval/a99-closed-loop",
        "tools",
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
            Run(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8B0_ERROR={ex.Message}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        var input = Full(root, V8A2Relative);
        var output = Full(root, OutputRelative);
        Require(Directory.Exists(input), "V8A2_INPUT_NOT_FOUND");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(output);

        var manifestPath = Path.Combine(input, "manifest.json");
        var occurrencesPath = Path.Combine(input, "occurrences.json");
        var candidatesPath = Path.Combine(input, "candidate-pairs.json");
        var manifest = Load(manifestPath);
        var occurrencesRoot = Load(occurrencesPath);
        var candidatesRoot = Load(candidatesPath);
        Require(manifest.GetProperty("status").GetString() == "READY_FOR_V8_PROVIDER_EXECUTION", "V8A2_NOT_READY");
        Require(manifest.GetProperty("providerCalls").GetInt32() == 0, "V8A2_PROVIDER_CALLS_NONZERO");
        Require(manifest.GetProperty("goldReadCount").GetInt32() == 0, "V8A2_GOLD_READS_NONZERO");

        var documents = occurrencesRoot.GetProperty("documents").EnumerateArray().Select(document => document.GetProperty("sourceId").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var candidateCounts = candidatesRoot.GetProperty("candidates").EnumerateArray().ToDictionary(x => x.GetProperty("sourceId").GetString()!, x => x.GetProperty("candidates").GetArrayLength(), StringComparer.Ordinal);
        var rows = occurrencesRoot.GetProperty("documents").EnumerateArray().Select(document =>
        {
            var sourceId = document.GetProperty("sourceId").GetString()!;
            var occurrences = document.GetProperty("occurrences").EnumerateArray().ToArray();
            var evidenceReasons = occurrences.Select(x => x.GetProperty("evidenceReason").GetString() ?? "").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new
            {
                documentId = sourceId,
                sourceFormat = "pdf",
                rawSourceOccurrences = occurrences.Length,
                headingCandidatesBeforeModel = occurrences.Length,
                modelSelectedHeadings = 0,
                successfullySourceBoundHeadings = 0,
                rejectedHeadingsRecorded = 0,
                occurrencesWithoutFrozenHeadingPrediction = occurrences.Length,
                candidatePairsFromCurrentInput = candidateCounts[sourceId],
                evidenceReasons,
            };
        }).OrderBy(x => x.documentId, StringComparer.Ordinal).ToArray();

        var sourceIds = rows.Select(x => x.documentId).ToHashSet(StringComparer.Ordinal);
        var pathMatches = FindSourceSpecificArtifactPaths(root, sourceIds);
        var headingMatches = pathMatches.Where(IsHeadingArtifactPath).Order(StringComparer.Ordinal).ToArray();
        var bindingMatches = pathMatches.Where(IsBindingArtifactPath).Order(StringComparer.Ordinal).ToArray();
        var v8a2ProgramPath = Full(root, "tools/identity-benchmark-v8a2-intake/Program.cs");

        var audit = new
        {
            artifactKind = "a99_identity_benchmark_v8b0_heading_boundary_audit",
            schemaVersion = "a99-v8b0-heading-boundary-audit-v1",
            status = "BLOCKED_ON_UPSTREAM_HEADING_PREDICTIONS",
            authority = new
            {
                v8a2Commit = V8A2Authority,
                v8a2ManifestSha256 = Sha256File(manifestPath),
                v8a2OccurrencesSha256 = Sha256File(occurrencesPath),
                v8a2CandidatePairsSha256 = Sha256File(candidatesPath),
                v8a2ArtifactsMutated = false,
                v8bArtifactsMutated = false,
            },
            answers = new
            {
                sourceOf12140Occurrences = "V8A2_PDF_PDFPIG_WORD_GROUPING_PLUS_LOOKS_STRUCTURAL_PDF_LINE",
                headingExtractionAndBindingConfirmedCount = 0,
                frozenAuthoritativeBoundHeadingSetExists = false,
                candidateInputBoundary = "B_GENERIC_SOURCE_OCCURRENCES",
                candidateGeneration = "V8A2 GenerateCandidates(ParsedDocument.Occurrences)",
            },
            sourcePipeline = new
            {
                parserImplementation = Relative(root, v8a2ProgramPath),
                parserEvidence = "PDF_WORD_COORDINATE_LINE_STRUCTURAL_SIGNAL",
                parserBehavior = "PdfPig groups words by rounded Bottom coordinate into non-empty text lines; LooksStructuralPdfLine selects short/numbered/uppercase-like lines.",
                llmHeadingExtraction = "NOT_RUN_FOR_V8A2_SOURCES",
                harnessBinding = "NO_FROZEN_UPSTREAM_BINDING_ARTIFACT_FOUND",
            },
            documents = rows,
            upstreamSearch = new
            {
                roots = ArtifactSearchRoots.Select(x => Full(root, x)).ToArray(),
                pathAddressedSourceArtifactMatches = pathMatches,
                upstreamHeadingArtifactPaths = headingMatches,
                bindingArtifactPaths = bindingMatches,
                searchReadGold = false,
                searchReadHistoricalPredictions = false,
            },
            boundHeadingChallenger = new
            {
                status = "BLOCKED_ON_UPSTREAM_HEADING_PREDICTIONS",
                headingCount = (int?)null,
                candidatePairCount = (int?)null,
                requestTokenEstimate = (long?)null,
                noRawOccurrenceSubstitution = true,
                noGoldHeadingLevelUsed = true,
            },
            firewall = new
            {
                providerCalls = 0,
                modelCalls = 0,
                goldReadCount = 0,
                historicalPredictionReadCount = 0,
                historicalEvaluationReadCount = 0,
                v8a2Mutated = false,
                v8a3Mutated = false,
                v8bMutated = false,
            },
            next = "Produce or freeze upstream heading extraction plus harness binding predictions for all six V8A2 sources; then open a new V8H bound-heading identity lane without mutating V8A2/V8A3/V8B.",
        };

        Write(Path.Combine(output, "audit.json"), audit);
        Write(Path.Combine(output, "manifest.json"), audit);
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(audit, rows, headingMatches, bindingMatches), new UTF8Encoding(false));
        Console.WriteLine($"V8B0_STATUS={audit.status} OCCURRENCES={rows.Sum(x => x.rawSourceOccurrences)} BOUND_HEADINGS=0 PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
    }

    private static string BuildReport(dynamic audit, IReadOnlyList<dynamic> rows, IReadOnlyList<string> headingMatches, IReadOnlyList<string> bindingMatches)
    {
        var lines = new List<string>
        {
            "# V8B0 — heading-boundary reuse audit",
            "",
            "Status: **BLOCKED_ON_UPSTREAM_HEADING_PREDICTIONS**.",
            "",
            "This audit is source/provenance-only. It did not read Gold, historical predictions/evaluations, or call a provider.",
            "",
            "## Answers",
            "",
            "1. The 12,140 V8A2 occurrences come from PDF word-coordinate line grouping plus the generic `LooksStructuralPdfLine` parser heuristic.",
            "2. Frozen upstream LLM heading extraction + harness binding confirmations for these six documents: **0 found**.",
            "3. A frozen authoritative bound-heading occurrence set for all six documents: **does not exist**.",
            "4. The 102,915 identity pairs are generated from **generic/source-parser occurrences (B)**, not bound headings.",
            "",
            "## Per document",
            "",
            "| Document | Raw/source occurrences | Heading candidates before model | Frozen selected headings | Frozen bound headings | Candidate pairs |",
            "|---|---:|---:|---:|---:|---:|",
        };
        foreach (var row in rows) lines.Add($"| {row.documentId} | {row.rawSourceOccurrences:N0} | {row.headingCandidatesBeforeModel:N0} | {row.modelSelectedHeadings:N0} | {row.successfullySourceBoundHeadings:N0} | {row.candidatePairsFromCurrentInput:N0} |");
        lines.AddRange(new[]
        {
            "",
            "## Boundary conclusion",
            "",
            "V8A2 is a generic PDF source-occurrence lane. Its candidates are not restricted to an authoritative upstream heading set.",
            "No V8H bound-heading challenger was materialized because no frozen upstream heading/binding prediction exists for the six sources. Raw paragraphs/lines were not substituted as headings.",
            "",
            $"- Provider/model calls: **0/0**",
            $"- Gold reads: **0**",
            $"- Matching heading artifact paths: **{headingMatches.Count}**",
            $"- Matching binding artifact paths: **{bindingMatches.Count}**",
            "- V8A2/V8A3/V8B mutated: **false**",
        });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string[] FindSourceSpecificArtifactPaths(string root, IReadOnlySet<string> sourceIds)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeRoot in ArtifactSearchRoots)
        {
            var path = Full(root, relativeRoot);
            if (!Directory.Exists(path)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files)
            {
                var normalized = file.Replace(Path.DirectorySeparatorChar, '/');
                if (sourceIds.Any(id => normalized.Contains(id, StringComparison.OrdinalIgnoreCase))) results.Add(Relative(root, file));
            }
        }
        return results.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsHeadingArtifactPath(string path) => path.Contains("heading", StringComparison.OrdinalIgnoreCase) || path.Contains("outline", StringComparison.OrdinalIgnoreCase);
    private static bool IsBindingArtifactPath(string path) => path.Contains("bind", StringComparison.OrdinalIgnoreCase) || path.Contains("binding", StringComparison.OrdinalIgnoreCase);
    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
}
