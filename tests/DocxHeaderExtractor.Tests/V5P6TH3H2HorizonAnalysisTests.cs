using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free descriptive analysis of request horizon, Gold exit position, and H2 continuation
/// run length for the frozen full-tail population. This is diagnostic, not a causal estimate.
/// </summary>
public sealed class V5P6TH3H2HorizonAnalysisTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string AuditPath = Root + "/p6th3-full-population-gold-audit/g2a-h2-canonical-gold-anchor-scoped-audit.v1.json";
    private const string ManifestPath = Root + "/p6th3-full-population-h2-preflight/g2a-raw-audit-and-h2-full-request-manifest.v1.json";
    private const string FullCaptureRoot = Root + "/p6th3-full-population-h2-canary-20261005";
    private const string BoundedPreflightPath = Root + "/p6th2-function-conditioned-continuation-preflight/continuation-boundary-preflight.v1.json";
    private const string BoundedScorePath = Root + "/p6th2-function-conditioned-continuation-gold-audit/continuation-boundary-gold-audit.v1.json";
    private const string BoundedExecutionPath = Root + "/p6th2-function-conditioned-continuation-canary-20261004/result.v1.json";
    private const string BoundedRawPath = Root + "/p6th2-function-conditioned-continuation-canary-20261004/SRC-089.raw-capture.v1.json";
    private const string OutputRoot = Root + "/p6th3-full-population-h2-horizon-audit";

    [Fact]
    public void Frozen_full_tail_H2_is_analyzed_against_request_horizon_and_bounded_canary()
    {
        using var auditDoc = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(AuditPath)));
        using var manifestDoc = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(ManifestPath)));
        var audit = auditDoc.RootElement;
        var manifest = manifestDoc.RootElement;
        Assert.Equal("FROZEN_PROVIDER_FREE_GOLD_AUDIT_RUNTIME_UNCHANGED", audit.GetProperty("status").GetString());
        Assert.Equal("H2_FULL_REQUEST_UNIVERSE_FROZEN_NOT_AUTHORIZED", manifest.GetProperty("status").GetString());
        Assert.Equal(31, manifest.GetProperty("requestUniverse").GetArrayLength());

        var requests = manifest.GetProperty("requestUniverse").EnumerateArray()
            .ToDictionary(RequestKey, StringComparer.Ordinal);
        var goldStarts = audit.GetProperty("g2a").GetProperty("decisions").EnumerateArray().ToArray();
        var rows = new List<Row>();
        foreach (var extent in audit.GetProperty("h2").GetProperty("trueAnchorExtents").EnumerateArray())
        {
            var documentId = extent.GetProperty("DocumentId").GetString()!;
            var packId = extent.GetProperty("PackId").GetString()!;
            var anchor = extent.GetProperty("Anchor").GetString()!;
            Assert.True(requests.TryGetValue(Key(documentId, packId, anchor), out var request), $"Missing frozen request:{documentId}/{anchor}");
            var capturePath = TestRepository.Path($"{FullCaptureRoot}/{documentId}_{anchor}.raw-capture.v1.json");
            using var captureDoc = JsonDocument.Parse(File.ReadAllBytes(capturePath));
            var capture = captureDoc.RootElement;
            Assert.Equal(request.GetProperty("providerBodySha256").GetString(), capture.GetProperty("providerBodySha256").GetString());
            Assert.Equal(request.GetProperty("providerBodyBytes").GetInt32(), capture.GetProperty("providerBodyBytes").GetInt32());
            Assert.Equal("stop", capture.GetProperty("finishReason").GetString());

            var goldAliases = extent.GetProperty("goldPartAliases").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var exit = request.GetProperty("edges").EnumerateArray()
                .SingleOrDefault(edge => edge.GetProperty("leftAlias").GetString() == goldAliases[^1]);
            Assert.NotEqual(default(JsonElement), exit);
            var exitOrdinal = exit.GetProperty("ordinal").GetInt32();
            var edgeCount = request.GetProperty("edgeCount").GetInt32();
            var anchorOrdinal = ParseOccurrenceOrdinal(anchor);
            var futureRequests = requests.Values.Count(value =>
                value.GetProperty("documentId").GetString() == documentId &&
                ParseOccurrenceOrdinal(value.GetProperty("anchor").GetString()!) > anchorOrdinal);
            var futureG2AHas = goldStarts.Count(value =>
                value.GetProperty("DocumentId").GetString() == documentId &&
                value.GetProperty("predictedHasStructuralExtent").GetBoolean() &&
                ParseOccurrenceOrdinal(value.GetProperty("Occurrence").GetString()!) > anchorOrdinal);

            var goldInternal = goldAliases.Length - 1;
            var continues = extent.GetProperty("modelContinues").GetInt32();
            var outcome = extent.GetProperty("outcome").GetString()!;
            rows.Add(new Row(
                documentId,
                packId,
                anchor,
                goldAliases.Length > 1 ? "MULTIPART" : "SINGLETON",
                goldAliases.Length,
                request.GetProperty("userMessageUtf8Bytes").GetInt32(),
                capture.GetProperty("providerBodyBytes").GetInt32(),
                capture.GetProperty("promptTokens").GetInt32(),
                edgeCount,
                exitOrdinal,
                (double)(exitOrdinal + 1) / edgeCount,
                edgeCount - exitOrdinal - 1,
                futureRequests,
                futureG2AHas,
                continues,
                goldInternal,
                Math.Max(0, continues - goldInternal),
                extent.GetProperty("ExitStopCorrect").GetBoolean(),
                outcome));
        }

        Assert.Equal(27, rows.Count);
        Assert.Equal(6, rows.Count(value => value.Outcome == "EXACT"));
        Assert.Equal(21, rows.Count(value => value.Outcome == "OVEREXTENT"));
        Assert.Equal(20, rows.Count(value => value.GoldParts == 1));
        Assert.Equal(2, rows.Count(value => value.GoldParts == 1 && value.ExitOrdinal == 0 && value.EdgeCount <= 4 && value.Outcome == "OVEREXTENT"));
        Assert.Contains(rows, value => value.DocumentId == "DOC-0252" && value.Anchor == "O95" && value.EdgeCount == 1 && value.Outcome == "OVEREXTENT");
        Assert.Equal(6, rows.Count(value => value.ExitStopCorrect));

        var boundedPreflight = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(BoundedPreflightPath))).RootElement;
        var boundedCall = boundedPreflight.GetProperty("callPlan");
        var boundedScore = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(BoundedScorePath))).RootElement;
        var boundedExecution = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(BoundedExecutionPath))).RootElement;
        var boundedRaw = JsonDocument.Parse(File.ReadAllBytes(TestRepository.Path(BoundedRawPath))).RootElement;
        Assert.Equal(18, boundedScore.GetProperty("edgeScore").GetProperty("total").GetInt32());
        Assert.Equal(17, boundedScore.GetProperty("edgeScore").GetProperty("correct").GetInt32());
        Assert.Equal(5, boundedScore.GetProperty("reconstructedExtentScore").GetProperty("exact").GetInt32());

        var multipart = rows.Where(value => value.GoldParts > 1).ToArray();
        var singleton = rows.Where(value => value.GoldParts == 1).ToArray();
        var exactRows = rows.Where(value => value.Outcome == "EXACT").ToArray();
        var overRows = rows.Where(value => value.Outcome == "OVEREXTENT").ToArray();
        var boundedExtents = boundedScore.GetProperty("reconstructedExtentScore").GetProperty("rows").EnumerateArray()
            .ToDictionary(value => value.GetProperty("anchor").GetString()!, StringComparer.Ordinal);
        var boundedEdges = boundedScore.GetProperty("edgeScore").GetProperty("rows").EnumerateArray()
            .GroupBy(value => value.GetProperty("anchor").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var sameSrc089Anchors = rows.Where(value => value.DocumentId == "SRC-089")
            .OrderBy(value => ParseOccurrenceOrdinal(value.Anchor)).Select(value =>
            {
                var boundedRun = boundedEdges[value.Anchor]
                    .TakeWhile(edge => edge.GetProperty("observed").GetString() == "CONTINUES_STRUCTURAL_UNIT").Count();
                var boundedExtent = boundedExtents[value.Anchor];
                var expectedInternal = value.GoldParts - 1;
                return new
                {
                    value.Anchor,
                    goldParts = value.GoldParts,
                    boundedOutcome = boundedExtent.GetProperty("classification").GetString(),
                    boundedContinueRun = boundedRun,
                    boundedExcess = Math.Max(0, boundedRun - expectedInternal),
                    fullTailOutcome = value.Outcome,
                    fullTailContinueRun = value.ContinueRun,
                    fullTailExcess = value.Overrun,
                };
            }).ToArray();
        Assert.Equal(6, sameSrc089Anchors.Length);
        var o17Pair = Assert.Single(sameSrc089Anchors, value => value.Anchor == "O17");
        Assert.Equal(1, o17Pair.boundedExcess);
        Assert.Equal(8, o17Pair.fullTailExcess);
        var o19Pair = Assert.Single(sameSrc089Anchors, value => value.Anchor == "O19");
        Assert.Equal("EXACT", o19Pair.boundedOutcome);
        Assert.Equal("OVEREXTENT", o19Pair.fullTailOutcome);
        Assert.Equal(4, o19Pair.fullTailExcess);
        var edgeBuckets = new[]
        {
            Bucket("1-16", rows.Where(value => value.EdgeCount <= 16)),
            Bucket("17-32", rows.Where(value => value.EdgeCount is >= 17 and <= 32)),
            Bucket("33-64", rows.Where(value => value.EdgeCount is >= 33 and <= 64)),
            Bucket(">64", rows.Where(value => value.EdgeCount > 64)),
        };

        FreezeArtifact.AssertJson(OutputRoot, "h2-full-tail-horizon-diagnostic.v1.json", new
        {
            schemaVersion = "v5-p6th3-h2-full-tail-horizon-diagnostic-v1",
            status = "FROZEN_PROVIDER_FREE_DESCRIPTIVE_DIAGNOSTIC_NOT_CAUSAL",
            authorities = new
            {
                goldAudit = Relative(TestRepository.Path(AuditPath)),
                requestManifest = Relative(TestRepository.Path(ManifestPath)),
                fullCaptureRoot = FullCaptureRoot,
                boundedPreflight = Relative(TestRepository.Path(BoundedPreflightPath)),
                boundedAudit = Relative(TestRepository.Path(BoundedScorePath)),
                providerCallsDuringAudit = 0,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            fullTailPopulation = new
            {
                trueGoldAnchors = rows.Count,
                exact = exactRows.Length,
                overextent = overRows.Length,
                underextent = rows.Count(value => value.Outcome == "UNDEREXTENT"),
                wrongParts = rows.Count(value => value.Outcome == "WRONG_PARTS"),
                request = new
                {
                    edgeCount = Range(rows.Select(value => value.EdgeCount)),
                    userMessageUtf8Bytes = Range(rows.Select(value => value.UserBytes)),
                    providerBodyBytes = Range(rows.Select(value => value.BodyBytes)),
                    promptTokens = Range(rows.Select(value => value.PromptTokens)),
                },
                goldExit = new
                {
                    edgeOrdinalZeroBased = Distribution(rows.Select(value => value.ExitOrdinal)),
                    relativePosition = Summary(rows.Select(value => value.ExitFraction)),
                    edgesAfterGoldExit = Range(rows.Select(value => value.EdgesAfterExit)),
                    trueAnchorDistanceInContinuationEdges = Distribution(rows.Select(value => value.GoldParts - 1)),
                },
                downstream = new
                {
                    laterH2RequestAnchors = Summary(rows.Select(value => (double)value.DownstreamH2Roots)),
                    laterG2AHasDecisions = Summary(rows.Select(value => (double)value.DownstreamG2AHas)),
                },
                modelRun = new
                {
                    initialContinueRun = Summary(rows.Select(value => (double)value.ContinueRun)),
                    continuationExcessBeyondGoldExit = Summary(rows.Select(value => (double)value.Overrun)),
                    internalGoldContinuationCorrect = 8,
                    internalGoldContinuationTotal = 8,
                    immediateGoldExitStopCorrect = 6,
                    immediateGoldExitTotal = 27,
                },
                byExtentShape = new
                {
                    multipart = ShapeSummary(multipart),
                    singleton = ShapeSummary(singleton),
                },
                exactRateByIssuedEdges = edgeBuckets,
                requestAssociation = new
                {
                    pearsonRWithOverextentIndicator = new
                    {
                        edgeCount = Correlation(rows, value => value.EdgeCount, value => value.Outcome == "OVEREXTENT" ? 1 : 0),
                        providerBodyBytes = Correlation(rows, value => value.BodyBytes, value => value.Outcome == "OVEREXTENT" ? 1 : 0),
                        promptTokens = Correlation(rows, value => value.PromptTokens, value => value.Outcome == "OVEREXTENT" ? 1 : 0),
                        downstreamH2RequestAnchors = Correlation(rows, value => value.DownstreamH2Roots, value => value.Outcome == "OVEREXTENT" ? 1 : 0),
                        tailEdgesAfterGoldExit = Correlation(rows, value => value.EdgesAfterExit, value => value.Outcome == "OVEREXTENT" ? 1 : 0),
                    },
                    pearsonRWithExcessContinueRun = new
                    {
                        edgeCount = Correlation(rows, value => value.EdgeCount, value => value.Overrun),
                        providerBodyBytes = Correlation(rows, value => value.BodyBytes, value => value.Overrun),
                        promptTokens = Correlation(rows, value => value.PromptTokens, value => value.Overrun),
                        downstreamH2RequestAnchors = Correlation(rows, value => value.DownstreamH2Roots, value => value.Overrun),
                        tailEdgesAfterGoldExit = Correlation(rows, value => value.EdgesAfterExit, value => value.Overrun),
                    },
                    interpretationLimit = "n=27 descriptive only; document/shape clustering, confounding, and the small cohort preclude causal inference or significance claims.",
                },
                cases = rows,
            },
            boundedCanaryComparison = new
            {
                sameDocumentAnchors = 6,
                requestRoots = 6,
                issuedEdges = boundedScore.GetProperty("edgeScore").GetProperty("total").GetInt32(),
                correctEdges = boundedScore.GetProperty("edgeScore").GetProperty("correct").GetInt32(),
                exactExtents = boundedScore.GetProperty("reconstructedExtentScore").GetProperty("exact").GetInt32(),
                providerBodyBytes = boundedCall.GetProperty("providerBodyBytes").GetInt32(),
                userMessageUtf8Bytes = boundedCall.GetProperty("userMessageUtf8Bytes").GetInt32(),
                promptTokens = boundedExecution.GetProperty("row").GetProperty("promptTokens").GetInt32(),
                fullTailSrc089SameAnchors = new
                {
                    count = rows.Count(value => value.DocumentId == "SRC-089"),
                    exact = rows.Count(value => value.DocumentId == "SRC-089" && value.Outcome == "EXACT"),
                    pairedByAnchor = sameSrc089Anchors,
                },
                systemPromptHashDiffers = boundedCall.GetProperty("systemPromptSha256").GetString() != requests.Values.First().GetProperty("systemPromptSha256").GetString(),
                requestShapeDiffers = true,
                causalInterpretation = "Bounded canary is suggestive only, not a matched causal comparison: it batches six roots and 18 edges with a different request envelope/prompt hash; full-tail uses one call per root and requests the entire remaining owned tail.",
                boundedRawResponseSha256 = boundedRaw.GetProperty("rawResponseSha256").GetString(),
            },
            findings = new
            {
                longHorizonAssociation = "WEAK_OR_ABSENT_FOR_BINARY_OVEREXTENT_IN_THIS_N27_COHORT",
                longHorizonSeverityAssociation = "MODEST_DESCRIPTIVE_ASSOCIATION_ONLY_AND_PARTLY_EXPOSURE_BOUNDED",
                tailLengthAloneIsNecessary = false,
                evidence = "DOC-0252/O95 overextends at a one-edge, 2,090-byte request; two short singleton tails of 1-4 edges both overextend. Conversely, several 42-87-edge requests stop exactly at Gold exits.",
                likelyNextQuestion = "A local/bounded continuation treatment is worth a matched experiment, but freeze identical prompt/model/context and change only issued decision horizon/output cardinality before attributing any improvement to locality.",
            },
        });
    }

    private static object Bucket(string label, IEnumerable<Row> source)
    {
        var rows = source.ToArray();
        return new { edgeCount = label, n = rows.Length, exact = rows.Count(value => value.Outcome == "EXACT"), overextent = rows.Count(value => value.Outcome == "OVEREXTENT") };
    }

    private static object ShapeSummary(IReadOnlyCollection<Row> rows) => new
    {
        total = rows.Count,
        exact = rows.Count(value => value.Outcome == "EXACT"),
        overextent = rows.Count(value => value.Outcome == "OVEREXTENT"),
        meanEdges = Mean(rows.Select(value => value.EdgeCount)),
        meanProviderBodyBytes = Mean(rows.Select(value => value.BodyBytes)),
        meanPromptTokens = Mean(rows.Select(value => value.PromptTokens)),
        meanContinueRun = Mean(rows.Select(value => value.ContinueRun)),
        meanOverrun = Mean(rows.Select(value => value.Overrun)),
    };

    private static object Range(IEnumerable<int> source)
    {
        var values = source.Order().ToArray();
        return new { min = values[0], median = Median(values.Select(value => (double)value)), max = values[^1] };
    }

    private static object Distribution(IEnumerable<int> source) => source.GroupBy(value => value).OrderBy(group => group.Key)
        .Select(group => new { value = group.Key, count = group.Count() }).ToArray();

    private static object Summary(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return new { min = values[0], mean = Math.Round(values.Average(), 3), median = Math.Round(Median(values), 3), max = values[^1] };
    }

    private static double Mean(IEnumerable<int> source) => Math.Round(source.Average(), 3);

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2.0;
    }

    private static double Correlation(IReadOnlyCollection<Row> rows, Func<Row, int> xSelector, Func<Row, int> ySelector)
    {
        var x = rows.Select(value => (double)xSelector(value)).ToArray();
        var y = rows.Select(value => (double)ySelector(value)).ToArray();
        var xMean = x.Average();
        var yMean = y.Average();
        var covariance = x.Zip(y, (left, right) => (left - xMean) * (right - yMean)).Sum();
        var xVariance = x.Sum(value => (value - xMean) * (value - xMean));
        var yVariance = y.Sum(value => (value - yMean) * (value - yMean));
        return xVariance == 0 || yVariance == 0 ? 0 : Math.Round(covariance / Math.Sqrt(xVariance * yVariance), 3);
    }

    private static string RequestKey(JsonElement value) => Key(
        value.GetProperty("documentId").GetString()!,
        value.GetProperty("packId").GetString()!,
        value.GetProperty("anchor").GetString()!);

    private static string Key(string documentId, string packId, string anchor) => $"{documentId}|{packId}|{anchor}";

    private static int ParseOccurrenceOrdinal(string value) => int.Parse(value.AsSpan(1));

    private static string Relative(string fullPath) => Path.GetRelativePath(TestRepository.Root(), fullPath).Replace('\\', '/');

    private sealed record Row(
        string DocumentId,
        string PackId,
        string Anchor,
        string GoldExtentShape,
        int GoldParts,
        int UserBytes,
        int BodyBytes,
        int PromptTokens,
        int EdgeCount,
        int ExitOrdinal,
        double ExitFraction,
        int EdgesAfterExit,
        int DownstreamH2Roots,
        int DownstreamG2AHas,
        int ContinueRun,
        int GoldInternalContinuations,
        int Overrun,
        bool ExitStopCorrect,
        string Outcome);
}
