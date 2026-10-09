using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

if (args.Length != 4) throw new ArgumentException("Usage: P7GoldFreezeQualification <gold-v2-dir> <source-pack> <new-private-dir> <new-public-receipt>");
if (Directory.Exists(args[2]) || File.Exists(args[3])) throw new InvalidOperationException("NEW_OUTPUT_REQUIRED");
var manifestBytes = ReadPinned(Path.Combine(args[0], "gold-manifest.v2.json"), "614b2e09e7568446d343dbab18f4f9d405007657d6f5a665fb1538909de073f7");
using var manifest = JsonDocument.Parse(manifestBytes); var m = manifest.RootElement;
var policyBytes = ReadPinned(Path.Combine(args[0], "evaluation-policy.v1.json"), m.GetProperty("evaluationPolicySha256").GetString()!);
ReadPinned(Path.Combine(args[0], "user-approval.v1.json"), m.GetProperty("approvalSha256").GetString()!);
var oldScorerBytes = ReadPinned(Path.Combine(args[0], "scorer-manifest.v1.json"), m.GetProperty("scorerManifestSha256").GetString()!);
using var oldScorer = JsonDocument.Parse(oldScorerBytes);
foreach (var file in oldScorer.RootElement.GetProperty("sourceFiles").EnumerateArray())
    ReadPinned(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!);
var scenarios = new[] { "BASELINE", "SIDECAR_DELETED", "ALL_ROLE_AND_EXPLANATION_TEXT_CHANGED", "NEW_UNKNOWN_ROLE" };
var summaries = new List<object>(); var memberships = 0; var units = 0; var spans = 0; var comparisons = 0;
var scopeCrossings = 0; var baselineScenarios = 0; var oracleTP = 0;
var functions = new Dictionary<string, int>(StringComparer.Ordinal);
var sourceFiles = new[] { "P7SourceOnlyReview.cs", "P7EvaluationUniverse.cs", "P7PilotScorer.cs", "P7ApprovedPilotGold.cs", "P7PilotGoldReader.cs" }
    .Select(f => "src/DocxHeaderExtractor.V5Qualification/P7/" + f)
    .Concat(["scripts/P7GoldFreezeQualification/Program.cs", "scripts/P7GoldFreezeQualification/P7GoldFreezeQualification.csproj"])
    .Select(f => new { path = f, sha256 = HashFile(f) }).ToArray();
Directory.CreateDirectory(args[2]);
foreach (var d in m.GetProperty("documents").EnumerateArray())
{
    var id = d.GetProperty("document").GetString()!; var filename = d.GetProperty("goldFile").GetString()!;
    var goldBytes = ReadPinned(Path.Combine(args[0], filename), d.GetProperty("goldSha256").GetString()!);
    var sidecarFile = d.GetProperty("sidecarFile").GetString()!;
    var sidecarBytes = ReadPinned(Path.Combine(args[0], sidecarFile), d.GetProperty("sidecarSha256").GetString()!);
    var snapshotBytes = ReadPinned(Path.Combine(args[1], id, "snapshot.json"), d.GetProperty("snapshotSha256").GetString()!);
    Require(HashFile(Path.Combine(args[1], id, "source.pdf")) == d.GetProperty("sourceSha256").GetString(), "SOURCE_PDF_DRIFT");
    using var snapshot = JsonDocument.Parse(snapshotBytes);
    var source = snapshot.RootElement.GetProperty("atoms").EnumerateArray().Select(a => new PilotSourceAtom(
        a.GetProperty("alias").GetString()!, a.GetProperty("sourceId").GetString()!, a.GetProperty("ordinal").GetInt32(),
        a.GetProperty("page").GetInt32(), a.GetProperty("text").GetString()!)).ToArray();
    var byAlias = source.ToDictionary(a => a.Alias);
    var atoms = source.Select(a => new ReviewOccurrence(a.Alias, a.Page, a.Text.Length)).ToArray();
    var original = P7PilotGoldReader.Read(goldBytes, policyBytes);
    Require(original.Document == id && original.Scope.SourceSha256 == d.GetProperty("sourceSha256").GetString() &&
        original.Scope.UniverseSha256 == d.GetProperty("universeSha256").GetString() &&
        original.Scope.SnapshotSha256 == d.GetProperty("snapshotSha256").GetString() &&
        original.Scope.Pages.SequenceEqual(d.GetProperty("pages").EnumerateArray().Select(p => p.GetInt32())), "SOURCE_SCOPE_MISMATCH");
    Require(original.ReviewedRows.Select(r => r.Alias).Order().SequenceEqual(source.Where(a => original.Scope.Pages.Contains(a.Page)).Select(a => a.Alias).Order()), "REVIEWED_ROW_PARTITION_MISMATCH");
    foreach (var row in original.ReviewedRows)
    {
        var a = byAlias[row.Alias]; var annotation = original.Annotations.Single(x => x.Alias == row.Alias);
        Require(row.SourceId == a.SourceId && row.Ordinal == a.Ordinal && row.Page == a.Page &&
            annotation.Status == "ADJUDICATED" && annotation.SemanticFunction == row.SemanticFunction &&
            annotation.HeadingMembership == row.HeadingMembership && row.IsDistinctAnchor == original.Units.Any(u => u.Anchor == row.Alias), "DECISION_OR_IDENTITY_MISMATCH");
    }
    using var goldJson = JsonDocument.Parse(goldBytes);
    var sourceParts = goldJson.RootElement.GetProperty("sourceParts").EnumerateArray().ToArray();
    Require(sourceParts.Length == original.Units.Sum(u => u.Parts.Count), "SPAN_CARDINALITY_MISMATCH");
    foreach (var p in sourceParts)
    {
        var a = byAlias[p.GetProperty("alias").GetString()!];
        Require(p.GetProperty("sourceId").GetString() == a.SourceId && p.GetProperty("text").GetString() == a.Text &&
            p.GetProperty("ordinal").GetInt32() == a.Ordinal && p.GetProperty("start").GetInt32() == 0 &&
            p.GetProperty("length").GetInt32() == a.Text.Length && original.Units.Single(u => u.Anchor == p.GetProperty("unitAnchor").GetString())
                .Parts.Contains(new(a.Alias, 0, a.Text.Length)), "EXPLICIT_UTF16_SPAN_MISMATCH");
    }
    var oracle = original.Units.Select((u, i) => new PilotPrediction("oracle-" + i, u.Anchor, u.Parts)).ToArray();
    var negative = original.ReviewedRows.First(r => !r.HeadingMembership);
    var positive = original.Units[0];
    var exiting = original.Units.First(u => !original.ReviewedRows.Single(r => r.Alias == u.ReviewedFirstOutside).HeadingMembership);
    var outside = byAlias[exiting.ReviewedFirstOutside!];
    var predictions = new List<(string Name, PilotPrediction[] Predictions)> {
        ("ORACLE", oracle), ("NONE", []),
        ("EXTRA_FP", [.. oracle, new("fp", negative.Alias, [new(negative.Alias, 0, byAlias[negative.Alias].Text.Length)])]),
        ("OVEREXTENT", [new("over", exiting.Anchor, [.. exiting.Parts, new(outside.Alias, 0, outside.Text.Length)])]),
        ("DUPLICATE_OVERLAP", [oracle[0], oracle[0] with { Id = "duplicate" }]),
        ("INVALID_REFERENCE", [new("invalid", "UNKNOWN", [new("UNKNOWN", 0, 1)])]),
        ("WRONG_SPAN", [new("wrong-span", positive.Anchor, positive.Parts.Select((p, i) => i == 0 ? p with { Start = 1, Length = p.Length - 1 } : p).ToArray())]) };
    var multipart = original.Units.FirstOrDefault(u => u.Parts.Count > 1);
    if (multipart is not null) predictions.Add(("UNDEREXTENT", [new("under", multipart.Anchor, [multipart.Parts[0]])]));
    var last = Array.FindLastIndex(source, a => original.Scope.Pages.Contains(a.Page));
    if (last + 1 < source.Length)
    {
        var end = source[last + 1];
        var crossing = source.SkipWhile(a => a.Alias != positive.Anchor).TakeWhile(a => a.Ordinal <= end.Ordinal)
            .Select(a => new ReviewedPart(a.Alias, 0, a.Text.Length)).ToArray();
        predictions.Add(("CROSS_SCOPE", [new("crossing", positive.Anchor, crossing)])); scopeCrossings++;
    }
    var baselineScores = predictions.Select(p => new { p.Name, score = P7PilotScorer.Score(original.Scope, atoms, original.Annotations, original.Units, p.Predictions) }).ToArray();
    var oracleScore = baselineScores.Single(x => x.Name == "ORACLE").score;
    Require(oracleScore.EvaluableMembership && oracleScore.ReadinessGaps.Count == 0 && oracleScore.Exact == original.Units.Count &&
        oracleScore.NotEvaluable == 0 && oracleScore.MembershipFP == 0 && oracleScore.MembershipFN == 0 &&
        oracleScore.MembershipTP == original.ReviewedRows.Count(r => r.HeadingMembership), "ORACLE_NOT_READY");
    oracleTP += oracleScore.MembershipTP!.Value;
    foreach (var row in original.ReviewedRows) functions[row.SemanticFunction] = functions.GetValueOrDefault(row.SemanticFunction) + 1;
    if (baselineScores.Any(x => x.Name == "CROSS_SCOPE"))
    {
        var cross = baselineScores.Single(x => x.Name == "CROSS_SCOPE").score;
        Require(cross.NotEvaluable == 1 && cross.MembershipFP > 0 && cross.Wrong == 0 &&
            cross.Boundaries[0].FullPrediction.SequenceEqual(predictions.Single(x => x.Name == "CROSS_SCOPE").Predictions[0].Parts), "CROSS_SCOPE_CONTRACT_FAILED");
    }
    var checks = new List<object>();
    foreach (var variant in scenarios)
    {
        var directory = Path.Combine(args[2], filename.Split('.')[0], variant); Directory.CreateDirectory(directory);
        Write(Path.Combine(directory, filename), goldBytes); Write(Path.Combine(directory, "evaluation-policy.v1.json"), policyBytes);
        var sidecarPath = Path.Combine(directory, sidecarFile);
        Write(sidecarPath, sidecarBytes);
        if (variant == "SIDECAR_DELETED") File.Delete(sidecarPath); // only the newly-created private copy
        else if (variant != "BASELINE")
        {
            var node = JsonNode.Parse(sidecarBytes)!;
            if (variant == "ALL_ROLE_AND_EXPLANATION_TEXT_CHANGED") ReplaceInterpretations(node);
            else node["notes"]!.AsArray().Add(new JsonObject { ["alias"] = negative.Alias,
                ["proposalRoleNote"] = "UNSEEN_ROLE_QUALIFICATION_ONLY_20261009", ["explanation"] = "A new unconstrained reviewer hypothesis, not a fact or scoring label." });
            File.WriteAllBytes(sidecarPath, Bytes(node)); // changes only the newly-created private copy
            Require(HashFile(sidecarPath) != Hash(sidecarBytes), "SIDECAR_MUTATION_NOT_EXERCISED");
        }
        // This is the actual scoring entrypoint: two paths, never a sidecar or archive manifest.
        var current = P7PilotGoldReader.Load(Path.Combine(directory, filename), Path.Combine(directory, "evaluation-policy.v1.json"));
        Require(Bytes(current).SequenceEqual(Bytes(original)), "SIDECAR_CHANGED_PARSED_GOLD");
        var scores = predictions.Select(p => new { p.Name, score = P7PilotScorer.Score(current.Scope, atoms, current.Annotations, current.Units, p.Predictions) }).ToArray();
        Require(Bytes(scores).SequenceEqual(Bytes(baselineScores)), "SIDECAR_CHANGED_SCORING");
        Require(HashFile(Path.Combine(directory, filename)) == Hash(goldBytes), "GOLD_DECISION_BYTES_MUTATED");
        if (variant != "BASELINE") comparisons += scores.Length;
        var scoreBytes = Bytes(scores); Write(Path.Combine(directory, "synthetic-scores.json"), scoreBytes);
        checks.Add(new { variant, sidecarPresent = File.Exists(sidecarPath),
            sidecarSha256 = File.Exists(sidecarPath) ? HashFile(sidecarPath) : null,
            goldSha256 = Hash(goldBytes), scoreSha256 = Hash(scoreBytes), parsedGoldByteIdentical = true, fullScoreByteIdentical = true });
    }
    baselineScenarios += predictions.Count; memberships += original.ReviewedRows.Count; units += original.Units.Count; spans += sourceParts.Length;
    summaries.Add(new { document = id, goldSha256 = Hash(goldBytes), originalSidecarSha256 = Hash(sidecarBytes),
        rows = original.ReviewedRows.Count, closedUnits = original.Units.Count, explicitSpans = sourceParts.Length,
        scenarioCount = predictions.Count, checks, crossing = last + 1 < source.Length ? "EXERCISED" : "NO_OUTSIDE_OCCURRENCE_D05" });
}
Require(memberships == 213 && units == 15 && spans == 20 && summaries.Count == 5 && oracleTP == 20 &&
    functions.Count == 3 && functions.GetValueOrDefault("ESTABLISHES_STRUCTURE") == 20 &&
    functions.GetValueOrDefault("REPRESENTS_STRUCTURE") == 22 && functions.GetValueOrDefault("OTHER") == 171, "PILOT_CARDINALITY_DRIFT");
var qualificationManifest = Bytes(new { version = "P7_GOLD_V2_FREEZE_QUALIFICATION_HARNESS_V1", goldManifestSha256 = Hash(manifestBytes),
    scorerManifestSha256 = Hash(oldScorerBytes), policySha256 = Hash(policyBytes), readerVersion = P7PilotGoldReader.Version,
    sourceFiles, sidecarIsolation = "SCORING_OPENS_ONLY_GOLD_AND_POLICY_ARCHIVE_INTEGRITY_SEPARATE",
    scenarios, providerCalls = 0 });
Write(Path.Combine(args[2], "qualification-harness-manifest.v1.json"), qualificationManifest);
var receipt = Bytes(new { version = "P7_D23_GOLD_V2_FREEZE_QUALIFICATION_V1", status = "GOLD_SCORER_QUALIFICATION_PASS_REQUEST_MANIFEST_NOT_FROZEN",
    goldManifestSha256 = Hash(manifestBytes), scorerManifestSha256 = Hash(oldScorerBytes), policySha256 = Hash(policyBytes),
    qualificationHarnessManifestSha256 = Hash(qualificationManifest), documents = summaries,
    sidecarIsolationDocumentChecks = summaries.Count * 3, syntheticBaselineScenarios = baselineScenarios,
    sidecarIsolationScoreComparisons = comparisons, allParsedGoldAndFullScoresByteIdentical = true,
    adjudicatedMembershipRows = memberships, oracleExactBoundaries = units, explicitUtf16SpansValidated = spans,
    semanticFunctionCounts = functions, oracleTP, oracleFP = 0, oracleFN = 0, oracleNotEvaluable = 0, crossScopeCases = scopeCrossings,
    crossScopeFullPredictionPreserved = true, knownMembershipFPsScoredDespiteNotEvaluableBoundary = true,
    approvalAndPolicyProvenanceHashVerified = true, sourceAndGoldMutation = "NONE_NEW_TEST_COPIES_ONLY",
    syntheticResultsAreNotModelAccuracy = true, goldScorerQualification = "PASS", requestManifests = "NOT_FROZEN",
    d23 = "OPEN_REQUEST_FREEZE_PENDING", exactTokenizerMapping = "BLOCKED", providerUsageMeasurement = "BLOCKED",
    providerCalls = 0, productionChanged = false, frozenRequestsChanged = false, providerExecution = "LOCKED", productionPromotion = "LOCKED" });
Write(Path.Combine(args[2], "freeze-qualification-receipt.v1.json"), receipt); Write(args[3], receipt);
Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", receiptSha256 = Hash(receipt), sidecarChecks = 15,
    scoreComparisons = comparisons, rows = memberships, units, spans, providerCalls = 0 }));

static void ReplaceInterpretations(JsonNode node)
{
    if (node is JsonArray array) { foreach (var child in array) if (child is not null) ReplaceInterpretations(child); }
    else if (node is JsonObject obj)
        foreach (var property in obj.ToArray())
        {
            if (property.Key is "proposalRoleNote" or "explanation" or "interpretation" or "alternativeInterpretation")
                obj[property.Key] = "REPLACED_FREE_TEXT_WITHOUT_TASK_AUTHORITY_" + property.Key;
            else if (property.Value is not null) ReplaceInterpretations(property.Value);
        }
}
static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
static byte[] ReadPinned(string path, string sha) { var bytes = File.ReadAllBytes(path); Require(Hash(bytes) == sha, "PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return bytes; }
static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
static void Write(string path, byte[] bytes) { using var f = new FileStream(path, FileMode.CreateNew); f.Write(bytes); f.Flush(true); }
