using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityBenchmarkV8Contract;

internal static class Program
{
    private const string OutputRelative = "artifacts/identity-benchmark/v8/proof-carrying-identity/preflight-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root, args.Contains("--replace-preflight", StringComparer.Ordinal));
            Console.WriteLine("V8_CONTRACT_STATUS=OFFLINE_PREFLIGHT_FROZEN PROVIDER_CALLS=0 GOLD_READ_COUNT=0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V8_CONTRACT_ERROR={ex.Message}");
            return 2;
        }
    }

    private static async Task RunAsync(string root, bool replacePreflight)
    {
        var output = Full(root, OutputRelative);
        if (replacePreflight && Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }
        Require(!Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(), "V8_OUTPUT_ALREADY_EXISTS_NO_OVERWRITE");
        var contract = new
        {
            schemaVersion = "a99-v8-proof-carrying-identity-contract-v1",
            status = "OFFLINE_CONTRACT_FROZEN_BEFORE_HOLDOUT_SELECTION",
            purpose = "Separate positive identity proof from independent distinctness challenge before any collapse authority.",
            stages = new
            {
                sourceOnlyCandidate = "deterministic source-owned candidate pair",
                positiveProposer = "MODEL_PROPOSED_PROOF_ONLY",
                adversarialFalsifier = "INDEPENDENT_MODEL_DISTINCTNESS_CHALLENGE_ONLY",
                deterministicGate = "ACCEPTED_FOR_V8_GRAPH_ONLY or KEEP_SPLIT",
                downstream = "V7C conservative clustering; ACCEPTED is not automatic collapse",
            },
            proposerOutput = new
            {
                positiveProofs = new
                {
                    candidateId = "exact frozen candidate ID",
                    relation = new[] { "SAME_SEMANTIC_REPEAT", "CONTINUATION_OF" },
                    proofStatus = new[] { "SUFFICIENT", "INSUFFICIENT", "UNRESOLVED" },
                    proofClaims = "one or more generic source-backed claims",
                    evidenceRefs = "must reference supplied parser/source evidence",
                    direction = "required for CONTINUATION_OF",
                },
                omittedCandidate = "NO_CLAIM / KEEP_SPLIT",
            },
            falsifierOutput = new
            {
                challenges = new
                {
                    candidateId = "exact frozen candidate ID",
                    decision = new[] { "NO_CREDIBLE_DISTINCTNESS", "DISTINCTNESS_FOUND", "UNRESOLVED" },
                    contradictionClaims = "zero or more generic distinct-node claims",
                    evidenceRefs = "must reference supplied parser/source evidence",
                },
                independentInput = "falsifier does not receive proposer rationale or proof claims",
                omittedCandidate = "UNRESOLVED / KEEP_SPLIT",
            },
            deterministicPromotionGate = new
            {
                accept = new[]
                {
                    "valid proposer proofStatus=SUFFICIENT",
                    "valid falsifier decision=NO_CREDIBLE_DISTINCTNESS",
                    "relation and direction agree",
                    "all evidence references resolve",
                    "no schema/identity/direction conflict",
                },
                keepSplit = new[]
                {
                    "missing proposer proof",
                    "proofStatus=INSUFFICIENT or UNRESOLVED",
                    "falsifier DISTINCTNESS_FOUND or UNRESOLVED",
                    "invalid/missing evidence",
                    "relation/direction conflict",
                    "unknown/duplicate candidate",
                },
                autoCollapse = false,
                connectedComponentsAuthority = false,
                absenceOfContradictionIsNotProof = true,
            },
            genericEvidenceCategories = new[]
            {
                "INDEPENDENT_ORGANIZATIONAL_FUNCTION",
                "LOCAL_DISCOURSE_ROLE",
                "STRUCTURAL_INSTANTIATION",
                "READING_POSITION_AND_CONTEXT",
                "SEMANTIC_OBLIGATION_CONTEXT",
                "REFERENCE_OR_INDEX_REPRESENTATION",
                "INDEPENDENT_CONTENT_BODY",
            },
            forbidden = new[]
            {
                "Gold-derived instructions", "DOC-0205-specific veto", "Agenda-specific veto", "TOC-specific veto",
                "same-text automatic merge", "same-owner automatic merge", "connected-component collapse",
                "parent", "ROOT", "level", "numeric threshold as sole promotion authority",
            },
            firewall = new
            {
                providerCalls = 0,
                modelCalls = 0,
                goldReadCount = 0,
                v7PredictionReadCount = 0,
                holdoutSelected = false,
                generalizationClaim = false,
            },
        };
        var synthetic = RunSyntheticGateChecks();
        Directory.CreateDirectory(output);
        await WriteAsync(Path.Combine(output, "contract.json"), contract);
        await WriteAsync(Path.Combine(output, "synthetic-gate-checks.json"), synthetic);
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v8-proof-carrying-identity-preflight-manifest-v1",
            status = "V8_CONTRACT_FROZEN_HOLDOUT_NOT_SELECTED",
            contractSha256 = Sha256Text(JsonSerializer.Serialize(contract, JsonOptions)),
            syntheticSha256 = Sha256Text(JsonSerializer.Serialize(synthetic, JsonOptions)),
            providerCalls = 0,
            modelCalls = 0,
            goldReadCount = 0,
            v7PredictionReadCount = 0,
            holdoutSelected = false,
            generalizationClaim = false,
        });
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), "# A99 V8 — Proof-Carrying Identity contract preflight\n\nThis phase freezes only the generic proposer/falsifier/gate contract and synthetic deterministic invariants. It does not select a holdout, read Gold or V7 predictions, call a provider, tune a prompt, or make an accuracy claim.\n\nA positive proposer proof is never sufficient alone: the independent falsifier must find no credible distinct-node contradiction. Any uncertainty remains `KEEP_SPLIT`. Accepted edges are graph candidates only and are not automatic collapse authority.\n", new UTF8Encoding(false));
    }

    private static object RunSyntheticGateChecks()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { "C001", "C002", "C003", "C004", "C005", "C006" };
        var evidence = new HashSet<string>(StringComparer.Ordinal) { "E001", "E002" };
        var cases = new[]
        {
            new SyntheticCase("proof_and_no_contradiction", new("C001", "SAME_SEMANTIC_REPEAT", "SUFFICIENT", new[] { "E001" }, null), new("C001", "NO_CREDIBLE_DISTINCTNESS", new[] { "E002" }), "ACCEPTED_FOR_V8_GRAPH_ONLY"),
            new SyntheticCase("proof_without_falsifier_clearance", new("C002", "SAME_SEMANTIC_REPEAT", "SUFFICIENT", new[] { "E001" }, null), new("C002", "UNRESOLVED", Array.Empty<string>()), "KEEP_SPLIT"),
            new SyntheticCase("distinctness_challenge_wins", new("C003", "SAME_SEMANTIC_REPEAT", "SUFFICIENT", new[] { "E001" }, null), new("C003", "DISTINCTNESS_FOUND", new[] { "E002" }), "KEEP_SPLIT"),
            new SyntheticCase("weak_positive_proof", new("C004", "SAME_SEMANTIC_REPEAT", "INSUFFICIENT", new[] { "E001" }, null), new("C004", "NO_CREDIBLE_DISTINCTNESS", new[] { "E002" }), "KEEP_SPLIT"),
            new SyntheticCase("both_uncertain", new("C005", "CONTINUATION_OF", "UNRESOLVED", new[] { "E001" }, "U002->U001"), new("C005", "UNRESOLVED", Array.Empty<string>()), "KEEP_SPLIT"),
            new SyntheticCase("omitted_proposer", null, new("C006", "NO_CREDIBLE_DISTINCTNESS", new[] { "E002" }), "KEEP_SPLIT"),
        };
        var results = cases.Select(x =>
        {
            var proposerError = ValidateProposer(x.Proposer, candidates, evidence);
            var falsifierError = ValidateFalsifier(x.Falsifier, x.Proposer, candidates, evidence);
            var outcome = proposerError is not null || falsifierError is not null
                ? "KEEP_SPLIT"
                : x.Proposer is not null && x.Proposer.ProofStatus == "SUFFICIENT" && x.Falsifier.Decision == "NO_CREDIBLE_DISTINCTNESS"
                    ? "ACCEPTED_FOR_V8_GRAPH_ONLY"
                    : "KEEP_SPLIT";
            Require(outcome == x.Expected, $"V8_SYNTHETIC_OUTCOME_{x.Name}");
            return new
            {
                name = x.Name,
                expected = x.Expected,
                actual = outcome,
                proposerValid = proposerError is null,
                falsifierValid = falsifierError is null,
                proposerError,
                falsifierError,
                autoCollapse = false,
            };
        }).ToArray();
        Require(results.Count(x => x.actual == "ACCEPTED_FOR_V8_GRAPH_ONLY") == 1, "V8_SYNTHETIC_ACCEPT_COUNT");
        Require(results.All(x => x.actual == "KEEP_SPLIT" || x.actual == "ACCEPTED_FOR_V8_GRAPH_ONLY"), "V8_SYNTHETIC_OUTCOME_ENUM");
        return new
        {
            schemaVersion = "a99-v8-synthetic-gate-checks-v1",
            status = "PASS",
            providerCalls = 0,
            goldReadCount = 0,
            candidateUniverse = candidates.OrderBy(x => x).ToArray(),
            evidenceUniverse = evidence.OrderBy(x => x).ToArray(),
            cases = results,
            invariants = new[]
            {
                "proof alone cannot accept",
                "no contradiction is not identity proof",
                "falsifier uncertainty keeps split",
                "positive acceptance is not auto-collapse",
                "omitted proposal keeps split",
            },
        };
    }

    private static string? ValidateProposer(ProposerDecision? decision, HashSet<string> candidates, HashSet<string> evidence)
    {
        if (decision is null) return null;
        if (!candidates.Contains(decision.CandidateId)) return "UNKNOWN_CANDIDATE";
        if (decision.Relation is not ("SAME_SEMANTIC_REPEAT" or "CONTINUATION_OF")) return "INVALID_RELATION";
        if (decision.ProofStatus is not ("SUFFICIENT" or "INSUFFICIENT" or "UNRESOLVED")) return "INVALID_PROOF_STATUS";
        if (decision.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() != decision.EvidenceRefs.Length) return "DUPLICATE_EVIDENCE_REF";
        if (decision.EvidenceRefs.Any(x => !evidence.Contains(x))) return "UNKNOWN_EVIDENCE_REF";
        if (decision.Relation == "CONTINUATION_OF" && string.IsNullOrWhiteSpace(decision.Direction)) return "MISSING_CONTINUATION_DIRECTION";
        if (decision.Relation == "SAME_SEMANTIC_REPEAT" && decision.Direction is not null) return "UNEXPECTED_DIRECTION";
        return null;
    }

    private static string? ValidateFalsifier(FalsifierDecision decision, ProposerDecision? proposer, HashSet<string> candidates, HashSet<string> evidence)
    {
        if (!candidates.Contains(decision.CandidateId)) return "UNKNOWN_CANDIDATE";
        if (proposer is not null && decision.CandidateId != proposer.CandidateId) return "CANDIDATE_MISMATCH";
        if (decision.Decision is not ("NO_CREDIBLE_DISTINCTNESS" or "DISTINCTNESS_FOUND" or "UNRESOLVED")) return "INVALID_FALSIFIER_DECISION";
        if (decision.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() != decision.EvidenceRefs.Length) return "DUPLICATE_EVIDENCE_REF";
        if (decision.EvidenceRefs.Any(x => !evidence.Contains(x))) return "UNKNOWN_EVIDENCE_REF";
        return null;
    }

    private sealed record SyntheticCase(string Name, ProposerDecision? Proposer, FalsifierDecision Falsifier, string Expected);
    private sealed record ProposerDecision(string CandidateId, string Relation, string ProofStatus, string[] EvidenceRefs, string? Direction);
    private sealed record FalsifierDecision(string CandidateId, string Decision, string[] EvidenceRefs);

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static async Task WriteAsync(string path, object value) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
