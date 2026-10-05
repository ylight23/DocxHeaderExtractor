using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Raw-only census for the frozen five-pack G2A and 31-call H2 population. Gold is deliberately
/// not read here; overlapping anchor-scoped H2 judgements are surfaced instead of tie-broken.
/// </summary>
public sealed class V5P6TH3FullPopulationRawOnlyCensusTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string G2ARoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string H2Root = Root + "/p6th3-full-population-h2-canary-20261005";
    private const string H2ManifestPath = Root + "/p6th3-full-population-h2-preflight/g2a-raw-audit-and-h2-full-request-manifest.v1.json";
    private const string OutputRoot = Root + "/p6th3-full-population-raw-only-census";

    private static readonly Source[] Sources =
    [
        new("SRC-089", "p6tf1-preflight/retry-src089-result.v1.json", F1Kind.ResultRow),
        new("SRC-041", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture),
        new("SRC-095", "p6tf1-preflight/result.v1.json", F1Kind.ResultRows),
        new("DOC-0252", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture),
        new("DOC-0256", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture),
    ];

    [Fact]
    public void Full_G2A_and_H2_raw_ledgers_join_without_Gold_and_preserve_anchor_overlap_disagreements()
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(H2ManifestPath)));
        var manifest = manifestDoc.RootElement;
        Assert.Equal("H2_FULL_REQUEST_UNIVERSE_FROZEN_NOT_AUTHORIZED", manifest.GetProperty("status").GetString());
        Assert.Equal(31, manifest.GetProperty("requestUniverse").GetArrayLength());
        Assert.Equal(0, manifest.GetProperty("execution").GetProperty("providerCalls").GetInt32());
        Assert.False(manifest.GetProperty("execution").GetProperty("goldRead").GetBoolean());

        using var h2ResultDoc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{H2Root}/result.v1.json")));
        var h2Result = h2ResultDoc.RootElement;
        Assert.Equal("ALL_FROZEN_H2_LEDGERS_ACCEPTED", h2Result.GetProperty("status").GetString());
        Assert.Equal(31, h2Result.GetProperty("providerCalls").GetInt32());
        Assert.Equal(31, h2Result.GetProperty("acceptedLedgers").GetInt32());
        Assert.Equal(0, h2Result.GetProperty("retry").GetInt32());
        Assert.False(h2Result.GetProperty("repair").GetBoolean());
        Assert.False(h2Result.GetProperty("fallback").GetBoolean());
        Assert.False(h2Result.GetProperty("goldRead").GetBoolean());
        Assert.False(h2Result.GetProperty("runtimeChanged").GetBoolean());

        var requestRows = manifest.GetProperty("requestUniverse").EnumerateArray().ToArray();
        var resultRows = h2Result.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(31, resultRows.Length);
        var requestByKey = requestRows.ToDictionary(RequestKey, StringComparer.Ordinal);
        var responseEdges = new Dictionary<string, List<H2Decision>>(StringComparer.Ordinal);
        var h2RawAudit = new List<object>();

        foreach (var resultRow in resultRows)
        {
            Assert.Equal("stop", resultRow.GetProperty("finishReason").GetString());
            Assert.Equal(0, resultRow.GetProperty("retryCount").GetInt32());
            Assert.True(resultRow.GetProperty("ledgerAccepted").GetBoolean());
            var docId = resultRow.GetProperty("documentId").GetString()!;
            var packId = resultRow.GetProperty("packId").GetString()!;
            var anchor = resultRow.GetProperty("anchor").GetString()!;
            var callKey = $"{docId}|{packId}|{anchor}";
            Assert.True(requestByKey.TryGetValue(callKey, out var request), $"H2 request absent from frozen manifest:{callKey}");
            Assert.Equal(request!.GetProperty("providerBodySha256").GetString(), resultRow.GetProperty("providerBodySha256").GetString());
            Assert.Equal(request.GetProperty("edgeCount").GetInt32(), resultRow.GetProperty("issued").GetInt32());

            var rawPath = TestRepository.Path($"{H2Root}/{docId}_{anchor}.raw-capture.v1.json");
            var rawBytes = File.ReadAllBytes(rawPath);
            using var rawDoc = JsonDocument.Parse(rawBytes);
            var raw = rawDoc.RootElement;
            Assert.Equal("v5-p6th3-full-population-h2-raw-capture-v1", raw.GetProperty("schemaVersion").GetString());
            Assert.Equal(docId, raw.GetProperty("documentId").GetString());
            Assert.Equal(packId, raw.GetProperty("packId").GetString());
            Assert.Equal(anchor, raw.GetProperty("anchor").GetString());
            Assert.Equal(1, raw.GetProperty("providerCalls").GetInt32());
            Assert.Equal(resultRow.GetProperty("providerCallOrdinal").GetInt32(), raw.GetProperty("providerCallOrdinal").GetInt32());
            Assert.Equal(request.GetProperty("providerBodySha256").GetString(), raw.GetProperty("providerBodySha256").GetString());
            Assert.Equal(request.GetProperty("userMessageSha256").GetString(), raw.GetProperty("userMessageSha256").GetString());
            Assert.Equal(request.GetProperty("systemPromptSha256").GetString(), raw.GetProperty("systemPromptSha256").GetString());
            Assert.Equal(request.GetProperty("edgeCount").GetInt32(), raw.GetProperty("issuedEdgeCount").GetInt32());
            Assert.Equal("stop", raw.GetProperty("finishReason").GetString());
            Assert.Equal(0, raw.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(raw.GetProperty("rawResponse").GetString()!), raw.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(Hash(raw.GetProperty("rawSse").GetString()!), raw.GetProperty("rawSseSha256").GetString());
            Assert.True(raw.GetProperty("reasoningExecutionConfirmed").GetBoolean(), $"reasoning execution not confirmed:{callKey}");
            Assert.True(raw.GetProperty("reasoningTokens").GetInt32() > 0, $"reasoning token usage absent:{callKey}");
            var g2aRawPath = TestRepository.Path($"{G2ARoot}/{docId}.raw-capture.v1.json");
            Assert.Equal(Hash(File.ReadAllBytes(g2aRawPath)), raw.GetProperty("g2aRawCaptureSha256").GetString());

            using var responseDoc = JsonDocument.Parse(raw.GetProperty("rawResponse").GetString()!);
            var root = responseDoc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal(1, root.EnumerateObject().Count());
            var decisions = root.GetProperty("decisions").EnumerateArray().ToArray();
            Assert.Equal(request.GetProperty("edgeCount").GetInt32(), decisions.Length);
            var expectedEdges = request.GetProperty("edges").EnumerateArray()
                .ToDictionary(edge => $"{edge.GetProperty("left").GetString()}|{edge.GetProperty("right").GetString()}", StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var decision in decisions)
            {
                Assert.Equal(4, decision.EnumerateObject().Count());
                var edgeAnchor = decision.GetProperty("anchor").GetString()!;
                var left = decision.GetProperty("left").GetString()!;
                var right = decision.GetProperty("right").GetString()!;
                var boundary = decision.GetProperty("boundary").GetString()!;
                var edgeKey = $"{left}|{right}";
                Assert.Equal(anchor, edgeAnchor);
                Assert.Contains(boundary, new[] { "CONTINUES_STRUCTURAL_UNIT", "STOPS_STRUCTURAL_UNIT" });
                Assert.True(expectedEdges.ContainsKey(edgeKey), $"unissued H2 edge:{callKey}:{edgeKey}");
                Assert.True(seen.Add(edgeKey), $"duplicate H2 edge inside one response:{callKey}:{edgeKey}");
                var censusKey = $"{docId}|{packId}|{left}|{right}";
                if (!responseEdges.TryGetValue(censusKey, out var list)) responseEdges[censusKey] = list = [];
                list.Add(new H2Decision(anchor, boundary, raw.GetProperty("rawResponseSha256").GetString()!));
            }
            Assert.Equal(expectedEdges.Count, seen.Count);
            h2RawAudit.Add(new
            {
                documentId = docId, packId, anchor,
                requestSha256 = request.GetProperty("providerBodySha256").GetString(),
                rawCapturePath = Relative(rawPath), rawCaptureSha256 = Hash(rawBytes),
                rawResponseSha256 = raw.GetProperty("rawResponseSha256").GetString(),
                rawSseSha256 = raw.GetProperty("rawSseSha256").GetString(),
                finishReason = "stop", retryCount = 0,
                issued = decisions.Length,
                continues = decisions.Count(value => value.GetProperty("boundary").GetString() == "CONTINUES_STRUCTURAL_UNIT"),
                stops = decisions.Count(value => value.GetProperty("boundary").GetString() == "STOPS_STRUCTURAL_UNIT"),
                reasoningTokens = raw.GetProperty("reasoningTokens").GetInt32(),
                reasoningExecutionConfirmed = raw.GetProperty("reasoningExecutionConfirmed").GetBoolean(),
                latencyMs = raw.GetProperty("latencyMs").GetDouble(),
            });
        }

        var edgeRows = new List<EdgeRow>();
        var docRows = new List<object>();
        foreach (var source in Sources)
        {
            var f1Path = TestRepository.Path($"{Root}/{source.F1Path}");
            using var f1Doc = JsonDocument.Parse(File.ReadAllText(f1Path));
            var f1Row = source.Kind switch
            {
                F1Kind.RawCapture => f1Doc.RootElement,
                F1Kind.ResultRow => f1Doc.RootElement.GetProperty("row"),
                F1Kind.ResultRows => f1Doc.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
                _ => throw new InvalidOperationException("unknown-F1-kind"),
            };
            using var f1Response = JsonDocument.Parse(f1Row.GetProperty("rawResponse").GetString()!);
            var f1Occurrences = f1Response.RootElement.GetProperty("decisions").EnumerateArray()
                .Select(value => value.GetProperty("occurrence").GetString()!).ToArray();
            Assert.Equal(96, f1Occurrences.Length);
            Assert.Equal(96, f1Occurrences.Distinct(StringComparer.Ordinal).Count());
            var packId = f1Row.GetProperty("packId").GetString()!;
            var g2aPath = TestRepository.Path($"{G2ARoot}/{source.DocumentId}.raw-capture.v1.json");
            var g2aBytes = File.ReadAllBytes(g2aPath);
            using var g2aDoc = JsonDocument.Parse(g2aBytes);
            var g2a = g2aDoc.RootElement;
            Assert.Equal("stop", g2a.GetProperty("finishReason").GetString());
            Assert.Equal(0, g2a.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(g2a.GetProperty("rawResponse").GetString()!), g2a.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(Hash(g2a.GetProperty("rawSse").GetString()!), g2a.GetProperty("rawSseSha256").GetString());
            var g2aDecisionRows = ParseG2A(g2a.GetProperty("rawResponse").GetString()!);
            Assert.Equal(g2a.GetProperty("issuedPrimaryCount").GetInt32(), g2aDecisionRows.Count);
            Assert.Equal(g2aDecisionRows.Count, g2aDecisionRows.Keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(g2aDecisionRows.Keys, value => Assert.Contains(value, f1Occurrences));

            var adjacent = f1Occurrences.Zip(f1Occurrences.Skip(1), (left, right) => (left, right)).ToArray();
            var h2DecidedEdges = responseEdges.Where(pair => pair.Key.StartsWith($"{source.DocumentId}|{packId}|", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key[(source.DocumentId.Length + packId.Length + 2)..], pair => pair.Value, StringComparer.Ordinal);
            var docEdgeRows = new List<EdgeRow>();
            for (var index = 0; index < adjacent.Length; index++)
            {
                var (left, right) = adjacent[index];
                var key = $"{left}|{right}";
                var hasG2A = g2aDecisionRows.TryGetValue(right, out var anchorResult);
                var hasH2 = h2DecidedEdges.TryGetValue(key, out var overlapping);
                var boundaries = overlapping?.Select(value => value.Boundary).Distinct(StringComparer.Ordinal).ToArray() ?? [];
                var status = !hasG2A && !hasH2 ? "UNPAIRED_NO_G2A_AND_NO_H2"
                    : !hasG2A ? "UNPAIRED_NO_G2A_RIGHT"
                    : !hasH2 ? "UNPAIRED_NO_H2"
                    : boundaries.Length > 1 ? "H2_OVERLAP_DISAGREEMENT"
                    : anchorResult == "HAS_STRUCTURAL_EXTENT" && boundaries[0] == "CONTINUES_STRUCTURAL_UNIT" ? "CONFLICT"
                    : "NON_CONFLICT";
                var row = new EdgeRow(source.DocumentId, packId, index, left, right, hasG2A ? anchorResult : null,
                    hasH2 ? boundaries.Length == 1 ? boundaries[0] : "DISAGREEMENT" : null,
                    overlapping?.Select(value => value.Anchor).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [],
                    overlapping?.Count ?? 0, status);
                docEdgeRows.Add(row);
                edgeRows.Add(row);
            }
            docRows.Add(new
            {
                documentId = source.DocumentId,
                packId,
                f1OwnedOccurrences = f1Occurrences.Length,
                sourceAdjacentEdges = adjacent.Length,
                g2aIssuedPrimaries = g2aDecisionRows.Count,
                g2aHas = g2aDecisionRows.Count(value => value.Value == "HAS_STRUCTURAL_EXTENT"),
                g2aNo = g2aDecisionRows.Count(value => value.Value == "NO_STRUCTURAL_EXTENT"),
                h2Calls = resultRows.Count(value => value.GetProperty("documentId").GetString() == source.DocumentId),
                h2IssuedEdgeJudgments = resultRows.Where(value => value.GetProperty("documentId").GetString() == source.DocumentId).Sum(value => value.GetProperty("issued").GetInt32()),
                h2UniqueSourceEdges = h2DecidedEdges.Count,
                h2OverlappedSourceEdges = h2DecidedEdges.Count(value => value.Value.Count > 1),
                h2OverlapDisagreementEdges = docEdgeRows.Count(value => value.Status == "H2_OVERLAP_DISAGREEMENT"),
                categories = new
                {
                    pairableEdges = docEdgeRows.Count(value => value.Status is "CONFLICT" or "NON_CONFLICT"),
                    conflicts = docEdgeRows.Count(value => value.Status == "CONFLICT"),
                    nonConflicts = docEdgeRows.Count(value => value.Status == "NON_CONFLICT"),
                    noG2ARight = docEdgeRows.Count(value => value.Status == "UNPAIRED_NO_G2A_RIGHT"),
                    noH2 = docEdgeRows.Count(value => value.Status == "UNPAIRED_NO_H2"),
                    neither = docEdgeRows.Count(value => value.Status == "UNPAIRED_NO_G2A_AND_NO_H2"),
                    overlapDisagreement = docEdgeRows.Count(value => value.Status == "H2_OVERLAP_DISAGREEMENT"),
                },
            });
        }

        var pairableCount = edgeRows.Count(value => value.Status is "CONFLICT" or "NON_CONFLICT");
        var conflictCount = edgeRows.Count(value => value.Status == "CONFLICT");
        Assert.Equal(31, h2RawAudit.Count);
        Assert.Equal(1_790, resultRows.Sum(value => value.GetProperty("issued").GetInt32()));
        Assert.Equal(475, edgeRows.Count);
        Assert.Equal(25, pairableCount);
        Assert.Equal(3, conflictCount);
        Assert.Equal(15, edgeRows.Count(value => value.Status == "H2_OVERLAP_DISAGREEMENT"));
        FreezeArtifact.AssertJson(OutputRoot, "g2a-h2-full-pack-raw-only-edge-census.v1.json", new
        {
            schemaVersion = "v5-p6th3-g2a-h2-full-pack-raw-only-census-v1",
            status = "FROZEN_RAW_ONLY_CENSUS_GOLD_NOT_READ",
            joinKey = "(documentId, packId, leftOccurrence, rightOccurrence); H2 anchor-scoped duplicate decisions are retained and disagreement is explicit",
            populationScope = "Five explicitly frozen RESOURCE_BOUNDED_SOURCE_PACKING_V1 packs (one per document); H2 calls were issued for each G2A HAS primary and its owned-pack tail. This is not a whole-document or corpus-wide H2 census.",
            inputs = new { f1 = "full 96-occurrence owned pack order", g2a = "complete G2A ledger over F1 ESTABLISHES_STRUCTURE primaries", h2 = "31 immutable anchor-scoped H2 responses" },
            capture = new
            {
                providerCalls = 31,
                primaryCalls = 31,
                acceptedLedgers = 31,
                finishStop = true,
                retry = 0,
                repair = false,
                fallback = false,
                reasoningExecutionConfirmed = true,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
            },
            noGold = new { goldRead = false, goldUsedForCohortConstruction = false, goldMutation = "NONE" },
            statusDefinitions = new
            {
                PAIRABLE_EDGE = "G2A has a decision for right and all overlapping H2 decisions agree on one boundary; outcome is CONFLICT or NON_CONFLICT.",
                UNPAIRED_NO_G2A_RIGHT = "At least one H2 judgment exists but G2A has no decision for right.",
                UNPAIRED_NO_H2 = "G2A has a decision for right but no H2 judgment exists for this adjacent edge.",
                UNPAIRED_NO_G2A_AND_NO_H2 = "Neither G2A(right) nor H2(left,right) is present; reported separately so edge accounting is exhaustive.",
                H2_OVERLAP_DISAGREEMENT = "H2 issued multiple anchor-scoped judgments for one source edge and they disagree; not pairable and never tie-broken.",
                CONFLICT = "PAIRABLE_EDGE with G2A(right)=HAS_STRUCTURAL_EXTENT and H2=CONTINUES_STRUCTURAL_UNIT.",
                NON_CONFLICT = "PAIRABLE_EDGE that does not meet the conflict predicate.",
            },
            denominator = new
            {
                sourceAdjacentEdges = edgeRows.Count,
                pairableEdges = pairableCount,
                conflicts = conflictCount,
                nonConflicts = edgeRows.Count(value => value.Status == "NON_CONFLICT"),
                conflictRateAmongPairable = pairableCount == 0 ? (double?)null : (double)conflictCount / pairableCount,
                nonPairable = edgeRows.Count - pairableCount,
                qualificationInterpretation = "Observed denominator for these five selected packs only; not a random corpus sample or general H3 qualification population.",
            },
            coverage = new
            {
                documents = docRows,
                perCallRawIntegrity = "Every raw response and SSE hash verified; each exact manifest request and issued edge set matched.",
                overlapRule = "No candidate authority, Gold, majority, first-anchor preference, or tie-break is used.",
            },
            h2Captures = h2RawAudit,
            edges = edgeRows.OrderBy(value => value.DocumentId, StringComparer.Ordinal).ThenBy(value => value.Ordinal).Select(value => new
            {
                value.DocumentId, value.PackId, value.Ordinal, value.Left, value.Right,
                g2aRight = value.G2ARight,
                h2Boundary = value.H2Boundary,
                h2Anchors = value.H2Anchors,
                h2JudgmentCount = value.H2JudgmentCount,
                status = value.Status,
            }).ToArray(),
            conclusion = "31_OF_31_FROZEN_H2_CALLS_ACCEPTED;_RAW_ONLY_FULL_PACK_CENSUS_FROZEN_FOR_THE_FIVE_SELECTED_PACKS;_GOLD_REMAINS_UNREAD;_NO_RUNTIME_PROMOTION",
        });
    }

    private static Dictionary<string, string> ParseG2A(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal(1, root.EnumerateObject().Count());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("decisions").EnumerateArray())
        {
            Assert.Equal(2, row.EnumerateObject().Count());
            var primary = row.GetProperty("primary").GetString()!;
            var anchor = row.GetProperty("anchor").GetString()!;
            Assert.Contains(anchor, new[] { "HAS_STRUCTURAL_EXTENT", "NO_STRUCTURAL_EXTENT" });
            Assert.True(result.TryAdd(primary, anchor), $"duplicate G2A primary:{primary}");
        }
        return result;
    }

    private static string RequestKey(JsonElement value) =>
        $"{value.GetProperty("documentId").GetString()}|{value.GetProperty("packId").GetString()}|{value.GetProperty("anchor").GetString()}";

    private static string Relative(string path) => Path.GetRelativePath(TestRepository.Root(), path).Replace('\\', '/');
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record Source(string DocumentId, string F1Path, F1Kind Kind);
    private enum F1Kind { RawCapture, ResultRow, ResultRows }
    private sealed record H2Decision(string Anchor, string Boundary, string ResponseSha256);
    private sealed record EdgeRow(string DocumentId, string PackId, int Ordinal, string Left, string Right,
        string? G2ARight, string? H2Boundary, IReadOnlyList<string> H2Anchors, int H2JudgmentCount, string Status);
}
