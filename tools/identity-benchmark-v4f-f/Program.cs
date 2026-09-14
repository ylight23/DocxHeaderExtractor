using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;

namespace IdentityBenchmarkV4FF;

internal static class Program
{
    private const string RootRelative = "artifacts/identity-benchmark/v4/projected-verifier-experiment";
    private const string SourceRelative = "artifacts/identity-benchmark/v2/source-catalog.json";
    private const int ExpectedCandidates = 128;
    private const int ExpectedAttempts = 256;
    private const string Model = "qwen/qwen3.7-flash";
    private const string Provider = "OpenRouter";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex HttpCode = new(@"OpenRouter returned (?<code>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProviderCode = new(@"provider_error_code[""']?\s*:\s*[""'](?<code>[^""']+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ProviderName = new(@"provider_name[""']?\s*:\s*[""'](?<name>[^""']+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory());
        try
        {
            await RunAsync(root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"V4F_F_ERROR={ex}");
            return 2;
        }
    }

    private static async Task RunAsync(string root)
    {
        var execution = Full(root, RootRelative + "/execution");
        var output = Full(root, RootRelative + "/forensics");
        var beforeHashes = Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories)
            .ToDictionary(x => Path.GetRelativePath(execution, x), Sha256File, StringComparer.Ordinal);

        var manifest = ReadJson(Full(root, RootRelative + "/execution/execution-manifest.json"));
        var sample = ReadJson(Full(root, RootRelative + "/sample.json"));
        var paired = ReadJson(Full(root, RootRelative + "/paired-request-manifest.json"));
        var packets = ReadJson(Full(root, "artifacts/identity-benchmark/v4/context-projection/packet-manifest.json"));
        var availability = ReadJson(Full(root, "artifacts/identity-benchmark/v4/context-projection/evidence-availability.json"));
        var source = ReadJson(Full(root, SourceRelative));
        var attempts = LoadAttempts(execution);
        Require(attempts.Count == ExpectedAttempts, "V4F_F_ATTEMPT_COUNT");
        Require(attempts.Select(x => x.Sequence).Order().SequenceEqual(Enumerable.Range(1, ExpectedAttempts)), "V4F_F_ATTEMPT_SEQUENCE");
        Require(attempts.Count(x => x.Arm == "ARM_A") == ExpectedCandidates && attempts.Count(x => x.Arm == "ARM_B") == ExpectedCandidates, "V4F_F_ARM_BALANCE");
        Require(manifest.GetProperty("goldReadBeforeResponseFreeze").GetBoolean() == false, "V4F_F_GOLD_FIREWALL");

        var catalogFingerprint = source.GetProperty("catalogFingerprint").GetString()!;
        var sourceNodes = source.GetProperty("sourceOccurrences").EnumerateArray()
            .Select(x => new Node(x.GetProperty("nodeId").GetString()!, x.GetProperty("text").GetString()!, x.GetProperty("documentOrder").GetInt32()))
            .GroupBy(x => DocumentOf(x.Id), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidates = sample.GetProperty("candidates").EnumerateArray().Select(x => new Candidate(
            x.GetProperty("pairId").GetString()!, x.GetProperty("documentId").GetString()!, x.GetProperty("left").GetString()!,
            x.GetProperty("right").GetString()!, Optional(x, "packetClass"), Optional(x, "evidenceConfiguration"),
            Optional(x, "sourceOrderDistanceBucket"), Optional(x, "endpointDegreeBucket"), Optional(x, "requestSizeBucket"),
            x.TryGetProperty("reasons", out var reasons) ? reasons.EnumerateArray().Select(y => y.GetString()!).ToArray() : []
        )).ToDictionary(x => x.Id, StringComparer.Ordinal);
        Require(candidates.Count == ExpectedCandidates, "V4F_F_SAMPLE_COUNT");
        var pairedRows = paired.GetProperty("arms").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var packetRows = packets.GetProperty("packets").EnumerateArray().ToDictionary(x => x.GetProperty("candidateId").GetString()!, StringComparer.Ordinal);
        var rows = new List<Row>(ExpectedAttempts);

        foreach (var attempt in attempts.OrderBy(x => x.Sequence))
        {
            var candidate = candidates[attempt.CandidateId];
            var armRow = pairedRows[attempt.CandidateId];
            var request = attempt.Arm == "ARM_A" ? armRow.GetProperty("oldRequest") : armRow.GetProperty("projectedRequest");
            var packet = packetRows[attempt.CandidateId];
            var rawPath = Path.Combine(execution, "raw-responses", $"{attempt.Sequence:D4}-{attempt.Arm}.json");
            var rawAvailable = File.Exists(rawPath);
            var rawBytes = rawAvailable ? (int)new FileInfo(rawPath).Length : (int?)null;
            string outcome;
            string category;
            string? rejection = null;
            ResponseFields? responseFields = null;
            if (attempt.Status == "PROVIDER_ERROR")
            {
                outcome = "PROVIDER_FAILURE"; category = "PROVIDER_FAILURE";
            }
            else if (!rawAvailable && attempt.HttpStatus == 200)
            {
                outcome = "RAW_PERSISTENCE_INCIDENT"; category = "RAW_PERSISTENCE_INCIDENT";
            }
            else if (!rawAvailable)
            {
                outcome = "OTHER_EXPLICIT"; category = "OTHER_EXPLICIT";
            }
            else
            {
                var raw = File.ReadAllText(rawPath);
                Require(!string.IsNullOrWhiteSpace(attempt.RawResponseSha256) && string.Equals(Sha256File(rawPath), attempt.RawResponseSha256, StringComparison.OrdinalIgnoreCase), $"V4F_F_RAW_HASH_MISMATCH_{attempt.Sequence}");
                try
                {
                    var response = HdsaGlobalIdentityRetrieveVerifyContract.ParseVerification(raw);
                    responseFields = new(response.PairId, response.Left, response.Right, response.Relation, response.Direction);
                    var docNodes = sourceNodes[candidate.DocumentId].Select(x => new HdsaIdentityRoleNodeInput(x.Id, [x.Id], x.Text, x.Order, "UNAVAILABLE", false)).ToArray();
                    var requestModel = new HdsaIdentityPairVerificationRequest(catalogFingerprint, docNodes, new HdsaIdentityCandidatePair(candidate.Id, candidate.Left, candidate.Right), false);
                    var validation = HdsaGlobalIdentityRetrieveVerifyContract.ValidateVerification(requestModel, response);
                    if (validation.Accepted)
                    {
                        outcome = "HTTP_SUCCESS_PARSE_VALID"; category = "VALID";
                    }
                    else
                    {
                        outcome = "HTTP_SUCCESS_VALIDATOR_REJECTED"; rejection = validation.RejectionReason;
                        category = ValidatorCategory(validation.RejectionReason);
                    }
                }
                catch (Exception ex) when (ex is FormatException or JsonException)
                {
                    outcome = "HTTP_SUCCESS_PARSE_INVALID"; category = ParseCategory(ex.Message); rejection = ex.Message;
                }
            }
            rows.Add(new Row(attempt, candidate, packet, request, outcome, category, rejection, rawAvailable, rawBytes, responseFields));
        }

        var pairedRowsForAnalysis = rows.GroupBy(x => x.Candidate.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Attempt.Arm, StringComparer.Ordinal), StringComparer.Ordinal);
        var validPairs = pairedRowsForAnalysis.Values.Where(x => x.TryGetValue("ARM_A", out var a) && x.TryGetValue("ARM_B", out var b) && a.Category == "VALID" && b.Category == "VALID").ToArray();
        var agreementRows = validPairs.Select(x =>
        {
            var a = x["ARM_A"]; var b = x["ARM_B"];
            return new { candidateId = a.Candidate.Id, documentId = a.Candidate.DocumentId, oldRelation = a.Response!.Relation, projectedRelation = b.Response!.Relation, agreement = a.Response.Relation == b.Response.Relation, packetClass = a.Candidate.PacketClass, evidenceConfiguration = a.Candidate.EvidenceConfiguration, sourceOrderDistanceBucket = a.Candidate.DistanceBucket, structuralEvidence = a.Candidate.PacketClass == "STRUCTURALLY_PARTIAL", oldBytes = a.RequestBytes, projectedBytes = b.RequestBytes, oldRequestSha256 = a.Attempt.RequestSha256, projectedRequestSha256 = b.Attempt.RequestSha256, oldResponseSha256 = a.Attempt.RawResponseSha256, projectedResponseSha256 = b.Attempt.RawResponseSha256, localContext = true, interveningEvidence = a.Packet.GetProperty("sourceOccurrenceIds").GetArrayLength() > 2 };
        }).ToArray();

        var diagnosis = Diagnose(rows, validPairs.Length);
        var outputFiles = new Dictionary<string, object>
        {
            ["arm-outcome-breakdown.json"] = ArmOutcome(rows),
            ["invalid-response-taxonomy.json"] = InvalidTaxonomy(rows),
            ["provider-failure-analysis.json"] = ProviderAnalysis(rows),
            ["validity-strata.json"] = ValidityStrata(rows),
            ["disagreement-matrix.json"] = DisagreementMatrix(agreementRows),
            ["disagreement-strata.json"] = DisagreementStrata(agreementRows),
            ["target-grounding-analysis.json"] = TargetGrounding(rows),
            ["failure-attribution.json"] = diagnosis,
        };
        foreach (var item in outputFiles) await WriteAsync(Path.Combine(output, item.Key), item.Value);

        var afterHashes = Directory.EnumerateFiles(execution, "*", SearchOption.AllDirectories).ToDictionary(x => Path.GetRelativePath(execution, x), Sha256File, StringComparer.Ordinal);
        Require(beforeHashes.Count == afterHashes.Count && beforeHashes.All(x => afterHashes.TryGetValue(x.Key, out var hash) && hash == x.Value), "V4F_F_EXECUTION_ARTIFACT_MUTATED");
        await WriteAsync(Path.Combine(output, "manifest.json"), new
        {
            schemaVersion = "a99-v4f-f-forensics-manifest-v1", experiment = "IDENTITY_BENCHMARK_V4F_F", parent = "V4F-E@5ae41b6",
            providerCalls = 0, modelCalls = 0, goldReadCount = 0, executionArtifactsMutated = false,
            promptChanged = false, projectionChanged = false, parserChanged = false, goldDerivedInput = false,
            scheduledPairs = ExpectedCandidates, scheduledAttempts = ExpectedAttempts, bothParseValidPairs = validPairs.Length,
            agreementDenominator = validPairs.Length, disagreementCount = agreementRows.Count(x => !x.agreement), primaryDiagnosis = diagnosis.primaryDiagnosis,
            inputExecutionManifestSha256 = Sha256File(Full(root, RootRelative + "/execution/execution-manifest.json")),
            outputFiles = outputFiles.Keys.Order(StringComparer.Ordinal).ToArray(), createdUtc = DateTimeOffset.UtcNow,
        });
        await WriteAsync(Path.Combine(output, "report.md"), BuildReport(rows, validPairs.Length, agreementRows, diagnosis, beforeHashes.Count));
        Console.WriteLine($"V4F_F_COMPLETE diagnosis={diagnosis.primaryDiagnosis} validPairs={validPairs.Length} disagreements={agreementRows.Count(x => !x.agreement)} providerCalls=0 modelCalls=0 goldReadCount=0");
    }

    private static object ArmOutcome(IReadOnlyList<Row> rows) => new
    {
        schemaVersion = "a99-v4f-f-arm-outcome-breakdown-v1", arms = rows.GroupBy(x => x.Attempt.Arm, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new
        {
            arm = g.Key, scheduled = g.Count(), providerFailure = g.Count(x => x.Outcome == "PROVIDER_FAILURE"), rawPersistenceIncident = g.Count(x => x.Outcome == "RAW_PERSISTENCE_INCIDENT"),
            httpSuccessParseValid = g.Count(x => x.Outcome == "HTTP_SUCCESS_PARSE_VALID"), httpSuccessParseInvalid = g.Count(x => x.Outcome == "HTTP_SUCCESS_PARSE_INVALID"),
            httpSuccessValidatorRejected = g.Count(x => x.Outcome == "HTTP_SUCCESS_VALIDATOR_REJECTED"), otherExplicit = g.Count(x => x.Outcome == "OTHER_EXPLICIT"),
            rawAvailable = g.Count(x => x.RawAvailable), rawPersistenceSequences = g.Where(x => x.Outcome == "RAW_PERSISTENCE_INCIDENT").Select(x => x.Attempt.Sequence).Order().ToArray(), rawBytes = g.Where(x => x.RawBytes.HasValue).Select(x => x.RawBytes!.Value).ToArray(),
        }).ToArray(), denominatorPolicy = "provider failures and raw persistence incidents are excluded from semantic agreement" };

    private static object InvalidTaxonomy(IReadOnlyList<Row> rows) => new
    {
        schemaVersion = "a99-v4f-f-invalid-response-taxonomy-v1", categories = rows.Where(x => x.Outcome is "HTTP_SUCCESS_PARSE_INVALID" or "HTTP_SUCCESS_VALIDATOR_REJECTED").GroupBy(x => new { x.Attempt.Arm, x.Category }).Select(g => new { arm = g.Key.Arm, category = g.Key.Category, count = g.Count(), sequences = g.Select(x => x.Attempt.Sequence).Order().ToArray(), rejectionReasons = g.Select(x => x.Rejection).Where(x => x is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }).OrderBy(x => x.arm).ThenBy(x => x.category).ToArray(), rows = rows.Where(x => x.Outcome is "HTTP_SUCCESS_PARSE_INVALID" or "HTTP_SUCCESS_VALIDATOR_REJECTED").OrderBy(x => x.Attempt.Sequence).Select(x => new { sequence = x.Attempt.Sequence, arm = x.Attempt.Arm, candidateId = x.Candidate.Id, outcome = x.Outcome, category = x.Category, rejection = x.Rejection, rawResponseSha256 = x.Attempt.RawResponseSha256, parsedResponseSha256 = x.Attempt.ParsedResponseSha256 }).ToArray(), classificationUses = "frozen parser and validator behavior; no manual repair" };

    private static object ProviderAnalysis(IReadOnlyList<Row> rows)
    {
        var failures = rows.Where(x => x.Outcome == "PROVIDER_FAILURE").Select(x =>
        {
            var error = x.Attempt.Error ?? string.Empty;
            return new { x.Attempt.Sequence, arm = x.Attempt.Arm, httpCode = HttpCode.Match(error).Groups["code"].Value, providerErrorCode = ProviderCode.Match(error).Groups["code"].Value, providerName = ProviderName.Match(error).Groups["name"].Value, errorClass = error.Split(':', 2)[0] };
        }).ToArray();
        return new { schemaVersion = "a99-v4f-f-provider-failure-analysis-v1", provider = Provider, failures, byArm = failures.GroupBy(x => x.arm, StringComparer.Ordinal).Select(g => new { arm = g.Key, count = g.Count(), httpCodes = g.GroupBy(x => x.httpCode).Select(x => new { code = x.Key, count = x.Count() }).ToArray(), providerErrorCodes = g.GroupBy(x => x.providerErrorCode).Select(x => new { code = x.Key, count = x.Count() }).ToArray() }).ToArray(), noRerun = true };
    }

    private static object ValidityStrata(IReadOnlyList<Row> rows) => new
    {
        schemaVersion = "a99-v4f-f-validity-strata-v1", rows = rows.Select(x => new
        {
            sequence = x.Attempt.Sequence, arm = x.Attempt.Arm, candidateId = x.Candidate.Id, documentId = x.Candidate.DocumentId, outcome = x.Outcome, category = x.Category,
            packetClass = x.Candidate.PacketClass, evidenceConfiguration = x.Candidate.EvidenceConfiguration, sourceOrderDistanceBucket = x.Candidate.DistanceBucket,
            endpointDegreeBucket = x.Candidate.EndpointDegreeBucket, requestSizeBucket = x.Candidate.RequestSizeBucket, reasons = x.Candidate.Reasons,
            requestBytes = x.RequestBytes, estimatedInputTokens = (x.RequestBytes + 3) / 4, localContextAvailable = true,
            structuralEvidenceAvailable = x.Candidate.PacketClass == "STRUCTURALLY_PARTIAL", interveningEvidenceAvailable = x.Packet.GetProperty("sourceOccurrenceIds").GetArrayLength() > 2,
            branchMarkerAvailable = x.Candidate.Reasons.Contains("BRANCH_MARKER", StringComparer.Ordinal), outputTokens = x.Attempt.OutputTokens, responseBytes = x.RawBytes,
        }).ToArray(), projectedValidVsInvalid = rows.Where(x => x.Attempt.Arm == "ARM_B").GroupBy(x => x.Category).Select(g => new { category = g.Key, count = g.Count() }).OrderBy(x => x.category).ToArray(), sourceOfAvailability = "frozen V4F-B packet manifest and source-only sample metadata" };

    private static object DisagreementMatrix(IEnumerable<dynamic> agreements)
    {
        var rows = agreements.ToArray();
        var matrix = rows.GroupBy(x => $"{x.oldRelation}->{x.projectedRelation}", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { schemaVersion = "a99-v4f-f-disagreement-matrix-v1", bothParseValidPairs = rows.Length, agreementCount = rows.Count(x => x.agreement), disagreementCount = rows.Count(x => !x.agreement), relationTransitionMatrix = matrix, disagreements = rows.Where(x => !x.agreement).ToArray(), rationaleAvailable = false, rationaleClassification = "NOT_PRESENT_IN_FROZEN_RESPONSE_SCHEMA", semanticAccuracy = "not measured; no Gold read" };
    }

    private static object DisagreementStrata(IEnumerable<dynamic> agreements)
    {
        var rows = agreements.ToArray();
        object Group(Func<dynamic, string> key) => rows.GroupBy(key, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(g => new { stratum = g.Key, count = g.Count(), agreement = g.Count(x => x.agreement), disagreement = g.Count(x => !x.agreement), agreementRate = g.Count() == 0 ? (double?)null : g.Count(x => x.agreement) / (double)g.Count() }).ToArray();
        return new { schemaVersion = "a99-v4f-f-disagreement-strata-v1", bothValidRows = rows.Length, byDocument = Group(x => x.documentId), byPacketClass = Group(x => x.packetClass), byEvidenceConfiguration = Group(x => x.evidenceConfiguration), byDistanceBucket = Group(x => x.sourceOrderDistanceBucket), byStructuralEvidence = Group(x => x.structuralEvidence ? "AVAILABLE" : "UNAVAILABLE"), byLocalInterveningEvidence = Group(x => x.interveningEvidence ? "AVAILABLE" : "UNAVAILABLE"), byProjectedSizeBucket = Group(x => SizeBucket(x.projectedBytes)) };
    }

    private static object TargetGrounding(IReadOnlyList<Row> rows) => new
    {
        schemaVersion = "a99-v4f-f-target-grounding-analysis-v1", byArm = rows.GroupBy(x => x.Attempt.Arm, StringComparer.Ordinal).Select(g => new
        {
            arm = g.Key, parsedResponses = g.Count(x => x.Response is not null), targetPairMismatch = g.Count(x => x.Category == "TARGET_ID_MISMATCH"),
            relationInvalid = g.Count(x => x.Category == "UNKNOWN_RELATION_LABEL"), directionInvalid = g.Count(x => x.Category.Contains("DIRECTION", StringComparison.Ordinal)),
            malformedOrMissing = g.Count(x => x.Outcome == "HTTP_SUCCESS_PARSE_INVALID"), examples = g.Where(x => x.Category == "TARGET_ID_MISMATCH").Take(20).Select(x => new { x.Attempt.Sequence, x.Candidate.Id, response = x.Response }).ToArray(),
        }).ToArray(), interpretation = "target identity is evaluated only against frozen target pair; no Gold" };

    private static Diagnosis Diagnose(IReadOnlyList<Row> rows, int validPairs)
    {
        var old = rows.Where(x => x.Attempt.Arm == "ARM_A").ToArray(); var projected = rows.Where(x => x.Attempt.Arm == "ARM_B").ToArray();
        var pInvalid = projected.Count(x => x.Outcome is "HTTP_SUCCESS_PARSE_INVALID" or "HTTP_SUCCESS_VALIDATOR_REJECTED");
        var oInvalid = old.Count(x => x.Outcome is "HTTP_SUCCESS_PARSE_INVALID" or "HTTP_SUCCESS_VALIDATOR_REJECTED");
        var pTarget = projected.Count(x => x.Category == "TARGET_ID_MISMATCH");
        var pProvider = projected.Count(x => x.Outcome == "PROVIDER_FAILURE");
        string primary;
        if (pInvalid > 0 && pTarget * 2 >= pInvalid && pInvalid > oInvalid) primary = "PROJECTED_TARGET_GROUNDING_DEGRADATION";
        else if (pInvalid > oInvalid) primary = "PROJECTED_RESPONSE_CONTRACT_DEGRADATION";
        else if (pProvider > 64) primary = "PROVIDER_INSTABILITY_DOMINANT";
        else if (pInvalid == 0 && validPairs > 0) primary = "PROJECTED_EVIDENCE_INSUFFICIENCY";
        else primary = "PROJECTED_FAILURE_CAUSE_UNRESOLVED";
        return new Diagnosis(
            primary,
            primary == "PROJECTED_TARGET_GROUNDING_DEGRADATION" ? "Projected HTTP-success invalidity is dominated by frozen validator target-pair mismatches, not Gold accuracy." : "Diagnosis is based only on frozen provider/parser/validator outcomes and source-only request strata.",
            ExpectedCandidates,
            ExpectedAttempts,
            rows.GroupBy(x => x.Candidate.Id).Count(g => g.All(x => x.Attempt.HttpStatus == 200)),
            rows.GroupBy(x => x.Candidate.Id).Count(g => g.All(x => x.RawAvailable)),
            validPairs,
            validPairs,
            rows.GroupBy(x => x.Candidate.Id).Count(g => g.All(x => x.Category == "VALID") && g.Select(x => x.Response?.Relation).Distinct().Count() > 1),
            oInvalid,
            pInvalid,
            pTarget,
            rows.Count(x => x.Outcome == "PROVIDER_FAILURE"),
            rows.Count(x => x.Outcome == "RAW_PERSISTENCE_INCIDENT"),
            0);
    }

    private static string BuildReport(IReadOnlyList<Row> rows, int validPairs, IReadOnlyList<dynamic> agreements, dynamic diagnosis, int executionFileCount)
    {
        var old = rows.Where(x => x.Attempt.Arm == "ARM_A").ToArray(); var projected = rows.Where(x => x.Attempt.Arm == "ARM_B").ToArray();
        var bothTransport = rows.GroupBy(x => x.Candidate.Id).Count(g => g.All(x => x.Attempt.HttpStatus == 200));
        var bothRaw = rows.GroupBy(x => x.Candidate.Id).Count(g => g.All(x => x.RawAvailable));
        return $"# A99 V4F-F — offline projected-verifier failure attribution\n\nStatus: **COMPLETE_OFFLINE_FORENSICS**\n\nPrimary diagnosis: **{diagnosis.primaryDiagnosis}**\n\n## Frozen denominators\n\n- Scheduled pairs: **{ExpectedCandidates}**\n- Scheduled attempts: **{ExpectedAttempts}**\n- Both HTTP-success pairs: **{bothTransport}**\n- Both raw-available pairs: **{bothRaw}**\n- Both parse/validator-valid pairs: **{validPairs}**\n- Agreement denominator: **{validPairs}**\n- Disagreements: **{agreements.Count(x => !x.agreement)}**\n\n## Arm outcomes\n\n- OLD: VALID **{old.Count(x => x.Category == "VALID")}/{ExpectedCandidates}**, provider failures **{old.Count(x => x.Outcome == "PROVIDER_FAILURE"):N0}**, HTTP-success invalid/rejected **{old.Count(x => x.Outcome is "HTTP_SUCCESS_PARSE_INVALID" or "HTTP_SUCCESS_VALIDATOR_REJECTED"):N0}**.\n- PROJECTED: VALID **{projected.Count(x => x.Category == "VALID")}/{ExpectedCandidates}**, provider failures **{projected.Count(x => x.Outcome == "PROVIDER_FAILURE"):N0}**, HTTP-success invalid/rejected **{projected.Count(x => x.Outcome is "HTTP_SUCCESS_PARSE_INVALID" or "HTTP_SUCCESS_VALIDATOR_REJECTED"):N0}**.\n\n## Firewall\n\nProvider calls: **0**; model calls: **0**; Gold reads: **0**. Execution artifacts were hash-checked before and after and were not mutated. No prompt, projection, parser, or response was changed. Frozen response bodies contain no rationale field, so rationale-use classification is not applicable. Finish-reason metadata was not present in the frozen attempt schema.\n\nSee the JSON artifacts in this directory for exact per-attempt classifications, request-side strata, target grounding, provider 429 attribution, and valid-pair transition matrix. No semantic accuracy claim is made.\n";
    }

    private static string ValidatorCategory(string? reason) => reason switch
    {
        "TARGET_PAIR_MISMATCH" => "TARGET_ID_MISMATCH",
        "RELATION_INVALID" => "UNKNOWN_RELATION_LABEL",
        "CONTINUATION_DIRECTION_INVALID" or "NON_CONTINUATION_DIRECTION_INVALID" => "DIRECTION_CONTRACT_MISMATCH",
        "GOLD_DERIVED_INPUT" => "OTHER_EXPLICIT",
        _ => "VALIDATOR_REJECTED_OTHER",
    };

    private static string ParseCategory(string message)
    {
        if (message.Contains("EXTRA_PROPERTY", StringComparison.Ordinal)) return "EXTRA_WRAPPER_TEXT";
        if (message.Contains("REQUIRED_PROPERTY", StringComparison.Ordinal) || message.Contains("_MISSING", StringComparison.Ordinal)) return "MISSING_REQUIRED_FIELD";
        if (message.Contains("NOT_ARRAY", StringComparison.Ordinal) || message.Contains("INVALID", StringComparison.Ordinal)) return "SCHEMA_TYPE_ERROR";
        return "MALFORMED_JSON";
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static string SizeBucket(int bytes) => bytes <= 10_000 ? "SMALL_LE_10K" : bytes <= 100_000 ? "MEDIUM_LE_100K" : "LARGE_GT_100K";
    private static string Optional(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() ?? "UNAVAILABLE" : "UNAVAILABLE";
    private static string DocumentOf(string id) => id.Split(':', 2)[0];
    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task WriteAsync(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, new UTF8Encoding(false)); }
    private static List<Attempt> LoadAttempts(string execution) => Directory.EnumerateFiles(Path.Combine(execution, "attempts"), "*.json").Select(x => JsonSerializer.Deserialize<Attempt>(File.ReadAllText(x), JsonOptions) ?? throw new InvalidDataException(x)).OrderBy(x => x.Sequence).ToList();
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string[] DistinctValues(IEnumerable<string?> values) => values.Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private sealed record Attempt(int Sequence, string CandidateId, string Arm, string RequestId, string RequestSha256, int RequestBytes, int AttemptNumber, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string Status, int? HttpStatus, string? ProviderRequestId, long LatencyMs, int? InputTokens, int? OutputTokens, string? RawResponseSha256, string? ParsedResponseSha256, string? Error, bool GoldReadBeforeFreeze);
    private sealed record Node(string Id, string Text, int Order);
    private sealed record Candidate(string Id, string DocumentId, string Left, string Right, string PacketClass, string EvidenceConfiguration, string DistanceBucket, string EndpointDegreeBucket, string RequestSizeBucket, IReadOnlyList<string> Reasons);
    private sealed record ResponseFields(string PairId, string Left, string Right, string Relation, string Direction);
    private sealed record Diagnosis(string primaryDiagnosis, string rationale, int scheduledPairs, int scheduledAttempts, int bothTransportSuccessPairs, int bothRawAvailablePairs, int bothParseValidPairs, int agreementDenominator, int disagreementCount, int oldInvalidHttpSuccess, int projectedInvalidHttpSuccess, int projectedTargetMismatch, int providerFailures, int rawPersistenceIncidents, int goldReadCount)
    {
        public string schemaVersion => "a99-v4f-f-failure-attribution-v1";
    }
    private sealed record Row(Attempt Attempt, Candidate Candidate, JsonElement Packet, JsonElement Request, string Outcome, string Category, string? Rejection, bool RawAvailable, int? RawBytes, ResponseFields? Response)
    {
        public int RequestBytes => Request.GetProperty("requestBytes").GetInt32();
    }
    private sealed record _Dummy;
}
