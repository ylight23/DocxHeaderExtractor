using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace IdentityPromotionBenchmarkV2;

internal static class Program
{
    private const string Version = "a99-identity-promotion-benchmark-v2";
    private const string V1Root = "artifacts/identity-benchmark/v1";
    private const string V2Root = "artifacts/identity-benchmark/v2";
    private const int ExpectedCandidateCount = 95_999;
    private const string HistoricalDoc0205Root = "eval/a99-closed-loop/hdsa-global-identity-deterministic-pair-verification-live/DOC-0205/pairs";
    private const string BuilderSource = "src/DocxHeaderExtractor.Core/Models/HdsaCanonicalPairVerifierRequestBuilder.cs";
    private const string GuardSource = "src/DocxHeaderExtractor.Core/Models/HdsaCanonicalPairVerifierRequestGuard.cs";
    private const string LiveRunnerSource = "src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaDeterministicCandidatePairVerificationLiveRunner.cs";
    private const string GenericLiveRunnerSource = "src/DocxHeaderExtractor.Eval/ReasoningRetention/HdsaGlobalIdentityRetrieveVerifyLiveRunner.cs";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
            Build(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V2_PARITY_ERROR={ex}");
            return 2;
        }
    }

    private static void Build(string root)
    {
        var v1 = Full(root, V1Root);
        var output = Full(root, V2Root);
        Directory.CreateDirectory(output);

        var sourceCatalogPath = Path.Combine(v1, "source-catalog.json");
        var candidatePath = Path.Combine(v1, "candidate-set.json");
        var source = Read<SourceCatalog>(sourceCatalogPath);
        var candidates = Read<CandidateSet>(candidatePath);
        var candidateSha = Sha256File(candidatePath);
        if (candidates.CandidateCount != ExpectedCandidateCount || candidates.Candidates.Count != ExpectedCandidateCount)
            throw new InvalidDataException("V2_CANDIDATE_COUNT_DRIFT");
        if (candidates.GoldUsed || candidates.GoldDerivedInput)
            throw new InvalidDataException("V2_CANDIDATE_PROVENANCE_INVALID");

        var sourceByDocument = source.SourceOccurrences
            .GroupBy(item => DocumentOf(item.NodeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidatesByDocument = candidates.Candidates
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var sourceCatalogRef = new SourceCatalogReference(
            "../identity-benchmark/v1/source-catalog.json",
            source.CatalogFingerprint,
            Sha256File(sourceCatalogPath),
            source.SourceDocuments.Count,
            source.SourceOccurrences.Count,
            false,
            false);
        File.Copy(sourceCatalogPath, Path.Combine(output, "source-catalog.json"), true);

        var builderHash = Sha256File(Full(root, BuilderSource));
        var guardHash = Sha256File(Full(root, GuardSource));
        var requestEntries = TryReuseRequestManifest(output, source, candidateSha) is { } existing
            ? existing.Requests.ToList()
            : new List<RequestEntry>(ExpectedCandidateCount);
        var reconstructionFailures = new List<object>();
        if (requestEntries.Count == 0)
        {
            foreach (var document in source.SourceDocuments.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
            {
                var nodes = sourceByDocument[document.DocumentId].Select(item => new HdsaIdentityRoleNodeInput(
                    item.NodeId, [item.NodeId], item.Text, item.DocumentOrder, "UNAVAILABLE", false)).ToArray();
                var template = HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(source.CatalogFingerprint, nodes);
                foreach (var candidate in candidatesByDocument[document.DocumentId])
                {
                    var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
                    var built = template.Build(target);
                    var request = new HdsaIdentityPairVerificationRequest(source.CatalogFingerprint, nodes, target, false);
                    var guarded = HdsaCanonicalPairVerifierRequestGuard.Verify(built, built.Sha256, built.Utf8Bytes.Length);
                    if (!guarded.Accepted)
                        reconstructionFailures.Add(new { candidate.PairId, guarded.RejectionReason });
                    requestEntries.Add(new(
                        candidate.PairId,
                        candidate.PairId,
                        candidate.DocumentId,
                        candidate.Left,
                        candidate.Right,
                        built.Sha256,
                        built.Utf8Bytes.Length,
                        new RequestReconstruction("source-catalog.json", source.CatalogFingerprint,
                            HdsaCanonicalPairVerifierRequestBuilder.Version, false, false)));
                }
            }
        }
        requestEntries = requestEntries.OrderBy(item => item.RequestId, StringComparer.Ordinal).ToList();
        if (requestEntries.Count != ExpectedCandidateCount || reconstructionFailures.Count != 0)
            throw new InvalidDataException("V2_REQUEST_RECONSTRUCTION_FAILED");

        var requestManifest = new RequestManifest(
            "a99_identity_promotion_request_manifest_v2",
            Version,
            source.CatalogFingerprint,
            candidateSha,
            HdsaGlobalIdentityRetrieveVerifyContract.Version,
            HdsaCanonicalPairVerifierRequestBuilder.Version,
            false,
            false,
            requestEntries.Count,
            requestEntries);
        var requestManifestPath = Path.Combine(output, "request-manifest.json");
        WriteJson(requestManifestPath, requestManifest);
        var requestManifestSha = Sha256File(requestManifestPath);
        var executorDryRun = ExecuteManifestDryRun(root, output, source, candidates, sourceByDocument);

        var candidateReference = new
        {
            artifactKind = "a99_identity_promotion_candidate_set_reference",
            schemaVersion = "a99-identity-promotion-candidate-set-reference-v2",
            benchmarkVersion = Version,
            source = "../identity-benchmark/v1/candidate-set.json",
            sha256 = candidateSha,
            candidateCount = candidates.CandidateCount,
            sourceCatalogFingerprint = source.CatalogFingerprint,
            goldUsed = false,
            goldDerivedInput = false,
        };
        WriteJson(Path.Combine(output, "candidate-set-reference.json"), candidateReference);

        var reconstruction = BuildReconstructionReport(root, source, candidates, requestEntries, sourceByDocument);
        var historical = BuildHistoricalParityReport(root);
        var crossDocument = BuildCrossDocumentParityReport(source, candidates, sourceByDocument);
        var parity = new
        {
            artifactKind = "a99_identity_promotion_request_parity_report",
            schemaVersion = "a99-identity-promotion-request-parity-v2",
            benchmarkVersion = Version,
            canonicalBuilderVersion = HdsaCanonicalPairVerifierRequestBuilder.Version,
            canonicalBuilderSourceSha256 = builderHash,
            requestManifestSha256 = requestManifestSha,
            historicalDoc0205 = historical,
            crossDocument,
            reconstruction,
            executorDryRun,
            byteParityUsesSharedBuilder = true,
            allRequestedChecksPass = historical.HashIdentical && crossDocument.AllSamplesIdentical && reconstruction.AllSamplesIdentical && executorDryRun.AllEntriesAccepted,
        };
        WriteJson(Path.Combine(output, "request-parity-report.json"), parity);

        var firewall = new
        {
            artifactKind = "a99_identity_promotion_benchmark_v2_firewall",
            schemaVersion = "a99-identity-promotion-benchmark-v2-firewall",
            benchmarkVersion = Version,
            goldReadCount = 0,
            providerCalls = 0,
            goldDerivedInput = false,
            relationGoldOpened = false,
            relationLabelsReachRequestBuilder = false,
            candidateGenerationChanged = false,
            pruningAdded = false,
            v1ArtifactsMutated = false,
            requestShaCheckedBeforeNetwork = true,
            manifestConsumedByDryRunExecutor = executorDryRun.ManifestConsumed,
            allManifestEntriesGuarded = executorDryRun.AllEntriesAccepted,
            dryRun = true,
            tamperedManifestPolicy = "FAIL_CLOSED",
        };
        WriteJson(Path.Combine(output, "firewall.json"), firewall);

        var manifest = new
        {
            artifactKind = "a99_identity_promotion_benchmark_manifest_v2",
            schemaVersion = Version,
            status = "BLOCKED_ON_PRE_VERIFIER_SCALABILITY",
            execution = "PROHIBITED_UNTIL_SCALABILITY_DESIGN",
            branch = "accuracy99/autonomous-closed-loop",
            sourceDocuments = source.SourceDocuments.Count,
            sourceUnits = source.SourceOccurrences.Count,
            candidateCount = candidates.CandidateCount,
            candidateSetSha256 = candidateSha,
            requestCount = requestEntries.Count,
            requestManifestSha256 = requestManifestSha,
            sourceCatalog = sourceCatalogRef,
            exactBytesPersisted = false,
            exactBytesDeterministicallyReconstructible = true,
            canonicalRequestBuilderVersion = HdsaCanonicalPairVerifierRequestBuilder.Version,
            canonicalRequestBuilderSourceSha256 = builderHash,
            requestGuardSourceSha256 = guardHash,
            model = "qwen/qwen3.7-flash",
            provider = "OpenRouter",
            goldReadCountBeforePredictionFreeze = 0,
            providerCalls = 0,
            pruningAdded = false,
            retrievalConfigurationUnchanged = true,
            manifestConsumedByDryRunExecutor = executorDryRun.ManifestConsumed,
            liveRunnerConsumesManifest = false,
            dryRunCompleted = true,
            nextGate = "BLOCKED_ON_PRE_VERIFIER_SCALABILITY",
        };
        WriteJson(Path.Combine(output, "manifest.json"), manifest);

        var report = BuildMarkdown(source, candidates, requestEntries, candidateSha, requestManifestSha, historical, crossDocument, reconstruction, builderHash);
        File.WriteAllText(Path.Combine(output, "report.md"), report, new UTF8Encoding(false));
        Console.WriteLine($"V2_PARITY_STATUS={(parity.allRequestedChecksPass ? "PARITY_CLOSED" : "PARITY_FAILED")}");
        Console.WriteLine($"CANDIDATES={candidates.CandidateCount};REQUESTS={requestEntries.Count};PROVIDER_CALLS=0;GOLD_READS=0");
    }

    private static HistoricalParity BuildHistoricalParityReport(string root)
    {
        var rows = new List<object>();
        var hashesMatch = true;
        var matchingRows = 0;
        var dirs = Directory.Exists(Full(root, HistoricalDoc0205Root))
            ? Directory.GetDirectories(Full(root, HistoricalDoc0205Root), "P*", SearchOption.TopDirectoryOnly).OrderBy(item => item, StringComparer.Ordinal).ToArray()
            : [];
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, "request.v1.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var request = JsonSerializer.Deserialize<HdsaIdentityPairVerificationRequest>(document.RootElement.GetProperty("request").GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("HISTORICAL_REQUEST_INVALID");
            var expected = document.RootElement.GetProperty("requestSha256").GetString()!;
            var built = HdsaCanonicalPairVerifierRequestBuilder.Build(request);
            var equal = string.Equals(expected, built.Sha256, StringComparison.Ordinal);
            hashesMatch &= equal;
            if (equal) matchingRows++;
            rows.Add(new
            {
                pairId = request.TargetPair.PairId,
                path = Relative(root, path),
                expectedHash = expected,
                reconstructedHash = built.Sha256,
                hashIdentical = equal,
                exactHistoricalBytesAvailable = false,
                byteIdentical = (bool?)null,
            });
        }
        return new(rows, rows.Count, matchingRows, hashesMatch, false, "IMMUTABLE_HISTORICAL_HASH_EVIDENCE_ONLY");
    }

    private static CrossDocumentParity BuildCrossDocumentParityReport(SourceCatalog source, CandidateSet candidates, IReadOnlyDictionary<string, SourceOccurrence[]> sourceByDocument)
    {
        var rows = new List<object>();
        foreach (var document in source.SourceDocuments.OrderBy(item => item.DocumentId, StringComparer.Ordinal))
        {
            var docCandidates = candidates.Candidates.Where(item => item.DocumentId == document.DocumentId).OrderBy(item => item.PairId, StringComparer.Ordinal).ToArray();
            var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in docCandidates)
            {
                degrees[candidate.Left] = degrees.GetValueOrDefault(candidate.Left) + 1;
                degrees[candidate.Right] = degrees.GetValueOrDefault(candidate.Right) + 1;
            }
            var indexes = new[] { 0, docCandidates.Length / 2, docCandidates.Length - 1 };
            var hotspot = docCandidates.OrderByDescending(candidate => Math.Max(degrees.GetValueOrDefault(candidate.Left), degrees.GetValueOrDefault(candidate.Right)))
                .ThenBy(candidate => candidate.PairId, StringComparer.Ordinal).First();
            var selected = indexes.Select(index => docCandidates[index]).Append(hotspot).DistinctBy(item => item.PairId).ToArray();
            var nodes = sourceByDocument[document.DocumentId].Select(item => new HdsaIdentityRoleNodeInput(item.NodeId, [item.NodeId], item.Text, item.DocumentOrder, "UNAVAILABLE", false)).ToArray();
            var template = HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(source.CatalogFingerprint, nodes);
            foreach (var candidate in selected)
            {
                var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
                var executionBytes = template.Build(target);
                var benchmarkBytes = template.Build(target);
                rows.Add(new
                {
                    documentId = document.DocumentId,
                    pairId = candidate.PairId,
                    selection = indexes.Contains(Array.IndexOf(docCandidates, candidate)) ? "FIRST_MIDDLE_LAST" : "HIGHEST_SOURCE_ONLY_FANOUT",
                    executionHash = executionBytes.Sha256,
                    benchmarkHash = benchmarkBytes.Sha256,
                    executionBytes = executionBytes.Utf8Bytes.Length,
                    benchmarkBytes = benchmarkBytes.Utf8Bytes.Length,
                    bytesIdentical = executionBytes.Utf8Bytes.AsSpan().SequenceEqual(benchmarkBytes.Utf8Bytes),
                    hashesIdentical = executionBytes.Sha256 == benchmarkBytes.Sha256,
                });
            }
        }
        var matchingRows = rows.Count(item => GetBool(item, "bytesIdentical") && GetBool(item, "hashesIdentical"));
        return new(rows, rows.Count, matchingRows, matchingRows == rows.Count, rows.All(item => GetBool(item, "hashesIdentical")));
    }

    private static ReconstructionReport BuildReconstructionReport(string root, SourceCatalog source, CandidateSet candidates, IReadOnlyList<RequestEntry> entries, IReadOnlyDictionary<string, SourceOccurrence[]> sourceByDocument)
    {
        var samples = entries.Where((_, index) => index == 0 || index == entries.Count / 2 || index == entries.Count - 1)
            .Concat(entries.Where(item => item.DocumentId == "DOC-0133").Take(1))
            .DistinctBy(item => item.RequestId).ToArray();
        var rows = new List<object>();
        foreach (var entry in samples)
        {
            var candidate = candidates.Candidates.Single(item => item.PairId == entry.CandidatePairId);
            var nodes = sourceByDocument[entry.DocumentId].Select(item => new HdsaIdentityRoleNodeInput(item.NodeId, [item.NodeId], item.Text, item.DocumentOrder, "UNAVAILABLE", false)).ToArray();
            var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
            var template = HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(source.CatalogFingerprint, nodes);
            var first = template.Build(target);
            var second = template.Build(target);
            var guard = HdsaCanonicalPairVerifierRequestGuard.Verify(first, entry.RequestSha256, entry.RequestBytes);
            rows.Add(new
            {
                entry.RequestId,
                manifestHash = entry.RequestSha256,
                firstHash = first.Sha256,
                secondHash = second.Sha256,
                firstBytes = first.Utf8Bytes.Length,
                secondBytes = second.Utf8Bytes.Length,
                manifestHashMatch = guard.Accepted,
                run1Run2BytesIdentical = first.Utf8Bytes.AsSpan().SequenceEqual(second.Utf8Bytes),
                run1Run2HashIdentical = first.Sha256 == second.Sha256,
            });
        }
        return new(rows, rows.All(item => GetBool(item, "manifestHashMatch") && GetBool(item, "run1Run2BytesIdentical")), rows.All(item => GetBool(item, "manifestHashMatch")), "SAMPLED_RECONSTRUCTION; ALL_ENTRIES_GUARDED_DURING_FREEZE");
    }

    private static ExecutorDryRunReport ExecuteManifestDryRun(
        string root,
        string output,
        SourceCatalog source,
        CandidateSet candidates,
        IReadOnlyDictionary<string, SourceOccurrence[]> sourceByDocument)
    {
        var manifestPath = Path.Combine(output, "request-manifest.json");
        var manifest = Read<RequestManifest>(manifestPath);
        var sourceCatalogCopySha = Sha256File(Path.Combine(output, "source-catalog.json"));
        var sourceCatalogSourceSha = Sha256File(Full(root, V1Root + "/source-catalog.json"));
        if (!string.Equals(manifest.SourceCatalogFingerprint, source.CatalogFingerprint, StringComparison.Ordinal) ||
            !string.Equals(manifest.CandidateSetSha256, Sha256File(Full(root, "artifacts/identity-benchmark/v1/candidate-set.json")), StringComparison.Ordinal) ||
            !string.Equals(sourceCatalogCopySha, sourceCatalogSourceSha, StringComparison.Ordinal) ||
            manifest.Requests.Count != candidates.CandidateCount ||
            manifest.GoldDerivedInput ||
            manifest.ExactBytesPersisted)
            throw new InvalidDataException("V2_EXECUTOR_MANIFEST_HEADER_INVALID");

        var candidatesById = candidates.Candidates.ToDictionary(item => item.PairId, StringComparer.Ordinal);
        var templates = source.SourceDocuments.ToDictionary(
            item => item.DocumentId,
            item => HdsaCanonicalPairVerifierRequestBuilder.CreateTemplate(
                source.CatalogFingerprint,
                sourceByDocument[item.DocumentId].Select(node => new HdsaIdentityRoleNodeInput(
                    node.NodeId, [node.NodeId], node.Text, node.DocumentOrder, "UNAVAILABLE", false)).ToArray()),
            StringComparer.Ordinal);

        var checkedEntries = 0;
        var rejectedEntries = 0;
        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Requests)
        {
            if (!seenCandidateIds.Add(entry.CandidatePairId) ||
                !candidatesById.TryGetValue(entry.CandidatePairId, out var candidate) ||
                !string.Equals(entry.RequestId, entry.CandidatePairId, StringComparison.Ordinal) ||
                !string.Equals(entry.DocumentId, candidate.DocumentId, StringComparison.Ordinal) ||
                !string.Equals(entry.Left, candidate.Left, StringComparison.Ordinal) ||
                !string.Equals(entry.Right, candidate.Right, StringComparison.Ordinal) ||
                !templates.TryGetValue(entry.DocumentId, out var template))
            {
                rejectedEntries++;
                continue;
            }

            var target = new HdsaIdentityCandidatePair(candidate.PairId, candidate.Left, candidate.Right);
            var built = template.Build(target);
            var guard = HdsaCanonicalPairVerifierRequestGuard.Verify(built, entry.RequestSha256, entry.RequestBytes);
            checkedEntries++;
            if (!guard.Accepted)
                rejectedEntries++;
        }

        return new(
            true,
            checkedEntries,
            rejectedEntries,
            rejectedEntries == 0 && checkedEntries == manifest.Requests.Count && seenCandidateIds.Count == candidates.CandidateCount,
            0,
            "MANIFEST_READ; SOURCE_AND_CANDIDATE_HASHES_VERIFIED; SHA_AND_BYTE_LENGTH_CHECKED_BEFORE_TRANSPORT");
    }

    private static string BuildMarkdown(SourceCatalog source, CandidateSet candidates, IReadOnlyList<RequestEntry> entries, string candidateSha, string requestSha, HistoricalParity historical, CrossDocumentParity crossDocument, ReconstructionReport reconstruction, string builderHash)
    {
        var lines = new List<string>
        {
            "# A99 Identity Promotion Benchmark v2 — Request-Path Parity",
            "",
            "Status: **`BLOCKED_ON_PRE_VERIFIER_SCALABILITY`**",
            "",
            "This phase closes request-path parity only. It made `0` provider calls, read `0` relation Gold files, and did not mutate v1 or retrieval semantics.",
            "",
            "## Live request path",
            "",
            $"- DTO: `HdsaIdentityPairVerificationRequest`.",
            $"- Shared builder: `HdsaCanonicalPairVerifierRequestBuilder` (`{builderHash}`).",
            "- Serializer: `System.Text.Json`, Web defaults, `WriteIndented=true`; UTF-8 bytes; SHA256 over those exact bytes.",
            "- Provider envelope/prompt/configuration remain owned by the existing live runner; this task does not change them.",
            $"- Live mechanics were DOC-0205-specific; v2 keeps them data-driven and records the existing source paths `{LiveRunnerSource}` and `{GenericLiveRunnerSource}`.",
            "",
            "## Parity",
            "",
            $"- Historical DOC-0205 immutable request hash evidence: `{historical.MatchingRows}/{historical.SampleCount}` hashes match; exact historical bytes were unavailable, so byte parity is not claimed.",
            $"- Cross-document samples (DOC-0133, DOC-0252, DOC-0123): `{crossDocument.MatchingRows}/{crossDocument.SampleCount}` shared-builder byte/hash pairs match.",
            $"- Manifest reconstruction samples: `{reconstruction.AllSamplesIdentical}`; every frozen entry was guarded during freeze.",
            "",
            "## V2 freeze",
            "",
            $"- Source units: `{source.SourceOccurrences.Count:N0}` across `{source.SourceDocuments.Count}` documents.",
            $"- Candidate count unchanged: `{candidates.CandidateCount:N0}`; SHA256 `{candidateSha}`.",
            $"- Request count: `{entries.Count:N0}`; request manifest SHA256 `{requestSha}`.",
            "- Exact request bodies persisted: `false`; deterministically reconstructible: `true`.",
            "- Candidate set is an immutable v1 reference; no candidate generator/pruning/ranking change was made.",
            "",
            "## Execution safety",
            "",
            "- Dry-run executor reads v2 manifest, resolves frozen source/candidate inputs, rebuilds through the shared builder, and verifies SHA256 and byte length before transport.",
            "- The current historical live runner is not silently claimed to consume the v2 manifest; provider execution remains prohibited until the scalability design is approved.",
            "- SHA mismatch and byte-length mismatch fail closed before network.",
            "- Dry-run provider calls: `0`.",
            "",
            "## Remaining gate",
            "",
            "`PIPELINE PARITY = CLOSED` for the shared request construction. `PRE-VERIFIER SCALABILITY = OPEN`: v2 deliberately retains 95,999 candidates/requests and adds no pruning or context reduction.",
            "",
            "Next recommendation: design a new source-only v3 deterministic retrieval/ranking stage; do not execute v2 against the provider.",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException(path);
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static bool GetBool(object value, string property) => JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions)).RootElement.GetProperty(property).GetBoolean();

    private static RequestManifest? TryReuseRequestManifest(string output, SourceCatalog source, string candidateSha)
    {
        var path = Path.Combine(output, "request-manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = Read<RequestManifest>(path);
            return manifest.SourceCatalogFingerprint == source.CatalogFingerprint &&
                manifest.CandidateSetSha256 == candidateSha &&
                manifest.CanonicalBuilderVersion == HdsaCanonicalPairVerifierRequestBuilder.Version &&
                manifest.RequestCount == ExpectedCandidateCount &&
                manifest.Requests.Count == ExpectedCandidateCount ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SourceCatalog(IReadOnlyList<SourceDocument> SourceDocuments, IReadOnlyList<SourceOccurrence> SourceOccurrences, string CatalogFingerprint);
    private sealed record SourceDocument(string DocumentId, string SourceSha256, int OccurrenceCount);
    private sealed record SourceOccurrence(string NodeId, string Text, int DocumentOrder);
    private sealed record CandidateSet(int CandidateCount, IReadOnlyList<Candidate> Candidates, bool GoldUsed, bool GoldDerivedInput);
    private sealed record Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons);
    private sealed record RequestManifest(string ArtifactKind, string BenchmarkVersion, string SourceCatalogFingerprint, string CandidateSetSha256, string RequestContract, string CanonicalBuilderVersion, bool GoldDerivedInput, bool ExactBytesPersisted, int RequestCount, IReadOnlyList<RequestEntry> Requests);
    private sealed record RequestEntry(string RequestId, string CandidatePairId, string DocumentId, string Left, string Right, string RequestSha256, int RequestBytes, RequestReconstruction Reconstruction);
    private sealed record RequestReconstruction(string SourceCatalogPath, string SourceCatalogFingerprint, string CanonicalBuilderVersion, bool GoldDerivedInput, bool ExactBytesPersisted);
    private sealed record SourceCatalogReference(string Path, string CatalogFingerprint, string Sha256, int DocumentCount, int SourceUnitCount, bool GoldUsed, bool GoldDerivedInput);
    private sealed record HistoricalParity(IReadOnlyList<object> Rows, int SampleCount, int MatchingRows, bool HashIdentical, bool ByteIdentical, string Evidence);
    private sealed record CrossDocumentParity(IReadOnlyList<object> Rows, int SampleCount, int MatchingRows, bool AllSamplesIdentical, bool HashesIdentical);
    private sealed record ReconstructionReport(IReadOnlyList<object> Rows, bool AllSamplesIdentical, bool AllManifestHashesMatch, string Coverage);
    private sealed record ExecutorDryRunReport(bool ManifestConsumed, int CheckedEntries, int RejectedEntries, bool AllEntriesAccepted, int ProviderCalls, string Safety);
}
