using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// P6R is an experimental, Gold-free semantic qualification lane. It changes only P6P's question
/// from local heading classification to owner-versus-representation resolution using deterministic
/// source-text correspondence. It is never connected to the shared runtime.
/// </summary>
internal static class P6RStructuralIdentityResolutionQualification
{
    private const string ArtifactRoot = "artifacts/v5-p6r-structural-identity-resolution";
    private const string Confirm = "yes-i-authorize-p6r-structural-identity-full31";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private sealed record Prepared(string RelativePath, PdfStructuralIdentityResolutionDocumentPlan Plan);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath)) return Fail("P6R result already exists; stop before network");
        var prepared = Prepare(repo);
        var rows = Rows(prepared);
        if (rows.Length != 31 || prepared.Sum(item => item.Plan.SourcePlan.SourceOccurrenceTotal) != 2_884)
            return Fail("P6R expected exactly 31 P05 packs and 2,884 owned source occurrences");
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(directory);
            WriteNew(manifestPath, new
            {
                schemaVersion = "v5-p6r-structural-identity-resolution-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
                preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE", productionPromotion = false,
                sourceAuthority = new { sourceBuilder = "PdfStructuredSourceAuthorityBuilder", packingPolicy = PdfHeadingMembershipProductionAdapter.PackingPolicy,
                    totalPacks = 31, ownedOccurrenceTotal = 2884, exactOnceOwnership = true, correspondence = "deterministic NFKC+whitespace exact then whitespace-insensitive source-text matching; no Gold" },
                treatment = new
                {
                    model = "qwen/qwen3.7-flash", provider = "Alibaba", providerPin = "alibaba", temperature = 0,
                    reasoning = new { enabled = true }, reasoningEffort = "OMITTED", ontologyPrompt = false, layoutFacts = false,
                    promptProtocol = V5FreeHeadingCandidateProtocolV1.PdfStructuralIdentityResolutionVersion,
                    systemPromptSha256 = Hash(V5FreeHeadingCandidateProtocolV1.StructuralIdentityResolutionSystemPrompt),
                    baseline = "P6P text-only document context, same P05 packing, same local locator binder",
                    addition = "read-only D# correspondence targets and C# context evidence; headings plus representations output",
                    semanticRegionLabels = false, contextOnlySelectable = false, contextOnlyLocatorHandlesIssued = false,
                    repair = false, semanticFallback = false, goldDuringRun = false,
                },
                route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0,
                    reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object", streaming = true, usageInclude = true,
                    fallbacks = false, outputTokenCeiling = PdfHeadingMembershipProductionAdapter.CompletionTokenCeiling, responseUtf8ByteCap = ResponseCap },
                executionGate = new { primaryLogicalCalls = 31, automaticRetryOnlyForFrozenProductionTransientTransportPolicy = true,
                    requiresExplicitAuthorization = true, goldRead = false, repair = false, fallback = false, sharedRuntime = "UNCHANGED" },
                rows,
            });
            Console.WriteLine("P6R provider-free manifest frozen: 31 P05 bodies; ProviderCalls=0; GoldRead=false.");
            return 0;
        }
        if (!ManifestParity(manifestPath, rows)) return Fail("P6R manifest/body parity failed; stop before network");
        if (!args.Contains($"--confirm-p6r-structural-identity-full31={Confirm}"))
        {
            Console.WriteLine("P6R PREPARED_NOT_AUTHORIZED; ProviderCalls=0; GoldRead=false.");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("P6R authorized but OPENROUTER_API_KEY is not set");
        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash"; options.OpenRouterProviderRoute = "alibaba"; options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true; options.MaxOutputTokens = PdfHeadingMembershipProductionAdapter.CompletionTokenCeiling;
        options.ProviderTransportTimeoutSeconds = 300; options.MaxParallelRequests = 1; options.Validate();
        var accepted = RecoverCheckpoint(checkpointPath);
        if (!CheckpointParity(accepted, rows)) return Fail("P6R checkpoint/request/raw-hash parity failed; stop before network");
        var completed = accepted.Select(row => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}").ToHashSet(StringComparer.Ordinal);
        if (accepted.Count == 0) AtomicWrite(checkpointPath, new { schemaVersion = "v5-p6r-checkpoint-v1", rows = accepted, goldRead = false });
        else Console.WriteLine($"P6R recovered {accepted.Count}/31 immutable completed rows; resumes without resending them.");
        foreach (var document in prepared)
        foreach (var preparedPack in document.Plan.Packs)
        {
            var pack = preparedPack.Pack; var watch = Stopwatch.StartNew(); FrozenHeaderExecutionResult? provider = null;
            V5FreeHeadingCandidateProtocolV1.StructuralIdentityResolutionResult? parsed = null; string? error = null;
            if (completed.Contains($"{pack.DocumentId}|{pack.PackId}")) continue;
            try
            {
                using var executor = OpenRouterQualificationTransport.CreateOwned(options);
                provider = await executor.ExecuteFrozenRequestAsync(pack.ProviderBody, pack.MaxCompletionTokens, pack.Request.SystemPrompt, pack.Request.UserMessage).ConfigureAwait(false);
                if (!string.Equals(provider.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                    parsed = PdfHeadingMembershipProductionAdapter.ParseStructuralIdentityResolution(preparedPack, provider.Content, ResponseCap);
                else error = "finish-reason-length";
            }
            catch (Exception exception) { error = exception.Message; }
            watch.Stop();
            var headings = parsed?.Headings;
            var row = new
            {
                documentId = pack.DocumentId, packOrdinal = pack.PackOrdinal, packId = pack.PackId,
                ownedOccurrenceCount = pack.OwnedAliases.Count, visibleOccurrenceCount = pack.VisibleAliases.Count,
                sourceSha256 = document.Plan.SourcePlan.SourceSha256, sourceUniverseSha256 = document.Plan.SourcePlan.SourceUniverseSha256,
                locatorRegistryFingerprint = pack.Registry.Fingerprint, userMessageSha256 = pack.Request.UserMessageSha256,
                providerRequestHash = pack.ProviderRequestHash, providerRequestBytes = pack.ProviderRequestBytes, maxCompletionTokens = pack.MaxCompletionTokens,
                transportAccepted = provider is not null, transportError = provider is null ? error : null, finishReason = provider?.FinishReason,
                usage = provider?.Usage, retryCount = provider?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds, sseEventCount = provider?.SseEventCount ?? 0,
                rawSse = provider?.RawSse, rawSseSha256 = provider is null ? null : Hash(provider.RawSse), rawContent = provider?.Content,
                rawContentSha256 = provider is null ? null : Hash(provider.Content), rawContentUtf8Bytes = provider is null ? 0 : Encoding.UTF8.GetByteCount(provider.Content),
                parserAccepted = parsed is not null, parseError = error, emittedHeadings = headings?.Response.Occurrences.Count ?? 0,
                boundHeadings = headings?.Response.Occurrences.Count ?? 0, headingQuarantine = headings?.Quarantined,
                emittedRepresentations = parsed?.RepresentationsAccepted ?? 0, representationQuarantine = parsed?.RepresentationQuarantine,
                contractValid = provider is not null && string.Equals(provider.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) && parsed is not null &&
                    headings!.Quarantined.Count == 0 && parsed.RepresentationQuarantine.Count == 0,
            };
            accepted.Add(JsonSerializer.SerializeToElement(row)); AtomicWrite(checkpointPath, new { schemaVersion = "v5-p6r-checkpoint-v1", rows = accepted, goldRead = false });
            Console.WriteLine($"[P6R {pack.DocumentId} {pack.PackId}] transport={provider is not null} finish={provider?.FinishReason ?? "n/a"} heading={headings?.Response.Occurrences.Count ?? 0} representation={parsed?.RepresentationsAccepted ?? 0} quarantine={(headings?.Quarantined.Count ?? 0) + (parsed?.RepresentationQuarantine.Count ?? 0)} ms={watch.ElapsedMilliseconds}");
        }
        WriteNew(resultPath, new { schemaVersion = "v5-p6r-structural-identity-resolution-result-v1", sourceManifest = $"{ArtifactRoot}/execution-manifest.v1.json",
            head = GitHead(repo), logicalProviderCalls = accepted.Count, maximumPrimaryLogicalCalls = 31, completedPrimaryPacks = accepted.Count,
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, reasoningEffort = "OMITTED" },
            repair = false, fallback = false, goldRead = false, goldMutation = "NONE", semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", rows = accepted });
        File.Delete(checkpointPath);
        Console.WriteLine("P6R execution complete; raw outputs persisted. Gold remains unread and provider gate is closed.");
        return 0;
    }

    /// <summary>Freezes immutable raw provider evidence before any separately authorized Gold audit.</summary>
    public static int FreezeResponseHashes(string repo)
    {
        var directory = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(directory, "result.v1.json");
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var freezePath = Path.Combine(directory, "response-hash-freeze.v1.json");
        if (!File.Exists(resultPath) || !File.Exists(manifestPath)) return Fail("P6R result/manifest missing; no hash freeze");
        if (File.Exists(freezePath)) return Fail("P6R response hash freeze already exists; stop before overwrite");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (result.RootElement.GetProperty("goldRead").GetBoolean() || result.RootElement.GetProperty("logicalProviderCalls").GetInt32() != 31)
            return Fail("P6R result is not a complete Gold-free 31-pack run");
        var requests = manifest.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(row =>
            $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}", StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var row in result.RootElement.GetProperty("rows").EnumerateArray())
        {
            var key = $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}";
            if (!requests.TryGetValue(key, out var request) || !row.GetProperty("transportAccepted").GetBoolean() ||
                row.GetProperty("finishReason").GetString() != "stop" || !row.GetProperty("parserAccepted").GetBoolean() ||
                request.GetProperty("providerRequestHash").GetString() != row.GetProperty("providerRequestHash").GetString() ||
                Hash(row.GetProperty("rawContent").GetString()!) != row.GetProperty("rawContentSha256").GetString() ||
                Hash(row.GetProperty("rawSse").GetString()!) != row.GetProperty("rawSseSha256").GetString())
                return Fail($"P6R raw/request hash verification failed at {key}; Gold remains unopened");
            rows.Add(new { documentId = row.GetProperty("documentId").GetString(), packId = row.GetProperty("packId").GetString(),
                providerRequestHash = row.GetProperty("providerRequestHash").GetString(), rawContentSha256 = row.GetProperty("rawContentSha256").GetString(),
                rawSseSha256 = row.GetProperty("rawSseSha256").GetString(), finishReason = row.GetProperty("finishReason").GetString(), retryCount = row.GetProperty("retryCount").GetInt32() });
        }
        WriteNew(freezePath, new { schemaVersion = "v5-p6r-structural-identity-resolution-response-hash-freeze-v1",
            resultFileSha256 = Hash(File.ReadAllText(resultPath)), manifestFileSha256 = Hash(File.ReadAllText(manifestPath)), rows,
            verifiedBeforeGoldRead = true, providerCallsDuringFreeze = 0, goldRead = false, goldMutation = "NONE" });
        Console.WriteLine("P6R raw content/SSE and request hashes verified and frozen: 31/31; ProviderCalls=0; GoldRead=false.");
        return 0;
    }

    /// <summary>
    /// One logical retry only for packs whose immutable P6R.0 primary response was contract-invalid.
    /// Valid primary packs are never resent; this artifact never overwrites the primary result.
    /// </summary>
    public static async Task<int> RetryContractInvalidPacksAsync(string repo, string[] args)
    {
        const string retryConfirm = "yes-i-authorize-p6r-contract-invalid-pack-retries";
        var directory = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var freezePath = Path.Combine(directory, "response-hash-freeze.v1.json");
        var retriesPath = Path.Combine(directory, "contract-invalid-pack-retries.v1.json");
        if (!args.Contains($"--confirm-p6r-contract-invalid-pack-retries={retryConfirm}"))
        { Console.WriteLine("P6R invalid-pack retries not authorized; ProviderCalls=0; GoldRead=false."); return 0; }
        if (File.Exists(retriesPath)) return Fail("P6R retry artifact already exists; stop before network");
        if (!File.Exists(manifestPath) || !File.Exists(resultPath) || !File.Exists(freezePath)) return Fail("P6R primary artifact/hash freeze missing; no retry");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))) return Fail("P6R retries authorized but OPENROUTER_API_KEY is not set");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var freeze = JsonDocument.Parse(File.ReadAllText(freezePath));
        if (Hash(File.ReadAllText(resultPath)) != freeze.RootElement.GetProperty("resultFileSha256").GetString() ||
            Hash(File.ReadAllText(manifestPath)) != freeze.RootElement.GetProperty("manifestFileSha256").GetString() ||
            result.RootElement.GetProperty("goldRead").GetBoolean()) return Fail("P6R immutable primary hash gate failed; no retry");
        var invalid = result.RootElement.GetProperty("rows").EnumerateArray().Where(row => !row.GetProperty("contractValid").GetBoolean()).ToArray();
        if (invalid.Length == 0) return Fail("P6R has no contract-invalid primary pack to retry");
        var prepared = Prepare(repo); var rows = Rows(prepared);
        if (!ManifestParity(manifestPath, rows)) return Fail("P6R frozen body parity failed; no retry");
        var byKey = prepared.SelectMany(document => document.Plan.Packs).ToDictionary(pack => $"{pack.Pack.DocumentId}|{pack.Pack.PackId}", StringComparer.Ordinal);
        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash"; options.OpenRouterProviderRoute = "alibaba"; options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true; options.MaxOutputTokens = PdfHeadingMembershipProductionAdapter.CompletionTokenCeiling;
        options.ProviderTransportTimeoutSeconds = 300; options.MaxParallelRequests = 1; options.Validate();
        var retryRows = new List<object>(invalid.Length);
        foreach (var primary in invalid)
        {
            var key = $"{primary.GetProperty("documentId").GetString()}|{primary.GetProperty("packId").GetString()}";
            var pack = byKey[key];
            if (pack.Pack.ProviderRequestHash != primary.GetProperty("providerRequestHash").GetString()) return Fail($"P6R retry body hash mismatch at {key}; no retry");
            FrozenHeaderExecutionResult? provider = null; V5FreeHeadingCandidateProtocolV1.StructuralIdentityResolutionResult? parsed = null; string? error = null;
            var watch = Stopwatch.StartNew();
            try
            {
                using var executor = OpenRouterQualificationTransport.CreateOwned(options);
                provider = await executor.ExecuteFrozenRequestAsync(pack.Pack.ProviderBody, pack.Pack.MaxCompletionTokens, pack.Pack.Request.SystemPrompt, pack.Pack.Request.UserMessage).ConfigureAwait(false);
                if (string.Equals(provider.FinishReason, "stop", StringComparison.OrdinalIgnoreCase)) parsed = PdfHeadingMembershipProductionAdapter.ParseStructuralIdentityResolution(pack, provider.Content, ResponseCap);
                else error = "finish-reason-not-stop";
            }
            catch (Exception exception) { error = exception.Message; }
            watch.Stop(); var headings = parsed?.Headings;
            retryRows.Add(new
            {
                documentId = pack.Pack.DocumentId, packId = pack.Pack.PackId, originalPrimaryResultSha256 = Hash(File.ReadAllText(resultPath)),
                providerRequestHash = pack.Pack.ProviderRequestHash, userMessageSha256 = pack.Pack.Request.UserMessageSha256,
                locatorRegistryFingerprint = pack.Pack.Registry.Fingerprint, retryLogicalCalls = 1,
                transportAccepted = provider is not null, transportError = provider is null ? error : null, finishReason = provider?.FinishReason,
                usage = provider?.Usage, retryCount = provider?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds,
                rawSse = provider?.RawSse, rawSseSha256 = provider is null ? null : Hash(provider.RawSse), rawContent = provider?.Content,
                rawContentSha256 = provider is null ? null : Hash(provider.Content), rawContentUtf8Bytes = provider is null ? 0 : Encoding.UTF8.GetByteCount(provider.Content),
                parserAccepted = parsed is not null, parseError = error, boundHeadings = headings?.Response.Occurrences.Count ?? 0,
                headingQuarantine = headings?.Quarantined, representationsAccepted = parsed?.RepresentationsAccepted ?? 0, representationQuarantine = parsed?.RepresentationQuarantine,
                contractValid = provider is not null && string.Equals(provider.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) && parsed is not null &&
                    headings!.Quarantined.Count == 0 && parsed.RepresentationQuarantine.Count == 0,
                headingScoreEligible = provider is not null && string.Equals(provider.FinishReason, "stop", StringComparison.OrdinalIgnoreCase) && parsed is not null && headings!.Quarantined.Count == 0,
                goldRead = false, repair = false, fallback = false,
            });
            Console.WriteLine($"[P6R retry {key}] transport={provider is not null} finish={provider?.FinishReason ?? "n/a"} heading={headings?.Response.Occurrences.Count ?? 0} repQ={parsed?.RepresentationQuarantine.Count ?? 0}");
        }
        WriteNew(retriesPath, new { schemaVersion = "v5-p6r-contract-invalid-pack-retries-v1", primaryResultSha256 = Hash(File.ReadAllText(resultPath)),
            retrySelection = "all and only primary contract-invalid packs; exactly one logical retry each; valid primary packs never resent",
            goldRead = false, goldMutation = "NONE", providerCalls = retryRows.Count, rows = retryRows });
        Console.WriteLine($"P6R retried {retryRows.Count} frozen contract-invalid packs; GoldRead=false.");
        return 0;
    }

    private static Prepared[] Prepare(string repo) => new[] { ("SRC-089", Src089), ("SRC-095", Src095) }.Select(item =>
    {
        var path = Path.Combine(repo, item.Item2.Replace('/', Path.DirectorySeparatorChar));
        return new Prepared(item.Item2, PdfHeadingMembershipProductionAdapter.PrepareStructuralIdentityResolution(path, item.Item1, Contract));
    }).ToArray();

    private static object[] Rows(IEnumerable<Prepared> documents) => documents.SelectMany(document => document.Plan.Packs.Select(item => (object)new
    {
        documentId = item.Pack.DocumentId, parentOrdinal = item.Pack.PackOrdinal, packId = item.Pack.PackId,
        ownedOccurrenceCount = item.Pack.OwnedAliases.Count, visibleOccurrenceCount = item.Pack.VisibleAliases.Count,
        ownedAliases = item.Pack.OwnedAliases, visibleAliases = item.Pack.VisibleAliases, sourceSha256 = document.Plan.SourcePlan.SourceSha256,
        sourceUniverseSha256 = document.Plan.SourcePlan.SourceUniverseSha256, locatorRegistryFingerprint = item.Pack.Registry.Fingerprint,
        // Preserve the manifest's original diagnostic meaning: unique D# handles visible in this pack.
        // Per-subject authorization is enforced by the parser, not encoded as a changed frozen-row field.
        allowedCorrespondenceTargetCount = item.AllowedCorrespondenceTargetsByPrimaryAtom.Values.SelectMany(targets => targets).Distinct(StringComparer.Ordinal).Count(), allowedContextEvidenceCount = item.AllowedContextEvidence.Count,
        userMessageSha256 = item.Pack.Request.UserMessageSha256, userMessageUtf8Bytes = item.Pack.Request.UserMessageUtf8Bytes,
        systemPromptSha256 = Hash(item.Pack.Request.SystemPrompt), providerRequestHash = item.Pack.ProviderRequestHash,
        providerRequestBytes = item.Pack.ProviderRequestBytes, maxCompletionTokens = item.Pack.MaxCompletionTokens,
    })).ToArray();

    private static bool ManifestParity(string path, object[] current)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var frozen = document.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        if (frozen.Length != current.Length || document.RootElement.GetProperty("providerCalls").GetInt32() != 0 || document.RootElement.GetProperty("goldRead").GetBoolean()) return false;
        return Hash(JsonSerializer.Serialize(frozen)) == Hash(JsonSerializer.Serialize(current));
    }

    /// <summary>Recovers the newest complete atomic checkpoint, including an interrupted .tmp rename.</summary>
    private static List<JsonElement> RecoverCheckpoint(string path)
    {
        var candidates = new[] { path, path + ".tmp" }.Where(File.Exists).Select(file =>
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.GetProperty("goldRead").GetBoolean()) return (File: file, Rows: new List<JsonElement>());
                return (File: file, Rows: document.RootElement.GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToList());
            }
            catch (JsonException) { return (File: file, Rows: new List<JsonElement>()); }
        }).OrderByDescending(candidate => candidate.Rows.Count).ThenBy(candidate => candidate.File, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return [];
        var chosen = candidates[0];
        if (chosen.Rows.Count != 0 && chosen.File != path)
        {
            File.Move(chosen.File, path, true);
            Console.WriteLine($"P6R recovered interrupted atomic checkpoint with {chosen.Rows.Count} rows.");
        }
        return chosen.Rows;
    }

    private static bool CheckpointParity(IReadOnlyList<JsonElement> checkpoint, object[] manifestRows)
    {
        if (checkpoint.Count > 31) return false;
        using var current = JsonDocument.Parse(JsonSerializer.Serialize(manifestRows));
        var requests = current.RootElement.EnumerateArray().ToDictionary(row => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}", StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in checkpoint)
        {
            if (!row.TryGetProperty("documentId", out var documentId) || !row.TryGetProperty("packId", out var packId) ||
                !row.TryGetProperty("providerRequestHash", out var requestHash) || !row.TryGetProperty("rawContent", out var raw) ||
                !row.TryGetProperty("rawContentSha256", out var rawHash)) return false;
            var key = $"{documentId.GetString()}|{packId.GetString()}";
            if (!seen.Add(key) || !requests.TryGetValue(key, out var request) ||
                request.GetProperty("providerRequestHash").GetString() != requestHash.GetString() ||
                Hash(raw.GetString() ?? string.Empty) != rawHash.GetString()) return false;
        }
        return true;
    }

    private static void WriteNew(string path, object value) { if (File.Exists(path)) throw new InvalidOperationException($"refusing to overwrite {path}"); AtomicWrite(path, value); }
    private static void AtomicWrite(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
    private static string GitHead(string repo) { using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); return process?.StandardOutput.ReadToEnd().Trim() ?? "UNKNOWN"; }
}
