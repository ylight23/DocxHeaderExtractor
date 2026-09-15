using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityBenchmarkV8BSparseProjection;

internal static class Program
{
    private const string V8A2Relative = "artifacts/identity-benchmark/v8a2/new-source-intake-v1";
    private const string OutputRelative = "artifacts/identity-benchmark/v8b/sparse-projection-v1";
    private const string V8A2Authority = "579b3fd";
    private const string RetrievalVersion = "v8b-source-only-multichannel-topk-v1";
    private const string ProjectionVersion = "v8b-deduplicated-handle-projection-v1";
    private const int PerChannelTopK = 2;
    private const int PerOccurrenceFinalTopK = 4;
    private const int ContextRadius = 1;
    private const int MaxRequestEstimatedTokens = 700_000;
    private const long MaxTotalEstimatedInputTokens = 5_000_000;
    private const int ContextLimitTokens = 1_000_000;
    private const double BytesPerEstimatedToken = 4.0;
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberingRegex = new(@"^\s*(?:(?:\(?\d+(?:\.\d+)*\)?|[IVXLC]+)[\s.)\-:]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
            Console.Error.WriteLine($"V8B_ERROR={ex.Message}");
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

        var v8a2ManifestPath = Path.Combine(input, "manifest.json");
        var v8a2Manifest = Load(v8a2ManifestPath);
        Require(v8a2Manifest.GetProperty("status").GetString() == "READY_FOR_V8_PROVIDER_EXECUTION", "V8A2_NOT_READY");
        Require(v8a2Manifest.GetProperty("providerCalls").GetInt32() == 0, "V8A2_PROVIDER_CALLS_NONZERO");
        Require(v8a2Manifest.GetProperty("goldReadCount").GetInt32() == 0, "V8A2_GOLD_READS_NONZERO");

        var occurrenceRoot = Load(Path.Combine(input, "occurrences.json"));
        var candidateRoot = Load(Path.Combine(input, "candidate-pairs.json"));
        var documents = ReadDocuments(occurrenceRoot);
        var fullCandidates = ReadCandidates(candidateRoot, documents);
        var candidateUniverseSha = Sha256File(Path.Combine(input, "candidate-pairs.json"));
        Require(fullCandidates.Count == v8a2Manifest.GetProperty("candidatePairCount").GetInt32(), "V8A2_CANDIDATE_COUNT_DRIFT");
        Require(documents.Sum(x => x.Occurrences.Count) == v8a2Manifest.GetProperty("occurrenceCount").GetInt32(), "V8A2_OCCURRENCE_COUNT_DRIFT");

        var retrieval = documents.Select(document => Retrieve(document, fullCandidates.Where(x => x.DocumentId == document.DocumentId).ToArray())).ToArray();
        var shortlisted = retrieval.SelectMany(x => x.Selected).OrderBy(x => x.Candidate.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Candidate.CandidateId, StringComparer.Ordinal).ToArray();
        var selectedByDocument = retrieval.ToDictionary(x => x.DocumentId, x => x.Selected.OrderBy(y => y.Candidate.CandidateId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var handles = BuildHandleMaps(documents);
        var shardPlans = documents.SelectMany(document => BuildShards(document, selectedByDocument[document.DocumentId], handles[document.DocumentId])).ToArray();
        var proposerRequests = shardPlans.SelectMany(plan => BuildRequests(plan, "V8B_POSITIVE_PROOF_PROPOSER", "PROPOSER")).ToArray();
        var falsifierRequests = shardPlans.SelectMany(plan => BuildRequests(plan, "V8B_INDEPENDENT_DISTINCTNESS_FALSIFIER", "FALSIFIER")).ToArray();
        Require(proposerRequests.Length == falsifierRequests.Length, "V8B_STREAM_SHARD_COUNT_DRIFT");

        var requestRows = proposerRequests.Concat(falsifierRequests).OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.Stream, StringComparer.Ordinal).ThenBy(x => x.ShardId, StringComparer.Ordinal).ToArray();
        var maxTokens = requestRows.Max(x => x.EstimatedInputTokens);
        var totalTokens = requestRows.Sum(x => (long)x.EstimatedInputTokens);
        var allUnderSafety = maxTokens <= MaxRequestEstimatedTokens;
        var totalWithinBudget = totalTokens <= MaxTotalEstimatedInputTokens;
        var status = allUnderSafety && totalWithinBudget ? "READY_FOR_V8B_PROVIDER_AUTHORIZATION" : "BLOCKED_ON_V8B_SCALABILITY";
        var shortlistCounts = retrieval.Select(x => new { documentId = x.DocumentId, fullCandidateCount = x.FullCount, shortlistedCandidateCount = x.Selected.Count, reductionPercentage = Percentage(x.FullCount - x.Selected.Count, x.FullCount), perOccurrenceFanout = Distribution(x.SelectedFanout.Values.Select(y => (long)y)), channelStats = x.ChannelStats, channelOverlap = x.ChannelOverlap }).ToArray();
        var streamCoverage = new
        {
            proposerCandidateCount = proposerRequests.SelectMany(x => x.Candidates.Select(y => $"{x.DocumentId}:{y.CandidateId}")).Distinct(StringComparer.Ordinal).Count(),
            falsifierCandidateCount = falsifierRequests.SelectMany(x => x.Candidates.Select(y => $"{x.DocumentId}:{y.CandidateId}")).Distinct(StringComparer.Ordinal).Count(),
            shortlistCandidateCount = shortlisted.Length,
            proposerDuplicateCount = DuplicateCount(proposerRequests.SelectMany(x => x.Candidates.Select(y => $"{x.DocumentId}:{y.CandidateId}"))),
            falsifierDuplicateCount = DuplicateCount(falsifierRequests.SelectMany(x => x.Candidates.Select(y => $"{x.DocumentId}:{y.CandidateId}"))),
        };
        Require(streamCoverage.proposerCandidateCount == shortlisted.Length && streamCoverage.falsifierCandidateCount == shortlisted.Length, "V8B_STREAM_COVERAGE_NOT_100_PERCENT");
        Require(streamCoverage.proposerDuplicateCount == 0 && streamCoverage.falsifierDuplicateCount == 0, "V8B_STREAM_DUPLICATE_CANDIDATE");
        var proposerIds = proposerRequests.SelectMany(x => x.Candidates.Select(y => y.CandidateId)).ToHashSet(StringComparer.Ordinal);
        var falsifierIds = falsifierRequests.SelectMany(x => x.Candidates.Select(y => y.CandidateId)).ToHashSet(StringComparer.Ordinal);
        Require(proposerIds.SetEquals(falsifierIds), "V8B_STREAM_CANDIDATE_SET_DRIFT");

        var manifest = new
        {
            artifactKind = "a99_identity_benchmark_v8b_sparse_projection",
            schemaVersion = "a99-v8b-sparse-projection-v1",
            status,
            authority = new { v8a2Commit = V8A2Authority, parentCandidateUniverseSha256 = candidateUniverseSha, v8a2ManifestSha256 = Sha256File(v8a2ManifestPath), v8a2ArtifactsMutated = false },
            retrieval = new { version = RetrievalVersion, projectionVersion = ProjectionVersion, fullCandidateCount = fullCandidates.Count, shortlistedCandidateCount = shortlisted.Length, reductionPercentage = Percentage(fullCandidates.Count - shortlisted.Length, fullCandidates.Count), perChannelTopK = PerChannelTopK, perOccurrenceFinalTopK = PerOccurrenceFinalTopK, contextRadius = ContextRadius, goldUsed = false, historicalPredictionsUsed = false },
            projection = new { occurrenceHandleCount = handles.Values.Sum(x => x.OccurrenceHandles.Count), evidenceHandleCount = handles.Values.Sum(x => x.EvidenceHandles.Count), proposerShardCount = proposerRequests.Length, falsifierShardCount = falsifierRequests.Length, candidateCoverage = streamCoverage },
            scale = new { totalProposerBytes = proposerRequests.Sum(x => (long)x.RequestBytes), totalFalsifierBytes = falsifierRequests.Sum(x => (long)x.RequestBytes), totalBytes = requestRows.Sum(x => (long)x.RequestBytes), totalEstimatedInputTokens = totalTokens, maximumEstimatedInputTokens = maxTokens, maximumRequestBytes = requestRows.Max(x => x.RequestBytes), requestTokenDistribution = Distribution(requestRows.Select(x => (long)x.EstimatedInputTokens)), shortlistFanoutDistribution = Distribution(retrieval.SelectMany(x => x.SelectedFanout.Values.Select(y => (long)y))), repeatedContextRatio = RepeatedContextRatio(requestRows) },
            contextPolicy = new { configuredContextLimitTokens = ContextLimitTokens, safetyCeilingTokens = MaxRequestEstimatedTokens, inheritedTotalInputBudgetTokens = MaxTotalEstimatedInputTokens, providerLimitVerified = false },
            gate = new { allRequestsBelowSafetyCeiling = allUnderSafety, totalEstimatedInputWithinBudget = totalWithinBudget, blockers = new[] { allUnderSafety ? null : "REQUEST_EXCEEDS_PER_REQUEST_SAFETY_CEILING", totalWithinBudget ? null : "TOTAL_ESTIMATED_INPUT_TOKEN_BUDGET_EXCEEDED" }.Where(x => x is not null).ToArray() },
            firewall = new { providerCalls = 0, modelCalls = 0, goldReadCount = 0, historicalPredictionReadCount = 0, historicalEvaluationReadCount = 0, v8a2Mutated = false, v8a3Mutated = false, omittedPairsAre = "NOT_RETRIEVED_NO_PROPOSAL_PATH", noSemanticDistinctInference = true },
            next = status == "READY_FOR_V8B_PROVIDER_AUTHORIZATION" ? "Provider authorization may be considered; no authorization is granted by this artifact." : "Prepare a new source-only retrieval/projection challenger; do not mutate V8A2/V8A3.",
        };

        Write(Path.Combine(output, "retrieval-manifest.json"), new { schemaVersion = "a99-v8b-retrieval-manifest-v1", authority = manifest.authority, retrieval = manifest.retrieval, documents = shortlistCounts, fullCandidateCount = fullCandidates.Count, shortlistedCandidateCount = shortlisted.Length });
        Write(Path.Combine(output, "retrieval-channel-stats.json"), new { schemaVersion = "a99-v8b-retrieval-channel-stats-v1", documents = shortlistCounts });
        Write(Path.Combine(output, "shortlist.json"), new { schemaVersion = "a99-v8b-shortlist-v1", sourceOnly = true, candidates = shortlisted.Select(x => new { x.Candidate.CandidateId, documentId = x.Candidate.DocumentId, x.Candidate.Left, x.Candidate.Right, x.Candidate.DocumentOrderDistance, channels = x.Channels, sourceOnlyScore = x.Score }).ToArray() });
        Write(Path.Combine(output, "candidate-index.json"), new { schemaVersion = "a99-v8b-candidate-index-v1", sourceOnly = true, fullCandidateUniverseSha256 = candidateUniverseSha, candidates = shortlisted.Select(x => new { x.Candidate.CandidateId, documentId = x.Candidate.DocumentId, x.Candidate.Left, x.Candidate.Right, retrievalChannels = x.Channels, retrievalScore = x.Score, shardIds = requestRows.Where(r => r.DocumentId == x.Candidate.DocumentId && r.Candidates.Any(c => c.CandidateId == x.Candidate.CandidateId)).Select(r => r.ShardId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }).ToArray() });
        Write(Path.Combine(output, "handle-map.json"), new { schemaVersion = "a99-v8b-handle-map-v1", deterministic = true, documents = handles.Values.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ToArray() });
        Write(Path.Combine(output, "proposer-requests.json"), new { schemaVersion = "a99-v8b-proposer-requests-v1", requestSetFrozenBeforeGold = true, requests = proposerRequests.Select(x => x.ToPublic()) });
        Write(Path.Combine(output, "falsifier-requests.json"), new { schemaVersion = "a99-v8b-falsifier-requests-v1", independentFromProposer = true, proposerRationaleExcluded = true, requests = falsifierRequests.Select(x => x.ToPublic()) });
        Write(Path.Combine(output, "request-index.json"), new { schemaVersion = "a99-v8b-request-index-v1", requests = requestRows.Select(x => new { x.Stream, x.DocumentId, x.ShardId, candidateCount = x.Candidates.Count, occurrenceRefCount = x.OccurrenceRefCount, x.RequestBytes, x.EstimatedInputTokens, x.RequestSha256 }) });
        Write(Path.Combine(output, "scale-audit.json"), new { schemaVersion = "a99-v8b-scale-audit-v1", status, contextPolicy = manifest.contextPolicy, gate = manifest.gate, global = manifest.scale, perRequest = requestRows.Select(x => x.ToScalePublic()).ToArray(), perDocument = requestRows.GroupBy(x => x.DocumentId, StringComparer.Ordinal).Select(g => new { documentId = g.Key, requestCount = g.Count(), totalBytes = g.Sum(x => (long)x.RequestBytes), estimatedTokens = g.Sum(x => (long)x.EstimatedInputTokens), maxTokens = g.Max(x => x.EstimatedInputTokens) }).OrderBy(x => x.documentId, StringComparer.Ordinal) });
        Write(Path.Combine(output, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(output, "report.md"), BuildReport(manifest, shortlistCounts, requestRows), new UTF8Encoding(false));
        Console.WriteLine($"V8B_STATUS={status} PROVIDER_CALLS=0 GOLD_READ_COUNT=0 FULL_CANDIDATES={fullCandidates.Count} SHORTLIST={shortlisted.Length} MAX_TOKENS={maxTokens}");
    }

    private static IReadOnlyList<Document> ReadDocuments(JsonElement root)
    {
        return root.GetProperty("documents").EnumerateArray().Select(document => new Document(
            document.GetProperty("sourceId").GetString()!,
            document.GetProperty("occurrences").EnumerateArray().Select(x => new Occurrence(
                x.GetProperty("occurrenceId").GetString()!, x.GetProperty("text").GetString() ?? "", x.GetProperty("documentOrder").GetInt32(), x.GetProperty("style").GetString() ?? "", x.GetProperty("outlineLevel").GetString() ?? "", x.GetProperty("hasNumbering").GetBoolean(), x.GetProperty("containerDepth").GetInt32(), x.GetProperty("pageNumber").GetInt32(), x.GetProperty("evidenceReason").GetString() ?? "")).ToArray())).ToArray();
    }

    private static IReadOnlyList<Candidate> ReadCandidates(JsonElement root, IReadOnlyList<Document> documents)
    {
        return root.GetProperty("candidates").EnumerateArray().SelectMany(document => document.GetProperty("candidates").EnumerateArray().Select(x => new Candidate(document.GetProperty("sourceId").GetString()!, x.GetProperty("candidateId").GetString()!, x.GetProperty("left").GetString()!, x.GetProperty("right").GetString()!, x.GetProperty("documentOrderDistance").GetInt32(), x.GetProperty("reasons").EnumerateArray().Select(y => y.GetString()!).ToArray()))).ToArray();
    }

    private static RetrievalResult Retrieve(Document document, IReadOnlyList<Candidate> candidates)
    {
        var occurrenceById = document.Occurrences.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var scored = candidates.Select(candidate => Score(candidate, occurrenceById[candidate.Left], occurrenceById[candidate.Right])).ToArray();
        var incidentByOccurrence = document.Occurrences.ToDictionary(x => x.Id, _ => new List<ScoredCandidate>(), StringComparer.Ordinal);
        foreach (var item in scored)
        {
            incidentByOccurrence[item.Candidate.Left].Add(item);
            incidentByOccurrence[item.Candidate.Right].Add(item);
        }
        var channelNames = new[] { "EXACT_NORMALIZED_TEXT", "HIGH_LEXICAL_SIMILARITY", "NUMBERING_BASE_FORM", "STRUCTURAL_CONTEXT_COMPATIBLE", "ACRONYM_OR_VARIANT", "READING_DISTANCE" };
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var selectedByOccurrence = document.Occurrences.ToDictionary(x => x.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var channel in channelNames)
        foreach (var occurrence in document.Occurrences)
        {
            foreach (var item in incidentByOccurrence[occurrence.Id].Where(x => x.Channels.Contains(channel, StringComparer.Ordinal)).OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.DocumentOrderDistance).ThenBy(x => x.Candidate.CandidateId, StringComparer.Ordinal).Take(PerChannelTopK))
            {
                selectedByOccurrence[occurrence.Id].Add(item.Candidate.CandidateId);
                selectedIds.Add(item.Candidate.CandidateId);
            }
        }
        var finalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in document.Occurrences)
        foreach (var item in incidentByOccurrence[occurrence.Id].Where(x => selectedByOccurrence[occurrence.Id].Contains(x.Candidate.CandidateId)).OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.DocumentOrderDistance).ThenBy(x => x.Candidate.CandidateId, StringComparer.Ordinal).Take(PerOccurrenceFinalTopK)) finalIds.Add(item.Candidate.CandidateId);
        var selected = scored.Where(x => finalIds.Contains(x.Candidate.CandidateId)).OrderBy(x => x.Candidate.CandidateId, StringComparer.Ordinal).ToArray();
        var selectedFanout = document.Occurrences.ToDictionary(x => x.Id, _ => 0, StringComparer.Ordinal);
        foreach (var item in selected) { selectedFanout[item.Candidate.Left]++; selectedFanout[item.Candidate.Right]++; }
        var channelStats = channelNames.Select(channel => new { channel, fullCount = scored.Count(x => x.Channels.Contains(channel, StringComparer.Ordinal)), selectedCount = selected.Count(x => x.Channels.Contains(channel, StringComparer.Ordinal)), selectedShare = Percentage(selected.Count(x => x.Channels.Contains(channel, StringComparer.Ordinal)), selected.Count()) }).ToArray();
        var channelOverlap = channelNames.SelectMany(left => channelNames.Where(right => string.CompareOrdinal(left, right) < 0).Select(right => new { left, right, fullIntersection = scored.Count(x => x.Channels.Contains(left, StringComparer.Ordinal) && x.Channels.Contains(right, StringComparer.Ordinal)), selectedIntersection = selected.Count(x => x.Channels.Contains(left, StringComparer.Ordinal) && x.Channels.Contains(right, StringComparer.Ordinal)) })).ToArray();
        return new RetrievalResult(document.DocumentId, candidates.Count, selected, selectedFanout, channelStats, channelOverlap);
    }

    private static ScoredCandidate Score(Candidate candidate, Occurrence left, Occurrence right)
    {
        var leftNormalized = Normalize(left.Text);
        var rightNormalized = Normalize(right.Text);
        var leftBase = StripNumbering(left.Text);
        var rightBase = StripNumbering(right.Text);
        var leftTokens = Tokens(leftBase);
        var rightTokens = Tokens(rightBase);
        var channels = new List<string>();
        if (leftNormalized.Length > 0 && leftNormalized == rightNormalized) channels.Add("EXACT_NORMALIZED_TEXT");
        if (leftBase.Length > 0 && leftBase == rightBase) channels.Add("NUMBERING_BASE_FORM");
        if (Jaccard(leftTokens, rightTokens) >= .55 || ContainsAll(leftTokens, rightTokens) || ContainsAll(rightTokens, leftTokens)) channels.Add("HIGH_LEXICAL_SIMILARITY");
        if (StructuralCompatible(left, right)) channels.Add("STRUCTURAL_CONTEXT_COMPATIBLE");
        if (Acronym(leftTokens) == Acronym(rightTokens) && Acronym(leftTokens).Length >= 3) channels.Add("ACRONYM_OR_VARIANT");
        if (candidate.DocumentOrderDistance <= 8) channels.Add("READING_DISTANCE");
        if (channels.Count == 0) channels.Add("READING_DISTANCE");
        var score = channels.Sum(channel => channel switch { "EXACT_NORMALIZED_TEXT" => 100, "NUMBERING_BASE_FORM" => 90, "HIGH_LEXICAL_SIMILARITY" => 70, "ACRONYM_OR_VARIANT" => 60, "STRUCTURAL_CONTEXT_COMPATIBLE" => 35, _ => 20 });
        return new ScoredCandidate(candidate, channels.Order(StringComparer.Ordinal).ToArray(), score);
    }

    private static Dictionary<string, HandleMap> BuildHandleMaps(IReadOnlyList<Document> documents)
    {
        return documents.ToDictionary(document => document.DocumentId, document =>
        {
            var occurrenceHandles = document.Occurrences.Select((occurrence, index) => new HandleEntry($"U{index + 1:D5}", occurrence.Id)).ToArray();
            var evidenceHandles = document.Occurrences.Select((occurrence, index) => new HandleEntry($"E{index + 1:D5}", occurrence.Id)).ToArray();
            return new HandleMap(document.DocumentId, occurrenceHandles, evidenceHandles);
        }, StringComparer.Ordinal);
    }

    private static IReadOnlyList<ShardPlan> BuildShards(Document document, IReadOnlyList<ScoredCandidate> candidates, HandleMap handles)
    {
        var shards = new List<ShardPlan>();
        var offset = 0;
        while (offset < candidates.Count)
        {
            var remaining = candidates.Skip(offset).ToArray();
            var fitCount = LargestFittingPrefix(document, handles, remaining);
            Require(fitCount > 0, "V8B_SINGLE_CANDIDATE_EXCEEDS_SAFETY_CEILING");
            var shardIndex = shards.Count + 1;
            shards.Add(new ShardPlan(document, handles, shardIndex, $"{document.DocumentId}-SHARD-{shardIndex:D4}", remaining.Take(fitCount).ToArray()));
            offset += fitCount;
        }
        Require(shards.All(shard => shard.Candidates.Count == 0 || RequestFits(document, handles, shard.Candidates)), "V8B_SINGLE_SHARD_EXCEEDS_SAFETY_CEILING");
        return shards;
    }

    private static int LargestFittingPrefix(Document document, HandleMap handles, IReadOnlyList<ScoredCandidate> candidates)
    {
        var low = 1;
        var high = candidates.Count;
        var best = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (RequestFits(document, handles, candidates.Take(middle).ToArray()))
            {
                best = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return best;
    }

    private static bool RequestFits(Document document, HandleMap handles, IReadOnlyList<ScoredCandidate> candidates)
    {
        var request = BuildRequest(document, handles, candidates, "SIZE", 1, "V8B_INTERNAL_SIZE_CHECK", "PROPOSER");
        return EstimateTokens(Encoding.UTF8.GetByteCount(Canonical(request))) <= MaxRequestEstimatedTokens;
    }

    private static IReadOnlyList<RequestRow> BuildRequests(ShardPlan plan, string task, string stream)
    {
        var request = BuildRequest(plan.Document, plan.Handles, plan.Candidates, plan.ShardId, plan.ShardIndex, task, stream);
        var serialized = Canonical(request);
        using var parsed = JsonDocument.Parse(serialized);
        var requestElement = parsed.RootElement.Clone();
        var candidates = plan.Candidates.Select(x => new ShardCandidate(x.Candidate.CandidateId, plan.Handles.OccurrenceHandles.Single(h => h.CanonicalId == x.Candidate.Left).Handle, plan.Handles.OccurrenceHandles.Single(h => h.CanonicalId == x.Candidate.Right).Handle)).ToArray();
        var requestBytes = Encoding.UTF8.GetByteCount(serialized);
        var occurrencePayloadBytes = Encoding.UTF8.GetByteCount(Canonical(requestElement.GetProperty("occurrences")));
        return new[] { new RequestRow(stream, plan.Document.DocumentId, plan.ShardId, candidates, requestElement.GetProperty("occurrences").EnumerateObject().Count(), occurrencePayloadBytes, requestBytes, EstimateTokens(requestBytes), Sha256Text(serialized), requestElement) };
    }

    private static object BuildRequest(Document document, HandleMap handles, IReadOnlyList<ScoredCandidate> candidates, string shardId, int shardIndex, string task, string stream)
    {
        var indexById = document.Occurrences.Select((x, i) => new { x.Id, Index = i }).ToDictionary(x => x.Id, x => x.Index, StringComparer.Ordinal);
        var needed = new HashSet<int>();
        foreach (var candidate in candidates) foreach (var id in new[] { candidate.Candidate.Left, candidate.Candidate.Right }) { var index = indexById[id]; for (var offset = -ContextRadius; offset <= ContextRadius; offset++) if (index + offset >= 0 && index + offset < document.Occurrences.Count) needed.Add(index + offset); }
        var occurrenceHandle = handles.OccurrenceHandles.ToDictionary(x => x.CanonicalId, x => x.Handle, StringComparer.Ordinal);
        var evidenceHandle = handles.EvidenceHandles.ToDictionary(x => x.CanonicalId, x => x.Handle, StringComparer.Ordinal);
        var occurrencePayload = needed.OrderBy(x => x).ToDictionary(index => occurrenceHandle[document.Occurrences[index].Id], index => ToOccurrencePayload(document.Occurrences[index], occurrenceHandle[document.Occurrences[index].Id], evidenceHandle[document.Occurrences[index].Id]), StringComparer.Ordinal);
        var evidencePayload = needed.OrderBy(x => x).ToDictionary(index => evidenceHandle[document.Occurrences[index].Id], index => ToEvidencePayload(document.Occurrences[index], occurrenceHandle[document.Occurrences[index].Id], evidenceHandle[document.Occurrences[index].Id]), StringComparer.Ordinal);
        var candidatePayload = candidates.Select(x => new { candidateId = x.Candidate.CandidateId, leftRef = occurrenceHandle[x.Candidate.Left], rightRef = occurrenceHandle[x.Candidate.Right], documentOrderDistance = x.Candidate.DocumentOrderDistance, retrievalChannels = x.Channels }).ToArray();
        return new
        {
            schemaVersion = "a99-v8-proof-carrying-identity-contract-v1",
            task,
            stream,
            sourceId = document.DocumentId,
            shardId,
            occurrences = occurrencePayload,
            evidence = evidencePayload,
            candidatePairs = candidatePayload,
            evidencePolicy = "source-only retrieval and parser-owned evidence are attention evidence, not merge authority",
            forbidden = new[] { "Gold", "historical predictions", "pair labels", "parent", "ROOT", "level", "automatic collapse", "connected components" },
            proposerOutput = stream == "PROPOSER" ? new { positiveProofs = new[] { "SUFFICIENT", "INSUFFICIENT", "UNRESOLVED" }, allowedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" }, omitted = "NO_CLAIM_KEEP_SPLIT" } : null,
            falsifierOutput = stream == "FALSIFIER" ? new { decisions = new[] { "NO_CREDIBLE_DISTINCTNESS", "DISTINCTNESS_FOUND", "UNRESOLVED" }, omitted = "UNRESOLVED_KEEP_SPLIT", proposerRationaleVisible = false } : null,
            autoCollapse = false,
        };
    }

    private static object ToOccurrencePayload(Occurrence occurrence, string handle, string evidenceHandle) => new { @ref = handle, text = occurrence.Text, documentOrder = occurrence.Order, style = occurrence.Style, outlineLevel = occurrence.Outline, hasNumbering = occurrence.Numbered, containerDepth = occurrence.ContainerDepth, pageNumber = occurrence.Page, evidenceRef = evidenceHandle };
    private static object ToEvidencePayload(Occurrence occurrence, string occurrenceHandle, string evidenceHandle) => new { evidenceRef = evidenceHandle, occurrenceRef = occurrenceHandle, evidenceReason = occurrence.EvidenceReason, style = occurrence.Style, outlineLevel = occurrence.Outline, hasNumbering = occurrence.Numbered, containerDepth = occurrence.ContainerDepth, pageNumber = occurrence.Page };

    private static string BuildReport(dynamic manifest, IReadOnlyList<object> shortlistCounts, IReadOnlyList<RequestRow> requests)
    {
        var lines = new List<string> { "# V8B — source-only sparse retrieval + scalable projection", "", $"Status: **{manifest.status}**.", "", $"V8A2 remains immutable (`{V8A2Authority}`); the full candidate universe is preserved as diagnostic authority. No Gold, historical predictions, or provider calls were used.", "", $"- Full candidates: **{manifest.retrieval.fullCandidateCount:N0}**", $"- Shortlist: **{manifest.retrieval.shortlistedCandidateCount:N0}** ({manifest.retrieval.reductionPercentage:P2} reduction)", $"- Proposer shards: **{manifest.projection.proposerShardCount}**; falsifier shards: **{manifest.projection.falsifierShardCount}**", $"- Total estimated input: **{manifest.scale.totalEstimatedInputTokens:N0} tokens** (inherited budget: **{MaxTotalEstimatedInputTokens:N0}**)", $"- Maximum request: **{manifest.scale.maximumEstimatedInputTokens:N0} tokens / {manifest.scale.maximumRequestBytes:N0} bytes**", $"- Per-request safety ceiling: **{MaxRequestEstimatedTokens:N0} tokens**", "", "Omitted candidates remain `NOT_RETRIEVED / NO_PROPOSAL_PATH`; V8B does not infer `DISTINCT` from retrieval omission.", "", "## Per document", "", "| Document | Full | Shortlist | Reduction |", "|---|---:|---:|---:|" };
        foreach (var row in shortlistCounts) lines.Add($"| {row.GetType().GetProperty("documentId")!.GetValue(row)} | {row.GetType().GetProperty("fullCandidateCount")!.GetValue(row):N0} | {row.GetType().GetProperty("shortlistedCandidateCount")!.GetValue(row):N0} | {row.GetType().GetProperty("reductionPercentage")!.GetValue(row):P2} |");
        lines.AddRange(new[] { "", "## Gate", "", $"- Provider/model calls: **0/0**", "- Gold/historical semantic reads: **0**", $"- Deterministic source-only retrieval: **{RetrievalVersion}**", $"- Deduplicated handle projection: **{ProjectionVersion}**", $"- Decision: **{manifest.status}**", "", "If blocked, the next step is a new sparse-retrieval challenger; do not mutate V8A2 or V8A3 and do not call provider." });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static double RepeatedContextRatio(IReadOnlyList<RequestRow> rows) { var total = rows.Sum(x => (double)x.RequestBytes); var repeated = rows.GroupBy(x => x.DocumentId, StringComparer.Ordinal).Sum(g => g.Where(x => x.Stream == "PROPOSER").Select(x => (double)x.OccurrencePayloadBytes).Sum()); return total == 0 ? 0 : repeated / total; }
    private static int DuplicateCount(IEnumerable<string> values) { var array = values.ToArray(); return array.Length - array.Distinct(StringComparer.Ordinal).Count(); }
    private static string Normalize(string text) => string.Join(' ', TokenRegex.Matches(text.Normalize(NormalizationForm.FormKC).ToUpperInvariant()).Select(x => x.Value));
    private static string StripNumbering(string text) => NumberingRegex.Replace(text.Normalize(NormalizationForm.FormKC).ToUpperInvariant(), "").Trim();
    private static HashSet<string> Tokens(string text) => TokenRegex.Matches(text).Select(x => x.Value).Where(x => x.Length > 1).ToHashSet(StringComparer.Ordinal);
    private static bool ContainsAll(HashSet<string> left, HashSet<string> right) => right.Count > 0 && right.IsSubsetOf(left);
    private static double Jaccard(HashSet<string> left, HashSet<string> right) => left.Count == 0 || right.Count == 0 ? 0 : left.Intersect(right, StringComparer.Ordinal).Count() / (double)left.Union(right, StringComparer.Ordinal).Count();
    private static string Acronym(HashSet<string> tokens) => string.Concat(tokens.Order(StringComparer.Ordinal).Select(x => x[0]));
    private static bool StructuralCompatible(Occurrence left, Occurrence right) => (left.Style.Length > 0 && left.Style == right.Style) || left.ContainerDepth == right.ContainerDepth || (left.Page > 0 && left.Page == right.Page);
    private static int EstimateTokens(int bytes) => (int)Math.Ceiling(bytes / BytesPerEstimatedToken);
    private static double Percentage(long numerator, long denominator) => denominator == 0 ? 0 : numerator / (double)denominator;
    private static object Distribution(IEnumerable<long> input) { var values = input.OrderBy(x => x).ToArray(); if (values.Length == 0) return new { count = 0, min = 0L, p50 = 0L, p95 = 0L, max = 0L, mean = 0d, total = 0L }; long P(double p) => values[Math.Min(values.Length - 1, Math.Max(0, (int)Math.Ceiling(values.Length * p) - 1))]; return new { count = values.Length, min = values[0], p50 = P(.50), p95 = P(.95), max = values[^1], mean = values.Average(), total = values.Sum() }; }
    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static string Canonical(object value) => JsonSerializer.Serialize(value, JsonOptions);
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }

    private sealed record Document(string DocumentId, IReadOnlyList<Occurrence> Occurrences);
    private sealed record Occurrence(string Id, string Text, int Order, string Style, string Outline, bool Numbered, int ContainerDepth, int Page, string EvidenceReason);
    private sealed record Candidate(string DocumentId, string CandidateId, string Left, string Right, int DocumentOrderDistance, string[] Reasons);
    private sealed record ScoredCandidate(Candidate Candidate, string[] Channels, int Score);
    private sealed record RetrievalResult(string DocumentId, int FullCount, IReadOnlyList<ScoredCandidate> Selected, IReadOnlyDictionary<string, int> SelectedFanout, object ChannelStats, object ChannelOverlap);
    private sealed record HandleEntry(string Handle, string CanonicalId);
    private sealed record HandleMap(string DocumentId, IReadOnlyList<HandleEntry> OccurrenceHandles, IReadOnlyList<HandleEntry> EvidenceHandles);
    private sealed record ShardPlan(Document Document, HandleMap Handles, int ShardIndex, string ShardId, IReadOnlyList<ScoredCandidate> Candidates);
    private sealed record RequestRow(string Stream, string DocumentId, string ShardId, IReadOnlyList<ShardCandidate> Candidates, int OccurrenceRefCount, int OccurrencePayloadBytes, int RequestBytes, int EstimatedInputTokens, string RequestSha256, JsonElement Request)
    {
        public object ToPublic() => new { stream = Stream, documentId = DocumentId, shardId = ShardId, candidateCount = Candidates.Count, occurrenceRefCount = OccurrenceRefCount, requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, requestSha256 = RequestSha256, request = Request };
        public object ToScalePublic() => new { stream = Stream, documentId = DocumentId, shardId = ShardId, candidateCount = Candidates.Count, occurrenceRefCount = OccurrenceRefCount, requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, requestSha256 = RequestSha256 };
    }
    private sealed record ShardCandidate(string CandidateId, string LeftRef, string RightRef);
}
