using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>Provider-free freeze and explicitly gated P6P full-31 qualification.</summary>
internal static class P6PDocumentAwarePdfQualification
{
    private const string Confirm = "yes-i-authorize-p6p-full31-production-candidate";
    private const string RetryConfirm = "yes-i-authorize-p6p-src089-pack002-single-rerun";
    private const string ArtifactRoot = "artifacts/v5-p6p-document-aware-pdf";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();

    private sealed record Prepared(string RelativePdf, string PdfPath, PdfHeadingMembershipDocumentPlan Plan);

    public static async Task<int> RunAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var checkpointPath = Path.Combine(directory, "result.in-progress.v1.json");
        if (File.Exists(resultPath) || File.Exists(checkpointPath))
            return Fail("P6P result/checkpoint already exists; stop before any request");

        var prepared = Prepare(repo);
        if (prepared.Sum(doc => doc.Plan.Packs.Count) != 31 || prepared.Sum(doc => doc.Plan.SourceOccurrenceTotal) != 2_884)
            return Fail("P6P expected exactly 31 original P05 packs and 2,884 owned occurrences");

        var rows = BuildRows(prepared);
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(directory);
            WriteNew(manifestPath, new
            {
                schemaVersion = "v5-p6p-document-aware-pdf-manifest-v1",
                status = "PREPARED_NOT_AUTHORIZED",
                preparedAtHead = GitHead(repo),
                providerCalls = 0,
                goldRead = false,
                goldMutation = "NONE",
                productionPromotion = false,
                sourceAuthority = new
                {
                    sourceBuilder = "PdfStructuredSourceAuthorityBuilder",
                    packingPolicy = PdfHeadingMembershipProductionAdapter.PackingPolicy,
                    documentPlans = prepared.Select(doc => new
                    {
                        documentId = doc.Plan.DocumentId, pdf = doc.RelativePdf,
                        sourceSha256 = doc.Plan.SourceSha256, sourceUniverseSha256 = doc.Plan.SourceUniverseSha256,
                        sourceOccurrenceTotal = doc.Plan.SourceOccurrenceTotal, physicalPageTotal = doc.Plan.PhysicalPageTotal,
                        packCount = doc.Plan.Packs.Count,
                    }).ToArray(),
                    totalPacks = rows.Length,
                    ownedOccurrenceTotal = prepared.Sum(doc => doc.Plan.SourceOccurrenceTotal),
                    exactOnceOwnership = true,
                    unchangedPackPartitionComparedWithP6NB = ComparePackLineage(repo, prepared),
                },
                treatment = new
                {
                    model = "qwen/qwen3.7-flash", provider = "Alibaba", providerPin = "alibaba", temperature = 0,
                    reasoning = new { enabled = true }, reasoningEffort = "OMITTED",
                    promptProtocol = PdfHeadingMembershipProductionAdapter.ProtocolVersion,
                    systemPromptSha256 = Sha(V5FreeHeadingCandidateProtocolV1.BoundLocatorSystemPrompt),
                    locatorContract = "headings[].sourceParts[]; P6N-B strict locator; no P6N-C boundary sentences",
                    ontologyPrompt = false, placement = "existing production placement architecture; qualification scorer placement off",
                    context = new
                    {
                        sourceTotalsAndOwnedPageOrderRange = true, deterministicDocumentPageMap = true,
                        repeatedNormalizedTextPositions = true, regionalTextOnlyContextEachSide = PdfHeadingMembershipProductionAdapter.WiderContextOccurrencesPerSide,
                        regionalContextMaxCharsEach = PdfHeadingMembershipProductionAdapter.WiderContextTextMaxChars,
                        semanticRegionLabels = false, contextOnlySelectable = false, contextOnlyLocatorHandlesIssued = false,
                    },
                    packing = "unchanged existing PDF P05 owned partition",
                    sourceAuthority = "PdfStructuredSourceAuthorityBuilder + exact source atoms; unchanged",
                    binder = "RequestLocalLocatorRegistry + ParseAndBindSourceParts; fail-closed unchanged",
                    retry = new { policy = "RemoteInferenceOptions production transient transport retries", maxRetriesPerLogicalPack = new RemoteInferenceOptions().TransientRequestRetries, semanticRetries = 0 },
                    repair = false, semanticFallback = false, goldDuringRun = false,
                },
                route = new
                {
                    gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0,
                    reasoning = new { enabled = true }, effort = "OMITTED", responseFormat = "json_object",
                    streaming = true, usageInclude = true, fallbacks = false, outputTokenCeiling = PdfHeadingMembershipProductionAdapter.CompletionTokenCeiling,
                    responseUtf8ByteCap = ResponseCap,
                },
                executionGate = new
                {
                    primaryLogicalCalls = 31, exactlyOnePrimaryPerPack = true,
                    automaticRetryOnlyForFrozenProductionTransientTransportPolicy = true,
                    requiresExplicitAuthorization = true, goldRead = false, repair = false, fallback = false,
                    productionPromotion = false, persistRawSseContentUsageAndHashes = true,
                },
                rows,
            });
            Console.WriteLine("P6P frozen provider-free manifest created: 31 P05 bodies, 2,884 owned occurrences; ProviderCalls=0, GoldRead=false.");
            PrintSummary(rows);
            return 0;
        }

        if (!ValidateManifest(repo, manifestPath, prepared, rows))
            return Fail("P6P frozen source/context/body/pack manifest parity failed; no network call");
        Console.WriteLine("P6P exact frozen source, P05 partition, context and provider-body parity PASS.");
        PrintSummary(rows);
        if (!args.Contains($"--confirm-p6p-full31={Confirm}"))
        {
            Console.WriteLine("PREPARED_NOT_AUTHORIZED; ProviderCalls=0, GoldRead=false. Execution requires the user's explicit P6P authorization.");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("P6P authorized but OPENROUTER_API_KEY is not set");
        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        // The exact body carries reasoning.enabled=true and intentionally omits effort.
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.MaxOutputTokens = PdfHeadingMembershipProductionAdapter.CompletionTokenCeiling;
        options.ProviderTransportTimeoutSeconds = 300;
        options.TransientRequestRetries = new RemoteInferenceOptions().TransientRequestRetries;
        options.MaxParallelRequests = 1;
        options.Validate();

        var acceptedRows = new List<object>(31);
        WriteNew(checkpointPath, Checkpoint(acceptedRows));
        foreach (var doc in prepared)
        foreach (var pack in doc.Plan.Packs)
        {
            var watch = Stopwatch.StartNew();
            PdfHeadingMembershipPackExecution? execution = null;
            string? transportError = null;
            try
            {
                using var executor = OpenRouterHeaderExtractor.CreateOwned(options);
                execution = await PdfHeadingMembershipProductionAdapter.ExecuteAndBindAsync(pack, executor,
                    ResponseCap).ConfigureAwait(false);
            }
            catch (Exception exception) { transportError = exception.Message; }
            watch.Stop();
            var provider = execution?.Provider;
            var binding = execution?.Binding;
            var rawBytes = provider is null ? 0 : Encoding.UTF8.GetByteCount(provider.Content);
            var row = new
            {
                documentId = pack.DocumentId, packOrdinal = pack.PackOrdinal, packId = pack.PackId,
                ownedOccurrenceCount = pack.OwnedAliases.Count, visibleOccurrenceCount = pack.VisibleAliases.Count,
                sourceSha256 = doc.Plan.SourceSha256, sourceUniverseSha256 = doc.Plan.SourceUniverseSha256,
                ownedAliasesSha256 = Sha(string.Join("\n", pack.OwnedAliases)), visibleAliasesSha256 = Sha(string.Join("\n", pack.VisibleAliases)),
                locatorRegistryFingerprint = pack.Registry.Fingerprint, userMessageSha256 = pack.Request.UserMessageSha256,
                providerRequestHash = pack.ProviderRequestHash, providerRequestBytes = pack.ProviderRequestBytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
                transportAccepted = provider is not null, transportError,
                finishReason = provider?.FinishReason, usage = provider?.Usage,
                retryCount = provider?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds,
                sseEventCount = provider?.SseEventCount ?? 0, rawSseSha256 = provider is null ? null : Sha(provider.RawSse), rawSse = provider?.RawSse,
                rawContentSha256 = provider is null ? null : Sha(provider.Content), rawContentUtf8Bytes = rawBytes, rawContent = provider?.Content,
                parserAccepted = binding is not null, parseError = execution?.ParseError,
                emittedOccurrences = binding?.Response.Occurrences.Count ?? 0,
                boundOccurrences = binding?.Response.Occurrences.Count ?? 0,
                quarantinedOccurrences = binding?.Quarantined.Count ?? 0, quarantine = binding?.Quarantined,
                contractValid = provider is not null && !string.Equals(provider.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
                    && binding is not null && binding.Quarantined.Count == 0,
            };
            acceptedRows.Add(row);
            AtomicWrite(checkpointPath, Checkpoint(acceptedRows));
            Console.WriteLine($"[{pack.DocumentId} {pack.PackId}] transport={provider is not null} finish={provider?.FinishReason ?? "n/a"} retry={provider?.RetryCount ?? 0} emitted={binding?.Response.Occurrences.Count ?? 0} quarantine={binding?.Quarantined.Count ?? 0} ms={watch.ElapsedMilliseconds}");
        }

        var result = new
        {
            schemaVersion = "v5-p6p-document-aware-pdf-result-v1", sourceManifest = $"{ArtifactRoot}/execution-manifest.v1.json",
            head = GitHead(repo), logicalProviderCalls = acceptedRows.Count,
            maximumPrimaryLogicalCalls = 31, completedPrimaryPacks = acceptedRows.Count,
            route = new { gateway = "OpenRouter", model = "qwen/qwen3.7-flash", providerPin = "alibaba", temperature = 0, reasoning = new { enabled = true }, reasoningEffort = "OMITTED" },
            automaticRetryPolicy = new RemoteInferenceOptions().TransientRequestRetries,
            repair = false, fallback = false, goldRead = false, goldMutation = "NONE", productionPromotion = false,
            semanticScore = "NOT_RUN", sharedRuntime = "UNCHANGED", maxResponseUtf8Bytes = ResponseCap, rows = acceptedRows,
        };
        WriteNew(resultPath, result);
        File.Delete(checkpointPath);
        Console.WriteLine("P6P provider run complete. Gold remains unread; execution gate is closed pending immutable-hash verification and offline score.");
        return 0;
    }

    public static int FreezeResponseHashes(string repo)
    {
        var directory = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var resultPath = Path.Combine(directory, "result.v1.json");
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var freezePath = Path.Combine(directory, "response-hash-freeze.v1.json");
        if (!File.Exists(resultPath) || !File.Exists(manifestPath)) return Fail("P6P result/manifest is missing; no hash freeze");
        if (File.Exists(freezePath)) return Fail("P6P response hash freeze already exists; stop before overwrite");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var rows = result.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        var requestRows = manifest.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}", StringComparer.Ordinal);
        if (rows.Length != 31 || result.RootElement.GetProperty("logicalProviderCalls").GetInt32() != 31 ||
            result.RootElement.GetProperty("goldRead").GetBoolean()) return Fail("P6P result envelope is not a complete Gold-free 31-pack run");
        var frozenRows = new List<object>(31);
        foreach (var row in rows)
        {
            var key = $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}";
            if (!requestRows.TryGetValue(key, out var request) || row.GetProperty("finishReason").GetString() != "stop" ||
                !row.GetProperty("transportAccepted").GetBoolean() || !row.GetProperty("parserAccepted").GetBoolean() ||
                Hash(row.GetProperty("rawContent").GetString()!) != row.GetProperty("rawContentSha256").GetString() ||
                Hash(row.GetProperty("rawSse").GetString()!) != row.GetProperty("rawSseSha256").GetString() ||
                request.GetProperty("providerRequestHash").GetString() != row.GetProperty("providerRequestHash").GetString() ||
                request.GetProperty("locatorRegistryFingerprint").GetString() != row.GetProperty("locatorRegistryFingerprint").GetString() ||
                request.GetProperty("userMessageSha256").GetString() != row.GetProperty("userMessageSha256").GetString())
                return Fail($"P6P raw/request hash verification failed at {key}; Gold remains unopened");
            frozenRows.Add(new
            {
                documentId = row.GetProperty("documentId").GetString(), packId = row.GetProperty("packId").GetString(),
                providerRequestHash = row.GetProperty("providerRequestHash").GetString(),
                rawContentSha256 = row.GetProperty("rawContentSha256").GetString(), rawSseSha256 = row.GetProperty("rawSseSha256").GetString(),
                rawContentUtf8Bytes = row.GetProperty("rawContentUtf8Bytes").GetInt32(),
                finishReason = row.GetProperty("finishReason").GetString(), retryCount = row.GetProperty("retryCount").GetInt32(),
            });
        }
        var resultHash = Hash(File.ReadAllText(resultPath));
        WriteNew(freezePath, new
        {
            schemaVersion = "v5-p6p-document-aware-pdf-response-hash-freeze-v1", resultFileSha256 = resultHash,
            manifestFileSha256 = Hash(File.ReadAllText(manifestPath)), rows = frozenRows,
            verifiedBeforeGoldRead = true, providerCallsDuringFreeze = 0, goldRead = false, goldMutation = "NONE",
        });
        Console.WriteLine("P6P raw content/SSE and request hashes verified and frozen: 31/31; ProviderCalls=0, GoldRead=false.");
        return 0;
    }

    /// <summary>One explicitly authorized exact-body rerun of the sole P6P contract-invalid pack.</summary>
    public static async Task<int> RerunFailedPackAsync(string repo, string[] args)
    {
        var directory = Path.Combine(repo, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(directory, "execution-manifest.v1.json");
        var resultPath = Path.Combine(directory, "result.v1.json");
        var freezePath = Path.Combine(directory, "response-hash-freeze.v1.json");
        var retryPath = Path.Combine(directory, "pack-retry-src089-pack002.v1.json");
        var checkpointPath = Path.Combine(directory, "pack-retry-src089-pack002.in-progress.v1.json");
        if (File.Exists(retryPath) || File.Exists(checkpointPath))
            return Fail("P6P failed-pack rerun artifact/checkpoint already exists; stop before any request");
        if (!args.Contains($"--confirm-p6p-failed-pack-rerun={RetryConfirm}"))
        {
            Console.WriteLine("P6P failed-pack rerun not authorized; ProviderCalls=0.");
            return 0;
        }
        if (!File.Exists(manifestPath) || !File.Exists(resultPath) || !File.Exists(freezePath))
            return Fail("P6P manifest/result/hash-freeze missing; no rerun");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            return Fail("P6P failed-pack rerun authorized but OPENROUTER_API_KEY is not set");

        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var resultDocument = JsonDocument.Parse(File.ReadAllText(resultPath));
        using var freezeDocument = JsonDocument.Parse(File.ReadAllText(freezePath));
        var manifest = manifestDocument.RootElement;
        var result = resultDocument.RootElement;
        var freeze = freezeDocument.RootElement;
        if (result.GetProperty("logicalProviderCalls").GetInt32() != 31 ||
            result.GetProperty("goldRead").GetBoolean() ||
            Hash(File.ReadAllText(resultPath)) != freeze.GetProperty("resultFileSha256").GetString() ||
            Hash(File.ReadAllText(manifestPath)) != freeze.GetProperty("manifestFileSha256").GetString())
            return Fail("P6P primary result/manifest no longer matches its immutable hash freeze; no rerun");

        var primaryRows = result.GetProperty("rows").EnumerateArray().ToArray();
        var invalidRows = primaryRows.Where(row => !row.GetProperty("contractValid").GetBoolean()).ToArray();
        if (invalidRows.Length != 1 || invalidRows[0].GetProperty("documentId").GetString() != "SRC-089" ||
            invalidRows[0].GetProperty("packId").GetString() != "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002" ||
            invalidRows[0].GetProperty("finishReason").GetString() != "stop" ||
            invalidRows[0].GetProperty("transportAccepted").GetBoolean() != true ||
            invalidRows[0].GetProperty("quarantinedOccurrences").GetInt32() != 2)
            return Fail("P6P rerun gate requires exactly the frozen SRC-089 PACK_002 contract-invalid attempt");

        var prepared = Prepare(repo);
        var currentRows = BuildRows(prepared);
        if (!ValidateManifest(repo, manifestPath, prepared, currentRows))
            return Fail("P6P source/context/body/ownership parity failed; no rerun");
        var doc = prepared.Single(item => item.Plan.DocumentId == "SRC-089");
        var pack = doc.Plan.Packs.Single(item => item.PackId == "RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002");
        if (pack.ProviderRequestHash != invalidRows[0].GetProperty("providerRequestHash").GetString())
            return Fail("P6P rerun request hash differs from the failed frozen request; no network call");

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none"; // reasoning.enabled=true is in the frozen body; effort remains omitted.
        options.RequireJsonObjectResponse = true;
        options.MaxOutputTokens = PdfHeadingMembershipProductionAdapter.CompletionTokenCeiling;
        options.ProviderTransportTimeoutSeconds = 300;
        options.TransientRequestRetries = new RemoteInferenceOptions().TransientRequestRetries;
        options.MaxParallelRequests = 1;
        options.Validate();

        WriteNew(checkpointPath, new
        {
            schemaVersion = "v5-p6p-single-pack-rerun-checkpoint-v1", status = "IN_PROGRESS",
            target = "SRC-089|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002",
            originalProviderRequestHash = pack.ProviderRequestHash, providerCallsStarted = 1,
            maxLogicalReruns = 1, repair = false, fallback = false, goldRead = false,
        });
        var watch = Stopwatch.StartNew();
        PdfHeadingMembershipPackExecution? execution = null;
        string? transportError = null;
        try
        {
            using var executor = OpenRouterHeaderExtractor.CreateOwned(options);
            execution = await PdfHeadingMembershipProductionAdapter.ExecuteAndBindAsync(pack, executor, ResponseCap).ConfigureAwait(false);
        }
        catch (Exception exception) { transportError = exception.Message; }
        watch.Stop();

        var provider = execution?.Provider;
        var binding = execution?.Binding;
        var content = provider?.Content;
        var retry = new
        {
            schemaVersion = "v5-p6p-single-pack-rerun-v1", target = "SRC-089|RESOURCE_BOUNDED_SOURCE_PACKING_V1:PACK_002",
            originalPrimaryResultSha256 = Hash(File.ReadAllText(resultPath)),
            providerRequestHash = pack.ProviderRequestHash, providerRequestBytes = pack.ProviderRequestBytes,
            locatorRegistryFingerprint = pack.Registry.Fingerprint, userMessageSha256 = pack.Request.UserMessageSha256,
            sourceSha256 = doc.Plan.SourceSha256, sourceUniverseSha256 = doc.Plan.SourceUniverseSha256,
            logicalReruns = 1, transportRetryPolicy = "frozen production transient transport retries only",
            transportAccepted = provider is not null, transportError, finishReason = provider?.FinishReason,
            usage = provider?.Usage, retryCount = provider?.RetryCount ?? 0, latencyMs = watch.Elapsed.TotalMilliseconds,
            sseEventCount = provider?.SseEventCount ?? 0, rawSse = provider?.RawSse,
            rawSseSha256 = provider is null ? null : Sha(provider.RawSse), rawContent = content,
            rawContentSha256 = content is null ? null : Sha(content), rawContentUtf8Bytes = content is null ? 0 : Encoding.UTF8.GetByteCount(content),
            parserAccepted = binding is not null, parseError = execution?.ParseError,
            emittedOccurrences = binding?.Response.Occurrences.Count ?? 0,
            boundOccurrences = binding?.Response.Occurrences.Count ?? 0,
            quarantinedOccurrences = binding?.Quarantined.Count ?? 0, quarantine = binding?.Quarantined,
            contractValid = provider is not null && !string.Equals(provider.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
                && binding is not null && binding.Quarantined.Count == 0,
            providerCallsDuringRerun = 1 + (provider?.RetryCount ?? 0), goldRead = false, goldMutation = "NONE",
            repair = false, fallback = false, productionPromotion = false,
        };
        WriteNew(retryPath, retry);
        File.Delete(checkpointPath);
        Console.WriteLine($"P6P SRC-089 PACK_002 rerun complete: transport={provider is not null}; finish={provider?.FinishReason ?? "n/a"}; retry={provider?.RetryCount ?? 0}; bound={binding?.Response.Occurrences.Count ?? 0}; quarantine={binding?.Quarantined.Count ?? 0}; GoldRead=false.");
        return 0;
    }

    private static Prepared[] Prepare(string repo)
    {
        var docs = new[]
        {
            (Id: "SRC-089", Path: Src089), (Id: "SRC-095", Path: Src095),
        };
        return docs.Select(doc =>
        {
            var path = Path.Combine(repo, doc.Path.Replace('/', Path.DirectorySeparatorChar));
            return new Prepared(doc.Path, path, PdfHeadingMembershipProductionAdapter.Prepare(path, doc.Id, Contract));
        }).ToArray();
    }

    private static object[] BuildRows(IReadOnlyList<Prepared> docs) => docs.SelectMany(doc => doc.Plan.Packs.Select(pack => (object)new
    {
        documentId = pack.DocumentId, parentOrdinal = pack.PackOrdinal, packId = pack.PackId,
        ownedOccurrenceCount = pack.OwnedAliases.Count, visibleOccurrenceCount = pack.VisibleAliases.Count,
        ownedAliases = pack.OwnedAliases, visibleAliases = pack.VisibleAliases,
        sourceSha256 = doc.Plan.SourceSha256, sourceUniverseSha256 = doc.Plan.SourceUniverseSha256,
        locatorRegistryFingerprint = pack.Registry.Fingerprint,
        userMessageSha256 = pack.Request.UserMessageSha256, userMessageUtf8Bytes = pack.Request.UserMessageUtf8Bytes,
        systemPromptSha256 = Sha(pack.Request.SystemPrompt), providerRequestHash = pack.ProviderRequestHash,
        providerRequestBytes = pack.ProviderRequestBytes, maxCompletionTokens = pack.MaxCompletionTokens,
        sourceContextSha256 = Sha(pack.Request.UserMessage),
    })).ToArray();

    private static bool ComparePackLineage(string repo, IReadOnlyList<Prepared> docs)
    {
        var path = Path.Combine(repo, "artifacts/v5-p6nb-full31-reasoning-lane/execution-manifest.v1.json".Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return false;
        using var old = JsonDocument.Parse(File.ReadAllText(path));
        var oldRows = old.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => $"{row.GetProperty("documentId").GetString()}|{row.GetProperty("packId").GetString()}", StringComparer.Ordinal);
        foreach (var pack in docs.SelectMany(doc => doc.Plan.Packs))
        {
            if (!oldRows.TryGetValue($"{pack.DocumentId}|{pack.PackId}", out var oldRow) ||
                oldRow.GetProperty("registryFingerprint").GetString() != pack.Registry.Fingerprint ||
                oldRow.GetProperty("packId").GetString() != pack.PackId ||
                oldRow.GetProperty("ownedAtoms").GetInt32() != pack.OwnedAliases.Count ||
                oldRow.GetProperty("maxCompletionTokens").GetInt32() != pack.MaxCompletionTokens)
                return false;
        }
        return oldRows.Count == 31;
    }

    private static bool ValidateManifest(string repo, string path, IReadOnlyList<Prepared> docs, object[] currentRows)
    {
        try
        {
            using var frozen = JsonDocument.Parse(File.ReadAllText(path));
            var root = frozen.RootElement;
            if (root.GetProperty("status").GetString() != "PREPARED_NOT_AUTHORIZED" ||
                root.GetProperty("providerCalls").GetInt32() != 0 || root.GetProperty("goldRead").GetBoolean() ||
                root.GetProperty("rows").GetArrayLength() != currentRows.Length) return false;
            var old = root.GetProperty("rows").EnumerateArray().ToArray();
            var current = JsonSerializer.SerializeToElement(currentRows).EnumerateArray().ToArray();
            for (var i = 0; i < old.Length; i++)
                foreach (var key in new[] { "documentId", "packId", "ownedOccurrenceCount", "visibleOccurrenceCount", "ownedAliases", "visibleAliases", "sourceSha256", "sourceUniverseSha256", "locatorRegistryFingerprint", "userMessageSha256", "providerRequestHash", "maxCompletionTokens" })
                    if (!JsonNode.DeepEquals(JsonNode.Parse(old[i].GetProperty(key).GetRawText()), JsonNode.Parse(current[i].GetProperty(key).GetRawText()))) return false;
            return ComparePackLineage(repo, docs);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static object Checkpoint(IReadOnlyList<object> rows) => new
    {
        schemaVersion = "v5-p6p-document-aware-pdf-checkpoint-v1", providerCalls = rows.Count,
        goldRead = false, goldMutation = "NONE", rows,
    };

    private static void PrintSummary(IReadOnlyList<object> rows)
    {
        var maxBody = 0; var maxUser = 0;
        foreach (var row in rows)
        {
            var e = JsonSerializer.SerializeToElement(row);
            maxBody = Math.Max(maxBody, e.GetProperty("providerRequestBytes").GetInt32());
            maxUser = Math.Max(maxUser, e.GetProperty("userMessageUtf8Bytes").GetInt32());
        }
        Console.WriteLine($"packs={rows.Count}; maxProviderBodyBytes={maxBody}; maxUserMessageBytes={maxUser}; ProviderCalls=0; GoldRead=false.");
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Hash(string text) => Sha(text);
    private static string GitHead(string repo)
    {
        try { return System.Diagnostics.Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }) is { } p ? p.StandardOutput.ReadToEnd().Trim() : "unknown"; }
        catch { return "unknown"; }
    }
    private static void WriteNew(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }
    private static void AtomicWrite(string path, object value)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
        File.Move(temporary, path, overwrite: true);
    }
    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
}
