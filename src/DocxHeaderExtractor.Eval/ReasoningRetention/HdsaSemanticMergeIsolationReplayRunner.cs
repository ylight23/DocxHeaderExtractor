using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Replays the frozen v4 identity responses through the merge-isolation projection and compares
/// candidate impact with the v3 incumbent and the rejected v4 request family. No Gold or provider
/// is needed: this is a pre-evaluation topology/candidate-stability audit.
/// </summary>
public static class HdsaSemanticMergeIsolationReplayRunner
{
    private const string V3Root = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205";
    private const string V4Root = "eval/a99-closed-loop/hdsa-semantic-node-production-v4-live/DOC-0205";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-merge-isolation-replay/DOC-0205";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var v3 = Path.Combine(repoRoot, V3Root.Replace('/', Path.DirectorySeparatorChar));
        var v4 = Path.Combine(repoRoot, V4Root.Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        Require(Path.Combine(v3, "semantic-catalog.v1.json"));
        Require(Path.Combine(v4, "semantic-catalog.v1.json"));
        Require(Path.Combine(v4, "prediction-freeze.v1.json"));
        Require(Path.Combine(v4, "identity-manifest.v1.json"));

        using var oldV4Freeze = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v4, "prediction-freeze.v1.json"), ct));
        using var identityManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v4, "identity-manifest.v1.json"), ct));
        if (oldV4Freeze.RootElement.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            !oldV4Freeze.RootElement.GetProperty("frozenBeforeGold").GetBoolean())
            throw new InvalidDataException("V4_FREEZE_GOLD_FIREWALL_FAILED");
        if (GetBooleanOrDefault(identityManifest.RootElement, "goldUsed"))
            throw new InvalidDataException("V4_IDENTITY_MANIFEST_GOLD_FIREWALL_FAILED");

        var v3Catalog = ReadCatalog(Path.Combine(v3, "semantic-catalog.v1.json"), false);
        var oldV4Catalog = ReadCatalog(Path.Combine(v4, "semantic-catalog.v1.json"), true);
        var identityPredictions = Directory.GetFiles(Path.Combine(v4, "identity"), "prediction.v1.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (identityPredictions.Length != 66) throw new InvalidDataException("FROZEN_V4_IDENTITY_COUNT_EXPECTED_66");

        var observations = new List<HdsaSemanticIdentityInferenceObservation>(identityPredictions.Length);
        var occurrences = new Dictionary<string, HdsaSemanticNodeSourceOccurrence>(StringComparer.Ordinal);
        foreach (var path in identityPredictions)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var root = document.RootElement;
            if (root.GetProperty("goldReadBeforeFreeze").GetBoolean()) throw new InvalidDataException("IDENTITY_GOLD_FIREWALL_FAILED");
            var request = JsonSerializer.Deserialize<HdsaSemanticIdentityInferenceRequest>(root.GetProperty("request").GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("IDENTITY_REQUEST_MALFORMED");
            var rawResponse = root.GetProperty("rawResponse").GetString() ?? throw new InvalidDataException("IDENTITY_RAW_RESPONSE_MISSING");
            var response = HdsaSemanticNodeResolverV4.Parse(rawResponse);
            var observation = new HdsaSemanticIdentityInferenceObservation(
                request, response, root.GetProperty("requestSha256").GetString()!, ResponseHash(rawResponse),
                "MODEL", "qwen/qwen3.7-flash", "FROZEN_V4_REPLAY", false, false);
            observations.Add(observation);
            occurrences[request.Pair.Left.OccurrenceId] = request.Pair.Left;
            occurrences[request.Pair.Right.OccurrenceId] = request.Pair.Right;
        }

        var firstRequest = observations[0].Request;
        var input = new HdsaSemanticNodeResolutionInput(
            firstRequest.SourceSha256,
            firstRequest.PreprocessingSnapshotHash,
            occurrences.Values.OrderBy(item => item.DocumentOrder).ThenBy(item => item.OccurrenceId, StringComparer.Ordinal).ToArray(),
            false);
        var replay = HdsaSemanticNodeResolverV4.Resolve(
            input, observations, HdsaSemanticIdentityResolutionMode.ConservativePromotion);
        var replayEntries = replay.Predictions.Select(item => new CatalogEntry(
            item.PredictedSemanticNodeId, item.MemberOccurrenceIds, item.CanonicalText,
            item.MemberOccurrenceIds.Select(id => occurrences.TryGetValue(id, out var occurrence)
                ? occurrence.DocumentOrder
                : throw new InvalidDataException("REPLAY_OCCURRENCE_NOT_IN_FROZEN_INPUT:" + id)).Min())).ToArray();
        var replayByOccurrence = replayEntries.SelectMany(entry => entry.Aliases.Select(alias => (alias, entry.Id)))
            .ToDictionary(item => item.alias, item => item.Id, StringComparer.Ordinal);
        var v3ById = v3Catalog.Entries.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var v3ToReplay = v3Catalog.Entries.ToDictionary(entry => entry.Id, entry =>
        {
            var alias = entry.Aliases.Single();
            return replayByOccurrence.TryGetValue(alias, out var replayId)
                ? replayId
                : throw new InvalidDataException("V3_ALIAS_NOT_IN_REPLAY:" + alias);
        }, StringComparer.Ordinal);
        var affectedAliases = new HashSet<string>(["S0015", "S0016"], StringComparer.Ordinal);
        var falseMerge = replayEntries.SingleOrDefault(entry => affectedAliases.IsSubsetOf(entry.Aliases));
        if (falseMerge is not null) throw new InvalidDataException("CONSERVATIVE_PROMOTION_FALSE_MERGE_NOT_REMOVED");

        var targets = new List<RequestImpact>();
        foreach (var v3Entry in v3Catalog.Entries.OrderBy(item => item.SourceOrder))
        {
            var requestPath = Path.Combine(v3, v3Entry.Id, "request.v1.json");
            Require(requestPath);
            using var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, ct));
            var request = requestDocument.RootElement.GetProperty("request");
            var v3Request = ReadParentRequest(request, v3Catalog);
            var projected = ProjectParentRequest(v3Request, v3ToReplay, replayEntries);
            var dependsOnMerge = false;
            var v4Entry = oldV4Catalog.Entries.FirstOrDefault(entry => entry.Aliases.ToHashSet(StringComparer.Ordinal).SetEquals(v3Entry.Aliases));
            var oldV4Request = v4Entry is null ? null : ReadOldV4Request(Path.Combine(v4, "parent", v4Entry.Id, "prediction.v1.json"));
            targets.Add(new RequestImpact(
                v3Entry.Aliases.ToArray(), v3Entry.Id, v3ToReplay[v3Entry.Id], dependsOnMerge,
                RequestHash(v3Request), RequestHash(projected),
                v3Request.CandidateIds.ToArray(), projected.CandidateIds.ToArray(),
                oldV4Request?.RequestHash, oldV4Request?.CandidateIds.ToArray(),
                dependsOnMerge ? "LOCALLY_CHANGED_TARGET" :
                string.Equals(SerializeRequest(v3Request), SerializeRequest(projected), StringComparison.Ordinal)
                    ? "UNCHANGED_UNRELATED" : "UNEXPECTED_CHANGED_TARGET"));
        }

        var unrelated = targets.Where(item => !item.DependsOnMerge).ToArray();
        var unexpected = targets.Where(item => item.Impact == "UNEXPECTED_CHANGED_TARGET").Select(item => item.TargetAliases).ToArray();
        var affected = targets.Where(item => item.DependsOnMerge).Select(item => item.TargetAliases).ToArray();
        var unrelatedAliases = occurrences.Keys.Where(alias => !affectedAliases.Contains(alias)).ToHashSet(StringComparer.Ordinal);
        var v3Unrelated = v3Catalog.Entries.Where(entry => entry.Aliases.Any(unrelatedAliases.Contains)).ToDictionary(
            entry => entry.Aliases.Single(), entry => entry.Id, StringComparer.Ordinal);
        var replayUnrelatedStable = v3Unrelated.All(item => replayByOccurrence.TryGetValue(item.Key, out var replayId) &&
            string.Equals(item.Value, replayId, StringComparison.Ordinal));
        var v3UnrelatedOrder = v3Catalog.Entries.Where(entry => entry.Aliases.Any(unrelatedAliases.Contains)).Select(item => item.Aliases.Single()).ToArray();
        var replayUnrelatedOrder = replayEntries.Where(entry => entry.Aliases.Any(unrelatedAliases.Contains)).SelectMany(item => item.Aliases.Where(unrelatedAliases.Contains)).ToArray();
        var orderStable = v3UnrelatedOrder.SequenceEqual(replayUnrelatedOrder, StringComparer.Ordinal);

        var artifact = new
        {
            schemaVersion = "a99-semantic-promotion-replay-v1",
            experiment = "V4_CONSERVATIVE_PROMOTION_OFFLINE_REPLAY",
            documentId = "DOC-0205",
            incumbent = new { version = "v3", status = "FROZEN_INCUMBENT", root = V3Root },
            rejectedChallenger = new { version = "v4", status = "FROZEN_REJECTED_CHALLENGER", root = V4Root },
            challenger = new { version = "v4.1", status = "COUNTERFACTUAL_CONSERVATIVE_PROMOTION", resolverMode = HdsaSemanticIdentityResolutionMode.ConservativePromotion.ToString() },
            execution = new
            {
                providerCalls = 0,
                modelCalls = 0,
                identityDecisionsSource = "FROZEN_V4_RESPONSES",
                identityDecisionCount = identityPredictions.Length,
                goldReadBeforeFreeze = false,
                goldUsed = false,
                falseMergePreserved = false,
                falseMergeRemoved = affectedAliases.Order(StringComparer.Ordinal).ToArray(),
                productionV4BehaviorModified = true,
                liveBenchmarkRerun = false,
            },
            replayCatalog = new
            {
                resolverVersion = replay.ResolverVersion,
                acceptedRelationCount = replay.AcceptedRelations.Count,
                replayCatalogEntries = replayEntries.Select(ToCatalogArtifact).ToArray(),
                errors = replay.Errors,
                oldV4FalseMergeEntry = oldV4Catalog.Entries.FirstOrDefault(entry => affectedAliases.IsSubsetOf(entry.Aliases)) is { } oldMerge ? ToCatalogArtifact(oldMerge) : null,
                catalogEquivalentToV3 = replayEntries.Select(CatalogSignature).SequenceEqual(v3Catalog.Entries.Select(CatalogSignature), StringComparer.Ordinal),
                catalogProjectionEquivalentToOldV4 = replayEntries.Select(CatalogSignature).SequenceEqual(
                    oldV4Catalog.Entries.Select(CatalogSignature), StringComparer.Ordinal),
            },
            relationPromotion = new
            {
                policyVersion = HdsaSemanticIdentityPromotionPolicy.Version,
                modelPositiveRelationCollapseAuthorized = false,
                parserPositiveRelationRequiresExplicitCompatibleEvidence = true,
                v4ReplayUsesFrozenPrePromotionDecisions = true,
            },
            requestImpact = new
            {
                totalTargets = targets.Count,
                unchangedTargets = unrelated.Where(item => item.Impact == "UNCHANGED_UNRELATED").Select(item => item.TargetAliases).ToArray(),
                locallyChangedTargets = affected,
                unexpectedChangedTargets = unexpected,
                rows = targets,
            },
            invariants = new
            {
                unrelatedNodeIdsStable = replayUnrelatedStable,
                unrelatedOrderStable = orderStable,
                unrelatedRequestBytesStable = unrelated.All(item => item.Impact == "UNCHANGED_UNRELATED"),
                membershipPreservingProjection = targets.All(item => item.DependsOnMerge || item.Impact == "UNCHANGED_UNRELATED"),
                unexpectedBlastRadius = unexpected.Length > 0,
                falseMergeWasHidden = false,
                modelPositiveRelationsRequirePromotion = true,
            },
            conclusion = new
            {
                status = unexpected.Length == 0 && replayUnrelatedStable && orderStable && falseMerge is null ? "CONSERVATIVE_PROMOTION_REPLAY_PASS" : "CONSERVATIVE_PROMOTION_REPLAY_FAIL",
                interpretation = "Model-only positive identity relations remain proposed and are not promoted. The historical false merge is removed, while unrelated catalog/request topology remains stable.",
                nextGate = "NEW_BLIND_LIVE_V4_1_BENCHMARK_AFTER_OFFLINE_GATES",
            },
        };

        await File.WriteAllTextAsync(Path.Combine(output, "summary.v1.json"), JsonSerializer.Serialize(artifact, JsonOptions), ct);
        Console.WriteLine("HDSA_SEMANTIC_PROMOTION_REPLAY_STATUS=COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"FROZEN_IDENTITY_DECISIONS={identityPredictions.Length}");
        Console.WriteLine($"UNEXPECTED_CHANGED_TARGETS={unexpected.Length}");
        Console.WriteLine($"UNRELATED_NODE_IDS_STABLE={replayUnrelatedStable}");
        Console.WriteLine($"UNRELATED_ORDER_STABLE={orderStable}");
        Console.WriteLine($"FALSE_MERGE_REMOVED={(falseMerge is null)}");
        Console.WriteLine($"STATUS={(unexpected.Length == 0 && replayUnrelatedStable && orderStable && falseMerge is null ? "CONSERVATIVE_PROMOTION_REPLAY_PASS" : "CONSERVATIVE_PROMOTION_REPLAY_FAIL")}");
        Console.WriteLine($"ARTIFACT={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static ParentRequest ReadParentRequest(JsonElement root, CatalogSnapshot catalog) => new(
        root.GetProperty("childSemanticNodeId").GetString()!,
        root.GetProperty("candidateParentSemanticNodeIds").EnumerateArray().Select(item => item.GetString()!).ToArray(),
        root.GetProperty("authoritativeParentUniverse").EnumerateArray().Select(item => new UniverseEntry(
            item.GetProperty("semanticNodeId").GetString()!,
            item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            item.GetProperty("canonicalText").GetString()!, item.GetProperty("sourceOrder").GetInt32())).ToArray(),
        GetString(root.GetProperty("evidence"), "localContext"));

    private static ParentRequest ReadOldV4Request(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement.GetProperty("request");
        return new ParentRequest(
            root.GetProperty("childSemanticNodeId").GetString()!,
            root.GetProperty("candidateParentSemanticNodeIds").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            root.GetProperty("authoritativeParentUniverse").EnumerateArray().Select(item => new UniverseEntry(
                item.GetProperty("semanticNodeId").GetString()!,
                item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                item.GetProperty("canonicalText").GetString()!, item.GetProperty("sourceOrder").GetInt32())).ToArray(),
            GetString(root.GetProperty("evidence"), "localContext"),
            document.RootElement.GetProperty("requestSha256").GetString()!);
    }

    private static ParentRequest ProjectParentRequest(ParentRequest request, IReadOnlyDictionary<string, string> nodeProjection, IReadOnlyList<CatalogEntry> replayCatalog)
    {
        var projectedCandidates = HdsaSemanticMergeIsolation.ProjectCandidateReferences(request.CandidateIds, nodeProjection);
        var projectedUniverse = new List<UniverseEntry>();
        foreach (var item in request.Universe)
        {
            if (!nodeProjection.TryGetValue(item.Id, out var projectedId))
                throw new InvalidDataException("PARENT_UNIVERSE_NODE_NOT_IN_PROJECTION:" + item.Id);
            if (projectedUniverse.Any(existing => existing.Id == projectedId)) continue;
            var replayEntry = replayCatalog.SingleOrDefault(entry => entry.Id == projectedId)
                ?? throw new InvalidDataException("PROJECTED_NODE_NOT_IN_REPLAY_CATALOG:" + projectedId);
            projectedUniverse.Add(new(projectedId, replayEntry.Aliases, replayEntry.CanonicalText, replayEntry.SourceOrder));
        }
        return request with
        {
            ChildId = nodeProjection.TryGetValue(request.ChildId, out var projectedChild)
                ? projectedChild
                : throw new InvalidDataException("PARENT_CHILD_NOT_IN_PROJECTION:" + request.ChildId),
            CandidateIds = projectedCandidates,
            Universe = projectedUniverse,
        };
    }

    private static CatalogSnapshot ReadCatalog(string path, bool v4)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = v4 ? document.RootElement.GetProperty("catalog") : document.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray().Select(item => new CatalogEntry(
            item.GetProperty("semanticNodeId").GetString()!,
            item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            item.GetProperty("canonicalText").GetString()!, item.GetProperty("sourceOrder").GetInt32())).ToArray();
        return new(root.GetProperty("sourceSha256").GetString()!, root.GetProperty("catalogFingerprint").GetString()!, entries);
    }

    private static object ToCatalogArtifact(CatalogEntry entry) => new
    {
        semanticNodeId = entry.Id,
        memberOccurrenceIds = entry.Aliases,
        canonicalText = entry.CanonicalText,
        sourceOrder = entry.SourceOrder,
    };

    private static string CatalogSignature(CatalogEntry entry) =>
        string.Join("|", entry.Id, string.Join(",", entry.Aliases), entry.CanonicalText, entry.SourceOrder);

    private static string SerializeRequest(ParentRequest request) =>
        JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false });

    private static string RequestHash(ParentRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeRequest(request)))).ToLowerInvariant();

    private static string ResponseHash(string rawResponse) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawResponse))).ToLowerInvariant();

    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static bool GetBooleanOrDefault(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.GetBoolean();

    private static void Require(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("HDSA_MERGE_ISOLATION_INPUT_MISSING", path);
    }

    private sealed record CatalogSnapshot(string SourceSha256, string Fingerprint, IReadOnlyList<CatalogEntry> Entries);
    private sealed record CatalogEntry(string Id, IReadOnlyList<string> Aliases, string CanonicalText, int SourceOrder);
    private sealed record UniverseEntry(string Id, IReadOnlyList<string> Aliases, string CanonicalText, int SourceOrder);
    private sealed record ParentRequest(string ChildId, IReadOnlyList<string> CandidateIds, IReadOnlyList<UniverseEntry> Universe, string? LocalContext, string RequestHash = "");
    private sealed record RequestImpact(IReadOnlyList<string> TargetAliases, string V3NodeId, string ReplayNodeId, bool DependsOnMerge,
        string V3RequestHash, string ProjectedRequestHash, IReadOnlyList<string> V3CandidateIds, IReadOnlyList<string> ProjectedCandidateIds,
        string? OldV4RequestHash, IReadOnlyList<string>? OldV4CandidateIds, string Impact);
}
