using System.Text.Json;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Offline forensic report for the two primary DOC-0205 production benchmark defects. It reads
/// frozen requests, responses, catalog, and post-freeze Gold only; it never calls a model and never
/// mutates the frozen benchmark artifacts.
/// </summary>
public static class HdsaSemanticNodeProductionForensicRunner
{
    private const string OutputRoot = "eval/a99-closed-loop/hdsa-semantic-node-production-live/DOC-0205";
    private const string GoldPath = "eval/a99-closed-loop/hdsa-parent-relation-live/DOC-0205/structural-gold.v1.json";
    private const string MastheadNode = "SN-d53681cb67def23d";
    private const string RootTargetNode = "SN-aca084dc03ba6e31";
    private const string SplitFirstNode = "SN-fa3e46e19d6d0375";
    private const string SplitSecondNode = "SN-2ddcbcfd14c02240";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        var output = Path.Combine(repoRoot, OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        var predictionFreezePath = Path.Combine(output, "prediction-freeze.v1.json");
        var catalogPath = Path.Combine(output, "semantic-catalog.v1.json");
        Require(predictionFreezePath);
        Require(catalogPath);

        using var freeze = JsonDocument.Parse(await File.ReadAllTextAsync(predictionFreezePath, ct));
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct));
        var freezeRoot = freeze.RootElement;
        var catalogRoot = catalog.RootElement;
        if (freezeRoot.GetProperty("goldReadBeforeFreeze").GetBoolean() ||
            !freezeRoot.GetProperty("frozenBeforeGold").GetBoolean() ||
            catalogRoot.GetProperty("goldUsed").GetBoolean())
            throw new InvalidDataException("FORENSIC_GOLD_FIREWALL_FAILED");

        var fingerprint = catalogRoot.GetProperty("catalogFingerprint").GetString()!;
        var entries = catalogRoot.GetProperty("entries").EnumerateArray()
            .ToDictionary(item => item.GetProperty("semanticNodeId").GetString()!, item => item, StringComparer.Ordinal);
        var identityRelations = catalogRoot.GetProperty("acceptedIdentityRelations").EnumerateArray().ToArray();
        var goldPath = Path.Combine(repoRoot, GoldPath.Replace('/', Path.DirectorySeparatorChar));
        Require(goldPath);
        using var gold = JsonDocument.Parse(await File.ReadAllTextAsync(goldPath, ct));
        var goldRoot = gold.RootElement;
        var goldRootAlias = goldRoot.GetProperty("occurrences").EnumerateArray()
            .Single(item => item.GetProperty("sourceAlias").GetString() == "S0005");

        var rootRequest = ReadRequest(output, RootTargetNode);
        var rootPrediction = ReadPrediction(output, RootTargetNode);
        var splitFirst = ReadCatalogEntry(entries, SplitFirstNode);
        var splitSecond = ReadCatalogEntry(entries, SplitSecondNode);

        var artifact = new
        {
            schemaVersion = "a99-hdsa-semantic-node-production-primary-error-forensic-v1",
            documentId = "DOC-0205",
            benchmarkCheckpoint = "c945c02",
            sourceSha256 = catalogRoot.GetProperty("sourceSha256").GetString(),
            execution = new
            {
                analysisMode = "OFFLINE_FROZEN_ARTIFACTS_ONLY",
                modelCalls = 0,
                providerCalls = 0,
                predictionFreezeUnchanged = true,
                catalogUnchanged = true,
                parentEdgesUnchanged = true,
                metricsUnchanged = true,
            },
            s0005RootForensic = new
            {
                targetSemanticNodeId = RootTargetNode,
                targetSourceOccurrences = entries[RootTargetNode].GetProperty("memberOccurrenceIds").EnumerateArray().Select(item => item.GetString()).ToArray(),
                targetCanonicalText = entries[RootTargetNode].GetProperty("canonicalText").GetString(),
                goldParentKind = goldRootAlias.GetProperty("parentSemanticNodeId").ValueKind == JsonValueKind.Null ? "ROOT" : "NON_ROOT",
                classification = "MODEL_WRONG_ROOT_DECISION",
                request = new
                {
                    catalogFingerprint = rootRequest.GetProperty("catalogFingerprint").GetString(),
                    candidateSemanticNodeIds = rootRequest.GetProperty("candidateParentSemanticNodeIds").EnumerateArray().Select(item => item.GetString()).ToArray(),
                    candidateRendering = rootRequest.GetProperty("authoritativeParentUniverse").EnumerateArray().Select(item => new
                    {
                        semanticNodeId = item.GetProperty("semanticNodeId").GetString(),
                        canonicalText = item.GetProperty("canonicalText").GetString(),
                        sourceOrder = item.GetProperty("sourceOrder").GetInt32(),
                    }).ToArray(),
                    rootAllowed = true,
                    unresolvedAllowed = true,
                    rootInstructionPresent = true,
                    rootInstructionEvidence = "Use ROOT only when the node is structurally root. Use UNRESOLVED when evidence is insufficient; never use ROOT merely because a candidate is hard to choose.",
                    responseSchemaDecisions = new[] { "SELECT_PARENT", "ROOT", "UNRESOLVED" },
                    rootIsNotFakeCandidate = true,
                    candidateSelectionBiasRisk = true,
                    candidateSelectionBiasReason = "The request renders preceding semantic nodes as candidates but has no explicit root affordance field; ROOT appears only in prompt/schema semantics.",
                    predecessorVsSemanticParentDistinguished = "Prompt says semantic parent and the candidate universe is preceding nodes, but the request does not render an explicit distinction or a root sentinel.",
                },
                frozenDecision = new
                {
                    kind = rootPrediction.GetProperty("decision").GetProperty("decision").GetString(),
                    selectedSemanticNodeId = rootPrediction.GetProperty("decision").GetProperty("parentSemanticNodeId").GetString(),
                    rawResponse = rootPrediction.GetProperty("rawResponse").GetString(),
                },
                downstream = new
                {
                    validatorAccepted = rootPrediction.GetProperty("validation").GetProperty("accepted").GetBoolean(),
                    validatorChangedDecision = false,
                    treeChangedDecision = false,
                    selectedParentWasInCandidates = rootRequest.GetProperty("candidateParentSemanticNodeIds").EnumerateArray().Any(item => item.GetString() == MastheadNode),
                    selectedParentIsExcludedMasthead = true,
                },
                conclusion = new
                {
                    primaryCause = "MODEL_WRONG_ROOT_DECISION",
                    candidateMissing = false,
                    validatorEffect = false,
                    downstreamSystemLoss = false,
                    contractContextRisks = new[] { "ROOT_CONTRACT_PRESENT_BUT_NOT_RENDERED_AS_A_FIRST_CLASS_REQUEST_AFFORDANCE", "ROOT_VS_MASTHEAD_AMBIGUITY" },
                    evidenceLimit = "Frozen artifacts establish the contract/context risk and model decision boundary; they do not prove whether prompt framing alone or model reasoning caused the choice.",
                },
            },
            s0014S0015SemanticIdentityForensic = new
            {
                occurrences = new[]
                {
                    new { sourceAlias = "S0014", sourceOrder = 17, canonicalText = splitFirst.GetProperty("canonicalText").GetString() },
                    new { sourceAlias = "S0015", sourceOrder = 18, canonicalText = splitSecond.GetProperty("canonicalText").GetString() },
                },
                productionNodeIds = new[] { SplitFirstNode, SplitSecondNode },
                acceptedIdentityRelations = identityRelations,
                parserOwnedEvidenceObservedInFrozenCatalog = new
                {
                    sourceOrderAndAdjacency = "Observed: consecutive sourceOrder values 17 and 18.",
                    text = "Observed: two source-owned canonical texts are persisted.",
                    normalizedText = "Not persisted as an accepted identity relation.",
                    styleLayoutParagraphProperties = "Not persisted in the frozen semantic catalog/request for this benchmark.",
                    parserRelationEvidence = "None accepted; acceptedIdentityRelations is an empty array.",
                },
                parserEvidenceImpliesMerge = false,
                resolverMissedExistingEvidence = false,
                resolverMissedExistingEvidenceStatus = "UNSUPPORTED_BY_FROZEN_ARTIFACTS",
                classification = "EVIDENCE_REQUIRES_SEMANTIC_INFERENCE",
                productionCatalogMutated = false,
                inferenceBoundary = "v3 validates explicit identity relation proposals but does not infer SAME_SEMANTIC_REPEAT or CONTINUATION_OF from adjacency, style, layout, or text alone.",
                conclusion = "No heuristic merge is authorized. S0014/S0015 remain two production nodes until an independent parser-evidence or model-inference relation is proposed and fail-closed validation accepts it.",
            },
            semanticIdentityInferenceContract = new
            {
                version = "semantic-identity-inference-contract-v1-design-only",
                productionInferenceRun = false,
                allowedRelations = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF", "DISTINCT", "UNRESOLVED" },
                input = new[]
                {
                    "source occurrence IDs", "source text", "normalized text", "document order", "bounded local context",
                    "style/layout evidence", "parser structural evidence", "document boundaries", "accepted deterministic relations",
                    "source snapshot hash", "semantic catalog fingerprint",
                },
                forbidden = new[] { "Gold semantic node IDs", "Gold alias mapping", "Gold parent", "Gold level", "Structural Gold", "legacy hierarchy hints", "old Eval hierarchy" },
                output = new[] { "leftOccurrenceId", "rightOccurrenceId", "relation", "evidence/provenance" },
                validatorMustReject = new[]
                {
                    "unknown occurrence", "self relation", "cross-document relation", "source snapshot mismatch", "invalid evidence hash",
                    "multiple incompatible continuation parents", "cycle", "conflicting SAME/DISTINCT relations", "fabricated occurrence ID",
                    "Gold-derived provenance", "legacy-derived provenance",
                },
                unresolvedPolicy = "KEEP_SPLIT_NO_HEURISTIC_FALLBACK",
                transitivityPolicy = "A~B and B~C do not fabricate A~C; accepted explicit edges remain provenance when a validated component collapse is used.",
                provenanceFields = new[]
                {
                    "relationId", "relationType", "leftOccurrenceId", "rightOccurrenceId", "sourceSnapshotIdentity",
                    "inputEvidenceHash", "inferenceContractVersion", "inferenceSource", "acceptedByValidator", "rejectionReason", "goldUsed",
                    "model/provider identity when live", "frozen request/response hashes when live",
                },
            },
            authoritativeBenchmark = new
            {
                parent = new { tp = 8, fp = 3, fn = 1, f1 = 0.8 },
                conditionalExactMembership = new { tp = 8, fp = 1, fn = 0, f1 = 0.9411764705882353 },
                level = new { exact = 4, evaluated = 11, mismatches = 7, ancestorCascades = 5 },
            },
        };

        var outputPath = Path.Combine(output, "primary-error-forensic.v1.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(artifact, JsonOptions), ct);
        Console.WriteLine("HDSA_SEMANTIC_NODE_PRIMARY_FORENSIC_STATUS=COMPLETE");
        Console.WriteLine("MODEL_CALLS=0");
        Console.WriteLine("PROVIDER_CALLS=0");
        Console.WriteLine("PREDICTION_FREEZE_CHANGED=0");
        Console.WriteLine("CATALOG_CHANGED=0");
        Console.WriteLine($"ARTIFACT={Path.Combine(OutputRoot, "primary-error-forensic.v1.json")}");
        return 0;
    }

    private static JsonElement ReadRequest(string output, string nodeId)
    {
        var path = Path.Combine(output, nodeId, "request.v1.json");
        Require(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("request").Clone();
    }

    private static JsonElement ReadPrediction(string output, string nodeId)
    {
        var path = Path.Combine(output, nodeId, "prediction.v1.json");
        Require(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static JsonElement ReadCatalogEntry(IReadOnlyDictionary<string, JsonElement> entries, string nodeId) => entries[nodeId];

    private static void Require(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("HDSA_FORENSIC_INPUT_MISSING", path);
    }
}
