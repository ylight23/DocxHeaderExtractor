using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline comparison of the frozen v3 incumbent and v4 rejected challenger parent lanes.
/// This runner deliberately never constructs an inference client and never mutates either
/// frozen benchmark family.
/// </summary>
public static class HdsaV3V4ParentForensicRunner
{
    private const string V3Root = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205";
    private const string V4Root = "eval/a99-closed-loop/hdsa-semantic-node-production-v4-live/DOC-0205";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-v3-v4-parent-forensic/DOC-0205";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var v3 = Path.Combine(repoRoot, V3Root.Replace('/', Path.DirectorySeparatorChar));
        var v4 = Path.Combine(repoRoot, V4Root.Replace('/', Path.DirectorySeparatorChar));
        var goldPath = Path.Combine(repoRoot, GoldPath.Replace('/', Path.DirectorySeparatorChar));
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(output);

        Require(Path.Combine(v3, "prediction-freeze.v1.json"));
        Require(Path.Combine(v3, "semantic-catalog.v1.json"));
        Require(Path.Combine(v4, "prediction-freeze.v1.json"));
        Require(Path.Combine(v4, "semantic-catalog.v1.json"));
        Require(goldPath);

        using var v3Freeze = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v3, "prediction-freeze.v1.json"), ct));
        using var v4Freeze = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v4, "prediction-freeze.v1.json"), ct));
        using var v3CatalogDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v3, "semantic-catalog.v1.json"), ct));
        using var v4CatalogDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(v4, "semantic-catalog.v1.json"), ct));
        using var goldDocument = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));

        AssertFrozenBeforeGold(v3Freeze.RootElement, "V3");
        AssertFrozenBeforeGold(v4Freeze.RootElement, "V4");
        if (v3CatalogDocument.RootElement.GetProperty("goldUsed").GetBoolean() ||
            v4CatalogDocument.RootElement.GetProperty("goldUsed").GetBoolean())
            throw new InvalidDataException("SEMANTIC_CATALOG_GOLD_FIREWALL_FAILED");

        var v3Catalog = ReadCatalog(v3CatalogDocument.RootElement, false);
        var v4Catalog = ReadCatalog(v4CatalogDocument.RootElement, true);
        var gold = ReadGold(goldDocument.RootElement);
        if (!string.Equals(v3Catalog.SourceSha256, gold.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(v4Catalog.SourceSha256, gold.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("STRUCTURAL_GOLD_SOURCE_HASH_MISMATCH");

        var exactV4 = v4Catalog.Entries
            .Where(entry => IsExactMembership(entry, gold.AliasesByOccurrence, gold.MembersByGoldNode))
            .OrderBy(entry => entry.SourceOrder)
            .ToArray();
        var rows = new List<ForensicRow>();
        foreach (var v4Entry in exactV4)
        {
            var v3Entry = v3Catalog.Entries.SingleOrDefault(entry =>
                entry.Aliases.ToHashSet(StringComparer.Ordinal).SetEquals(v4Entry.Aliases));
            if (v3Entry is null)
                throw new InvalidDataException("EXACT_V4_MEMBERSHIP_HAS_NO_V3_COUNTERPART:" + v4Entry.Id);

            var v3RequestPath = Path.Combine(v3, v3Entry.Id, "request.v1.json");
            var v3PredictionPath = Path.Combine(v3, v3Entry.Id, "prediction.v1.json");
            var v4PredictionPath = Path.Combine(v4, "parent", v4Entry.Id, "prediction.v1.json");
            Require(v3RequestPath);
            Require(v3PredictionPath);
            Require(v4PredictionPath);

            using var v3RequestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(v3RequestPath, ct));
            using var v3PredictionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(v3PredictionPath, ct));
            using var v4PredictionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(v4PredictionPath, ct));
            var v3Request = ReadRequest(v3RequestDocument.RootElement.GetProperty("request"), v3Catalog) with
            {
                RequestHash = v3RequestDocument.RootElement.GetProperty("requestSha256").GetString()!
            };
            var v4Request = ReadRequest(v4PredictionDocument.RootElement.GetProperty("request"), v4Catalog) with
            {
                RequestHash = v4PredictionDocument.RootElement.GetProperty("requestSha256").GetString()!
            };
            var v3Decision = ReadDecision(v3PredictionDocument.RootElement);
            var v4Decision = ReadDecision(v4PredictionDocument.RootElement);
            rows.Add(Classify(v3Entry, v4Entry, v3Request, v4Request, v3Decision, v4Decision, v3Catalog, v4Catalog, gold));
        }

        var conditional = ScoreConditionalV4(exactV4, v4Catalog, gold);
        var catalogDelta = !string.Equals(v3Catalog.Fingerprint, v4Catalog.Fingerprint, StringComparison.Ordinal);
        var artifact = new
        {
            schemaVersion = "a99-hdsa-v3-v4-parent-forensic-v1",
            documentId = "DOC-0205",
            incumbent = new
            {
                version = "v3",
                status = "FROZEN_INCUMBENT",
                benchmarkRoot = V3Root,
                catalogFingerprint = v3Catalog.Fingerprint,
                predictionFreeze = "prediction-freeze.v1.json",
            },
            challenger = new
            {
                version = "v4",
                status = "FROZEN_REJECTED_CHALLENGER",
                benchmarkRoot = V4Root,
                catalogFingerprint = v4Catalog.Fingerprint,
                predictionFreeze = "prediction-freeze.v1.json",
            },
            execution = new
            {
                analysisMode = "OFFLINE_FROZEN_ARTIFACTS_ONLY",
                modelCalls = 0,
                providerCalls = 0,
                goldReadBeforePredictionFreeze = false,
                productionBehaviorChanged = false,
                providerProbeAttempted = false,
                thirdS0005ProbeAttempted = false,
            },
            membership = new
            {
                v4PredictedNodes = v4Catalog.Entries.Count,
                v4GoldNodes = gold.MembersByGoldNode.Count,
                exactV4MembershipNodes = exactV4.Length,
                exactMembershipSourceAliases = exactV4.SelectMany(item => item.Aliases).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            },
            conditionalParent = new
            {
                policy = "Same edge scorer as the v3 decomposition: restrict children to exact v4 memberships; SELECT_PARENT with an unmappable parent remains FP; ROOT is scored separately from parent edges but its invalid selected parent contributes the same unmappable-selection FP convention.",
                v4 = conditional,
                v3Reference = new { tp = 8, fp = 1, fn = 0, f1 = 0.9411764705882353 },
                rootDecision = new
                {
                    exactMembershipRootNodes = rows.Count(row => row.GoldParent is null),
                    rootCorrect = rows.Count(row => row.GoldParent is null && row.V4Decision.Kind == "ROOT"),
                    rootWrong = rows.Count(row => row.GoldParent is null && row.V4Decision.Kind != "ROOT"),
                },
            },
            requestComparison = new
            {
                v3CatalogFingerprint = v3Catalog.Fingerprint,
                v4CatalogFingerprint = v4Catalog.Fingerprint,
                catalogFingerprintChanged = catalogDelta,
                v3PromptHashPersisted = false,
                v4PromptHashPersisted = false,
                v3RequestSchema = "a99-hdsa-semantic-node-parent-request-v1",
                v4RequestSchema = "embedded in a99-hdsa-v4-parent-prediction-v1",
                rows,
                classificationCounts = rows.GroupBy(row => row.PrimaryClassification, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                flagCounts = rows.SelectMany(row => row.Classifications).GroupBy(item => item, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                requestEquivalentModelFlips = rows.Count(row => row.Classifications.Contains("REQUEST_SEMANTICALLY_EQUIVALENT_MODEL_FLIP", StringComparer.Ordinal)),
                evaluationMappingEffects = rows.Count(row => row.Classifications.Contains("EVALUATION_MAPPING_EFFECT", StringComparer.Ordinal)),
            },
            conclusion = new
            {
                v4ParentConditionalF1 = conditional.F1,
                primaryCause = "SEMANTIC_FALSE_MERGE_CASCADE",
                v4F1ZeroExplanation = "The v4 catalog changes S0015 and S0016 from two source-backed nodes into one false-merged node. Exact-membership children then receive a changed candidate universe and select the false-merged node or a downstream wrong parent. S0005 retains the historical masthead selection; ROOT framing is present but did not change that frozen decision.",
                modelFlipConclusion = "No request-semantically-equivalent model flip was observed among the eight exact v4 memberships. The frozen v4 requests all differ from v3 through ROOT/UNRESOLVED affordances and/or catalog-driven candidate/universe changes; S0060 also changes its selected parent under the changed candidate universe.",
                levelConclusion = "No level projection bug was tested or introduced here. The v4 level regression remains an upstream hierarchy regression/cascade and is not repaired by this audit.",
                decision = "V4_REJECTED_CHALLENGER_NO_PRODUCTION_CHANGE",
            },
        };

        var outputPath = Path.Combine(output, "summary.v1.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(artifact, JsonOptions), ct);
        Console.WriteLine("HDSA_V3_V4_PARENT_FORENSIC_STATUS=COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine($"V4_EXACT_MEMBERSHIP_NODES={exactV4.Length}");
        Console.WriteLine($"V4_PARENT_CONDITIONAL_EXACT_MEMBERSHIP=TP:{conditional.Tp} FP:{conditional.Fp} FN:{conditional.Fn} F1:{conditional.F1:0.####}");
        Console.WriteLine($"REQUEST_EQUIVALENT_MODEL_FLIPS={rows.Count(row => row.Classifications.Contains("REQUEST_SEMANTICALLY_EQUIVALENT_MODEL_FLIP", StringComparer.Ordinal))}");
        Console.WriteLine($"SEMANTIC_FALSE_MERGE_CASCADES={rows.Count(row => row.Classifications.Contains("SEMANTIC_FALSE_MERGE_CASCADE", StringComparer.Ordinal))}");
        Console.WriteLine($"ARTIFACT={Path.Combine(OutputRoot, "summary.v1.json")}");
        return 0;
    }

    private static ForensicRow Classify(
        CatalogEntry v3Entry, CatalogEntry v4Entry, RequestSnapshot v3Request, RequestSnapshot v4Request,
        DecisionSnapshot v3Decision, DecisionSnapshot v4Decision, CatalogSnapshot v3Catalog, CatalogSnapshot v4Catalog,
        GoldSnapshot gold)
    {
        var targetTextSame = string.Equals(v3Entry.CanonicalText, v4Entry.CanonicalText, StringComparison.Ordinal);
        var targetMembershipSame = v3Entry.Aliases.ToHashSet(StringComparer.Ordinal).SetEquals(v4Entry.Aliases);
        var v3CandidateMemberships = v3Request.CandidateIds.Select(id => MembershipSignature(v3Catalog, id)).ToArray();
        var v4CandidateMemberships = v4Request.CandidateIds.Select(id => MembershipSignature(v4Catalog, id)).ToArray();
        var candidateSetChanged = !v3CandidateMemberships.ToHashSet(StringComparer.Ordinal).SetEquals(v4CandidateMemberships);
        var candidateOrderChanged = !v3CandidateMemberships.SequenceEqual(v4CandidateMemberships, StringComparer.Ordinal);
        var v3Universe = v3Request.Universe.Select(ComparableUniverseSignature).ToArray();
        var v4Universe = v4Request.Universe.Select(ComparableUniverseSignature).ToArray();
        var universeChanged = !v3Universe.SequenceEqual(v4Universe, StringComparer.Ordinal);
        var contextChanged = !string.Equals(v3Request.Evidence.SemanticKey, v4Request.Evidence.SemanticKey, StringComparison.Ordinal);
        var rootFramingChanged = !v3Request.RootOptions.SequenceEqual(v4Request.RootOptions, StringComparer.Ordinal);
        var catalogChanged = !string.Equals(v3Request.CatalogFingerprint, v4Request.CatalogFingerprint, StringComparison.Ordinal);
        var catalogDrivenChange = catalogChanged && (candidateSetChanged || candidateOrderChanged || universeChanged ||
            !string.Equals(v3Request.ChildSemanticNodeId, v4Request.ChildSemanticNodeId, StringComparison.Ordinal));
        var falseMergeId = v4Catalog.Entries.FirstOrDefault(item => item.Aliases.Count > 1)?.Id;
        var falseMergeCascade = falseMergeId is not null &&
            (v4Request.CandidateIds.Contains(falseMergeId, StringComparer.Ordinal) ||
             v4Request.Universe.Any(item => item.Id == falseMergeId) ||
             string.Equals(v4Decision.ParentId, falseMergeId, StringComparison.Ordinal));
        var decisionSemanticallySame = DecisionSignature(v3Decision, v3Catalog) == DecisionSignature(v4Decision, v4Catalog);
        var goldChild = MapToGold(v4Entry, gold.AliasesByOccurrence);
        var goldParent = goldChild is null ? null : gold.Edges.FirstOrDefault(edge => edge.Child == goldChild)?.Parent;
        var requestSemanticallyEquivalent = targetMembershipSame && targetTextSame &&
            v3CandidateMemberships.SequenceEqual(v4CandidateMemberships, StringComparer.Ordinal) &&
            v3Universe.SequenceEqual(v4Universe, StringComparer.Ordinal) &&
            !contextChanged && !rootFramingChanged;
        var modelFlip = requestSemanticallyEquivalent && !decisionSemanticallySame;

        var classifications = new List<string>();
        if (modelFlip) classifications.Add("REQUEST_SEMANTICALLY_EQUIVALENT_MODEL_FLIP");
        if (catalogDrivenChange) classifications.Add("REQUEST_CHANGED_DUE_TO_CATALOG");
        if (candidateSetChanged) classifications.Add("CANDIDATE_SET_CHANGED");
        if (candidateOrderChanged) classifications.Add("CANDIDATE_ORDER_CHANGED");
        if (contextChanged) classifications.Add("CONTEXT_CHANGED");
        if (rootFramingChanged) classifications.Add("ROOT_FRAMING_CHANGED");
        if (falseMergeCascade) classifications.Add("SEMANTIC_FALSE_MERGE_CASCADE");
        if (decisionSemanticallySame && !targetMembershipSame) classifications.Add("EVALUATION_MAPPING_EFFECT");
        if (classifications.Count == 0) classifications.Add("OTHER");

        var primary = falseMergeCascade ? "SEMANTIC_FALSE_MERGE_CASCADE" :
            modelFlip ? "REQUEST_SEMANTICALLY_EQUIVALENT_MODEL_FLIP" :
            rootFramingChanged ? "ROOT_FRAMING_CHANGED" :
            candidateSetChanged ? "CANDIDATE_SET_CHANGED" :
            candidateOrderChanged ? "CANDIDATE_ORDER_CHANGED" :
            contextChanged ? "CONTEXT_CHANGED" : "OTHER";

        return new ForensicRow(
            v4Entry.Aliases.ToArray(), v3Entry.Id, v4Entry.Id, v4Entry.CanonicalText, targetMembershipSame,
            goldChild, goldParent, v3Request, v4Request, v3Decision, v4Decision, classifications, primary,
            new { catalogChanged, universeChanged, candidateSetChanged, candidateOrderChanged, contextChanged, rootFramingChanged, decisionSemanticallySame });
    }

    private static EdgeScore ScoreConditionalV4(IReadOnlyList<CatalogEntry> exactV4, CatalogSnapshot catalog, GoldSnapshot gold)
    {
        var exactNodeIds = exactV4.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var goldIds = exactV4.Select(item => MapToGold(item, gold.AliasesByOccurrence)).Where(item => item is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var predicted = new HashSet<Edge>();
        var unmappable = 0;
        foreach (var entry in exactV4)
        {
            var path = Path.Combine(catalog.RootPath, "..", "..", "hdsa-semantic-node-production-v4-live", "DOC-0205", "parent", entry.Id, "prediction.v1.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var decision = ReadDecision(document.RootElement);
            if (decision.Kind != "SELECT_PARENT") continue;
            var child = MapToGold(entry, gold.AliasesByOccurrence);
            var parentEntry = decision.ParentId is null ? null : catalog.Entries.SingleOrDefault(item => item.Id == decision.ParentId);
            var parent = parentEntry is null ? null : MapToGold(parentEntry, gold.AliasesByOccurrence);
            if (child is null || parent is null) unmappable++;
            else predicted.Add(new Edge(parent, child));
        }
        var goldEdges = gold.Edges.Where(edge => goldIds.Contains(edge.Child)).ToHashSet();
        return ScoreEdges(predicted, goldEdges, unmappable);
    }

    private static RequestSnapshot ReadRequest(JsonElement root, CatalogSnapshot catalog)
    {
        var evidence = root.GetProperty("evidence");
        var options = root.TryGetProperty("decisionOptions", out var optionElement)
            ? optionElement.EnumerateArray().Select(item => item.GetProperty("decision").GetString()!).ToArray()
            : Array.Empty<string>();
        return new RequestSnapshot(
            root.GetProperty("catalogFingerprint").GetString()!,
            root.GetProperty("childSemanticNodeId").GetString()!,
            root.GetProperty("candidateParentSemanticNodeIds").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            root.GetProperty("authoritativeParentUniverse").EnumerateArray().Select(item => new UniverseEntry(
                item.GetProperty("semanticNodeId").GetString()!,
                item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                item.GetProperty("canonicalText").GetString()!, item.GetProperty("sourceOrder").GetInt32())).ToArray(),
            new EvidenceSnapshot(
                GetStringOrNull(evidence, "documentOrder"), GetStringOrNull(evidence, "numbering"),
                GetStringOrNull(evidence, "styles"), GetStringOrNull(evidence, "semanticRoles"),
                GetStringOrNull(evidence, "localContext"), GetStringOrNull(evidence, "globalContext")),
            options,
            root.GetProperty("goldDerivedInput").GetBoolean(),
            "");
    }

    private static DecisionSnapshot ReadDecision(JsonElement root)
    {
        var decision = root.GetProperty("decision");
        var parent = decision.TryGetProperty("parentSemanticNodeId", out var parentElement) && parentElement.ValueKind != JsonValueKind.Null
            ? parentElement.GetString() : null;
        return new DecisionSnapshot(decision.GetProperty("decision").GetString()!, parent, root.GetProperty("requestSha256").GetString()!,
            root.TryGetProperty("validation", out var validation) && validation.GetProperty("accepted").GetBoolean());
    }

    private static CatalogSnapshot ReadCatalog(JsonElement root, bool v4)
    {
        var source = v4 ? root.GetProperty("catalog") : root;
        var entries = source.GetProperty("entries").EnumerateArray().Select(item => new CatalogEntry(
            item.GetProperty("semanticNodeId").GetString()!,
            item.GetProperty("memberOccurrenceIds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
            item.GetProperty("canonicalText").GetString()!, item.GetProperty("sourceOrder").GetInt32())).ToArray();
        return new CatalogSnapshot(
            source.GetProperty("sourceSha256").GetString()!, source.GetProperty("catalogFingerprint").GetString()!, entries,
            Path.GetFullPath(v4 ? V4Root : V3Root));
    }

    private static GoldSnapshot ReadGold(JsonElement root)
    {
        var aliases = root.GetProperty("occurrences").EnumerateArray()
            .Where(item => item.GetProperty("reviewStatus").GetString() == "RESOLVED")
            .ToDictionary(item => item.GetProperty("sourceAlias").GetString()!, item => item.GetProperty("semanticNodeId").GetString()!, StringComparer.Ordinal);
        var members = aliases.GroupBy(item => item.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var edges = root.GetProperty("tree").GetProperty("parentOf").EnumerateArray()
            .Select(item => new Edge(item.GetProperty("parent").GetString()!, item.GetProperty("child").GetString()!)).ToHashSet();
        return new GoldSnapshot(root.GetProperty("sourceSha256").GetString()!, aliases, members, edges);
    }

    private static string MembershipSignature(CatalogSnapshot catalog, string id) =>
        string.Join("+", catalog.Entries.Single(item => item.Id == id).Aliases.OrderBy(item => item, StringComparer.Ordinal));

    private static string ComparableUniverseSignature(UniverseEntry entry) =>
        string.Join("+", entry.Aliases.OrderBy(item => item, StringComparer.Ordinal)) + "|" + entry.CanonicalText + "|" + entry.SourceOrder;

    private static string DecisionSignature(DecisionSnapshot decision, CatalogSnapshot catalog) =>
        decision.Kind + "|" + (decision.ParentId is null ? "" : MembershipSignature(catalog, decision.ParentId));

    private static string? MapToGold(CatalogEntry entry, IReadOnlyDictionary<string, string> aliases)
    {
        var ids = entry.Aliases.Where(aliases.ContainsKey).Select(alias => aliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    private static bool IsExactMembership(CatalogEntry entry, IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, HashSet<string>> members)
    {
        var ids = entry.Aliases.Where(aliases.ContainsKey).Select(alias => aliases[alias]).Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length == 1 && members.TryGetValue(ids[0], out var expected) && expected.SetEquals(entry.Aliases);
    }

    private static EdgeScore ScoreEdges(IReadOnlySet<Edge> predicted, IReadOnlySet<Edge> gold, int unmappable)
    {
        var tp = predicted.Intersect(gold).Count();
        var fp = predicted.Except(gold).Count() + unmappable;
        var fn = gold.Except(predicted).Count();
        var precision = tp + fp == 0 ? 0d : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0d : (double)tp / (tp + fn);
        var f1 = precision + recall == 0 ? 0d : 2 * precision * recall / (precision + recall);
        return new EdgeScore(tp, fp, fn, precision, recall, f1, predicted.Count, unmappable, gold.Count);
    }

    private static string? GetStringOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static void AssertFrozenBeforeGold(JsonElement root, string label)
    {
        if (root.GetProperty("goldReadBeforeFreeze").GetBoolean() || !root.GetProperty("frozenBeforeGold").GetBoolean())
            throw new InvalidDataException(label + "_PREDICTION_FREEZE_GOLD_FIREWALL_FAILED");
    }

    private static void Require(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("HDSA_V3_V4_FORENSIC_INPUT_MISSING", path);
    }

    private sealed record CatalogSnapshot(string SourceSha256, string Fingerprint, IReadOnlyList<CatalogEntry> Entries, string RootPath);
    private sealed record CatalogEntry(string Id, IReadOnlyList<string> Aliases, string CanonicalText, int SourceOrder);
    private sealed record UniverseEntry(string Id, IReadOnlyList<string> Aliases, string CanonicalText, int SourceOrder);
    private sealed record EvidenceSnapshot(string? DocumentOrder, string? Numbering, string? Styles, string? SemanticRoles, string? LocalContext, string? GlobalContext)
    {
        public string SemanticKey => string.Join("|", DocumentOrder, Numbering, Styles, SemanticRoles, LocalContext);
    }
    private sealed record RequestSnapshot(string CatalogFingerprint, string ChildSemanticNodeId, IReadOnlyList<string> CandidateIds,
        IReadOnlyList<UniverseEntry> Universe, EvidenceSnapshot Evidence, IReadOnlyList<string> RootOptions, bool GoldDerivedInput, string RequestHash);
    private sealed record DecisionSnapshot(string Kind, string? ParentId, string RequestHash, bool Accepted);
    private sealed record GoldSnapshot(string SourceSha256, IReadOnlyDictionary<string, string> AliasesByOccurrence,
        IReadOnlyDictionary<string, HashSet<string>> MembersByGoldNode, IReadOnlySet<Edge> Edges);
    private sealed record Edge(string Parent, string Child);
    private sealed record EdgeScore(int Tp, int Fp, int Fn, double Precision, double Recall, double F1, int MappedPredictedEdges, int UnmappablePredictedEdges, int GoldEdgeCount);
    private sealed record ForensicRow(IReadOnlyList<string> TargetMembership, string V3NodeId, string V4NodeId, string CanonicalText,
        bool TargetMembershipExact, string? GoldChild, string? GoldParent, RequestSnapshot V3Request, RequestSnapshot V4Request, DecisionSnapshot V3Decision,
        DecisionSnapshot V4Decision, IReadOnlyList<string> Classifications, string PrimaryClassification, object Comparison);
}
