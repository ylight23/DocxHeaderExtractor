using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// The first V5 31-pack measurement cohort (SRC-089: 7 packs, SRC-095: 24 packs). Measurement, not
/// promotion: nothing here carries a refusal threshold or a pass/fail.
/// <list type="bullet">
/// <item><c>--cohort31-preflight --out=DIR</c>: provider-free. Builds the cohort and the environment
/// fingerprint into DIR.</item>
/// <item><c>--cohort31-seal --run-a=DIR --run-b=DIR</c>: provider-free. Requires two independent
/// preflight runs to be byte-identical, then freezes them under
/// <c>artifacts/v5-provider-cohort-31/preflight</c>.</item>
/// <item><c>--cohort31-execute --confirm-cohort31=...</c>: the only mode that can call a provider.
/// Refuses unless the source tree is exactly the frozen one, a fresh in-process rebuild reproduces
/// every frozen hash and body byte, the provider output directory does not exist yet, and
/// <c>OPENROUTER_API_KEY</c> is set. Sends the 31 frozen bodies sequentially, persists the raw
/// provider artifacts first, then derives the qualification.</item>
/// <item><c>--cohort31-qualify</c>: provider-free. Re-derives <c>cohort-result.v1.json</c> from
/// the frozen raw provider artifacts alone.</item>
/// </list>
/// Never reads Gold.
/// </summary>
internal static class Cohort31
{
    public const string ConfirmSentinel = "yes-i-authorize-exactly-31-frozen-qwen-flash-alibaba-calls";
    private const string CohortRoot = "artifacts/v5-provider-cohort-31";
    private const string PreflightDir = CohortRoot + "/preflight";
    private const string ProviderDir = CohortRoot + "/provider";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string root, string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        if (args.Contains("--cohort31-preflight"))
            return Preflight(root, Arg("--out") ?? throw new ArgumentException("--out=DIR is required"));
        var cohortRoot = Arg("--cohort-root") ?? CohortRoot;
        if (args.Contains("--cohort31-seal"))
            return Seal(root, Arg("--run-a") ?? throw new ArgumentException("--run-a=DIR is required"),
                Arg("--run-b") ?? throw new ArgumentException("--run-b=DIR is required"), cohortRoot);
        if (args.Contains("--cohort31-qualify"))
            return Qualify(root, cohortRoot);
        if (args.Contains("--cohort31-execute"))
            return await ExecuteAsync(root, Arg("--confirm-cohort31") == ConfirmSentinel, cohortRoot);
        throw new ArgumentException("unknown cohort31 mode");
    }

    // ---------------------------------------------------------------- preflight (provider-free)

    private static int Preflight(string root, string outDir)
    {
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var cohort = V5CohortPreflightBuilder.Build(root, contract);
        Directory.CreateDirectory(Path.Combine(outDir, "bodies"));
        foreach (var row in cohort.Rows)
            File.WriteAllBytes(Path.Combine(outDir, "bodies", BodyFileName(row)), row.ProviderBody);
        WriteJson(Path.Combine(outDir, "cohort.v1.json"), CohortDocument(cohort));
        WriteJson(Path.Combine(outDir, "fingerprint.v1.json"), Fingerprint(root));
        Console.WriteLine($"cohort31 preflight: rows={cohort.Rows.Count} out={outDir} providerCalls=0 goldRead=false");
        return 0;
    }

    private static object CohortDocument(V5CohortPreflight cohort) => new
    {
        schemaVersion = "v5-provider-cohort-31-v1",
        purpose = "MEASUREMENT_NOT_PROMOTION",
        protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
        composerVersion = V5SemanticRequestComposerV2_1.Version,
        packingPolicy = V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId,
        payload = new
        {
            model = V5CohortPreflightBuilder.Envelope.Model,
            providerSlug = V5CohortPreflightBuilder.Envelope.Provider,
            reasoning = V5CohortPreflightBuilder.Envelope.Reasoning,
            temperature = 0,
            responseFormat = V5CohortPreflightBuilder.Envelope.ResponseFormat,
            stream = true,
            usageInclude = true,
            allowFallbacks = false,
            requireParameters = true,
            dataCollection = "deny",
            zdr = false,
        },
        aggregate = new
        {
            totalPacks = cohort.Rows.Count,
            SRC089 = cohort.Rows.Count(row => row.DocumentId == "SRC-089"),
            SRC095 = cohort.Rows.Count(row => row.DocumentId == "SRC-095"),
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
        },
        documents = cohort.Documents,
        rows = cohort.Rows.Select(row => new
        {
            row.Ordinal,
            row.DocumentId,
            row.PackId,
            row.OwnedCount,
            row.ContextOnlyCount,
            row.SemanticRequestHash,
            row.SemanticRequestBytes,
            row.PromptHash,
            row.SchemaHash,
            row.TaskContractHash,
            row.SourceUniverseHash,
            row.ProviderRequestHash,
            row.ProviderRequestBytes,
            row.MaxCompletionTokens,
            bodyFile = "bodies/" + BodyFileName(row),
            ownedAliases = row.OwnedAliases,
            visibleAliases = row.VisibleAliases,
        }).ToArray(),
    };

    private static string BodyFileName(V5CohortRow row) =>
        $"{row.Ordinal:00}-{row.DocumentId}-{row.PackId[(row.PackId.LastIndexOf(':') + 1)..]}.json";

    // ---------------------------------------------------------------- seal (provider-free)

    private static int Seal(string root, string runA, string runB, string cohortRoot)
    {
        var preflightDir = cohortRoot + "/preflight";
        var filesA = RelativeFiles(runA);
        var filesB = RelativeFiles(runB);
        if (!filesA.Keys.SequenceEqual(filesB.Keys, StringComparer.Ordinal))
            return Fail("cohort31 seal: the two preflight runs produced different file sets");
        // Every file, the environment fingerprint included, must match: both runs are one environment.
        var differing = filesA.Keys.Where(key => filesA[key] != filesB[key]).ToArray();
        if (differing.Length > 0)
            return Fail($"cohort31 seal: runs differ in {string.Join(", ", differing)}");

        var cohort = JsonNode.Parse(File.ReadAllText(Path.Combine(runA, "cohort.v1.json")))!;
        var fingerprint = JsonNode.Parse(File.ReadAllText(Path.Combine(runA, "fingerprint.v1.json")))!;
        // A cohort frozen from an uncommitted tree could never be tied to one execution source head.
        if (fingerprint["sourceTreeCleanOutsideArtifacts"]?.GetValue<bool>() != true ||
            fingerprint["executionSourceHead"]?.GetValue<string>() != Git(root, "rev-parse HEAD"))
            return Fail("cohort31 seal: preflight was not taken from the current, clean, committed source head");

        var target = Path.Combine(root, preflightDir);
        if (Directory.Exists(target))
            return Fail($"cohort31 seal: {preflightDir} already exists; a frozen cohort is never overwritten");
        Directory.CreateDirectory(Path.Combine(target, "bodies"));
        foreach (var relative in filesA.Keys.Where(key => key != "fingerprint.v1.json"))
            File.Copy(Path.Combine(runA, relative), Path.Combine(target, relative));

        var reference = PriorReferenceParity(root, cohort);
        WriteJson(Path.Combine(target, "environment.v1.json"), new
        {
            schemaVersion = "v5-provider-cohort-31-environment-v1",
            fingerprint,
            determinism = new
            {
                independentPreflightRuns = 2,
                byteIdentical = true,
                filesCompared = filesA.Count,
                cohortSha256 = filesA["cohort.v1.json"],
                bodiesSha256 = V5CohortPreflightBuilder.Sha256(Encoding.UTF8.GetBytes(string.Join("\n",
                    filesA.Where(item => item.Key.StartsWith("bodies/", StringComparison.Ordinal)).Select(item => $"{item.Key} {item.Value}")))),
            },
            priorReferenceParity = reference,
            baseline = "REBASELINED_PROVIDER_FREE_ON_THIS_PINNED_ENVIRONMENT",
            baselineReason =
                "The prior frozen references (3-pack canary preflight) were produced on a different extraction " +
                "environment; this environment does not reproduce SRC-089 byte for byte, so it is used only " +
                "against its own two independent, byte-identical preflight runs and never mixed with the old hashes.",
            environmentParity = true,
            environmentParityDefinition =
                "The authoritative gate is hash equality, not font or OS identity: a provider run is allowed only " +
                "where a fresh rebuild reproduces every documents[].sourceUniverseHash/sourceTextSha256/pdfSha256, " +
                "every rows[].semanticRequestHash and providerRequestHash, and every body byte in bodies/. The " +
                "fingerprint is provenance; a fingerprint difference is reported but only a hash difference blocks.",
            deterministicSerializationAssumptions = new[]
            {
                "System.Text.Json with the repository's CanonicalJson options; no culture-sensitive formatting in any hashed bytes",
                "PdfPig text extraction depends on system fonts for non-embedded fonts (SRC-089 references TimesNewRomanPSMT without embedding it); source atom bounding-box coordinates are part of each sourceId and so of every hash",
                "local time zone is not an input to any hashed bytes; it is recorded as provenance only",
                "font files are never committed; their hashes are recorded here",
            },
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
        });
        Console.WriteLine($"cohort31 sealed into {preflightDir}: files={filesA.Count} byteIdentical=true providerCalls=0");
        return 0;
    }

    private static object PriorReferenceParity(string root, JsonNode cohort)
    {
        var canaryPreflight = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "artifacts/v5-provider-canary-current/preflight.json")))!;
        var canaryResult = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "artifacts/v5-provider-canary-current/canary-result.v1.json")))!;
        var documents = cohort["documents"]!.AsArray().Select(document =>
        {
            var id = document!["DocumentId"]!.GetValue<string>();
            var frozen = canaryPreflight["sourceUniverseHashes"]![id]!.GetValue<string>();
            var now = document["SourceUniverseHash"]!.GetValue<string>();
            var atomDiff = AtomDiffAgainstQualificationV1(root, id);
            return new { documentId = id, referenceSourceUniverseHash = frozen, sourceUniverseHash = now, equal = frozen == now, atomDiff };
        }).ToArray();
        var packs = canaryResult["results"]!.AsArray().Select(result =>
        {
            var id = result!["documentId"]!.GetValue<string>();
            var pack = result["packId"]!.GetValue<string>();
            var frozen = result["providerRequestHash"]!.GetValue<string>();
            var now = cohort["rows"]!.AsArray().Single(row => row!["DocumentId"]!.GetValue<string>() == id && row["PackId"]!.GetValue<string>() == pack)!["ProviderRequestHash"]!.GetValue<string>();
            return new { documentId = id, packId = pack, referenceProviderRequestHash = frozen, providerRequestHash = now, equal = frozen == now };
        }).ToArray();
        return new
        {
            reference = "artifacts/v5-provider-canary-current/preflight.json + canary-result.v1.json (captured at 966c16a on the original environment)",
            parity = documents.All(item => item.equal) && packs.All(item => item.equal),
            documents,
            canaryPacks = packs,
        };
    }

    /// <summary>Atom-level comparison against the original environment's frozen v1 prompts, which carry every atom's sourceId and text.</summary>
    private static object AtomDiffAgainstQualificationV1(string root, string documentId)
    {
        var path = Path.Combine(root, $"artifacts/v5-provider-qualification-v1/{documentId}.preflight.v1.json");
        if (!File.Exists(path)) return new { available = false };
        var reference = new Dictionary<string, (string SourceId, string Text)>(StringComparer.Ordinal);
        foreach (var request in JsonNode.Parse(File.ReadAllText(path))!["requests"]!.AsArray())
        {
            var prompt = JsonNode.Parse(request!["request"]!["prompt"]!.GetValue<string>())!;
            foreach (var evidence in prompt["packet"]!["visibleEvidence"]!.AsArray())
                reference[evidence!["sourceAlias"]!.GetValue<string>()] = (evidence["sourceId"]!.GetValue<string>(), evidence["text"]!.GetValue<string>());
        }
        var pdf = Path.Combine(root, V5CohortPreflightBuilder.Documents.Single(item => item.DocumentId == documentId).PdfRelativePath);
        var atoms = V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var differing = reference
            .Where(item => !atoms.TryGetValue(item.Key, out var atom) || atom.SourceId != item.Value.SourceId || atom.Text != item.Value.Text)
            .Select(item => new
            {
                alias = item.Key,
                referenceSourceId = item.Value.SourceId,
                sourceId = atoms.GetValueOrDefault(item.Key)?.SourceId,
                textEqual = atoms.GetValueOrDefault(item.Key)?.Text == item.Value.Text,
            })
            .ToArray();
        return new
        {
            available = true,
            referenceAtoms = reference.Count,
            atoms = atoms.Count,
            aliasSetEqual = reference.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(atoms.Keys),
            differingAtoms = differing.Length,
            differing,
        };
    }

    // ---------------------------------------------------------------- environment fingerprint

    private static object Fingerprint(string root)
    {
        var pdfPig = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) == true)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .Select(assembly => new
            {
                name = assembly.GetName().Name,
                version = assembly.GetName().Version?.ToString(),
                informationalVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                sha256 = FileSha(assembly.Location),
            })
            .ToArray();
        var coreLib = typeof(object).Assembly.Location;
        return new
        {
            executionSourceHead = Git(root, "rev-parse HEAD"),
            sourceTreeCleanOutsideArtifacts = Git(root, "status --porcelain -- . :(exclude)artifacts") is "",
            os = new
            {
                description = RuntimeInformation.OSDescription,
                prettyName = OsReleasePrettyName(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            },
            dotnet = new
            {
                framework = RuntimeInformation.FrameworkDescription,
                runtimeVersion = Environment.Version.ToString(),
                sdkVersion = Run("dotnet", "--version", root),
                coreLibSha256 = FileSha(coreLib),
                runtimeDirectory = Path.GetDirectoryName(coreLib),
            },
            packages = new { pdfPig },
            culture = new
            {
                currentCulture = CultureInfo.CurrentCulture.Name,
                currentUICulture = CultureInfo.CurrentUICulture.Name,
                invariantGlobalization = AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant) && invariant,
                LANG = Environment.GetEnvironmentVariable("LANG"),
                LC_ALL = Environment.GetEnvironmentVariable("LC_ALL"),
            },
            timeZone = new { id = TimeZoneInfo.Local.Id, influencesHashedBytes = false },
            fonts = FontInventory(),
            parser = new
            {
                sourceAuthority = "PdfStructuredSourceAuthorityBuilder (a99-pdf-segment-atom-universe-v1 / a99-pdf-model-visible-evidence-v2)",
                packingPolicy = V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId,
                protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
                composerVersion = V5SemanticRequestComposerV2_1.Version,
            },
        };
    }

    private static object FontInventory()
    {
        var roots = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts") }
            : OperatingSystem.IsMacOS()
                ? new[] { "/System/Library/Fonts", "/Library/Fonts", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts") }
                : new[] { "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"),
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts") };
        var files = roots.Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".pfb" or ".afm")
            .Select(path => (Path: path.Replace('\\', '/'), Sha: FileSha(path), Size: new FileInfo(path).Length))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        // SRC-089 references TimesNewRomanPSMT (+ Bold/Italic/BoldItalic) without embedding them.
        var relevant = files.Where(item =>
        {
            var name = Path.GetFileName(item.Path).ToLowerInvariant();
            return name.StartsWith("times", StringComparison.Ordinal) || name.StartsWith("liberationserif", StringComparison.Ordinal);
        }).Select(item => new { path = item.Path, sha256 = item.Sha, bytes = item.Size }).ToArray();
        return new
        {
            searchRoots = roots,
            fontFileCount = files.Length,
            inventorySha256 = V5CohortPreflightBuilder.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(item => $"{item.Path} {item.Sha}")))),
            referencedByInputs = new[] { "TimesNewRomanPSMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-ItalicMT", "TimesNewRomanPS-BoldItalicMT" },
            relevant,
        };
    }

    // ---------------------------------------------------------------- execute (the only provider path)

    private static async Task<int> ExecuteAsync(string root, bool authorized, string cohortRoot)
    {
        var preflightDir = cohortRoot + "/preflight";
        var providerDir = cohortRoot + "/provider";
        var cohortPath = Path.Combine(root, preflightDir, "cohort.v1.json");
        var environmentPath = Path.Combine(root, preflightDir, "environment.v1.json");
        if (!File.Exists(cohortPath) || !File.Exists(environmentPath))
            return Fail("cohort31 execute: no frozen preflight");
        var environment = JsonNode.Parse(File.ReadAllText(environmentPath))!;
        if (environment["environmentParity"]?.GetValue<bool>() != true)
            return Fail("cohort31 execute: environmentParity is not true");

        // Source identity: nothing outside artifacts/ may differ from the commit that froze the cohort.
        var sourceHead = environment["fingerprint"]!["executionSourceHead"]!.GetValue<string>();
        // Fail closed: a git error (null) counts as a difference, never as "no difference".
        if (Git(root, $"diff --name-only {sourceHead} HEAD -- . :(exclude)artifacts") is not "" ||
            Git(root, "status --porcelain -- . :(exclude)artifacts") is not "")
            return Fail($"cohort31 execute: source differs from the frozen execution source head {sourceHead}");

        // Hash gate: a fresh rebuild on THIS machine must reproduce every frozen byte.
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var rebuilt = V5CohortPreflightBuilder.Build(root, contract);
        var frozenBytes = File.ReadAllBytes(cohortPath);
        var rebuiltBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(CohortDocument(rebuilt), Indented) + Environment.NewLine);
        if (!frozenBytes.AsSpan().SequenceEqual(rebuiltBytes))
            return Fail("cohort31 execute: rebuilt cohort differs from the frozen cohort - environment parity FAILED, no call made");
        foreach (var row in rebuilt.Rows)
        {
            var frozenBody = File.ReadAllBytes(Path.Combine(root, preflightDir, "bodies", BodyFileName(row)));
            if (!frozenBody.AsSpan().SequenceEqual(row.ProviderBody) ||
                V5CohortPreflightBuilder.Sha256(frozenBody) != row.ProviderRequestHash)
                return Fail($"cohort31 execute: body {row.Ordinal} differs from the frozen body - no call made");
        }
        Console.WriteLine("cohort31 execute: environment parity PASS (all 31 bodies reproduced byte for byte)");

        if (!authorized)
        {
            Console.WriteLine($"Not authorized (pass --confirm-cohort31={ConfirmSentinel}). providerCalls=0, goldRead=false.");
            return 0;
        }
        V5CohortGate.Authorize(rebuilt.Rows.Count, authorized);
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Fail("OPENROUTER_API_KEY is not set. Refusing to run.");
        var providerRoot = Path.Combine(root, providerDir);
        if (Directory.Exists(providerRoot))
            return Fail($"cohort31 execute: {providerDir} already exists; a cohort is executed exactly once");
        Directory.CreateDirectory(providerRoot);

        var executionHead = Git(root, "rev-parse HEAD");
        var wall = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var calls = new List<object>();
        var transport = new List<V5CohortCallTransport>();
        RemoteInferenceOptions? recordedOptions = null;
        foreach (var row in rebuilt.Rows)
        {
            var callDir = Path.Combine(providerRoot, "calls", Path.GetFileNameWithoutExtension(BodyFileName(row)));
            Directory.CreateDirectory(callDir);
            var options = RemoteInferenceOptions.FromEnvironment();
            // Frozen transport rules, independent of whatever the shell exports.
            options.TransientRequestRetries = 2;
            options.ProviderTransportTimeoutSeconds = V5CohortPreflightBuilder.Envelope.TimeoutSeconds;
            options.DebugLog = null;
            options.Observability = new ProviderObservabilityOptions
            {
                RootDirectory = callDir,
                CampaignId = "v5-provider-cohort-31",
                DocumentId = row.DocumentId,
                Provider = V5CohortPreflightBuilder.Envelope.Provider,
                Model = V5CohortPreflightBuilder.Envelope.Model,
            };
            recordedOptions ??= options;

            string? content = null;
            string? finishReason = null;
            string? transportError = null;
            var callStarted = DateTimeOffset.UtcNow;
            var latency = Stopwatch.StartNew();
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                (content, finishReason) = await client.ExecuteAsync(
                    row.ProviderBody, row.MaxCompletionTokens, V5SystemPromptV2_1.Text, ExtractUserMessage(row.ProviderBody));
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }
            latency.Stop();
            var callEnded = DateTimeOffset.UtcNow;

            if (content is not null)
                File.WriteAllText(Path.Combine(callDir, "content.txt"), content, new UTF8Encoding(false));
            var attempts = ReadAttempts(callDir);
            var usage = attempts.LastOrDefault(item => item.Usage is not null)?.Usage;
            var record = new
            {
                schemaVersion = "v5-provider-cohort-31-call-v1",
                ordinal = row.Ordinal,
                documentId = row.DocumentId,
                packId = row.PackId,
                providerRequestHash = row.ProviderRequestHash,
                provider = V5CohortPreflightBuilder.Envelope.Provider,
                model = V5CohortPreflightBuilder.Envelope.Model,
                startedAt = callStarted,
                endedAt = callEnded,
                latencyMs = latency.Elapsed.TotalMilliseconds,
                transportSucceeded = content is not null,
                transportError,
                finishReason,
                attempts = attempts.Select(item => item.ToJson()).ToArray(),
                usage,
                reassembledContentSha256 = content is null ? null : V5CohortPreflightBuilder.Sha256(Encoding.UTF8.GetBytes(content)),
                parsedResponseSha256 = ParsedSha(content),
            };
            WriteJson(Path.Combine(callDir, "call.v1.json"), record);
            calls.Add(record);
            transport.Add(new V5CohortCallTransport(row.Ordinal, row.DocumentId, row.PackId, content is not null,
                Math.Max(1, attempts.Count), finishReason,
                UsageInt(usage, "prompt_tokens"), UsageInt(usage, "completion_tokens"),
                UsageInt(usage, "completion_tokens_details", "reasoning_tokens"), latency.Elapsed.TotalMilliseconds));
            Console.WriteLine($"  [{row.Ordinal:00}/31] {row.DocumentId} {row.PackId}: finish={finishReason ?? "-"} attempts={attempts.Count} latencyMs={latency.Elapsed.TotalMilliseconds:0} error={transportError ?? "-"}");
        }
        wall.Stop();

        // Raw provider artifacts are frozen FIRST, before anything is derived from them.
        WriteJson(Path.Combine(providerRoot, "run-manifest.v1.json"), new
        {
            schemaVersion = "v5-provider-cohort-31-run-v1",
            executionHead,
            executionSourceHead = sourceHead,
            startedAt,
            endedAt = DateTimeOffset.UtcNow,
            providerCalls = calls.Count,
            semanticRetries = 0,
            goldRead = false,
            transportRules = new
            {
                transientRequestRetries = recordedOptions?.TransientRequestRetries,
                providerTransportTimeoutSeconds = recordedOptions?.ProviderTransportTimeoutSeconds,
                retryableHttpStatuses = new[] { 429, 502, 503, 504 },
                endpoint = recordedOptions?.Endpoint.ToString(),
            },
            transportAggregate = V5CohortTransportAggregate.From(transport, wall.Elapsed.TotalMilliseconds),
            calls = transport,
        });
        Console.WriteLine($"cohort31 execute: raw provider artifacts frozen under {providerDir}");
        return Qualify(root, cohortRoot);
    }

    private static string ExtractUserMessage(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
    }

    private sealed record AttemptRecord(
        int Number, string AttemptId, string? StartedAt, string? EndedAt, int? HttpStatus, double? RetryAfterSeconds,
        string Outcome, bool? Retryable, string? FinishReason, bool? DoneObserved, bool? CleanEof,
        JsonElement? Usage, string? RawSseSha256)
    {
        public object ToJson() => new
        {
            attemptNumber = Number, attemptId = AttemptId, startedAt = StartedAt, endedAt = EndedAt, httpStatus = HttpStatus,
            retryAfterSeconds = RetryAfterSeconds, retryClassification = Outcome, retryable = Retryable,
            sseCompletion = FinishReason is not null && DoneObserved == true && CleanEof == true ? "COMPLETE" : "INCOMPLETE",
            finishReason = FinishReason, doneObserved = DoneObserved, cleanEof = CleanEof, usage = Usage, rawSseSha256 = RawSseSha256,
        };
    }

    /// <summary>Reconstructs every attempt of one logical call from its own telemetry directory.</summary>
    private static List<AttemptRecord> ReadAttempts(string callDir)
    {
        var telemetry = Path.Combine(callDir, "telemetry");
        var eventsPath = Path.Combine(telemetry, "events.jsonl");
        if (!File.Exists(eventsPath)) return [];
        var events = File.ReadAllLines(eventsPath).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return events.Where(e => Str(e, "eventType") == "ATTEMPT_STARTED")
            .Select((started, index) =>
            {
                var id = Str(started, "attemptId")!;
                var mine = events.Where(e => Str(e, "attemptId") == id).ToArray();
                var headers = mine.FirstOrDefault(e => Str(e, "eventType") == "RESPONSE_HEADERS_RECEIVED");
                var complete = mine.FirstOrDefault(e => Str(e, "eventType") == "TRANSPORT_COMPLETE");
                var failed = mine.FirstOrDefault(e => Str(e, "eventType") == "ATTEMPT_FAILED");
                var ended = mine.LastOrDefault(e => Str(e, "eventType") is "ATTEMPT_COMPLETED" or "ATTEMPT_FAILED");
                int? status = headers.ValueKind == JsonValueKind.Object && headers.TryGetProperty("status", out var s) ? s.GetInt32() : null;
                JsonElement? failData = failed.ValueKind == JsonValueKind.Object && failed.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;
                double? retryAfter = failData is { } fd && fd.TryGetProperty("retryAfterSeconds", out var ra) && ra.ValueKind == JsonValueKind.Number ? ra.GetDouble() : null;
                bool? retryable = failData is { } fr && fr.TryGetProperty("retryable", out var rb) && rb.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? rb.GetBoolean()
                    : failed.ValueKind == JsonValueKind.Object ? true : null; // every non-HTTP transport failure is retryable
                bool? Flag(string name) => complete.ValueKind == JsonValueKind.Object && complete.TryGetProperty(name, out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : null;
                JsonElement? usage = complete.ValueKind == JsonValueKind.Object && complete.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u.Clone() : null;
                var raw = Path.Combine(telemetry, $"response.raw.{id}.txt");
                return new AttemptRecord(index + 1, id, Str(started, "timestamp"), ended.ValueKind == JsonValueKind.Object ? Str(ended, "timestamp") : null,
                    status, retryAfter,
                    complete.ValueKind == JsonValueKind.Object ? "SUCCESS" : failed.ValueKind == JsonValueKind.Object ? Str(failed, "reason") ?? "FAILED" : "UNKNOWN",
                    complete.ValueKind == JsonValueKind.Object ? false : retryable,
                    complete.ValueKind == JsonValueKind.Object ? Str(complete, "finishReason") : null,
                    Flag("doneObserved"), Flag("cleanEof"), usage, File.Exists(raw) ? FileSha(raw) : null);
            })
            .ToList();
    }

    private static int UsageInt(JsonElement? usage, params string[] path)
    {
        if (usage is not { } current) return 0;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return 0;
        }
        return current.ValueKind == JsonValueKind.Number ? current.GetInt32() : 0;
    }

    private static string? ParsedSha(string? content)
    {
        if (content is null) return null;
        try
        {
            using var document = JsonDocument.Parse(content);
            return V5CohortPreflightBuilder.Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document.RootElement)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- qualify (provider-free)

    private static int Qualify(string root, string cohortRoot)
    {
        var preflightDir = cohortRoot + "/preflight";
        var providerRoot = Path.Combine(root, cohortRoot + "/provider");
        if (!File.Exists(Path.Combine(providerRoot, "run-manifest.v1.json")))
            return Fail("cohort31 qualify: no frozen provider run");
        var cohort = JsonNode.Parse(File.ReadAllText(Path.Combine(root, preflightDir, "cohort.v1.json")))!;
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var atomsByDoc = V5CohortPreflightBuilder.Documents.ToDictionary(
            item => item.DocumentId,
            item => V5PdfPreflightBuilder.LoadAtoms(Path.Combine(root, item.PdfRelativePath)),
            StringComparer.Ordinal);

        var packs = new List<object>();
        var qualifications = new List<V5PackBindingQualification>();
        foreach (var row in cohort["rows"]!.AsArray())
        {
            var ordinal = row!["Ordinal"]!.GetValue<int>();
            var documentId = row["DocumentId"]!.GetValue<string>();
            var packId = row["PackId"]!.GetValue<string>();
            var callDir = Path.Combine(providerRoot, "calls", Path.GetFileNameWithoutExtension(row["bodyFile"]!.GetValue<string>()));
            var call = JsonNode.Parse(File.ReadAllText(Path.Combine(callDir, "call.v1.json")))!;
            var contentPath = Path.Combine(callDir, "content.txt");
            var content = File.Exists(contentPath) ? File.ReadAllText(contentPath) : null;
            var scope = ClaimBindingScope.Create(
                row["ownedAliases"]!.AsArray().Select(item => item!.GetValue<string>()),
                row["visibleAliases"]!.AsArray().Select(item => item!.GetValue<string>()));
            // The same codec, contract validation, binder, scope and classifier as runtime qualification.
            var (qualification, _, _) = V5BindingQualifier.Qualify(
                content, call["finishReason"]?.GetValue<string>(), call["transportError"]?.GetValue<string>(),
                contract, packId, atomsByDoc[documentId], scope);
            qualifications.Add(qualification);
            packs.Add(new
            {
                ordinal,
                documentId,
                packId,
                responseUsable = qualification.ResponseUsable,
                outcome = qualification.Outcome.ToString(),
                proposalCount = qualification.ProposalCount,
                boundCount = qualification.BoundCount,
                refusalCount = qualification.RefusalCount,
                boundFraction = qualification.BoundFraction,
                runtimeProcessedSafely = qualification.RuntimeProcessedSafely,
                unsafeRepairCount = qualification.UnsafeRepairCount,
                outOfScopeAcceptedCount = qualification.OutOfScopeAcceptedCount,
                qualification = qualification.ToReport(),
            });
        }

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(providerRoot, "run-manifest.v1.json")))!;
        WriteJson(Path.Combine(root, cohortRoot, "cohort-result.v1.json"), new
        {
            schemaVersion = "v5-provider-cohort-31-result-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION",
            goldRead = false,
            headingF1Scored = false,
            refusalThreshold = (string?)null,
            transport = manifest["transportAggregate"],
            binding = V5QualificationAggregate.From(qualifications),
            packs,
        });
        Console.WriteLine($"cohort31 qualify: wrote {cohortRoot}/cohort-result.v1.json (provider-free, goldRead=false)");
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, string> RelativeFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(dir, path).Replace('\\', '/'), FileSha, StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static string FileSha(string path) => V5CohortPreflightBuilder.Sha256(File.ReadAllBytes(path));

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Indented) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string? OsReleasePrettyName()
    {
        try
        {
            return File.Exists("/etc/os-release")
                ? File.ReadAllLines("/etc/os-release").FirstOrDefault(line => line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?[12..].Trim('"')
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? Git(string root, string arguments) => Run("git", arguments, root);

    private static string? Run(string file, string arguments, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
