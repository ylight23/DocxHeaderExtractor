using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Scores frozen G2A/H2 captures against the existing canonical Gold and builds the H2 conflict
/// census with anchor retained. It never mutates Gold or promotes runtime behavior.
/// </summary>
public sealed class V5P6TH3GoldPopulationAuditTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string G2APreflightPath = Root + "/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json";
    private const string G2ACaptureRoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string H2ManifestPath = Root + "/p6th3-full-population-h2-preflight/g2a-raw-audit-and-h2-full-request-manifest.v1.json";
    private const string H2CaptureRoot = Root + "/p6th3-full-population-h2-canary-20261005";
    private const string OutputRoot = Root + "/p6th3-full-population-gold-audit";

    private static readonly string[] DocumentIds = ["SRC-089", "SRC-041", "SRC-095", "DOC-0252", "DOC-0256"];

    [Fact]
    public void Frozen_G2A_H2_and_anchor_scoped_conflicts_are_audited_against_unchanged_canonical_Gold()
    {
        using var g2aPreflightDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G2APreflightPath)));
        using var g2aResultDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{G2ACaptureRoot}/result.v1.json")));
        using var h2ManifestDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(H2ManifestPath)));
        using var h2ResultDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{H2CaptureRoot}/result.v1.json")));
        var g2aPreflight = g2aPreflightDoc.RootElement;
        var h2Manifest = h2ManifestDoc.RootElement;
        var h2Result = h2ResultDoc.RootElement;
        Assert.Equal("ALL_FIVE_TOTAL_LEDGERS_ACCEPTED", g2aResultDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("ALL_FROZEN_H2_LEDGERS_ACCEPTED", h2Result.GetProperty("status").GetString());
        Assert.Equal(31, h2Result.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, h2Result.GetProperty("retry").GetInt32());
        Assert.False(h2Result.GetProperty("goldRead").GetBoolean());

        var goldByDocument = DocumentIds.ToDictionary(value => value, LoadGold, StringComparer.Ordinal);
        var g2aByDocument = new Dictionary<string, G2ADocument>(StringComparer.Ordinal);
        foreach (var documentId in DocumentIds)
        {
            var frozen = g2aPreflight.GetProperty("cohort").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == documentId);
            var capturePath = TestRepository.Path($"{G2ACaptureRoot}/{documentId}.raw-capture.v1.json");
            var bytes = File.ReadAllBytes(capturePath);
            using var captureDoc = JsonDocument.Parse(bytes);
            var capture = captureDoc.RootElement;
            Assert.Equal("stop", capture.GetProperty("finishReason").GetString());
            Assert.Equal(0, capture.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(capture.GetProperty("rawResponse").GetString()!), capture.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(Hash(capture.GetProperty("rawSse").GetString()!), capture.GetProperty("rawSseSha256").GetString());
            var issued = frozen.GetProperty("issuedPrimaries").EnumerateArray().ToDictionary(
                value => value.GetProperty("occurrence").GetString()!,
                value => value.GetProperty("alias").GetString()!, StringComparer.Ordinal);
            var decisions = ParseG2A(capture.GetProperty("rawResponse").GetString()!);
            Assert.Equal(issued.Count, decisions.Count);
            Assert.True(issued.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(decisions.Keys), $"G2A issued set mismatch:{documentId}");
            g2aByDocument.Add(documentId, new G2ADocument(frozen.GetProperty("packId").GetString()!, issued, decisions,
                Relative(capturePath), Hash(bytes), capture.GetProperty("rawResponseSha256").GetString()!, capture.GetProperty("rawSseSha256").GetString()!));
        }

        var g2aRows = new List<G2ARow>();
        foreach (var documentId in DocumentIds)
        {
            var gold = goldByDocument[documentId];
            var g2a = g2aByDocument[documentId];
            foreach (var (occurrence, alias) in g2a.IssuedAliases)
            {
                var predicted = g2a.Decisions[occurrence] == "HAS_STRUCTURAL_EXTENT";
            var expected = gold.GoldStarts.Contains(alias);
                var outcome = (expected, predicted) switch
                {
                    (true, true) => "TP",
                    (false, true) => "FP",
                    (true, false) => "FN",
                    (false, false) => "TN",
                };
                g2aRows.Add(new G2ARow(documentId, g2a.PackId, occurrence, alias, expected, predicted, outcome));
            }
        }
        Assert.Equal(48, g2aRows.Count);
        Assert.Equal(27, g2aRows.Count(value => value.Outcome == "TP"));
        Assert.Equal(4, g2aRows.Count(value => value.Outcome == "FP"));
        Assert.Equal(3, g2aRows.Count(value => value.Outcome == "FN"));
        Assert.Equal(14, g2aRows.Count(value => value.Outcome == "TN"));

        var requestRows = h2Manifest.GetProperty("requestUniverse").EnumerateArray().ToArray();
        var h2ResultRows = h2Result.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, requestRows.Length);
        Assert.Equal(31, h2ResultRows.Length);
        var resultByKey = h2ResultRows.ToDictionary(value => CallKey(value), StringComparer.Ordinal);
        var h2Calls = new List<H2Call>();
        var scopedEdges = new List<ScopedEdge>();
        foreach (var request in requestRows)
        {
            var documentId = request.GetProperty("documentId").GetString()!;
            var packId = request.GetProperty("packId").GetString()!;
            var anchor = request.GetProperty("anchor").GetString()!;
            var callKey = $"{documentId}|{packId}|{anchor}";
            Assert.True(resultByKey.TryGetValue(callKey, out var result), $"missing H2 result:{callKey}");
            Assert.Equal("stop", result!.GetProperty("finishReason").GetString());
            Assert.Equal(0, result.GetProperty("retryCount").GetInt32());
            Assert.True(result.GetProperty("ledgerAccepted").GetBoolean());
            Assert.True(result.GetProperty("monotonic").GetBoolean());

            var rawPath = TestRepository.Path($"{H2CaptureRoot}/{documentId}_{anchor}.raw-capture.v1.json");
            var rawBytes = File.ReadAllBytes(rawPath);
            using var rawDoc = JsonDocument.Parse(rawBytes);
            var raw = rawDoc.RootElement;
            Assert.Equal(request.GetProperty("providerBodySha256").GetString(), raw.GetProperty("providerBodySha256").GetString());
            Assert.Equal("stop", raw.GetProperty("finishReason").GetString());
            Assert.Equal(0, raw.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(raw.GetProperty("rawResponse").GetString()!), raw.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(Hash(raw.GetProperty("rawSse").GetString()!), raw.GetProperty("rawSseSha256").GetString());

            var g2a = g2aByDocument[documentId];
            Assert.Equal(g2a.RawCaptureSha256, raw.GetProperty("g2aRawCaptureSha256").GetString());
            Assert.Equal("HAS_STRUCTURAL_EXTENT", g2a.Decisions[anchor]);
            var decisionRows = ParseH2(raw.GetProperty("rawResponse").GetString()!);
            var expectedEdges = request.GetProperty("edges").EnumerateArray().ToArray();
            Assert.Equal(expectedEdges.Length, decisionRows.Count);
            var decisionByEdge = decisionRows.ToDictionary(value => $"{value.Left}|{value.Right}", StringComparer.Ordinal);
            Assert.Equal(expectedEdges.Length, decisionByEdge.Count);
            var edgeRows = new List<ScopedEdge>();
            var seenStop = false;
            foreach (var edge in expectedEdges)
            {
                var left = edge.GetProperty("left").GetString()!;
                var right = edge.GetProperty("right").GetString()!;
                var leftAlias = edge.GetProperty("leftAlias").GetString()!;
                var rightAlias = edge.GetProperty("rightAlias").GetString()!;
                var boundary = decisionByEdge[$"{left}|{right}"].Boundary;
                if (seenStop && boundary == "CONTINUES_STRUCTURAL_UNIT")
                    throw new InvalidDataException($"nonmonotonic H2 response after stop:{callKey}");
                if (boundary == "STOPS_STRUCTURAL_UNIT") seenStop = true;
                var g2aRight = g2a.Decisions.TryGetValue(right, out var rightStatus) ? rightStatus : null;
                var conflict = g2aRight == "HAS_STRUCTURAL_EXTENT" && boundary == "CONTINUES_STRUCTURAL_UNIT";
                var goldClass = !conflict ? null : GoldConflictClass(goldByDocument[documentId], leftAlias, rightAlias);
                var scoped = new ScopedEdge(documentId, packId, anchor, left, right, leftAlias, rightAlias, boundary, g2aRight, conflict, goldClass);
                edgeRows.Add(scoped);
                scopedEdges.Add(scoped);
            }
            h2Calls.Add(new H2Call(documentId, packId, anchor,
                g2a.IssuedAliases[anchor], decisionRows.Count,
                decisionRows.Count(value => value.Boundary == "CONTINUES_STRUCTURAL_UNIT"),
                decisionRows.Count(value => value.Boundary == "STOPS_STRUCTURAL_UNIT"),
                Relative(rawPath), Hash(rawBytes), raw.GetProperty("rawResponseSha256").GetString()!, raw.GetProperty("rawSseSha256").GetString()!,
                raw.GetProperty("reasoningTokens").GetInt32(), raw.GetProperty("latencyMs").GetDouble(), edgeRows));
        }

        var conflicts = scopedEdges.Where(value => value.Conflict).ToArray();
        Assert.Equal(1_790, scopedEdges.Count);
        Assert.Equal(19, conflicts.Length);
        Assert.Equal(14, conflicts.Select(value => PhysicalEdgeKey(value)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(15, conflicts.Count(value => value.GoldClass == "NEW_STRUCTURAL_UNIT"));
        Assert.Equal(4, conflicts.Count(value => value.GoldClass == "CONTINUES_LEFT_STRUCTURAL_UNIT"));
        Assert.Equal(0, conflicts.Count(value => value.GoldClass == "GOLD_AUTHORITY_GAP"));

        var trueAnchorRows = new List<ExtentRow>();
        var falseAnchorRows = new List<object>();
        foreach (var call in h2Calls)
        {
            var g2a = g2aByDocument[call.DocumentId];
            var gold = goldByDocument[call.DocumentId];
            var alias = call.AnchorAlias;
            var matchingGold = gold.GoldExtents.Where(value => value.Aliases[0] == alias).ToArray();
            if (matchingGold.Length == 0)
            {
                falseAnchorRows.Add(new { documentId = call.DocumentId, packId = call.PackId, anchor = call.Anchor, alias, g2a = g2a.Decisions[call.Anchor], h2Call = call.RawPath, h2ContinueEdges = call.Continues, h2StopEdges = call.Stops, extentScore = "EXCLUDED_FALSE_G2A_ANCHOR" });
                continue;
            }
            Assert.Equal("HAS_STRUCTURAL_EXTENT", g2a.Decisions[call.Anchor]);
            Assert.Single(matchingGold);
            var goldExtent = matchingGold[0];
            var firstStop = Array.FindIndex(call.Edges.ToArray(), value => value.Boundary == "STOPS_STRUCTURAL_UNIT");
            var predictedAliases = new List<string> { alias };
            foreach (var edge in call.Edges)
            {
                if (edge.Boundary != "CONTINUES_STRUCTURAL_UNIT") break;
                predictedAliases.Add(edge.RightAlias);
            }
            var outcome = ClassifyExtent(predictedAliases, goldExtent.Aliases);
            var requiredContinuationPairs = goldExtent.Aliases.Zip(goldExtent.Aliases.Skip(1), (left, right) => (left, right)).ToArray();
            var internalEdges = requiredContinuationPairs.Select(pair => call.Edges.SingleOrDefault(value => value.LeftAlias == pair.left && value.RightAlias == pair.right)).ToArray();
            Assert.All(internalEdges, edge => Assert.NotNull(edge));
            var internalContinueCorrect = internalEdges.Count(value => value!.Boundary == "CONTINUES_STRUCTURAL_UNIT");
            var goldLastAlias = goldExtent.Aliases[^1];
            var exitEdge = call.Edges.SingleOrDefault(value => value.LeftAlias == goldLastAlias);
            var exitStopCorrect = exitEdge?.Boundary == "STOPS_STRUCTURAL_UNIT";
            var wrongExitObserved = exitEdge?.Boundary == "CONTINUES_STRUCTURAL_UNIT";
            trueAnchorRows.Add(new ExtentRow(call.DocumentId, call.PackId, call.Anchor, alias,
                goldExtent.Aliases, predictedAliases, outcome, call.Continues, call.Stops,
                requiredContinuationPairs.Length, internalContinueCorrect,
                exitEdge is not null, exitStopCorrect, exitEdge?.RightAlias, exitEdge?.G2ARight,
                wrongExitObserved, firstStop));
        }

        Assert.Equal(27, trueAnchorRows.Count);
        Assert.Equal(4, falseAnchorRows.Count);
        Assert.Equal(6, trueAnchorRows.Count(value => value.ExtentOutcome == "EXACT"));
        Assert.Equal(21, trueAnchorRows.Count(value => value.ExtentOutcome == "OVEREXTENT"));
        Assert.Equal(0, trueAnchorRows.Count(value => value.ExtentOutcome == "UNDEREXTENT"));
        Assert.Equal(0, trueAnchorRows.Count(value => value.ExtentOutcome == "WRONG_PARTS"));
        Assert.Equal(8, trueAnchorRows.Sum(value => value.ExpectedInternalContinuationEdges));
        Assert.Equal(8, trueAnchorRows.Sum(value => value.CorrectInternalContinuationEdges));
        Assert.Equal(6, trueAnchorRows.Count(value => value.ExitEdgeIssued && value.ExitStopCorrect));
        var overextensions = trueAnchorRows.Where(value => value.ExtentOutcome == "OVEREXTENT").ToArray();
        Assert.Equal(21, overextensions.Length);
        Assert.All(overextensions, value => Assert.True(value.FirstWrongBoundaryContinued));
        Assert.Equal(5, overextensions.Count(value => value.G2ARightAtExit == "HAS_STRUCTURAL_EXTENT"));
        Assert.Equal(0, overextensions.Count(value => value.G2ARightAtExit == "NO_STRUCTURAL_EXTENT"));
        Assert.Equal(16, overextensions.Count(value => value.G2ARightAtExit is null));

        var perDocument = DocumentIds.Select(documentId =>
        {
            var rows = g2aRows.Where(value => value.DocumentId == documentId).ToArray();
            var extentRows = trueAnchorRows.Where(value => value.DocumentId == documentId).ToArray();
            var scopedConflicts = conflicts.Where(value => value.DocumentId == documentId).ToArray();
            return new
            {
                documentId,
                g2a = new
                {
                    issued = rows.Length,
                    tp = rows.Count(value => value.Outcome == "TP"),
                    fp = rows.Count(value => value.Outcome == "FP"),
                    fn = rows.Count(value => value.Outcome == "FN"),
                    tn = rows.Count(value => value.Outcome == "TN"),
                },
                h2 = new
                {
                    trueGoldAnchors = extentRows.Length,
                    exact = extentRows.Count(value => value.ExtentOutcome == "EXACT"),
                    underextent = extentRows.Count(value => value.ExtentOutcome == "UNDEREXTENT"),
                    overextent = extentRows.Count(value => value.ExtentOutcome == "OVEREXTENT"),
                    wrongParts = extentRows.Count(value => value.ExtentOutcome == "WRONG_PARTS"),
                    multipart = new
                    {
                        total = extentRows.Count(value => value.GoldAliases.Count > 1),
                        exact = extentRows.Count(value => value.GoldAliases.Count > 1 && value.ExtentOutcome == "EXACT"),
                    },
                    singleton = new
                    {
                        total = extentRows.Count(value => value.GoldAliases.Count == 1),
                        exact = extentRows.Count(value => value.GoldAliases.Count == 1 && value.ExtentOutcome == "EXACT"),
                    },
                    internalContinuationEdges = extentRows.Sum(value => value.ExpectedInternalContinuationEdges),
                    internalContinuationCorrect = extentRows.Sum(value => value.CorrectInternalContinuationEdges),
                    goldExitBoundaries = extentRows.Count(value => value.ExitEdgeIssued),
                    correctExitStops = extentRows.Count(value => value.ExitEdgeIssued && value.ExitStopCorrect),
                    overextensionFirstWrongBoundary = new
                    {
                        total = extentRows.Count(value => value.ExtentOutcome == "OVEREXTENT"),
                        rightG2AHas = extentRows.Count(value => value.ExtentOutcome == "OVEREXTENT" && value.G2ARightAtExit == "HAS_STRUCTURAL_EXTENT"),
                        rightG2ANo = extentRows.Count(value => value.ExtentOutcome == "OVEREXTENT" && value.G2ARightAtExit == "NO_STRUCTURAL_EXTENT"),
                        rightG2ANotIssued = extentRows.Count(value => value.ExtentOutcome == "OVEREXTENT" && value.G2ARightAtExit is null),
                    },
                },
                anchorScopedConflicts = new
                {
                    total = scopedConflicts.Length,
                    newStructuralUnit = scopedConflicts.Count(value => value.GoldClass == "NEW_STRUCTURAL_UNIT"),
                    continuesLeft = scopedConflicts.Count(value => value.GoldClass == "CONTINUES_LEFT_STRUCTURAL_UNIT"),
                    authorityGap = scopedConflicts.Count(value => value.GoldClass == "GOLD_AUTHORITY_GAP"),
                },
            };
        }).ToArray();

        FreezeArtifact.AssertJson(OutputRoot, "g2a-h2-canonical-gold-anchor-scoped-audit.v1.json", new
        {
            schemaVersion = "v5-p6th3-g2a-h2-canonical-gold-anchor-scoped-audit-v1",
            status = "FROZEN_PROVIDER_FREE_GOLD_AUDIT_RUNTIME_UNCHANGED",
            authorities = DocumentIds.Select(documentId => new
            {
                authorityId = documentId,
                canonicalGoldPath = FrozenHistoryGold.Entry(documentId).CanonicalGoldPath,
                goldSha256 = goldByDocument[documentId].GoldSha256,
                sourceSha256 = goldByDocument[documentId].SourceSha256,
                semanticHeadingTotal = FrozenHistoryGold.SemanticHeadingTotal(documentId),
                goldMutation = "NONE",
            }).ToArray(),
            g2a = new
            {
                issued = g2aRows.Count,
                tp = g2aRows.Count(value => value.Outcome == "TP"),
                fp = g2aRows.Count(value => value.Outcome == "FP"),
                fn = g2aRows.Count(value => value.Outcome == "FN"),
                tn = g2aRows.Count(value => value.Outcome == "TN"),
                correct = g2aRows.Count(value => value.Outcome is "TP" or "TN"),
                accuracy = (double)g2aRows.Count(value => value.Outcome is "TP" or "TN") / g2aRows.Count,
                precision = (double)g2aRows.Count(value => value.Outcome == "TP") / (g2aRows.Count(value => value.PredictedHas)),
                recall = (double)g2aRows.Count(value => value.Outcome == "TP") / (g2aRows.Count(value => value.GoldHas)),
                perDocument = perDocument.Select(value => value.g2a).ToArray(),
                decisions = g2aRows.Select(value => new { value.DocumentId, value.PackId, value.Occurrence, value.Alias, goldHasStructuralExtent = value.GoldHas, predictedHasStructuralExtent = value.PredictedHas, outcome = value.Outcome }).ToArray(),
            },
            h2 = new
            {
                calls = h2Calls.Count,
                callLedgerEdges = scopedEdges.Count,
                callLedgerContinue = h2Calls.Sum(value => value.Continues),
                callLedgerStop = h2Calls.Sum(value => value.Stops),
                g2aTrueAnchorsScored = trueAnchorRows.Count,
                falseG2AAnchorCallsExcluded = falseAnchorRows.Count,
                extentExact = trueAnchorRows.Count(value => value.ExtentOutcome == "EXACT"),
                extentUnder = trueAnchorRows.Count(value => value.ExtentOutcome == "UNDEREXTENT"),
                extentOver = trueAnchorRows.Count(value => value.ExtentOutcome == "OVEREXTENT"),
                extentWrongParts = trueAnchorRows.Count(value => value.ExtentOutcome == "WRONG_PARTS"),
                multipart = new
                {
                    total = trueAnchorRows.Count(value => value.GoldAliases.Count > 1),
                    exact = trueAnchorRows.Count(value => value.GoldAliases.Count > 1 && value.ExtentOutcome == "EXACT"),
                },
                singleton = new
                {
                    total = trueAnchorRows.Count(value => value.GoldAliases.Count == 1),
                    exact = trueAnchorRows.Count(value => value.GoldAliases.Count == 1 && value.ExtentOutcome == "EXACT"),
                },
                internalContinuationEdges = trueAnchorRows.Sum(value => value.ExpectedInternalContinuationEdges),
                internalContinuationCorrect = trueAnchorRows.Sum(value => value.CorrectInternalContinuationEdges),
                immediateGoldExitEdges = trueAnchorRows.Count(value => value.ExitEdgeIssued),
                immediateGoldExitStops = trueAnchorRows.Count(value => value.ExitEdgeIssued && value.ExitStopCorrect),
                overextensionFirstWrongBoundary = new
                {
                    total = overextensions.Length,
                    rightG2AHas = overextensions.Count(value => value.G2ARightAtExit == "HAS_STRUCTURAL_EXTENT"),
                    rightG2ANo = overextensions.Count(value => value.G2ARightAtExit == "NO_STRUCTURAL_EXTENT"),
                    rightG2ANotIssued = overextensions.Count(value => value.G2ARightAtExit is null),
                },
                rawCaptureHashes = h2Calls.Select(value => new
                {
                    value.DocumentId, value.PackId, value.Anchor,
                    rawCapturePath = value.RawPath, value.RawCaptureSha256,
                    value.RawResponseSha256, value.RawSseSha256,
                    value.ReasoningTokens, value.LatencyMs,
                    issuedEdges = value.IssuedEdges,
                }).ToArray(),
                trueAnchorExtents = trueAnchorRows.Select(value => new
                {
                    value.DocumentId, value.PackId, value.Anchor, value.AnchorAlias,
                    goldPartAliases = value.GoldAliases,
                    reconstructedPartAliases = value.PredictedAliases,
                    outcome = value.ExtentOutcome,
                    modelContinues = value.ModelContinueEdges,
                    modelStops = value.ModelStopEdges,
                    value.ExpectedInternalContinuationEdges,
                    value.CorrectInternalContinuationEdges,
                    value.ExitEdgeIssued,
                    value.ExitStopCorrect,
                    value.ExitRightAlias,
                    value.G2ARightAtExit,
                    value.FirstWrongBoundaryContinued,
                    value.FirstStopEdgeOrdinal,
                }).ToArray(),
                falseAnchorCalls = falseAnchorRows,
            },
            anchorScopedConflictCensus = new
            {
                joinKey = "(documentId, packId, anchor, leftOccurrence, rightOccurrence)",
                issuedH2EdgeObservations = scopedEdges.Count,
                pairableAnchorScopedEdges = scopedEdges.Count(value => value.G2ARight is not null),
                noG2ARight = scopedEdges.Count(value => value.G2ARight is null),
                conflicts = conflicts.Length,
                nonConflicts = scopedEdges.Count(value => value.G2ARight is not null && !value.Conflict),
                uniquePhysicalSourceEdges = conflicts.Select(PhysicalEdgeKey).Distinct(StringComparer.Ordinal).Count(),
                overlapVariationIsNotInconsistency = true,
                conflictRateScope = "conflicts / pairable anchor-scoped edge observations; never the collapsed physical-edge diagnostic and not a random corpus estimate",
                goldStratification = new
                {
                    newStructuralUnit = conflicts.Count(value => value.GoldClass == "NEW_STRUCTURAL_UNIT"),
                    continuesLeftStructuralUnit = conflicts.Count(value => value.GoldClass == "CONTINUES_LEFT_STRUCTURAL_UNIT"),
                    authorityGap = conflicts.Count(value => value.GoldClass == "GOLD_AUTHORITY_GAP"),
                },
                conflictRows = conflicts.Select(value => new
                {
                    value.DocumentId, value.PackId, value.Anchor,
                    value.Left, value.Right, value.LeftAlias, value.RightAlias,
                    h2Boundary = value.Boundary, g2aRight = value.G2ARight, goldClass = value.GoldClass,
                }).ToArray(),
            },
            perDocument,
            controls = new
            {
                providerCallsDuringAudit = 0,
                goldRead = true,
                goldMutation = "NONE",
                runtimeChanged = false,
                sharedRuntime = "UNCHANGED",
                previousCollapsedCensus = "IMMUTABLE_DIAGNOSTIC_ONLY; its (document,pack,left,right) collapse and overlap disagreement status are not a qualification denominator",
            },
            conclusion = "G2A_48_SCORED_AGAINST_CANONICAL_GOLD; H2_27_TRUE_ANCHORS_SCORE_6_EXACT_21_OVER; INTERNAL_CONTINUATIONS_8_OF_8; EXIT_STOP_6_OF_27; ANCHOR_SCOPED_CONFLICTS_19_WITH_15_NEW_UNIT_AND_4_CONTINUE; NO_GOLD_OR_RUNTIME_CHANGE",
        });
    }

    private static GoldDocument LoadGold(string authorityId)
    {
        var entry = FrozenHistoryGold.Entry(authorityId);
        FrozenHistoryGold.RequireCapability(authorityId, GoldCapability.Occurrence);
        using var doc = FrozenHistoryGold.Resolve(authorityId);
        var root = doc.RootElement;
        var sourceHash = root.GetProperty("source").GetProperty("sourceSha256").GetString()!;
        Assert.Equal(entry.SourceSha256, sourceHash);
        var claims = root.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            var role = claim.TryGetProperty("semanticRole", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
                ? roleValue.GetString()
                : null;
            var parts = claim.GetProperty("sourceParts").EnumerateArray().Select(part => new GoldPart(
                part.GetProperty("sourceAlias").GetString()!,
                part.TryGetProperty("selectionMode", out var selection) && selection.ValueKind == JsonValueKind.String ? selection.GetString()! : "UNKNOWN")).ToArray();
            Assert.NotEmpty(parts);
            return new GoldExtent(parts, role);
        }).ToArray();
        // Anchor/extent membership is scored against every approved heading occurrence. Preserve
        // semanticRole as metadata; do not silently reinterpret canonical Gold membership here.
        var duplicateStarts = claims.GroupBy(value => value.Aliases[0], StringComparer.Ordinal).Where(group => group.Count() > 1).ToArray();
        Assert.Empty(duplicateStarts);
        return new GoldDocument(authorityId, entry.GoldSha256, sourceHash, claims,
            claims.Select(value => value.Aliases[0]).ToHashSet(StringComparer.Ordinal));
    }

    private static Dictionary<string, string> ParseG2A(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(1, root.EnumerateObject().Count());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("decisions").EnumerateArray())
        {
            Assert.Equal(2, row.EnumerateObject().Count());
            var id = row.GetProperty("primary").GetString()!;
            var value = row.GetProperty("anchor").GetString()!;
            Assert.Contains(value, new[] { "HAS_STRUCTURAL_EXTENT", "NO_STRUCTURAL_EXTENT" });
            Assert.True(result.TryAdd(id, value));
        }
        return result;
    }

    private static IReadOnlyList<H2Decision> ParseH2(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(1, root.EnumerateObject().Count());
        return root.GetProperty("decisions").EnumerateArray().Select(row =>
        {
            Assert.Equal(4, row.EnumerateObject().Count());
            var boundary = row.GetProperty("boundary").GetString()!;
            Assert.Contains(boundary, new[] { "CONTINUES_STRUCTURAL_UNIT", "STOPS_STRUCTURAL_UNIT" });
            return new H2Decision(row.GetProperty("anchor").GetString()!, row.GetProperty("left").GetString()!, row.GetProperty("right").GetString()!, boundary);
        }).ToArray();
    }

    private static string GoldConflictClass(GoldDocument gold, string leftAlias, string rightAlias)
    {
        var starts = gold.GoldStarts.Contains(rightAlias);
        var continues = gold.GoldExtents.Any(value => value.Aliases.Zip(value.Aliases.Skip(1), (left, right) => (left, right))
            .Any(pair => pair.left == leftAlias && pair.right == rightAlias));
        if (starts && !continues) return "NEW_STRUCTURAL_UNIT";
        if (continues && !starts) return "CONTINUES_LEFT_STRUCTURAL_UNIT";
        return starts && continues ? "GOLD_OVERLAPPING_UNIT_AUTHORITY" : "GOLD_AUTHORITY_GAP";
    }

    private static string ClassifyExtent(IReadOnlyList<string> predicted, IReadOnlyList<string> gold)
    {
        if (predicted.SequenceEqual(gold, StringComparer.Ordinal)) return "EXACT";
        if (predicted.Count < gold.Count && predicted.SequenceEqual(gold.Take(predicted.Count), StringComparer.Ordinal)) return "UNDEREXTENT";
        if (predicted.Count > gold.Count && gold.SequenceEqual(predicted.Take(gold.Count), StringComparer.Ordinal)) return "OVEREXTENT";
        return "WRONG_PARTS";
    }

    private static string CallKey(JsonElement value) =>
        $"{value.GetProperty("documentId").GetString()}|{value.GetProperty("packId").GetString()}|{value.GetProperty("anchor").GetString()}";

    private static string PhysicalEdgeKey(ScopedEdge value) => $"{value.DocumentId}|{value.PackId}|{value.Left}|{value.Right}";
    private static string Relative(string path) => Path.GetRelativePath(TestRepository.Root(), path).Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record GoldPart(string Alias, string SelectionMode);
    private sealed record GoldExtent(IReadOnlyList<GoldPart> Parts, string? SemanticRole)
    {
        public IReadOnlyList<string> Aliases => Parts.Select(value => value.Alias).ToArray();
    }
    private sealed record GoldDocument(string AuthorityId, string GoldSha256, string SourceSha256,
        IReadOnlyList<GoldExtent> GoldExtents, IReadOnlySet<string> GoldStarts);
    private sealed record G2ADocument(string PackId, IReadOnlyDictionary<string, string> IssuedAliases,
        IReadOnlyDictionary<string, string> Decisions, string RawPath, string RawCaptureSha256,
        string RawResponseSha256, string RawSseSha256);
    private sealed record G2ARow(string DocumentId, string PackId, string Occurrence, string Alias,
        bool GoldHas, bool PredictedHas, string Outcome);
    private sealed record H2Decision(string Anchor, string Left, string Right, string Boundary);
    private sealed record ScopedEdge(string DocumentId, string PackId, string Anchor, string Left, string Right,
        string LeftAlias, string RightAlias, string Boundary, string? G2ARight, bool Conflict, string? GoldClass);
    private sealed record H2Call(string DocumentId, string PackId, string Anchor, string AnchorAlias,
        int IssuedEdges, int Continues, int Stops, string RawPath, string RawCaptureSha256,
        string RawResponseSha256, string RawSseSha256, int ReasoningTokens, double LatencyMs,
        IReadOnlyList<ScopedEdge> Edges);
    private sealed record ExtentRow(string DocumentId, string PackId, string Anchor, string AnchorAlias,
        IReadOnlyList<string> GoldAliases, IReadOnlyList<string> PredictedAliases, string ExtentOutcome,
        int ModelContinueEdges, int ModelStopEdges, int ExpectedInternalContinuationEdges,
        int CorrectInternalContinuationEdges, bool ExitEdgeIssued, bool ExitStopCorrect,
        string? ExitRightAlias, string? G2ARightAtExit, bool FirstWrongBoundaryContinued,
        int FirstStopEdgeOrdinal);
}
