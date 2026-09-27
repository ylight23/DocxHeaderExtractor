using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline-only preflight for the first semantic-function arm. It composes the actual PDF route's
/// requests into a capturing classifier; it neither constructs a provider nor opens Gold.
/// </summary>
public sealed class SemanticFunctionSingleAuthorityV4PreflightTests
{
    private const string Root = "eval/a99-closed-loop/semantic-function-single-authority-v4";
    private const string Model = "qwen/qwen3.7-flash";
    private const string PromptSha256 = "e996bef4346efff9b0544f34777192d5b59c7e7f75a749dbf91cbe9f0d5df93b";
    private const string SchemaSha256 = "7d8ae805c0373dad3795d819cd5b0229b9421c28600d5d6b001e818f839225db";
    private const int MaxCalls = 30;
    private const long MaxInputTokens = 2_000_000;
    private const long MaxOutputTokens = 250_000;
    private const int ProductionMaxOutputTokens = 32768;
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf),
    ];

    private static readonly CanonicalSemanticExperiment V4 = CanonicalSemanticExperiment.Baseline with
    {
        RequestVersion = SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY,
    };

    private static Task<StructuralAuthorityResult> Route(string pdf, IHeaderClassifier classifier) =>
        CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), classifier, CancellationToken.None,
            experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
            packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
            sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);

    [Fact]
    public void V4_is_explicit_only_and_normal_host_selection_remains_v2()
    {
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, SemanticRequestVersions.ProductionDefault);
        Assert.Equal(SemanticRequestVersion.V2_ATTENTION_FREE, CanonicalSemanticExperiment.Baseline.RequestVersion);
        Assert.NotEqual(V4.RequestVersion, CanonicalSemanticExperiment.Baseline.RequestVersion);
        Assert.Equal(SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY,
            SemanticRequestVersions.Require(V4.RequestVersion));

        var normalPrompt = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfStructuredSourceParts, CanonicalSemanticExperiment.Baseline);
        var v4Prompt = CanonicalSemanticEngine.SystemPromptFor(
            SemanticCoordinateContract.PdfSemanticFunctionMembershipV1, V4);
        Assert.NotEqual(normalPrompt, v4Prompt);
        Assert.DoesNotContain("semanticFunction", normalPrompt, StringComparison.Ordinal);
        Assert.Contains("semanticFunction", v4Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void V4_schema_is_closed_and_has_no_legacy_or_descriptive_membership_axis()
    {
        var schema = JsonSerializer.Serialize(SemanticFunctionMembershipContractV1.Schema());
        foreach (var absent in new[] { "isHeading", "semanticRole", "occurrenceRole", "scope", "titleRelation", "\"heading\"" })
            Assert.DoesNotContain(absent, schema, StringComparison.Ordinal);
        Assert.Equal(9, SemanticFunctionMembershipContractV1.Functions.Length);
        Assert.True(SemanticFunctionMembershipContractV1.IsMember("DOCUMENT_IDENTITY"));
        Assert.True(SemanticFunctionMembershipContractV1.IsMember("REGION_STRUCTURE"));
        Assert.False(SemanticFunctionMembershipContractV1.IsMember("NAVIGATION"));
        Assert.False(SemanticFunctionMembershipContractV1.IsMember("unknown"));

        using var invalid = JsonDocument.Parse("""
            {"headings":[{"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"NAVIGATION","isHeading":true}]}
            """);
        Assert.Contains(SemanticFunctionMembershipContractV1.ValidateJson(invalid.RootElement),
            issue => issue.Code == "FIELD_NOT_IN_CONTRACT" && issue.SourceAlias == "isHeading");
    }

    [Fact]
    public void V4_runtime_derives_membership_from_the_closed_function_not_from_a_second_field()
    {
        using var reply = JsonDocument.Parse("""
            {"headings":[
              {"sourceParts":[{"sourceAlias":"L0001:S0"}],"semanticFunction":"NAVIGATION"},
              {"sourceParts":[{"sourceAlias":"L0002:S0"}],"semanticFunction":"REGION_STRUCTURE"}
            ]}
            """);
        Assert.Empty(SemanticFunctionMembershipContractV1.ValidateJson(reply.RootElement));
        var entries = reply.RootElement.GetProperty("headings").EnumerateArray().ToArray();
        var navigation = Assert.Single(SemanticFunctionMembershipContractV1.Decode(entries[0]).Proposals);
        var region = Assert.Single(SemanticFunctionMembershipContractV1.Decode(entries[1]).Proposals);
        Assert.False(navigation.IsHeading);
        Assert.True(region.IsHeading);
        Assert.Equal("NAVIGATION", navigation.SemanticRole);
        Assert.Equal("REGION_STRUCTURE", region.SemanticRole);
    }

    [Fact]
    public async Task Freeze_the_provider_free_v4_preflight()
    {
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_V4_RUN") is "1" or "true" or "TRUE");
        var captured = new List<object>();
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await Route(pdf, capture);
            Assert.NotEmpty(capture.Requests);
            Assert.All(capture.Requests, request =>
            {
                Assert.Equal(CanonicalSemanticEngine.SystemPromptFor(
                    SemanticCoordinateContract.PdfSemanticFunctionMembershipV1, V4), request.SystemPrompt);
                Assert.Contains("\"semanticFunction\"", request.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("\"isHeading\"", request.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("\"semanticRole\"", request.UserMessage, StringComparison.Ordinal);
            });
            captured.Add(new
            {
                documentId = id,
                requests = capture.Requests.Count,
                requestSha256 = capture.Requests.Select(request => Sha(request.UserMessage)).ToArray(),
                expectedOwnedItems = capture.Requests.Select(request => request.ExpectedItemCount).ToArray(),
            });
        }

        FreezeArtifact.AssertJson(Root, "preflight.v1.json", new
        {
            artifactKind = "a99_semantic_function_single_authority_v4_preflight",
            study = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
            preflightOnly = true,
            modelProviderCalls = 0,
            requestVersion = V4.RequestVersion.ToString(),
            prompt = new
            {
                sha256 = Sha(CanonicalSemanticEngine.SystemPromptFor(
                    SemanticCoordinateContract.PdfSemanticFunctionMembershipV1, V4)),
                lineEndings = "LF normalized by CanonicalSemanticEngine",
            },
            schema = new
            {
                protocol = SemanticFunctionMembershipContractV1.ProtocolVersion,
                sha256 = SemanticCoordinateContract.PdfSemanticFunctionMembershipV1.SchemaHash(),
                outputFields = new[] { "sourceParts", "semanticFunction" },
            },
            factsVersion = PdfSourceFactsVersion.V3_RobustGlyphStatistics.ToString(),
            packing = new
            {
                policy = SemanticEvidencePackingPolicies.FixedOwnedCount120.PolicyId,
                version = SemanticEvidencePackingPolicies.FixedOwnedCount120.PolicyVersion,
                ownedPerPack = SemanticEvidencePackingPolicies.OwnedPerPack,
            },
            model = new { identity = Model, provider = "OpenRouter", notConstructedInPreflight = true },
            binder = "SemanticSourcePartCanonicalizer + SemanticSourcePartBinder (existing production binder)",
            documents = captured,
            gates = new
            {
                v2ProductionDefaultUnchanged = SemanticRequestVersions.ProductionDefault == SemanticRequestVersion.V2_ATTENTION_FREE,
                v4ExplicitSelectionOnly = true,
                normalHostPathCannotSelectV4WithoutExperiment = true,
                legacyFieldsAbsentFromSchema = true,
                goldFilesOpenedBeforeRawPredictionsPersist = false,
                noCohortExpansion = true,
                noPostFilter = true,
                noHierarchyArm = true,
            },
            nextGate = "Provider execution requires explicit authorization; persist raw predictions before opening Gold.",
        });
    }

    [Fact]
    public async Task Run_the_authorized_v4_arm()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_V4_RUN") != "1") return;
        var runPath = TestRepository.Path($"{Root}/run.v1.json");
        Assert.False(File.Exists(runPath), "V4 raw predictions already persist; refusing a second provider run.");
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/preflight.v1.json")));
        Assert.Equal(PromptSha256, preflight.RootElement.GetProperty("prompt").GetProperty("sha256").GetString());
        Assert.Equal(SchemaSha256, preflight.RootElement.GetProperty("schema").GetProperty("sha256").GetString());
        Assert.Equal("V3_RobustGlyphStatistics", preflight.RootElement.GetProperty("factsVersion").GetString());
        Assert.Equal("FIXED_OWNED_COUNT_120", preflight.RootElement.GetProperty("packing").GetProperty("policy").GetString());
        Assert.Equal(Model, preflight.RootElement.GetProperty("model").GetProperty("identity").GetString());
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        var expected = await PlannedHashes();
        Assert.Equal(25, expected.Sum(item => item.Value.Count));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var gate = new Gate(expected, MaxCalls, MaxInputTokens, MaxOutputTokens);
        var documents = new List<object>();
        string? stopped = null;
        foreach (var (id, pdf) in Documents)
        {
            if (stopped is not null) break;
            var telemetry = TestRepository.Path($"{Root}/{id}");
            Directory.CreateDirectory(telemetry);
            using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
            {
                ApiKey = apiKey,
                Model = Model,
                Observability = new ProviderObservabilityOptions { RootDirectory = telemetry, CampaignId = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY", DocumentId = id, Provider = "OpenRouter", Model = Model },
            });
            gate.Begin(id, provider, Path.Combine(telemetry, "telemetry"));
            var before = gate.Ledger.Count;
            try
            {
                var result = await Route(pdf, gate);
                documents.Add(new { documentId = id, completed = true, calls = gate.Ledger.Count - before, elements = result.Structure.Elements.Count });
            }
            catch (Exception error)
            {
                stopped = $"{id}: {error.GetType().Name}: {error.Message}";
                documents.Add(new { documentId = id, completed = false, calls = gate.Ledger.Count - before, elements = 0 });
            }
        }

        File.WriteAllText(runPath, JsonSerializer.Serialize(new
        {
            artifactKind = "a99_semantic_function_single_authority_v4_raw_run",
            study = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
            preflightSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/preflight.v1.json")),
            pins = new { requestVersion = V4.RequestVersion.ToString(), promptSha256 = PromptSha256, schemaSha256 = SchemaSha256, facts = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", model = Model },
            goldRead = false, hierarchyRun = false, postFilterApplied = false, stopped,
            totals = new { calls = gate.Ledger.Count, inputTokensCharged = gate.InputUsed, outputTokensCharged = gate.OutputUsed, caps = new { calls = MaxCalls, input = MaxInputTokens, output = MaxOutputTokens } },
            documents, ledger = gate.Ledger,
        }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        Assert.Null(stopped);
    }

    private static async Task<Dictionary<string, Queue<string>>> PlannedHashes()
    {
        var planned = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await Route(pdf, capture);
            planned[id] = new Queue<string>(capture.Requests.Select(request => Sha(request.UserMessage)));
        }
        return planned;
    }

    [Fact]
    public async Task Run_the_authorized_v4_continuation()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_V4_RESUME") != "1") return;
        var firstPath = TestRepository.Path($"{Root}/run.v1.json");
        var continuationPath = TestRepository.Path($"{Root}/continuation.v1.json");
        Assert.True(File.Exists(firstPath));
        Assert.False(File.Exists(continuationPath));
        using var first = JsonDocument.Parse(File.ReadAllText(firstPath));
        var totals = first.RootElement.GetProperty("totals");
        Assert.Equal(6, totals.GetProperty("calls").GetInt32());
        Assert.Equal(211022L, totals.GetProperty("inputTokensCharged").GetInt64());
        Assert.Equal(25799L, totals.GetProperty("outputTokensCharged").GetInt64());
        var successful = first.RootElement.GetProperty("ledger").EnumerateArray()
            .Where(x => x.GetProperty("Error").ValueKind == JsonValueKind.Null && x.GetProperty("Response").ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("RequestSha256").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(5, successful.Count);
        var planned = await PlannedHashes();
        foreach (var queue in planned.Values) planned[planned.First(x => x.Value == queue).Key] = new Queue<string>(queue.Where(hash => !successful.Contains(hash)));
        Assert.Empty(planned["SRC-089"]);
        Assert.Equal(20, planned["SRC-095"].Count);
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var gate = new Gate(planned, 24, 1_788_978, 224_201);
        var telemetry = TestRepository.Path($"{Root}/SRC-095-continuation");
        Directory.CreateDirectory(telemetry);
        using var provider = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions { ApiKey = apiKey, Model = Model,
            Observability = new ProviderObservabilityOptions { RootDirectory = telemetry, CampaignId = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY_CONTINUATION", DocumentId = "SRC-095", Provider = "OpenRouter", Model = Model } });
        gate.Begin("SRC-095", provider, Path.Combine(telemetry, "telemetry"));
        string? stopped = null;
        try { await Route(Src095BlindGeneralizationTests.Pdf, gate); }
        catch (Exception error) { stopped = $"SRC-095: {error.GetType().Name}: {error.Message}"; }
        File.WriteAllText(continuationPath, JsonSerializer.Serialize(new { artifactKind="a99_semantic_function_v4_continuation", attempt1Sha256=CanonicalArtifactHash.OfTextFile(firstPath), goldRead=false, hierarchyRun=false, postFilterApplied=false, carried=new { calls=6,input=211022,output=25799 }, stopped, ledger=gate.Ledger, totals=new { calls=6+gate.Ledger.Count,input=211022+gate.InputUsed,output=25799+gate.OutputUsed } }, FreezeArtifact.Json).ReplaceLineEndings("\n"));
        Assert.Null(stopped);
        Assert.Empty(planned["SRC-095"]);
    }

    private sealed record LedgerEntry(int Ordinal, string DocumentId, string SystemPromptSha256, string RequestSha256,
        int ExpectedItemCount, long InputBytes, int MaxTokens, int? PromptTokens, int? CompletionTokens, long ElapsedMs, string? Error, string? Response);

    private sealed class Gate(Dictionary<string, Queue<string>> planned, int callCap, long inputCap, long outputCap) : IHeaderClassifier
    {
        private IHeaderClassifier? _inner;
        private string _document = "";
        private string _telemetry = "";
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public List<LedgerEntry> Ledger { get; } = [];
        public long InputUsed { get; private set; }
        public long OutputUsed { get; private set; }
        public void Begin(string document, IHeaderClassifier inner, string telemetry) => (_document, _inner, _telemetry) = (document, inner, telemetry);
        public string ModelName => _inner!.ModelName;
        public int ContextSize => _inner!.ContextSize;
        public string RuntimeDescription => _inner!.RuntimeDescription;
        public int SharedPrefixTokens => _inner!.SharedPrefixTokens;

        public async Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Assert.Equal(PromptSha256, Sha(systemPrompt));
            Assert.True(planned.TryGetValue(_document, out var expected) && expected.Count > 0, "UNPLANNED_V4_REQUEST");
            Assert.Equal(expected!.Dequeue(), Sha(userMessage));
            var inputBytes = (long)Encoding.UTF8.GetByteCount(systemPrompt) + Encoding.UTF8.GetByteCount(userMessage);
            var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(userMessage, expectedItemCount, ProductionMaxOutputTokens);
            if (Ledger.Count >= callCap) throw new InvalidOperationException("V4_CALL_CAP_REACHED");
            if (InputUsed + inputBytes > inputCap) throw new InvalidOperationException("V4_INPUT_CAP_WOULD_BE_EXCEEDED");
            if (OutputUsed + maxTokens > outputCap) throw new InvalidOperationException("V4_OUTPUT_CAP_WOULD_BE_EXCEEDED");
            var started = DateTimeOffset.UtcNow;
            string? response = null;
            string? error = null;
            try { response = await _inner!.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount); return response; }
            catch (Exception exception) { error = $"{exception.GetType().Name}: {exception.Message}"; throw; }
            finally
            {
                var usage = LatestUsage();
                InputUsed += usage.Prompt ?? inputBytes;
                OutputUsed += usage.Completion ?? maxTokens;
                Ledger.Add(new(Ledger.Count + 1, _document, Sha(systemPrompt), Sha(userMessage), expectedItemCount, inputBytes, maxTokens,
                    usage.Prompt, usage.Completion, (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, error, response));
            }
        }

        private (int? Prompt, int? Completion) LatestUsage()
        {
            if (!Directory.Exists(_telemetry)) return (null, null);
            var files = Directory.GetFiles(_telemetry, "response.raw.*.txt").Where(file => _seen.Add(file)).ToArray();
            if (files.Length != 1) return (null, null);
            try
            {
                using var raw = JsonDocument.Parse(File.ReadAllText(files[0]));
                if (!raw.RootElement.TryGetProperty("usage", out var usage)) return (null, null);
                return (usage.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : null,
                    usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : null);
            }
            catch (JsonException) { return (null, null); }
        }

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
