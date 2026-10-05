using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>After all five G2A raw ledgers are accepted, freeze the complete anchor-derived H2 request universe.</summary>
public sealed class V5P6TH3FullPopulationH2PreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string CaptureRoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string G2APreflightPath = Root + "/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json";
    private const string OutputRoot = Root + "/p6th3-full-population-h2-preflight";
    private const int ResponseByteCap = 49_152;
    private static readonly string SystemPrompt = """
        Judge only source-order continuation boundaries for an already-qualified structural anchor. For each issued edge, decide whether right continues the same local structural unit begun at anchor, or whether the unit stops before right.

        CONTINUES_STRUCTURAL_UNIT means left and right belong to the same exact local structural unit. STOPS_STRUCTURAL_UNIT means the unit begun at anchor ends before right. The edges are consecutive source occurrences; do not use similarity, hierarchy, candidates, spans, locators, or hypothetical text not issued in the request. Once an anchor stops, every later issued edge for that anchor must also be STOPS_STRUCTURAL_UNIT.

        Return exactly one JSON object: {"decisions":[{"anchor":"O4","left":"O4","right":"O5","boundary":"CONTINUES_STRUCTURAL_UNIT"}]}. Return exactly one decision for every issued edge. Echo only issued O# values. Do not output candidate IDs, source text, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
        """;

    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Kind.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Kind.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
    ];

    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Kind Kind, bool F1UsedCorrespondences);
    private enum F1Kind { RawCapture, ResultRow, ResultRows }
    private sealed record Edge(string Anchor, string Left, string Right, int Ordinal, string LeftAlias, string RightAlias);
    private sealed record H2Request(string DocumentId, string PackId, string Anchor, string AnchorAlias,
        string G2ARawSha256, string User, string UserHash, int UserBytes, byte[] Body, string BodyHash, int BodyBytes,
        int MaxCompletionTokens, IReadOnlyList<Edge> Edges);

    [Fact]
    public void P6TH3_full_population_freezes_every_H2_request_from_all_accepted_G2A_HAS_outputs_without_Gold()
    {
        using var g2aPreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G2APreflightPath)));
        var allRequests = new List<H2Request>();
        var sourceRows = new List<object>();
        foreach (var source in Sources)
        {
            var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json"), source.DocumentId);
            var f1Path = TestRepository.Path($"{Root}/{source.F1Path}");
            using var f1Capture = JsonDocument.Parse(File.ReadAllText(f1Path));
            var f1Row = source.Kind switch
            {
                F1Kind.RawCapture => f1Capture.RootElement,
                F1Kind.ResultRow => f1Capture.RootElement.GetProperty("row"),
                F1Kind.ResultRows => f1Capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
                _ => throw new InvalidOperationException("unknown-f1-kind"),
            };
            var pack = plan.Packs.Single(value => value.PackId == f1Row.GetProperty("packId").GetString());
            var correspondences = source.F1UsedCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
            var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondences);
            var f1RequestHash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash) ? semanticHash.GetString() : f1Row.GetProperty("userMessageSha256").GetString();
            Assert.Equal(f1.Request.UserMessageSha256, f1RequestHash);
            Assert.Equal(96, f1.Request.Occurrences.Count);
            Assert.Equal("stop", f1Row.GetProperty("finishReason").GetString());
            var f1Response = f1Row.GetProperty("rawResponse").GetString()!;
            var functions = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Response);
            Assert.Equal(96, functions.Decisions.Count);

            var g2aPath = TestRepository.Path($"{CaptureRoot}/{source.DocumentId}.raw-capture.v1.json");
            var g2aRawBytes = File.ReadAllBytes(g2aPath);
            using var g2a = JsonDocument.Parse(g2aRawBytes);
            var g2aRoot = g2a.RootElement;
            Assert.Equal(source.DocumentId, g2aRoot.GetProperty("documentId").GetString());
            Assert.Equal(pack.PackId, g2aRoot.GetProperty("packId").GetString());
            Assert.Equal(plan.SourceSha256, g2aRoot.GetProperty("sourceSha256").GetString());
            Assert.Equal(plan.SourceUniverseSha256, g2aRoot.GetProperty("sourceUniverseSha256").GetString());
            Assert.Equal("stop", g2aRoot.GetProperty("finishReason").GetString());
            Assert.Equal(0, g2aRoot.GetProperty("retryCount").GetInt32());
            Assert.Equal(1, g2aRoot.GetProperty("providerCalls").GetInt32());
            Assert.Equal(Hash(g2aRoot.GetProperty("rawResponse").GetString()!), g2aRoot.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(Hash(g2aRoot.GetProperty("rawSse").GetString()!), g2aRoot.GetProperty("rawSseSha256").GetString());
            Assert.Equal("ALL_FIVE_TOTAL_LEDGERS_ACCEPTED", ReadResultStatus());

            var frozen = g2aPreflight.RootElement.GetProperty("cohort").EnumerateArray().Single(row => row.GetProperty("documentId").GetString() == source.DocumentId);
            Assert.Equal(Hash(File.ReadAllText(f1Path)), frozen.GetProperty("f1").GetProperty("rawCaptureSha256").GetString());
            Assert.Equal(Hash(f1Response), frozen.GetProperty("f1").GetProperty("acceptedRawResponseSha256").GetString());
            Assert.Equal(frozen.GetProperty("g2a").GetProperty("providerBodySha256").GetString(), g2aRoot.GetProperty("providerBodySha256").GetString());

            var rawResponse = g2aRoot.GetProperty("rawResponse").GetString()!;
            using var ledgerDoc = JsonDocument.Parse(rawResponse);
            var decisions = ledgerDoc.RootElement.GetProperty("decisions").EnumerateArray().ToArray();
            Assert.Equal(1, ledgerDoc.RootElement.EnumerateObject().Count());
            Assert.All(decisions, decision =>
            {
                Assert.Equal(2, decision.EnumerateObject().Count());
                Assert.Contains(decision.GetProperty("anchor").GetString(), new[] { "HAS_STRUCTURAL_EXTENT", "NO_STRUCTURAL_EXTENT" });
            });
            var expectedPrimaries = frozen.GetProperty("issuedPrimaries").EnumerateArray().Select(value => value.GetProperty("occurrence").GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(frozen.GetProperty("g2a").GetProperty("expectedLedgerCardinality").GetInt32(), decisions.Length);
            var returned = decisions.Select(value => value.GetProperty("primary").GetString()!).ToArray();
            Assert.Equal(returned.Length, returned.Distinct(StringComparer.Ordinal).Count());
            Assert.True(expectedPrimaries.SetEquals(returned), $"G2A ledger primary set mismatch:{source.DocumentId}");

            var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
            var owned = pack.OwnedAliases;
            var idByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
            var aliasById = f1.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
            var functionById = functions.Decisions.ToDictionary(value => value.OccurrenceId, value => value.Function, StringComparer.Ordinal);
            var has = decisions.Where(value => value.GetProperty("anchor").GetString() == "HAS_STRUCTURAL_EXTENT")
                .Select(value => value.GetProperty("primary").GetString()!).OrderBy(value => int.Parse(value.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.All(has, occurrence => Assert.Equal(V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE, functionById[occurrence]));
            var docRequests = new List<H2Request>();
            foreach (var anchorId in has)
            {
                var alias = aliasById[anchorId];
                var start = Array.IndexOf(owned.ToArray(), alias);
                Assert.True(start >= 0, $"G2A anchor is not owned:{source.DocumentId}:{anchorId}");
                if (start + 1 >= owned.Count) continue; // Terminal pack occurrences have no outgoing continuation edges.
                var chainAliases = owned.Skip(start).ToArray();
                var occurrenceRows = chainAliases.Select(item => new
                {
                    occurrence = idByAlias[item], page = atoms[item].Page, text = atoms[item].Text, selectable = false,
                }).ToArray();
                var edges = Enumerable.Range(0, chainAliases.Length - 1).Select(index => new Edge(anchorId,
                    idByAlias[chainAliases[index]], idByAlias[chainAliases[index + 1]], index, chainAliases[index], chainAliases[index + 1])).ToArray();
                var user = JsonSerializer.Serialize(new
                {
                    protocolVersion = "v5-function-conditioned-continuation-boundary-1",
                    anchors = new[] { new { anchor = anchorId, occurrences = occurrenceRows, edges = edges.Select(edge => new { anchor = edge.Anchor, left = edge.Left, right = edge.Right, ordinal = edge.Ordinal }).ToArray() } },
                });
                var request = new V5FreeHeadingRequestV1("v5-function-conditioned-continuation-boundary-1", SystemPrompt, user,
                    Hash(user), Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
                var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
                var worstCaseResponse = JsonSerializer.Serialize(new { decisions = edges.Select(edge => new
                {
                    anchor = edge.Anchor, left = edge.Left, right = edge.Right, boundary = "STOPS_STRUCTURAL_UNIT",
                }).ToArray() });
                Assert.True(Encoding.UTF8.GetByteCount(worstCaseResponse) < ResponseByteCap,
                    $"H2 singleton-anchor response envelope exceeds cap:{source.DocumentId}:{anchorId}");
                docRequests.Add(new H2Request(source.DocumentId, pack.PackId, anchorId, alias, Hash(g2aRawBytes), user,
                    request.UserMessageSha256, request.UserMessageUtf8Bytes, body.PayloadBytes, body.Hash, body.Bytes,
                    pack.MaxCompletionTokens, edges));
            }
            allRequests.AddRange(docRequests);
            sourceRows.Add(new
            {
                documentId = source.DocumentId,
                packId = pack.PackId,
                f1CaptureSha256 = Hash(File.ReadAllText(f1Path)),
                g2aRawCaptureSha256 = Hash(g2aRawBytes),
                g2aRequestBodySha256 = g2aRoot.GetProperty("providerBodySha256").GetString(),
                f1OwnedOccurrences = owned.Count,
                f1Establishes = functions.Decisions.Count(value => value.Function == V5OccurrenceFunctionF1.ESTABLISHES_STRUCTURE),
                g2aIssued = decisions.Length,
                g2aHas = has.Length,
                g2aNo = decisions.Length - has.Length,
                h2Requests = docRequests.Count,
                h2Edges = docRequests.Sum(value => value.Edges.Count),
                maxH2RequestBytes = docRequests.Count == 0 ? 0 : docRequests.Max(value => value.BodyBytes),
                maxWorstCaseResponseBytes = docRequests.Count == 0 ? 0 : docRequests.Max(value => WorstCaseResponseBytes(value.Edges)),
            });
        }

        Assert.Equal(5, sourceRows.Count);
        Assert.True(allRequests.Count > 0);
        FreezeArtifact.AssertJson(OutputRoot, "g2a-raw-audit-and-h2-full-request-manifest.v1.json", new
        {
            schemaVersion = "v5-p6th3-g2a-raw-audit-h2-full-population-preflight-v1",
            status = "H2_FULL_REQUEST_UNIVERSE_FROZEN_NOT_AUTHORIZED",
            authority = new
            {
                g2aPreflightSha256 = Hash(File.ReadAllText(TestRepository.Path(G2APreflightPath))),
                g2aCaptureRoot = CaptureRoot,
                allFiveG2AResponsesHashVerified = true,
                allFiveG2ALedgersExactlyMatchIssuedPrimarySets = true,
                allFiveFinishStop = true,
                allFiveRetryCountZero = true,
                noGoldRead = true,
            },
            population = sourceRows,
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                temperature = 0,
                route = "OPENROUTER_ALIBABA_PINNED",
            },
            h2Protocol = new
            {
                protocolVersion = "v5-function-conditioned-continuation-boundary-1",
                oneRequestPerG2AHasAnchorWithAtLeastOneLaterOwnedOccurrence = true,
                ownedPackTailOnly = true,
                terminalPackAnchorOmittedBecauseItHasNoOutgoingEdge = true,
                competingCandidateMenu = false,
                goldOrSourceReview = false,
                responseByteCap = ResponseByteCap,
                worstCaseResponseSizing = "ACTUAL_JSON_SERIALIZATION_WITH_EVERY_EDGE_SET_TO_STOPS_STRUCTURAL_UNIT",
            },
            requestUniverse = allRequests.Select(value => new
            {
                documentId = value.DocumentId,
                packId = value.PackId,
                anchor = value.Anchor,
                anchorAlias = value.AnchorAlias,
                g2aRawCaptureSha256 = value.G2ARawSha256,
                edgeCount = value.Edges.Count,
                edges = value.Edges.Select(edge => new { left = edge.Left, leftAlias = edge.LeftAlias, right = edge.Right, rightAlias = edge.RightAlias, ordinal = edge.Ordinal }).ToArray(),
                systemPromptSha256 = Hash(SystemPrompt),
                userMessageSha256 = value.UserHash,
                userMessageUtf8Bytes = value.UserBytes,
                providerBodySha256 = value.BodyHash,
                providerBodyBytes = value.BodyBytes,
                maxCompletionTokens = value.MaxCompletionTokens,
                worstCaseSerializedResponseUtf8Bytes = WorstCaseResponseBytes(value.Edges),
            }).ToArray(),
            execution = new
            {
                providerCalls = 0,
                maximumFuturePrimaryCalls = allRequests.Count,
                retry = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                sharedRuntime = "UNCHANGED",
                status = "AWAITING_SEPARATE_H2_PROVIDER_AUTHORIZATION",
            },
            nextGate = "RUN_RAW_ONLY_G2A_X_H2_EDGE_CENSUS_AFTER_FROZEN_H2_RAW_CAPTURE;_ONLY_THEN_OPEN_GOLD",
        });

        string ReadResultStatus()
        {
            using var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/result.v1.json")));
            return result.RootElement.GetProperty("status").GetString()!;
        }
    }

    private static int WorstCaseResponseBytes(IReadOnlyList<Edge> edges) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new
    {
        decisions = edges.Select(edge => new { anchor = edge.Anchor, left = edge.Left, right = edge.Right, boundary = "STOPS_STRUCTURAL_UNIT" }).ToArray(),
    }));

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> BuildCorrespondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var key = candidate.Endpoint.Parts[0].Alias;
            var list = result.TryGetValue(key, out var existing) ? existing.ToList() : [];
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
