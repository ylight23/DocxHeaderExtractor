using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityPromotionBenchmarkV4B;

internal static class Program
{
    private const string V1CandidatePath = "artifacts/identity-benchmark/v1/candidate-set.json";
    private const string V2SourcePath = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const string V3ManifestPath = "artifacts/identity-benchmark/v3/manifest.json";
    private const string GeneratorPath = "src/DocxHeaderExtractor.Core/Models/HdsaDeterministicIdentityCandidateGenerator.cs";
    private const int ExpectedSourceUnits = 6_538;
    private const int ExpectedV3BroadCandidates = 95_999;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex StructuralKeyRegex = new(
        @"^\s*(SESSION|SECTION)\s+([IVXLCDM]+|\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AcronymRegex = new(
        @"\(\s*[A-Z][A-Z0-9&./ -]{1,15}\s*\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ContinuationRegex = new(
        @"\(\s*cont(?:['’]?d|inued)\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args.Length == 1 ? args[0] : Directory.GetCurrentDirectory());
            Run(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4B_ERROR={ex}");
            return 2;
        }
    }

    private static void Run(string root)
    {
        var sourceDocument = ReadJson(root, V2SourcePath);
        var candidateDocument = ReadJson(root, V1CandidatePath);
        var v3Manifest = ReadJson(root, V3ManifestPath);
        var sourceNodes = ReadSourceNodes(sourceDocument);
        var v3Candidates = ReadCandidates(candidateDocument);
        ValidateInputs(root, sourceDocument, candidateDocument, v3Manifest, sourceNodes, v3Candidates);
        RunMetamorphicSelfTests();

        var v3PairKeys = v3Candidates.Select(item => PairKey(item.DocumentId, item.Left, item.Right)).ToHashSet(StringComparer.Ordinal);
        var signalCandidates = BuildSignalCandidates(sourceNodes);
        var mergedByPair = v3Candidates.ToDictionary(item => PairKey(item.DocumentId, item.Left, item.Right), StringComparer.Ordinal);
        var additions = new List<V4Candidate>();
        foreach (var proposal in signalCandidates.Values.OrderBy(item => item.DocumentId, StringComparer.Ordinal).ThenBy(item => item.LeftIndex).ThenBy(item => item.RightIndex))
        {
            var key = PairKey(proposal.DocumentId, proposal.Left, proposal.Right);
            if (mergedByPair.TryGetValue(key, out var existing))
            {
                mergedByPair[key] = existing with
                {
                    Reasons = existing.Reasons.Concat(proposal.Reasons).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    V4Evidence = existing.V4Evidence.Concat(proposal.V4Evidence).GroupBy(item => item.Signal, StringComparer.Ordinal).Select(group => group.First()).ToArray(),
                };
            }
            else
            {
                var id = $"{proposal.DocumentId}:V4P{proposal.LeftIndex + 1:D4}-{proposal.RightIndex + 1:D4}";
                var addition = proposal with { PairId = id, IsV4Addition = true };
                mergedByPair[key] = addition;
                additions.Add(addition);
            }
        }

        var broad = mergedByPair.Values
            .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.LeftIndex)
            .ThenBy(item => item.RightIndex)
            .ThenBy(item => item.PairId, StringComparer.Ordinal)
            .ToArray();
        var output = Full(root, "artifacts/identity-benchmark/v4/challenger");
        Directory.CreateDirectory(output);

        var sourceSha = Sha256File(Full(root, V2SourcePath));
        var v3CandidateSha = Sha256File(Full(root, V1CandidatePath));
        var generatorSha = Sha256File(Full(root, GeneratorPath));
        var additionsByReason = additions.SelectMany(item => item.Reasons.Select(reason => new { item, reason }))
            .GroupBy(item => item.reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var allByReason = broad.SelectMany(item => item.Reasons.Select(reason => new { item, reason }))
            .GroupBy(item => item.reason, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var additionDegree = additions.SelectMany(item => new[] { item.Left, item.Right }).GroupBy(item => item, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var broadArtifact = new
        {
            artifactKind = "a99_identity_benchmark_v4b_broad_candidate_set",
            schemaVersion = "a99-identity-benchmark-v4b-broad-candidate-set-v1",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            informedBy = new[] { "V3B_RETRIEVAL_FAILURE", "V4A_RETRIEVAL_FORENSICS" },
            independentGeneralizationClaim = false,
            sourceCatalogFingerprint = sourceDocument.RootElement.GetString("catalogFingerprint"),
            v3CandidateSetSha256 = v3CandidateSha,
            v3BroadCandidateCount = v3Candidates.Length,
            addedCandidateCount = additions.Count,
            candidates = broad.Select(item => new
            {
                candidateId = item.PairId,
                documentId = item.DocumentId,
                left = item.Left,
                right = item.Right,
                reasons = item.Reasons,
                v4Only = item.IsV4Addition,
                v4Evidence = item.V4Evidence,
            }).ToArray(),
            goldUsed = false,
            providerCalls = 0,
        };

        WriteJson(Path.Combine(output, "manifest.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_manifest",
            schemaVersion = "a99-identity-benchmark-v4b-manifest-v1",
            status = "READY_FOR_V4_RETRIEVAL_EVALUATION",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            informedBy = new[] { "V3B_RETRIEVAL_FAILURE", "V4A_RETRIEVAL_FORENSICS" },
            independentGeneralizationClaim = false,
            sourceDocuments = sourceNodes.Count,
            sourceUnits = sourceNodes.Values.Sum(item => item.Length),
            sourceCatalogPath = V2SourcePath,
            sourceCatalogSha256 = sourceSha,
            sourceCatalogFingerprint = sourceDocument.RootElement.GetString("catalogFingerprint"),
            v3BroadCandidateCount = v3Candidates.Length,
            v3BroadCandidateSetSha256 = v3CandidateSha,
            v4AddedCandidateCount = additions.Count,
            v4BroadCandidateCount = broad.Length,
            v4BroadCandidateSetSha256 = Sha256Object(broadArtifact),
            generatorSourceSha256 = generatorSha,
            candidateGenerationMode = "V3_SET_UNION_SOURCE_ONLY_STRUCTURAL_AND_LEXICAL_SIGNALS",
            signalFamilies = new[] { "SHARED_STRUCTURAL_HEADING_KEY", "TERMINAL_ACRONYM_VARIANT", "EXPLICIT_CONTINUATION_VARIANT" },
            v3PruningApplied = false,
            providerCalls = 0,
            modelCalls = 0,
            goldReadCount = 0,
            goldUsedForGeneration = false,
            requestArtifactsCreated = false,
            predictionArtifactsCreated = false,
            metamorphicSelfTests = "PASS",
            productionBehaviorChanged = false,
            nextGate = "READY_FOR_V4_RETRIEVAL_EVALUATION",
        });

        WriteJson(Path.Combine(output, "source-reference.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_source_reference",
            schemaVersion = "a99-identity-benchmark-v4b-source-reference-v1",
            path = V2SourcePath,
            sha256 = sourceSha,
            catalogFingerprint = sourceDocument.RootElement.GetString("catalogFingerprint"),
            documents = sourceNodes.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new
            {
                documentId = item.Key,
                sourceUnitCount = item.Value.Length,
                sourceSha256 = sourceDocument.RootElement.GetProperty("sourceDocuments").EnumerateArray().Single(doc => doc.GetString("documentId") == item.Key).GetString("sourceSha256"),
            }).ToArray(),
            goldReadCount = 0,
            providerCalls = 0,
        });

        WriteJson(Path.Combine(output, "retrieval-contract.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_retrieval_contract",
            schemaVersion = "a99-identity-benchmark-v4b-retrieval-contract-v1",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            informedBy = new[] { "V3B_RETRIEVAL_FAILURE", "V4A_RETRIEVAL_FORENSICS" },
            independentGeneralizationClaim = false,
            composition = "V3 broad candidate set UNION three source-only signal families; no weights and no pruning.",
            signals = new object[]
            {
                new { name = "SHARED_STRUCTURAL_HEADING_KEY", grammar = "leading SESSION or SECTION plus Roman/decimal identifier", eligibility = "same document, same kind, same normalized identifier", impliesRelation = false },
                new { name = "TERMINAL_ACRONYM_VARIANT", grammar = "one terminal bounded uppercase acronym parenthetical", eligibility = "exact existing narrow normalization of acronym-stripped base", impliesRelation = false },
                new { name = "EXPLICIT_CONTINUATION_VARIANT", grammar = "terminal Cont’d, Contd, or Continued", eligibility = "same document and exact base comparison or compatible structural key", impliesRelation = false },
            },
            forbidden = new[] { "strip_all_parentheticals", "fuzzy_matching", "relation_labels", "automatic_merge", "Gold", "model_output", "weights" },
            pdfTextPolicy = "rawVerbatimText remains distinct from any parser-backed canonical comparison text; no broad PDF fuzzy repair is introduced.",
            goldReadCount = 0,
            providerCalls = 0,
        });

        WriteJson(Path.Combine(output, "structural-key-extractions.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_structural_key_extractions",
            schemaVersion = "a99-identity-benchmark-v4b-structural-key-extractions-v1",
            extraction = "source-only deterministic grammar",
            rows = sourceNodes.SelectMany(item => item.Value.Select(node => new { documentId = item.Key, sourceOccurrenceId = node.NodeId, documentOrder = node.DocumentOrder, text = node.Text, key = StructuralKey(node.Text) }))
                .Where(item => item.key is not null).OrderBy(item => item.documentId, StringComparer.Ordinal).ThenBy(item => item.documentOrder).ThenBy(item => item.sourceOccurrenceId, StringComparer.Ordinal).ToArray(),
            goldUsed = false,
            providerCalls = 0,
        });

        WriteJson(Path.Combine(output, "lexical-variant-extractions.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_lexical_variant_extractions",
            schemaVersion = "a99-identity-benchmark-v4b-lexical-variant-extractions-v1",
            extraction = "source-only bounded terminal grammars",
            acronymRows = sourceNodes.SelectMany(item => item.Value.Select(node => BuildAcronymRow(item.Key, node))).Where(item => item is not null).ToArray(),
            continuationRows = sourceNodes.SelectMany(item => item.Value.Select(node => BuildContinuationRow(item.Key, node))).Where(item => item is not null).ToArray(),
            goldUsed = false,
            providerCalls = 0,
        });

        WriteJson(Path.Combine(output, "candidate-additions.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_candidate_additions",
            schemaVersion = "a99-identity-benchmark-v4b-candidate-additions-v1",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            candidates = additions.Select(item => new
            {
                candidateId = item.PairId,
                documentId = item.DocumentId,
                leftOccurrenceId = item.Left,
                rightOccurrenceId = item.Right,
                reasons = item.Reasons,
                structuralKey = item.V4Evidence.FirstOrDefault(e => e.Signal == "SHARED_STRUCTURAL_HEADING_KEY")?.StructuralKey,
                acronymBase = item.V4Evidence.FirstOrDefault(e => e.Signal == "TERMINAL_ACRONYM_VARIANT")?.AcronymBase,
                continuationMarker = item.V4Evidence.FirstOrDefault(e => e.Signal == "EXPLICIT_CONTINUATION_VARIANT")?.ContinuationMarker,
                sourceEvidenceReferences = item.V4Evidence.SelectMany(e => e.SourceEvidenceReferences).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            }).ToArray(),
            v3BroadCandidateCount = v3Candidates.Length,
            addedCandidateCount = additions.Count,
            goldUsed = false,
            providerCalls = 0,
        });

        WriteJson(Path.Combine(output, "broad-candidate-set.json"), broadArtifact);

        WriteJson(Path.Combine(output, "scalability-report.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_scalability_report",
            v3BroadCandidates = v3Candidates.Length,
            v4AddedCandidates = additions.Count,
            v4BroadCandidates = broad.Length,
            additionsByReason,
            broadByReason = allByReason,
            overlappingNewReasonPairs = additions.Count(item => item.Reasons.Count > 1),
            perDocument = broad.GroupBy(item => item.DocumentId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new
            {
                documentId = group.Key,
                v3BroadCandidates = v3Candidates.Count(item => item.DocumentId == group.Key),
                addedCandidates = additions.Count(item => item.DocumentId == group.Key),
                v4BroadCandidates = group.Count(),
                addedCandidatesByReason = additions.Where(item => item.DocumentId == group.Key).SelectMany(item => item.Reasons).GroupBy(item => item, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            }).ToArray(),
            maximumAddedEndpointFanOut = additionDegree.Values.DefaultIfEmpty(0).Max(),
            scaleGuard = additions.Count > v3Candidates.Length * 10 ? "BLOCKED_ON_V4_RETRIEVAL_EXPLOSION" : "PASS",
            pruningApplied = false,
            goldUsed = false,
            providerCalls = 0,
        });

        WriteJson(Path.Combine(output, "firewall.json"), new
        {
            artifactKind = "a99_identity_benchmark_v4b_firewall",
            schemaVersion = "a99-identity-benchmark-v4b-firewall-v1",
            developmentStatus = "DEV_EXPOSED_CHALLENGER",
            goldReadCount = 0,
            providerCalls = 0,
            modelCalls = 0,
            goldUsedForGeneration = false,
            goldUsedForRanking = false,
            goldUsedForPruning = false,
            goldUsedToChooseThreshold = false,
            relationLabelsConsumed = false,
            promotionApplied = false,
            v3ArtifactsModified = false,
            requestArtifactsCreated = false,
            predictionArtifactsCreated = false,
            sourceOrderCanonicalized = true,
            metamorphicTests = "PASS",
            sourceInputOnly = true,
        });

        File.WriteAllText(Path.Combine(output, "report.md"), BuildMarkdown(v3Candidates.Length, additions, broad.Length, additionsByReason, additionDegree.Values.DefaultIfEmpty(0).Max()), new UTF8Encoding(false));
        Console.WriteLine($"V4B_STATUS=READY_FOR_V4_RETRIEVAL_EVALUATION;V3_BROAD={v3Candidates.Length};ADDED={additions.Count};V4_BROAD={broad.Length};PROVIDER_CALLS=0;GOLD_READS=0");
    }

    private static void ValidateInputs(string root, JsonDocument source, JsonDocument candidate, JsonDocument v3Manifest,
        IReadOnlyDictionary<string, SourceNode[]> sourceNodes, IReadOnlyList<V4Candidate> candidates)
    {
        if (sourceNodes.Values.Sum(item => item.Length) != ExpectedSourceUnits || candidates.Count != ExpectedV3BroadCandidates)
            throw new InvalidDataException("V4B_INPUT_CARDINALITY_DRIFT");
        if (candidate.RootElement.GetString("sourceCatalogFingerprint") != source.RootElement.GetString("catalogFingerprint") ||
            candidate.RootElement.GetBoolean("goldDerivedInput") || candidate.RootElement.GetProperty("generator").GetBoolean("goldUsed") ||
            v3Manifest.RootElement.GetInt32("broadCandidateCount") != ExpectedV3BroadCandidates || v3Manifest.RootElement.GetInt32("providerCalls") != 0 || v3Manifest.RootElement.GetInt32("goldReadCount") != 0)
            throw new InvalidDataException("V4B_SOURCE_ONLY_INPUT_FIREWALL_FAILED");
        var sourceIds = sourceNodes.Values.SelectMany(item => item).Select(item => item.NodeId).ToHashSet(StringComparer.Ordinal);
        if (candidates.Any(item => item.DocumentId != DocumentOf(item.Left) || item.DocumentId != DocumentOf(item.Right) || !sourceIds.Contains(item.Left) || !sourceIds.Contains(item.Right)))
            throw new InvalidDataException("V4B_CANDIDATE_SOURCE_REFERENCE_INVALID");
    }

    private static Dictionary<string, SourceNode[]> ReadSourceNodes(JsonDocument source)
        => source.RootElement.GetProperty("sourceOccurrences").EnumerateArray()
            .Select(item => new SourceNode(item.GetString("nodeId")!, item.GetString("text")!, item.GetInt32("documentOrder")))
            .GroupBy(item => DocumentOf(item.NodeId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    private static V4Candidate[] ReadCandidates(JsonDocument document)
        => document.RootElement.GetProperty("candidates").EnumerateArray().Select(item => new V4Candidate(
            item.GetString("pairId")!, item.GetString("documentId")!, item.GetString("left")!, item.GetString("right")!,
            item.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()!).ToArray(), false, Array.Empty<SignalEvidence>(), 0, 0)).ToArray();

    private static Dictionary<string, V4Candidate> BuildSignalCandidates(IReadOnlyDictionary<string, SourceNode[]> sourceNodes)
    {
        var output = new Dictionary<string, V4Candidate>(StringComparer.Ordinal);
        foreach (var document in sourceNodes.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var nodes = document.Value.OrderBy(item => item.DocumentOrder).ThenBy(item => item.NodeId, StringComparer.Ordinal).ToArray();
            var index = nodes.Select((node, position) => (node.NodeId, position)).ToDictionary(item => item.NodeId, item => item.position, StringComparer.Ordinal);
            void Add(SourceNode left, SourceNode right, string reason, string? structuralKey = null, string? acronymBase = null, string? continuationMarker = null)
            {
                if (left.NodeId == right.NodeId) return;
                var leftIndex = index[left.NodeId];
                var rightIndex = index[right.NodeId];
                if (leftIndex > rightIndex) (left, right, leftIndex, rightIndex) = (right, left, rightIndex, leftIndex);
                var key = PairKey(document.Key, left.NodeId, right.NodeId);
                var evidence = new SignalEvidence(reason, structuralKey, acronymBase, continuationMarker,
                    new[] { V2SourcePath + "#" + left.NodeId, V2SourcePath + "#" + right.NodeId });
                if (output.TryGetValue(key, out var existing))
                {
                    output[key] = existing with
                    {
                        Reasons = existing.Reasons.Append(reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                        V4Evidence = existing.V4Evidence.Append(evidence).GroupBy(item => item.Signal, StringComparer.Ordinal).Select(group => group.First()).ToArray(),
                    };
                }
                else
                {
                    output[key] = new V4Candidate(string.Empty, document.Key, left.NodeId, right.NodeId, new[] { reason }, false, new[] { evidence }, leftIndex, rightIndex);
                }
            }

            foreach (var group in nodes.Select(node => (Node: node, Key: StructuralKey(node.Text))).Where(item => item.Key is not null).GroupBy(item => item.Key!, StringComparer.Ordinal))
            {
                var groupNodes = group.Select(item => item.Node).ToArray();
                for (var i = 0; i < groupNodes.Length; i++)
                for (var j = i + 1; j < groupNodes.Length; j++)
                    Add(groupNodes[i], groupNodes[j], "SHARED_STRUCTURAL_HEADING_KEY", group.Key);
            }

            var acronymRows = nodes.Select(node => (Node: node, Base: AcronymBase(node.Text))).Where(item => item.Base is not null).ToArray();
            foreach (var row in acronymRows)
            foreach (var other in nodes)
            {
                if (row.Node.NodeId == other.NodeId) continue;
                var otherFull = Normalize(other.Text);
                var otherAcronym = AcronymBase(other.Text);
                if (otherFull == row.Base || otherAcronym == row.Base)
                    Add(row.Node, other, "TERMINAL_ACRONYM_VARIANT", acronymBase: row.Base);
            }

            var continuationRows = nodes.Select(node => (Node: node, Base: ContinuationBase(node.Text), Key: StructuralKey(node.Text), Marker: ContinuationMarker(node.Text)))
                .Where(item => item.Marker is not null).ToArray();
            foreach (var row in continuationRows)
            foreach (var other in nodes)
            {
                if (row.Node.NodeId == other.NodeId) continue;
                var compatibleKey = row.Key is not null && row.Key == StructuralKey(other.Text);
                var exactBase = row.Base == Normalize(other.Text) || row.Base == ContinuationBase(other.Text);
                if (compatibleKey || exactBase)
                    Add(row.Node, other, "EXPLICIT_CONTINUATION_VARIANT", row.Key, continuationMarker: row.Marker);
            }
        }
        return output;
    }

    private static void RunMetamorphicSelfTests()
    {
        var nodes = new[]
        {
            new SourceNode("S:1", "SESSION V: Current Research", 0),
            new SourceNode("S:2", "SESSION V: Current Research (Cont’d)", 1),
            new SourceNode("S:3", "SECTION I - Instructions to Proposers (ITP)", 2),
            new SourceNode("S:4", "SECTION I - Instructions to Proposers", 3),
            new SourceNode("S:5", "SECTION II - Other", 4),
            new SourceNode("S:6", "SESSION I - Other", 5),
            new SourceNode("S:7", "Heading (Option 1)", 6),
            new SourceNode("S:8", "Heading (Continued)", 7),
            new SourceNode("S:9", "Previous Research (Cont’d)", 8),
        };
        var source = new Dictionary<string, SourceNode[]>(StringComparer.Ordinal) { ["S"] = nodes };
        var permutation = new Dictionary<string, SourceNode[]>(StringComparer.Ordinal) { ["S"] = nodes.Reverse().ToArray() };
        var first = CanonicalSelfTestOutput(BuildSignalCandidates(source));
        var second = CanonicalSelfTestOutput(BuildSignalCandidates(permutation));
        if (first != second || first != CanonicalSelfTestOutput(BuildSignalCandidates(source)))
            throw new InvalidDataException("V4B_METAMORPHIC_ORDER_OR_REPEAT_FAILURE");
        var pairs = BuildSignalCandidates(source);
        AssertPair(pairs, "S:1", "S:2", "EXPLICIT_CONTINUATION_VARIANT");
        AssertPair(pairs, "S:3", "S:4", "TERMINAL_ACRONYM_VARIANT");
        AssertPair(pairs, "S:1", "S:2", "SHARED_STRUCTURAL_HEADING_KEY");
        AssertNoPair(pairs, "S:5", "S:6");
        AssertNoPair(pairs, "S:7", "S:8");
        AssertNoPair(pairs, "S:1", "S:9");
    }

    private static string CanonicalSelfTestOutput(IReadOnlyDictionary<string, V4Candidate> candidates)
        => JsonSerializer.Serialize(candidates.Values.OrderBy(item => item.LeftIndex).ThenBy(item => item.RightIndex).Select(item => new { item.DocumentId, item.Left, item.Right, item.Reasons, evidence = item.V4Evidence.Select(e => new { e.Signal, e.StructuralKey, e.AcronymBase, e.ContinuationMarker }) }), JsonOptions);

    private static void AssertPair(IReadOnlyDictionary<string, V4Candidate> pairs, string left, string right, string reason)
    {
        var pair = pairs.Values.SingleOrDefault(item => SamePair(item.DocumentId, item.Left, item.Right, "S", left, right));
        if (pair is null || !pair.Reasons.Contains(reason, StringComparer.Ordinal)) throw new InvalidDataException($"V4B_SELF_TEST_MISSING:{left}:{right}:{reason}");
    }

    private static void AssertNoPair(IReadOnlyDictionary<string, V4Candidate> pairs, string left, string right)
    {
        if (pairs.Values.Any(item => SamePair(item.DocumentId, item.Left, item.Right, "S", left, right))) throw new InvalidDataException($"V4B_SELF_TEST_UNEXPECTED:{left}:{right}");
    }

    private static object? BuildAcronymRow(string documentId, SourceNode node)
        => AcronymBase(node.Text) is { } value ? new { documentId, sourceOccurrenceId = node.NodeId, documentOrder = node.DocumentOrder, rawText = node.Text, baseSurface = AcronymSurface(node.Text), normalizedBase = value, grammar = "TERMINAL_UPPERCASE_ACRONYM" } : null;
    private static object? BuildContinuationRow(string documentId, SourceNode node)
        => ContinuationMarker(node.Text) is { } marker ? new { documentId, sourceOccurrenceId = node.NodeId, documentOrder = node.DocumentOrder, rawText = node.Text, baseSurface = ContinuationSurface(node.Text), normalizedBase = Normalize(ContinuationSurface(node.Text)), continuationMarker = marker, structuralKey = StructuralKey(node.Text), grammar = "TERMINAL_CONTINUATION_MARKER" } : null;

    private static string? AcronymBase(string text) => AcronymRegex.IsMatch(text) ? Normalize(AcronymSurface(text)) : null;
    private static string AcronymSurface(string text) => AcronymRegex.Replace(text, string.Empty).TrimEnd();
    private static string? ContinuationBase(string text) => ContinuationRegex.IsMatch(text) ? Normalize(ContinuationSurface(text)) : null;
    private static string ContinuationSurface(string text) => ContinuationRegex.Replace(text, string.Empty).TrimEnd();
    private static string? ContinuationMarker(string text)
    {
        var match = ContinuationRegex.Match(text);
        return match.Success ? match.Value.Trim() : null;
    }
    private static string? StructuralKey(string text)
    {
        var match = StructuralKeyRegex.Match(text);
        return match.Success ? $"{match.Groups[1].Value.ToUpperInvariant()} {match.Groups[2].Value.ToUpperInvariant()}" : null;
    }
    private static string Normalize(string value) => string.Join(' ', value.Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static string PairKey(string documentId, string left, string right) => documentId + "|" + string.Join("|", new[] { left, right }.Order(StringComparer.Ordinal));
    private static bool SamePair(string documentA, string leftA, string rightA, string documentB, string leftB, string rightB) => documentA == documentB && (leftA == leftB && rightA == rightB || leftA == rightB && rightA == leftB);
    private static string DocumentOf(string nodeId) => nodeId.Split(':', 2)[0];
    private static JsonDocument ReadJson(string root, string relative) => JsonDocument.Parse(File.ReadAllText(Full(root, relative)));
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Sha256Object(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine))).ToLowerInvariant();
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
    private static string? GetString(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetString();
    private static string? GetString(this JsonElement element, string property) => element.GetProperty(property).GetString();
    private static int GetInt32(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetInt32();
    private static int GetInt32(this JsonElement element, string property) => element.GetProperty(property).GetInt32();
    private static bool GetBoolean(this JsonDocument document, string property) => document.RootElement.GetProperty(property).GetBoolean();
    private static bool GetBoolean(this JsonElement element, string property) => element.GetProperty(property).GetBoolean();

    private static string BuildMarkdown(int v3Count, IReadOnlyList<V4Candidate> additions, int v4Count, IReadOnlyDictionary<string, int> byReason, int maxFanOut)
    {
        var lines = new[]
        {
            "# A99 Identity Retrieval V4B — DEV-exposed structural/lexical challenger",
            "",
            "Status: `READY_FOR_V4_RETRIEVAL_EVALUATION`",
            "",
            "This artifact freezes a source-only broad-retrieval challenger. It is explicitly a `DEV_EXPOSED_CHALLENGER`, informed by `V3B_RETRIEVAL_FAILURE` and `V4A_RETRIEVAL_FORENSICS`; it makes no independent generalization claim.",
            "",
            "## Contract",
            "",
            "- `V4 broad = V3 broad UNION source-only structural/lexical candidates`.",
            "- Structural key grammar: leading `SESSION`/`SECTION` plus Roman or decimal identifier; same key is retrieval evidence only.",
            "- Acronym grammar: one terminal bounded uppercase parenthetical; arbitrary parentheticals are never stripped.",
            "- Continuation grammar: terminal `Cont’d`, `Contd`, or `Continued`; base comparison or compatible structural key is retrieval evidence only.",
            "- No weights, V3 pruning, request manifest, relation label, merge, Gold, or provider call.",
            "",
            "## Scale",
            "",
            $"- V3 broad candidates: `{v3Count:N0}`.",
            $"- V4-only additions: `{additions.Count:N0}`.",
            $"- V4 broad candidates: `{v4Count:N0}`.",
            $"- Added candidates by reason: `{JsonSerializer.Serialize(byReason)}`.",
            $"- Maximum added endpoint fan-out: `{maxFanOut}`.",
            "",
            "## Firewall",
            "",
            "- `developmentStatus=DEV_EXPOSED_CHALLENGER`.",
            "- `independentGeneralizationClaim=false`.",
            "- `GoldReadCount=0`; `MODEL_CALLS=0`; `PROVIDER_CALLS=0`.",
            "- Source order is canonicalized before generation; metamorphic self-tests pass.",
            "- V1/V2/V3 artifacts are inputs only and remain unchanged.",
            "",
            "## Gate",
            "",
            "`READY_FOR_V4_RETRIEVAL_EVALUATION`",
            "",
            "Next phase: evaluate this frozen V4 broad set against the already user-reviewed DEV Gold in a separate V4C task. Do not modify this freeze during evaluation.",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private sealed record SourceNode(string NodeId, string Text, int DocumentOrder);
    private sealed record SignalEvidence(string Signal, string? StructuralKey, string? AcronymBase, string? ContinuationMarker, IReadOnlyList<string> SourceEvidenceReferences);
    private sealed record V4Candidate(string PairId, string DocumentId, string Left, string Right, IReadOnlyList<string> Reasons, bool IsV4Addition, IReadOnlyList<SignalEvidence> V4Evidence, int LeftIndex, int RightIndex);
}
