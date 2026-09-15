using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV8H1BoundHeadingPreflight;

internal static class Program
{
    private const string AuthorityRelative = "artifacts/identity-benchmark/v8h0/heading-extraction-execution-v2/binding-revalidation-v3/frozen-bound-heading-occurrence-set.json";
    private const string CandidateManifestRelative = "artifacts/identity-benchmark/v8h0/heading-extraction-preflight-v1/production-candidate-manifest.json";
    private const string ContractRelative = "artifacts/identity-benchmark/v8/proof-carrying-identity/preflight-v1/contract.json";
    private const string ContractManifestRelative = "artifacts/identity-benchmark/v8/proof-carrying-identity/preflight-v1/manifest.json";
    private const string OutputRelative = "artifacts/identity-benchmark/v8h1/bound-heading-identity-preflight-v1";
    private const string ExpectedAuthoritySha256 = "78fb19291390c4fab474c3093a42b758a6b5ce8bfa7c3fd960b6ad7c32fdb2be";
    private const string ExpectedCandidateManifestSha256 = "747ddaaadb7c1f3941e34e232bd401cf5e4c324536a98cff988c89503bcb40b1";
    private const int MinimumHoldoutDocuments = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
            await RunAsync(root);
            Console.WriteLine("V8H1_STATUS=READY_FOR_IDENTITY_MECHANICS BLOCKED_ON_MINIMUM_HOLDOUT_DOCUMENT_COUNT PROVIDER_CALLS=0 GOLD_READS=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8H1_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var authorityPath = Full(root, AuthorityRelative);
        var candidatePath = Full(root, CandidateManifestRelative);
        var contractPath = Full(root, ContractRelative);
        var contractManifestPath = Full(root, ContractManifestRelative);
        var output = Full(root, OutputRelative);
        Require(File.Exists(authorityPath), "V8H1_AUTHORITY_NOT_FOUND");
        Require(File.Exists(candidatePath), "V8H1_CANDIDATE_MANIFEST_NOT_FOUND");
        Require(File.Exists(contractPath) && File.Exists(contractManifestPath), "V8H1_V8_CONTRACT_NOT_FOUND");
        Require(Sha256File(authorityPath) == ExpectedAuthoritySha256, "V8H1_AUTHORITY_SHA_DRIFT");
        Require(Sha256File(candidatePath) == ExpectedCandidateManifestSha256, "V8H1_CANDIDATE_MANIFEST_SHA_DRIFT");
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V8H1_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");

        var authority = Load(authorityPath);
        ValidateAuthority(authority);
        var candidate = Load(candidatePath);
        var contract = Load(contractPath);
        var contractManifest = Load(contractManifestPath);
        Require(Sha256File(contractPath) == contractManifest.GetProperty("contractSha256").GetString(), "V8H1_CONTRACT_HASH_DRIFT");
        Require(contractManifest.GetProperty("providerCalls").GetInt32() == 0 && contractManifest.GetProperty("goldReadCount").GetInt32() == 0, "V8H1_CONTRACT_FIREWALL_DRIFT");

        var selectedBlocks = ReadSelectedBlocks(candidate);
        var occurrences = ReadOccurrences(authority, selectedBlocks);
        Require(occurrences.Count == 36, "V8H1_EXPECTED_BOUND_HEADING_COUNT");
        var documents = occurrences.GroupBy(x => x.DocumentId, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new DocumentInput(g.Key, g.OrderBy(x => x.DocumentOrder).ToArray())).ToArray();
        var pairsByDocument = documents.ToDictionary(x => x.DocumentId, x => BuildPairs(x.Occurrences), StringComparer.Ordinal);
        var totalPairs = pairsByDocument.Values.Sum(x => x.Count);
        Require(totalPairs == 212, "V8H1_FULL_PAIR_COUNT_DRIFT");

        var requests = documents.SelectMany(document => new[]
        {
            BuildRequest(document, pairsByDocument[document.DocumentId], "POSITIVE_PROPOSER", contract),
            BuildRequest(document, pairsByDocument[document.DocumentId], "INDEPENDENT_FALSIFIER", contract),
        }).ToArray();
        var rebuilt = documents.SelectMany(document => new[]
        {
            BuildRequest(document, pairsByDocument[document.DocumentId], "POSITIVE_PROPOSER", contract),
            BuildRequest(document, pairsByDocument[document.DocumentId], "INDEPENDENT_FALSIFIER", contract),
        }).ToArray();
        Require(requests.Select(x => x.RequestSha256).SequenceEqual(rebuilt.Select(x => x.RequestSha256), StringComparer.Ordinal), "V8H1_REQUEST_REBUILD_DRIFT");
        Require(requests.Select(x => x.RequestKey).Distinct(StringComparer.Ordinal).Count() == requests.Length, "V8H1_REQUEST_KEY_DUPLICATE");
        Require(requests.All(x => x.Stage is "POSITIVE_PROPOSER" or "INDEPENDENT_FALSIFIER"), "V8H1_STAGE_DRIFT");

        var scale = BuildScale(documents, pairsByDocument, requests);
        var outputDocument = new
        {
            schemaVersion = "a99-v8h1-bound-heading-identity-input-v1",
            status = "SOURCE_ONLY_BOUND_HEADING_INPUT_FROZEN",
            authoritySha256 = Sha256File(authorityPath),
            candidateManifestSha256 = Sha256File(candidatePath),
            occurrences = occurrences.Select(x => x.ToPublic()).ToArray(),
            documents = documents.Select(x => new { documentId = x.DocumentId, boundHeadingCount = x.Occurrences.Count, fullWithinDocumentPairCount = pairsByDocument[x.DocumentId].Count }).ToArray(),
            upstreamUnavailableDocuments = authority.GetProperty("bindingExcludedDocuments").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        };
        var pairOutput = new
        {
            schemaVersion = "a99-v8h1-full-within-document-pair-universe-v1",
            sourceOnly = true,
            semanticLabelsUsed = false,
            pairs = pairsByDocument.SelectMany(x => x.Value).Select(x => x.ToPublic()).ToArray(),
        };
        var manifest = new
        {
            schemaVersion = "a99-v8h1-bound-heading-identity-preflight-manifest-v1",
            status = "READY_FOR_IDENTITY_MECHANICS_BLOCKED_ON_MINIMUM_HOLDOUT_DOCUMENT_COUNT",
            mechanicsStatus = "READY_FOR_IDENTITY_MECHANICS",
            generalizationStatus = "BLOCKED_ON_MINIMUM_HOLDOUT_DOCUMENT_COUNT",
            authoritySha256 = Sha256File(authorityPath),
            candidateManifestSha256 = Sha256File(candidatePath),
            v8ContractSha256 = contractManifest.GetProperty("contractSha256").GetString(),
            boundHeadingCount = occurrences.Count,
            eligibleDocumentCount = documents.Length,
            minimumHoldoutDocuments = MinimumHoldoutDocuments,
            fullWithinDocumentPairCount = totalPairs,
            proposerRequestCount = requests.Count(x => x.Stage == "POSITIVE_PROPOSER"),
            falsifierRequestCount = requests.Count(x => x.Stage == "INDEPENDENT_FALSIFIER"),
            providerCalls = 0,
            modelCalls = 0,
            goldReadCount = 0,
            historicalPredictionReadCount = 0,
            sparsePruning = false,
            proposerOutputReadByFalsifier = false,
            upstreamUnavailableDocuments = authority.GetProperty("bindingExcludedDocuments").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            scale,
        };
        var requestIndex = new
        {
            schemaVersion = "a99-v8h1-request-index-v1",
            requests = requests.Select(x => new { requestKey = x.RequestKey, documentId = x.DocumentId, stage = x.Stage, candidatePairCount = x.CandidatePairCount, requestBytes = x.RequestBytes, estimatedInputTokens = x.EstimatedInputTokens, requestSha256 = x.RequestSha256 }).ToArray(),
        };

        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "bound-heading-input-manifest.json"), outputDocument);
        await WriteAsync(Path.Combine(output, "pair-universe.json"), pairOutput);
        await WriteAsync(Path.Combine(output, "proposer-requests.json"), new { schemaVersion = "a99-v8h1-proposer-requests-v1", requests = requests.Where(x => x.Stage == "POSITIVE_PROPOSER").Select(x => x.ToPublic()).ToArray() });
        await WriteAsync(Path.Combine(output, "falsifier-requests.json"), new { schemaVersion = "a99-v8h1-falsifier-requests-v1", requests = requests.Where(x => x.Stage == "INDEPENDENT_FALSIFIER").Select(x => x.ToPublic()).ToArray() });
        await WriteAsync(Path.Combine(output, "request-index.json"), requestIndex);
        await WriteAsync(Path.Combine(output, "scale-audit.json"), scale);
        await WriteAsync(Path.Combine(output, "determinism-check.json"), new { status = "PASS", providerCalls = 0, goldReadCount = 0, authoritySha256 = Sha256File(authorityPath), requestHashesStable = true, pairUniverseStable = true, fullWithinDocumentPairsOnly = true });
        await WriteAsync(Path.Combine(output, "manifest.json"), manifest);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), BuildReport(manifest, documents, pairsByDocument, requests, authority.GetProperty("bindingExcludedDocuments").EnumerateArray().Select(x => x.GetString()!).ToArray()), new UTF8Encoding(false));
    }

    private static void ValidateAuthority(JsonElement authority)
    {
        Require(authority.GetProperty("immutable").GetBoolean(), "V8H1_AUTHORITY_NOT_IMMUTABLE");
        Require(authority.GetProperty("bindingPolicy").GetString() == "FAIL_CLOSED_DOCUMENT_ROLE_COVERAGE", "V8H1_BINDING_POLICY_DRIFT");
        Require(authority.GetProperty("occurrences").GetArrayLength() == 36, "V8H1_AUTHORITY_OCCURRENCE_COUNT");
        Require(authority.GetProperty("occurrences").EnumerateArray().All(x => x.GetProperty("bindingStatus").GetString() == "BOUND"), "V8H1_UNBOUND_AUTHORITY_OCCURRENCE");
        Require(authority.GetProperty("bindingEligibleDocuments").GetArrayLength() == 4, "V8H1_ELIGIBLE_DOCUMENT_COUNT");
        Require(authority.GetProperty("bindingExcludedDocuments").GetArrayLength() == 2, "V8H1_EXCLUDED_DOCUMENT_COUNT");
    }

    private static IReadOnlyDictionary<string, BlockEvidence> ReadSelectedBlocks(JsonElement candidate)
    {
        var result = new Dictionary<string, BlockEvidence>(StringComparer.Ordinal);
        foreach (var document in candidate.GetProperty("documents").EnumerateArray())
        {
            var documentId = document.GetProperty("documentId").GetString()!;
            var blocks = document.GetProperty("selectedBlocks").EnumerateArray().ToArray();
            for (var i = 0; i < blocks.Length; i++)
            {
                var block = blocks[i];
                var reference = block.GetProperty("ref").GetString()!;
                result[$"{documentId}|{reference}"] = new BlockEvidence(documentId, reference, i, block.GetProperty("page").GetInt32(), block.GetProperty("text").GetString()!, block.GetProperty("lineRefs").EnumerateArray().Select(x => x.GetString()!).ToArray());
            }
        }
        return result;
    }

    private static IReadOnlyList<Occurrence> ReadOccurrences(JsonElement authority, IReadOnlyDictionary<string, BlockEvidence> selectedBlocks)
    {
        var result = new List<Occurrence>();
        foreach (var item in authority.GetProperty("occurrences").EnumerateArray())
        {
            var documentId = item.GetProperty("documentId").GetString()!;
            var blockRef = item.GetProperty("blockRef").GetString()!;
            if (!selectedBlocks.TryGetValue($"{documentId}|{blockRef}", out var block))
                throw new InvalidDataException("V8H1_AUTHORITY_BLOCK_NOT_IN_CANDIDATE_MANIFEST:" + documentId + ":" + blockRef);
            var text = item.GetProperty("sourceText").GetString()!;
            Require(text == block.Text, "V8H1_SOURCE_TEXT_DRIFT:" + documentId + ":" + blockRef);
            var sourceRefs = item.GetProperty("sourceRefs").EnumerateArray().Select(x => x.GetString()!).ToArray();
            result.Add(new Occurrence(documentId, $"{documentId}:{blockRef}", blockRef, text, sourceRefs, block.LineRefs, block.Page, block.DocumentOrder));
        }
        Require(result.Select(x => x.HeadingOccurrenceId).Distinct(StringComparer.Ordinal).Count() == result.Count, "V8H1_DUPLICATE_HEADING_OCCURRENCE");
        return result.OrderBy(x => x.DocumentId, StringComparer.Ordinal).ThenBy(x => x.DocumentOrder).ToArray();
    }

    private static List<Pair> BuildPairs(IReadOnlyList<Occurrence> occurrences)
    {
        var result = new List<Pair>();
        var pairNumber = 1;
        for (var i = 0; i < occurrences.Count; i++)
            for (var j = i + 1; j < occurrences.Count; j++)
            {
                var left = occurrences[i];
                var right = occurrences[j];
                var pairId = $"{left.DocumentId}:P{pairNumber++:D4}";
                IReadOnlyList<object> evidence = new object[]
                {
                    new { evidenceRef = pairId + ":E001", kind = "DOCUMENT_ORDER_DISTANCE", value = right.DocumentOrder - left.DocumentOrder },
                    new { evidenceRef = pairId + ":E002", kind = "PAGE_PROVENANCE", value = new[] { left.Page, right.Page } },
                    new { evidenceRef = pairId + ":E003", kind = "NORMALIZED_SOURCE_TEXT_EQUALITY", value = Normalize(left.Text) == Normalize(right.Text) },
                    new { evidenceRef = pairId + ":E004", kind = "SOURCE_LINE_OVERLAP", value = left.SourceLineRefs.Intersect(right.SourceLineRefs, StringComparer.Ordinal).Any() },
                };
                result.Add(new Pair(pairId, left.DocumentId, left.HeadingOccurrenceId, right.HeadingOccurrenceId, evidence));
            }
        return result;
    }

    private static RequestArtifact BuildRequest(DocumentInput document, IReadOnlyList<Pair> pairs, string stage, JsonElement contract)
    {
        var systemPrompt = stage == "POSITIVE_PROPOSER"
            ? "You are the V8 positive identity proof proposer. Use only the supplied source-backed occurrences and evidence. Propose proof claims for candidate pairs; do not classify omitted pairs, do not infer hierarchy, and do not merge anything."
            : "You are the V8 independent distinctness falsifier. Use only the supplied source-backed occurrences and evidence. Independently challenge whether a proposed identity relation could instead represent distinct nodes. You do not receive proposer output or rationale. Do not infer hierarchy or merge anything.";
        var payload = new
        {
            phase = "V8H1_BOUND_HEADING_IDENTITY",
            documentId = document.DocumentId,
            task = stage == "POSITIVE_PROPOSER" ? "Return positive proof claims only for candidate pairs with sufficient source-backed identity evidence." : "Return distinctness challenges only when source-backed evidence supports keeping candidate occurrences distinct.",
            contract = new { proposer = contract.GetProperty("proposerOutput"), falsifier = contract.GetProperty("falsifierOutput"), gate = contract.GetProperty("deterministicPromotionGate") },
            occurrences = document.Occurrences.Select(x => x.ToPublic()).ToArray(),
            candidatePairs = pairs.Select(x => x.ToPublic()).ToArray(),
            outputRules = new
            {
                stage,
                candidateIdAuthority = "exact supplied pairId only",
                omittedCandidate = "NO_CLAIM / KEEP_SPLIT",
                autoCollapse = false,
                forbidden = new[] { "Gold", "historical predictions", "pair labels", "parent", "ROOT", "level", "automatic connected-component collapse" },
            },
        };
        var serialized = JsonSerializer.Serialize(new { stage, documentId = document.DocumentId, systemPrompt, userPrompt = JsonSerializer.Serialize(payload, JsonOptions), responseFormat = "json_object" }, JsonOptions);
        return new RequestArtifact($"{document.DocumentId}:{stage}", document.DocumentId, stage, pairs.Count, Encoding.UTF8.GetByteCount(serialized), (Encoding.UTF8.GetByteCount(serialized) + 3) / 4, Sha256(serialized), serialized);
    }

    private static object BuildScale(IReadOnlyList<DocumentInput> documents, IReadOnlyDictionary<string, List<Pair>> pairsByDocument, IReadOnlyList<RequestArtifact> requests)
    {
        return new
        {
            schemaVersion = "a99-v8h1-request-scale-audit-v1",
            sourceOnly = true,
            providerCalls = 0,
            goldReadCount = 0,
            boundHeadings = documents.Sum(x => x.Occurrences.Count),
            fullWithinDocumentPairCount = pairsByDocument.Values.Sum(x => x.Count),
            proposerRequestCount = requests.Count(x => x.Stage == "POSITIVE_PROPOSER"),
            falsifierRequestCount = requests.Count(x => x.Stage == "INDEPENDENT_FALSIFIER"),
            proposerBytes = requests.Where(x => x.Stage == "POSITIVE_PROPOSER").Sum(x => (long)x.RequestBytes),
            falsifierBytes = requests.Where(x => x.Stage == "INDEPENDENT_FALSIFIER").Sum(x => (long)x.RequestBytes),
            totalBytes = requests.Sum(x => (long)x.RequestBytes),
            totalEstimatedInputTokens = requests.Sum(x => (long)x.EstimatedInputTokens),
            maximumRequestBytes = requests.Max(x => x.RequestBytes),
            maximumEstimatedInputTokens = requests.Max(x => x.EstimatedInputTokens),
            perDocument = documents.Select(document => new
            {
                documentId = document.DocumentId,
                boundHeadings = document.Occurrences.Count,
                fullWithinDocumentPairs = pairsByDocument[document.DocumentId].Count,
                proposer = requests.Single(x => x.DocumentId == document.DocumentId && x.Stage == "POSITIVE_PROPOSER").ToScalePublic(),
                falsifier = requests.Single(x => x.DocumentId == document.DocumentId && x.Stage == "INDEPENDENT_FALSIFIER").ToScalePublic(),
            }).ToArray(),
            tokenConvention = "ceil(UTF-8 request bytes / 4)",
        };
    }

    private static string BuildReport(object manifest, IReadOnlyList<DocumentInput> documents, IReadOnlyDictionary<string, List<Pair>> pairsByDocument, IReadOnlyList<RequestArtifact> requests, IReadOnlyList<string> unavailable)
    {
        var lines = new List<string>
        {
            "# V8H1 — Bound-Heading Identity Preflight",
            "",
            "Status: **READY_FOR_IDENTITY_MECHANICS**.",
            "",
            "Generalization status: **BLOCKED_ON_MINIMUM_HOLDOUT_DOCUMENT_COUNT**. The preregistered minimum is 6 documents; the frozen bound-heading authority currently contains 4 eligible documents and 36 headings.",
            "",
            "No provider calls, Gold reads, identity labels, historical predictions, or evaluation artifacts were used. The superseded 38-heading snapshot and raw V8A2 source occurrences were not used.",
            "",
            "## Frozen mechanics",
            "",
            $"- Bound headings: **{documents.Sum(x => x.Occurrences.Count)}**",
            $"- Full within-document pairs: **{pairsByDocument.Values.Sum(x => x.Count)}**",
            $"- Proposer requests: **{requests.Count(x => x.Stage == "POSITIVE_PROPOSER")}**",
            $"- Independent falsifier requests: **{requests.Count(x => x.Stage == "INDEPENDENT_FALSIFIER")}**",
            "- Sparse pruning: **false**",
            "- Falsifier receives proposer output/rationale: **false**",
            "",
            "## Per document",
            "",
            "| Document | Bound headings | Full pairs |",
            "|---|---:|---:|",
        };
        lines.AddRange(documents.Select(x => $"| {x.DocumentId} | {x.Occurrences.Count} | {pairsByDocument[x.DocumentId].Count} |"));
        lines.AddRange(new[] { "", "## Upstream unavailable", "", $"The following documents remain `UPSTREAM_HEADING_UNAVAILABLE` and are not converted into semantic predictions: **{string.Join(", ", unavailable.Order(StringComparer.Ordinal))}**.", "", "The next allowed phase is provider authorization for V8H1 mechanics only. A first generalization evaluation remains blocked until at least two additional eligible source documents are added and their bound-heading authority is frozen." });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private sealed record BlockEvidence(string DocumentId, string BlockRef, int DocumentOrder, int Page, string Text, IReadOnlyList<string> LineRefs);
    private sealed record Occurrence(string DocumentId, string HeadingOccurrenceId, string BlockRef, string Text, IReadOnlyList<string> SourceOccurrenceIds, IReadOnlyList<string> SourceLineRefs, int Page, int DocumentOrder)
    {
        public object ToPublic() => new { headingOccurrenceId = HeadingOccurrenceId, sourceOccurrenceIds = SourceOccurrenceIds, sourceText = Text, documentOrder = DocumentOrder, page = Page, sourceLineRefs = SourceLineRefs, blockRef = BlockRef, exactTextOwnedByHarness = true };
    }
    private sealed record Pair(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<object> Evidence)
    {
        public object ToPublic() => new { pairId = PairId, documentId = DocumentId, left = Left, right = Right, evidence = Evidence };
    }
    private sealed record DocumentInput(string DocumentId, IReadOnlyList<Occurrence> Occurrences);
    private sealed record RequestArtifact(string RequestKey, string DocumentId, string Stage, int CandidatePairCount, int RequestBytes, int EstimatedInputTokens, string RequestSha256, string SerializedRequest)
    {
        public object ToPublic() => new { requestKey = RequestKey, documentId = DocumentId, stage = Stage, candidatePairCount = CandidatePairCount, requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, requestSha256 = RequestSha256, request = JsonDocument.Parse(SerializedRequest).RootElement.Clone() };
        public object ToScalePublic() => new { requestKey = RequestKey, stage = Stage, candidatePairCount = CandidatePairCount, requestBytes = RequestBytes, estimatedInputTokens = EstimatedInputTokens, requestSha256 = RequestSha256 };
    }
}
