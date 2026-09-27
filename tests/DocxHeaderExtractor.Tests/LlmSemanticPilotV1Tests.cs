using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// LLM_SEMANTIC_PILOT_V1 - the first measurement of the production LLM semantic pipeline against occurrence Gold, on two
/// documents only (user authorization 2026-09-27): SRC-089 (decree), where the deterministic V1.4 scored F1 0 on claim
/// extent, and SRC-095 (RFC). Frozen: PDF_SOURCE_FACTS_V3, request version V2_ATTENTION_FREE over the structured-source-parts
/// profile (MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V3), packing FIXED_OWNED_COUNT_120, model qwen/qwen3.7-flash, the
/// production binder. Hard caps, fail-closed and never raised: 30 provider calls, 2.0M input tokens, 250k output tokens,
/// retries included. Placement (hierarchy) is not run: it would spend calls on what nothing here scores.
/// <para>
/// Order: the preflight (no provider) is committed first; the run persists every raw provider response and never opens
/// a Gold; the score reads the Gold only after the run is committed, once, with the held-out scorer.
/// </para>
/// </summary>
public sealed class LlmSemanticPilotV1Tests
{
    internal const string Root = "eval/a99-closed-loop/llm-semantic-pilot-v1";
    private const string Model = "qwen/qwen3.7-flash";
    private const int MaxCalls = 30;
    private const long MaxInputTokens = 2_000_000;
    private const long MaxOutputTokens = 250_000;
    private const int ProductionMaxOutputTokens = 32768; // RemoteInferenceOptions default, unchanged
    private const string ContractFreeze = "eval/a99-closed-loop/hardcode-audit-v1/MODEL_VISIBLE_CONTRACT_V2.PDF_SOURCE_FACTS_V3.freeze.json";
    private const string Doc0252Pdf = "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";

    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf),
    ];

    private static readonly SemanticLaneOptions Lane = new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(60));

    private static Task<StructuralAuthorityResult> Route(string pdf, IHeaderClassifier classifier) =>
        CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), classifier, CancellationToken.None,
            semanticLaneOptions: Lane, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
            runPlacement: false, sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics);

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string Plan(IEnumerable<CapturedRequest> requests) =>
        CanonicalSemanticRequestComposer.Hash(string.Join("\u0000", requests.Select(r => CanonicalSemanticRequestComposer.Hash(r.UserMessage))));

    private static async Task<object> Preflight()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ContractFreeze)));
        var frozen = contract.RootElement.GetProperty("contract");

        // The pilot's route is the frozen contract's route: the same call signature reproduces its DOC-0252 plan offline.
        using var check = new RequestCapturingClassifier();
        await Route(Doc0252Pdf, check);
        Assert.Equal(frozen.GetProperty("structuredPdf").GetProperty("providerModelInputPlanSha256").GetString(), Plan(check.Requests));
        // The frozen hash is the production base prompt; the prompt sent also carries the coordinate contract's clause.
        Assert.Equal(frozen.GetProperty("systemPromptSha256").GetString(), CanonicalArtifactHash.OfText(CanonicalSemanticEngine.SystemPrompt));
        Assert.All(check.Requests, r => Assert.StartsWith(CanonicalSemanticEngine.SystemPrompt, r.SystemPrompt, StringComparison.Ordinal));
        var sentSystemPrompts = check.Requests.Select(r => CanonicalArtifactHash.OfText(r.SystemPrompt)).Distinct().ToArray();
        Assert.Single(sentSystemPrompts);

        var docs = new List<object>();
        long inputBound = 0;
        var outputReserve = 0L;
        var requests = 0;
        var largest = 0L;
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await Route(pdf, capture);
            var rows = capture.Requests.Select((r, k) => new
            {
                ordinal = k + 1,
                requestSha256 = Sha(r.UserMessage),
                systemPromptSha256 = Sha(r.SystemPrompt),
                expectedItemCount = r.ExpectedItemCount,
                inputBytes = (long)Encoding.UTF8.GetByteCount(r.SystemPrompt) + Encoding.UTF8.GetByteCount(r.UserMessage),
                maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(r.UserMessage, r.ExpectedItemCount, ProductionMaxOutputTokens),
            }).ToArray();
            inputBound += rows.Sum(r => r.inputBytes);
            outputReserve += rows.Sum(r => r.maxTokens);
            largest = Math.Max(largest, rows.Max(r => r.inputBytes));
            requests += rows.Length;
            docs.Add(new
            {
                documentId = id,
                source = new { path = pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(pdf)) },
                goldFileSha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")),
                requests = rows.Length,
                providerModelInputPlanSha256 = Plan(capture.Requests),
                perRequest = rows,
            });
        }
        var retryReserve = (MaxCalls - requests) * largest;

        return new
        {
            artifactKind = "a99_llm_semantic_pilot_preflight",
            study = "LLM_SEMANTIC_PILOT_V1",
            authorization = "user, 2026-09-27: SRC-089 and SRC-095 only; qwen/qwen3.7-flash; at most 30 provider calls, 2.0M input tokens, 250k output tokens; retries count; a cap reached stops the run, fail-closed",
            frozen = new
            {
                sourceFacts = PdfSourceFactsVersions.Id(PdfSourceFactsVersion.V3_RobustGlyphStatistics),
                requestVersion = frozen.GetProperty("requestVersion").GetString(),
                contractFreeze = new { path = ContractFreeze, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ContractFreeze)) },
                systemPromptSha256 = frozen.GetProperty("systemPromptSha256").GetString(),
                sentSystemPromptSha256 = sentSystemPrompts[0],
                routeReproducesFrozenDoc0252Plan = true,
                profile = PdfSemanticAuthorityProfile.StructuredSourceParts.ProfileId,
                packing = SemanticEvidencePackingPolicies.Default.PolicyId,
                model = Model,
                providerRoute = "OpenRouter",
                maxTokensFormula = "OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(user, expectedItems, 32768), production, unchanged",
                binder = "SemanticSourcePartCanonicalizer + SemanticSourcePartBinder (production), exact identities only",
                placement = "not run (hierarchy is not scored; it would spend calls)",
                lane = new { requestTimeoutSeconds = 90, batchTimeoutSeconds = 120, laneDeadlineMinutes = 60 },
            },
            caps = new { calls = MaxCalls, inputTokens = MaxInputTokens, outputTokens = MaxOutputTokens, retriesCount = true, onCap = "stop, fail-closed; never raised" },
            enforcement = new
            {
                calls = "counted before each request is sent; the call that would exceed the cap is refused",
                input = "before each request: tokens used (provider usage, else UTF-8 bytes) + this request's UTF-8 bytes must stay within the cap - a byte is an upper bound for a token",
                output = "before each request: tokens used (provider usage, else the request's max_tokens) + this request's max_tokens must stay within the cap",
            },
            plan = new
            {
                requests,
                inputBytesBound = inputBound,
                retryReserveBytes = retryReserve,
                maxTokensSumIfEveryReplyHitItsCeiling = outputReserve,
                requestsWithinCallCap = requests <= MaxCalls,
                plannedInputWithinCapByBytes = inputBound <= MaxInputTokens,
                note = "bytes over-count tokens (English JSON runs several bytes a token), so the byte bound alone may exceed the input cap while the tokens do not; the gate charges the provider's reported usage and, where a response reports none, the worst-case bound - so a missing usage report stops the run early, never past a cap",
            },
            goldRead = "no Gold is opened before the predictions are persisted; the Gold files are pinned by byte hash only",
            documents = docs,
            modelCallsInPreflight = 0,
        };
    }

    [Fact]
    public async Task Freeze_the_preflight() => FreezeArtifact.AssertJson(Root, "preflight.v1.json", await Preflight());

    // ---- the run ------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_the_pilot()
    {
        if (Environment.GetEnvironmentVariable("A99_LLM_PILOT_RUN") != "1") return;
        Assert.False(File.Exists(TestRepository.Path($"{Root}/run.v1.json")), "the pilot has run; it runs once");
        FreezeArtifact.AssertJson(Root, "preflight.v1.json", await Preflight()); // the committed preflight, unchanged
        using (var pre = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/preflight.v1.json"))))
            Assert.True(pre.RootElement.GetProperty("plan").GetProperty("requestsWithinCallCap").GetBoolean());
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "OPENROUTER_API_KEY is not set.");

        // Earlier attempts that stopped fail-closed (attempt-N/run.v1.json) spent part of the one budget: the caps are for
        // the pilot, not for an attempt, so their charges are carried in before anything is sent.
        var prior = Directory.Exists(TestRepository.Path(Root))
            ? Directory.GetDirectories(TestRepository.Path(Root), "attempt-*").Select(d => Path.Combine(d, "run.v1.json")).Where(File.Exists).ToArray()
            : [];
        long priorCalls = 0, priorIn = 0, priorOut = 0;
        foreach (var file in prior)
        {
            using var earlier = JsonDocument.Parse(File.ReadAllText(file));
            var t = earlier.RootElement.GetProperty("totals");
            priorCalls += t.GetProperty("calls").GetInt32();
            priorIn += t.GetProperty("inputTokensCharged").GetInt64();
            priorOut += t.GetProperty("outputTokensCharged").GetInt64();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var gate = new PilotGate((int)(MaxCalls - priorCalls), MaxInputTokens - priorIn, MaxOutputTokens - priorOut);
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
                Observability = new ProviderObservabilityOptions
                {
                    RootDirectory = telemetry, CampaignId = "LLM_SEMANTIC_PILOT_V1", DocumentId = id, Provider = "OpenRouter", Model = Model,
                },
            });
            gate.Begin(id, provider, Path.Combine(telemetry, "telemetry"));
            var before = gate.Ledger.Count;
            try
            {
                var authority = await Route(pdf, gate);
                documents.Add(new { documentId = id, completed = true, calls = gate.Ledger.Count - before, elements = authority.Structure.Elements.Count });
            }
            catch (Exception error)
            {
                stopped = $"{id}: {error.GetType().Name}: {error.Message}";
                documents.Add(new { documentId = id, completed = false, calls = gate.Ledger.Count - before, elements = 0 });
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            artifactKind = "a99_llm_semantic_pilot_run",
            study = "LLM_SEMANTIC_PILOT_V1",
            preflightSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/preflight.v1.json")),
            model = Model,
            goldRead = false,
            stopped,
            totals = new { calls = gate.Ledger.Count, inputTokensCharged = gate.InputUsed, outputTokensCharged = gate.OutputUsed, caps = new { calls = MaxCalls, input = MaxInputTokens, output = MaxOutputTokens } },
            priorAttempts = new { files = prior.Select(f => Path.GetRelativePath(TestRepository.Root(), f).Replace('\\', '/')).ToArray(), calls = priorCalls, inputTokensCharged = priorIn, outputTokensCharged = priorOut },
            pilotTotals = new { calls = priorCalls + gate.Ledger.Count, inputTokensCharged = priorIn + gate.InputUsed, outputTokensCharged = priorOut + gate.OutputUsed },
            documents,
            ledger = gate.Ledger,
        }, FreezeArtifact.Json);
        File.WriteAllText(TestRepository.Path($"{Root}/run.v1.json"), payload.ReplaceLineEndings("\n"));
        Assert.Null(stopped);
    }

    internal sealed record LedgerEntry(
        int Ordinal, string DocumentId, string SystemPromptSha256, string RequestSha256, int ExpectedItemCount, long InputBytes, int MaxTokens,
        int? PromptTokens, int? CompletionTokens, long ElapsedMs, string? Error, string? Response);

    /// <summary>The only way to the provider: counts calls, bounds input and output tokens before each request, fail-closed.</summary>
    private sealed class PilotGate(int maxCalls, long maxInput, long maxOutput) : IHeaderClassifier
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
            var inputBytes = (long)Encoding.UTF8.GetByteCount(systemPrompt) + Encoding.UTF8.GetByteCount(userMessage);
            var maxTokens = OpenRouterHeaderExtractor.BoundaryOutputBudgetFor(userMessage, expectedItemCount, ProductionMaxOutputTokens);
            if (Ledger.Count >= maxCalls) throw new InvalidOperationException($"CALL_CAP_REACHED at {maxCalls}; refusing to send.");
            if (InputUsed + inputBytes > maxInput) throw new InvalidOperationException($"INPUT_TOKEN_CAP_WOULD_BE_EXCEEDED ({InputUsed} + {inputBytes} > {maxInput}); refusing to send.");
            if (OutputUsed + maxTokens > maxOutput) throw new InvalidOperationException($"OUTPUT_TOKEN_CAP_WOULD_BE_EXCEEDED ({OutputUsed} + {maxTokens} > {maxOutput}); refusing to send.");

            var started = DateTimeOffset.UtcNow;
            string? response = null;
            string? error = null;
            try
            {
                response = await _inner!.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);
                return response;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                throw;
            }
            finally
            {
                var (prompt, completion) = LatestUsage();
                InputUsed += prompt ?? inputBytes;
                OutputUsed += completion ?? maxTokens;
                Ledger.Add(new LedgerEntry(Ledger.Count + 1, _document, Sha(systemPrompt), Sha(userMessage), expectedItemCount, inputBytes, maxTokens,
                    prompt, completion, (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, error, response));
            }
        }

        /// <summary>The provider's usage from the raw response body the telemetry persisted for this call, if any.</summary>
        private (int? Prompt, int? Completion) LatestUsage()
        {
            if (!Directory.Exists(_telemetry)) return (null, null);
            var fresh = Directory.GetFiles(_telemetry, "response.raw.*.txt").Where(f => _seen.Add(f)).ToArray();
            if (fresh.Length != 1) return (null, null);
            try
            {
                using var raw = JsonDocument.Parse(File.ReadAllText(fresh[0]));
                if (!raw.RootElement.TryGetProperty("usage", out var usage)) return (null, null);
                return (usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : null,
                    usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : null);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        public Task<ChunkResult> ClassifyAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> CritiqueAsync(string chunkXml, IReadOnlyList<int> allowedIndexes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChunkResult> ClassifyHierarchyAsync(IReadOnlyList<HierarchyItem> context, IReadOnlyList<HierarchyItem> headings, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    // ---- the score ----------------------------------------------------------------------------------

    [Fact]
    public async Task Score_the_pilot()
    {
        var scorePath = TestRepository.Path($"{Root}/score.v1.json");
        if (Environment.GetEnvironmentVariable("A99_LLM_PILOT_SCORE") != "1" && !File.Exists(scorePath)) return;
        FreezeArtifact.AssertJson(Root, "score.v1.json", await Score());
    }

    private static async Task<object> Score()
    {
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/run.v1.json")));
        var ledger = run.RootElement.GetProperty("ledger").EnumerateArray().ToArray();
        var contract = SemanticCoordinateContract.PdfStructuredSourceParts;
        var documents = new List<object>();
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await Route(pdf, capture); // the requests sent, recomposed offline: owned aliases per request
            var calls = ledger.Where(e => e.GetProperty("DocumentId").GetString() == id).ToArray();
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf), PdfSourceFactsVersion.V3_RobustGlyphStatistics).Atoms;
            var hypotheses = new List<ExactScorer.Hypothesis>();
            var refusals = new Dictionary<string, int>(StringComparer.Ordinal);
            var roles = new Dictionary<string, int>(StringComparer.Ordinal);
            var declined = 0;
            var invalidResponses = 0;
            for (var k = 0; k < calls.Length; k++)
            {
                var request = capture.Requests[k];
                Assert.Equal(Sha(request.UserMessage), calls[k].GetProperty("RequestSha256").GetString()); // sent = frozen plan
                var text = calls[k].GetProperty("Response").GetString();
                if (text is null) { invalidResponses++; continue; }
                var end = request.UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
                using var packet = JsonDocument.Parse(request.UserMessage[..end]);
                var owned = packet.RootElement.GetProperty("sourceEvidence").EnumerateArray()
                    .Where(i => i.GetProperty("owned").GetBoolean()).Select(i => i.GetProperty("alias").GetString()!).ToHashSet(StringComparer.Ordinal);
                JsonDocument response;
                try { response = JsonDocument.Parse(text); }
                catch (JsonException) { invalidResponses++; continue; }
                using (response)
                {
                    if (contract.Validate(response.RootElement).Count > 0 || !response.RootElement.TryGetProperty("headings", out var headings)) { invalidResponses++; continue; }
                    foreach (var entry in headings.EnumerateArray())
                    {
                        var decoded = contract.Decode(entry);
                        if (decoded.Proposals.Count == 0) { Count(refusals, "Undecodable"); continue; }
                        var proposal = decoded.Proposals[0];
                        var role = entry.TryGetProperty("semanticRole", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "(none)";
                        if (entry.TryGetProperty("isHeading", out var flag) && flag.ValueKind == JsonValueKind.False) { declined++; continue; }
                        var parts = proposal.SourceParts!;
                        if (parts.Any(p => !owned.Contains(p.SourceAlias))) { Count(refusals, "OutOfOwnedSegment"); continue; }
                        var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, parts);
                        if (!canonical.IsCanonical) { Count(refusals, canonical.Status.ToString()); continue; }
                        var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
                        if (!bound.IsBound) { Count(refusals, bound.Status.ToString()); continue; }
                        Count(roles, role);
                        hypotheses.Add(new ExactScorer.Hypothesis(hypotheses.Count, string.Join(" ", bound.Parts.Select(p => p.Text)), "TRUE", [], null, null, [], "TITLE", null,
                            [$"semanticRole={role}"], bound.Identity, null, bound.Parts.Select(p => new ExactScorer.Span(p.Alias, p.Start, p.End)).ToArray()));
                    }
                }
            }
            // duplicates across packs are one claim
            var unique = hypotheses.GroupBy(h => h.Identity).Select(g => g.First()).ToList();

            var goldPath = TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json");
            var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(pdf));
            var claims = ExactScorer.ReadGold(goldPath, universe);
            var score = ExactScorer.Compute(claims, unique);
            documents.Add(new
            {
                documentId = id,
                gold = new { path = $"eval/a99-closed-loop/gold/{id}.gold.json", sha256 = CanonicalArtifactHash.OfTextFile(goldPath), claims = claims.Count },
                calls = calls.Length,
                invalidResponses,
                declinedIsHeadingFalse = declined,
                boundProposals = hypotheses.Count,
                uniqueBoundProposals = unique.Count,
                refusals,
                semanticRoles = roles,
                headline = score.Headline(),
                residuals = score.Residuals(),
                byGoldPattern = ByPattern(goldPath, score),
            });
        }
        return new
        {
            artifactKind = "a99_llm_semantic_pilot_score",
            study = "LLM_SEMANTIC_PILOT_V1",
            scorer = ExactScorer.ScorerId,
            run = new { path = $"{Root}/run.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/run.v1.json")) },
            reviewState = "the production contract answers isHeading true or false; it has no NEEDS_REVIEW state, so review capture is not applicable",
            documents,
        };
    }

    private static void Count(Dictionary<string, int> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;

    /// <summary>Exact matches per Gold pattern (the review's pattern names, e.g. ARTICLE_HEADING, S095_Q2_INDEX_GROUP_LETTER).</summary>
    private static object ByPattern(string goldPath, ExactScorer.Score score)
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(goldPath));
        var patternOf = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(c => c.GetProperty("identity").GetString()!, c => c.GetProperty("pattern").GetString()!, StringComparer.Ordinal);
        using var residuals = JsonDocument.Parse(JsonSerializer.Serialize(score.Residuals(), FreezeArtifact.Json));
        var missed = residuals.RootElement.GetProperty("goldNotEngineTrue").EnumerateArray()
            .ToDictionary(x => x.GetProperty("goldIdentity").GetString()!, x => x.GetProperty("bucket").GetString()!, StringComparer.Ordinal);
        return patternOf.GroupBy(kv => kv.Value).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new
        {
            gold = g.Count(),
            exact = g.Count(kv => !missed.ContainsKey(kv.Key)),
            missedBy = g.Where(kv => missed.ContainsKey(kv.Key)).GroupBy(kv => missed[kv.Key]).OrderBy(b => b.Key, StringComparer.Ordinal).ToDictionary(b => b.Key, b => b.Count()),
        });
    }
}
