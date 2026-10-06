using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freezes a direct heading-end pointer treatment for the complete frozen G2A-HAS population, without Gold or provider access.</summary>
public sealed class V5P6TH2CEndPointerPreflightTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string G2ACaptureRoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string G2APreflightPath = Root + "/p6tg2a-full-pack-population-preflight/g2a-full-pack-preflight.v1.json";
    private const string OutputRoot = Root + "/p6th2c-end-pointer-preflight-v2";
    private const int ResponseByteCap = 49_152;

    private static readonly string SystemPrompt = """
        Determine the exact source extent of the one heading that begins at the issued anchor occurrence. A heading may consist of one or more consecutive source occurrences. Return every and only consecutive occurrence that belongs literally to this exact heading. Do not include later body content merely because it belongs to the same section, topic, agenda item, or semantic region.

        For each request, copy the anchor value exactly from that request's input anchor field. Never substitute an identifier from instructions, prior requests, or another occurrence. Return exactly one decision for that anchor with these five properties: anchor, headingMembers, endOccurrence, firstOutsideOccurrence, and firstOutsideRole. headingMembers must begin with that exact anchor value and be one contiguous prefix of the ordered issued occurrences. endOccurrence must equal its final member. firstOutsideOccurrence must be the immediate successor after endOccurrence, never a skipped occurrence. If every issued occurrence belongs to the heading and there is no visible successor, use null for firstOutsideOccurrence and NO_VISIBLE_SUCCESSOR for firstOutsideRole.

        When firstOutsideOccurrence is present, firstOutsideRole must be exactly one of NEW_HEADING, BODY_CONTENT, PAGE_FURNITURE, TABLE_OR_STRUCTURED_CONTENT, OTHER_NON_HEADING. These are descriptive roles of the first occurrence outside the exact heading, not permission to extend the heading. Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.

        Return one JSON object only with root property decisions and exactly one decision per input anchor. Each decision must have exactly the five required properties and no others. Copy only issued occurrence handles from the current request. Do not output source text or additional properties. This contract has no example identifiers; use the actual anchor and occurrence handles present in the current request.
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
    private sealed record RequestRow(string DocumentId, string PackId, string Anchor, string AnchorAlias,
        string SourceSha256, string SourceUniverseSha256, string F1CaptureSha256, string F1ResponseSha256,
        string G2ARawCaptureSha256, string G2AResponseSha256, string UserMessageSha256, int UserBytes,
        string ProviderBodySha256, int ProviderBodyBytes, int MaxCompletionTokens, int IssuedOccurrenceCount,
        int WorstCaseResponseBytes, IReadOnlyList<string> IssuedOccurrences);

    [Fact]
    public void H2C_end_pointer_parser_requires_a_contiguous_prefix_and_immediate_outside_successor()
    {
        Assert.DoesNotContain("O17", SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("copy the anchor value exactly from that request's input anchor field", SystemPrompt, StringComparison.Ordinal);
        var issued = new[] { "O17", "O18", "O19" };
        Assert.True(TryParseEndPointer("""{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O19","firstOutsideRole":"NEW_HEADING"}]}""", issued, "O17"));
        Assert.True(TryParseEndPointer("""{"decisions":[{"anchor":"O17","headingMembers":["O17","O18","O19"],"endOccurrence":"O19","firstOutsideOccurrence":null,"firstOutsideRole":"NO_VISIBLE_SUCCESSOR"}]}""", issued, "O17"));

        var invalid = new[]
        {
            """{"decisions":[{"anchor":"O17","headingMembers":["O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O19","firstOutsideRole":"NEW_HEADING"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O19"],"endOccurrence":"O19","firstOutsideOccurrence":null,"firstOutsideRole":"NO_VISIBLE_SUCCESSOR"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O17","firstOutsideOccurrence":"O19","firstOutsideRole":"NEW_HEADING"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17"],"endOccurrence":"O17","firstOutsideOccurrence":"O19","firstOutsideRole":"BODY_CONTENT"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O18","firstOutsideRole":"BODY_CONTENT"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O19","firstOutsideRole":"NO_VISIBLE_SUCCESSOR"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O19","firstOutsideRole":"SAME_SECTION"}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O19","firstOutsideRole":"NEW_HEADING","confidence":1}]}""",
            """{"decisions":[{"anchor":"O17","headingMembers":["O17","O18"],"endOccurrence":"O18","firstOutsideOccurrence":"O19","firstOutsideRole":"NEW_HEADING"}],"extra":true}""",
        };
        Assert.All(invalid, response => Assert.False(TryParseEndPointer(response, issued, "O17")));
    }

    [Fact]
    public void H2C_direct_end_pointer_freezes_the_same_full_G2A_HAS_population_with_neutral_facts_only()
    {
        using var g2aPreflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(G2APreflightPath)));
        var rows = new List<RequestRow>();
        var perDocument = new List<object>();
        foreach (var source in Sources)
        {
            var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
            var snapshotPath = TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json");
            var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, source.DocumentId);
            var snapshot = JsonSerializer.Deserialize<PdfCanonicalSourceSnapshotV1>(File.ReadAllText(snapshotPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("p6th2c-source-snapshot-invalid");
            Assert.Equal(plan.SourceSha256, snapshot.SourceSha256);
            Assert.Equal(plan.SourceUniverseSha256, snapshot.SourceAliasUniverseSha256);
            var evidence = snapshot.Evidence.ToDictionary(value => value.SourceAlias, StringComparer.Ordinal);

            var f1Path = TestRepository.Path($"{Root}/{source.F1Path}");
            var f1Bytes = File.ReadAllBytes(f1Path);
            using var f1Capture = JsonDocument.Parse(f1Bytes);
            var f1Row = source.Kind switch
            {
                F1Kind.RawCapture => f1Capture.RootElement,
                F1Kind.ResultRow => f1Capture.RootElement.GetProperty("row"),
                F1Kind.ResultRows => f1Capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
                _ => throw new InvalidOperationException("p6th2c-unknown-f1-kind"),
            };
            var pack = plan.Packs.Single(value => value.PackId == f1Row.GetProperty("packId").GetString());
            var correspondence = source.F1UsedCorrespondences ? BuildCorrespondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
            var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondence);
            var f1RequestHash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash) ? semanticHash.GetString() : f1Row.GetProperty("userMessageSha256").GetString();
            Assert.Equal(f1.Request.UserMessageSha256, f1RequestHash);
            Assert.Equal(96, f1.Request.Occurrences.Count);
            Assert.Equal("stop", f1Row.GetProperty("finishReason").GetString());
            var f1Response = f1Row.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(f1Response), ReadStringOrAlternative(f1Row, "rawResponseSha256", "acceptedRawResponseSha256"));
            var functions = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Response);
            Assert.Equal(96, functions.Decisions.Count);

            var g2aPath = TestRepository.Path($"{G2ACaptureRoot}/{source.DocumentId}.raw-capture.v1.json");
            var g2aBytes = File.ReadAllBytes(g2aPath);
            using var g2a = JsonDocument.Parse(g2aBytes);
            var g2aRoot = g2a.RootElement;
            Assert.Equal(source.DocumentId, g2aRoot.GetProperty("documentId").GetString());
            Assert.Equal(pack.PackId, g2aRoot.GetProperty("packId").GetString());
            Assert.Equal(plan.SourceSha256, g2aRoot.GetProperty("sourceSha256").GetString());
            Assert.Equal(plan.SourceUniverseSha256, g2aRoot.GetProperty("sourceUniverseSha256").GetString());
            Assert.Equal("stop", g2aRoot.GetProperty("finishReason").GetString());
            Assert.Equal(0, g2aRoot.GetProperty("retryCount").GetInt32());
            Assert.Equal(Hash(g2aRoot.GetProperty("rawResponse").GetString()!), g2aRoot.GetProperty("rawResponseSha256").GetString());
            Assert.Equal(Hash(g2aRoot.GetProperty("rawSse").GetString()!), g2aRoot.GetProperty("rawSseSha256").GetString());

            var frozen = g2aPreflight.RootElement.GetProperty("cohort").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId);
            Assert.Equal(Hash(f1Response), frozen.GetProperty("f1").GetProperty("acceptedRawResponseSha256").GetString());
            Assert.Equal(frozen.GetProperty("g2a").GetProperty("providerBodySha256").GetString(), g2aRoot.GetProperty("providerBodySha256").GetString());
            var f1Ledger = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, f1Response);
            var functionById = f1Ledger.Decisions.ToDictionary(value => value.OccurrenceId, value => value.Function, StringComparer.Ordinal);
            var g2aDecisions = ParseG2A(g2aRoot.GetProperty("rawResponse").GetString()!);
            var issuedPrimaries = frozen.GetProperty("issuedPrimaries").EnumerateArray().Select(value => value.GetProperty("occurrence").GetString()!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(issuedPrimaries.Count, g2aDecisions.Count);
            Assert.True(issuedPrimaries.SetEquals(g2aDecisions.Keys));

            var atomsByAlias = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
            var owned = pack.OwnedAliases;
            var idByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
            var aliasById = f1.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
            var has = g2aDecisions.Where(value => value.Value == "HAS_STRUCTURAL_EXTENT")
                .OrderBy(value => int.Parse(value.Key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var documentRows = new List<RequestRow>();
            foreach (var (anchorId, _) in has)
            {
                Assert.Equal(OccurrenceFunction.EstablishesStructure, functionById[anchorId]);
                var anchorAlias = aliasById[anchorId];
                var start = Array.IndexOf(owned.ToArray(), anchorAlias);
                Assert.True(start >= 0 && start + 1 < owned.Count, $"H2C requires at least one visible successor:{source.DocumentId}:{anchorId}");
                var tailAliases = owned.Skip(start).ToArray();
                var occurrenceRows = tailAliases.Select(alias =>
                {
                    var item = evidence[alias];
                    var style = item.StyleFacts;
                    var location = item.LocationFacts;
                    return new
                    {
                        occurrence = idByAlias[alias],
                        page = atomsByAlias[alias].Page,
                        text = atomsByAlias[alias].Text,
                        style = new
                        {
                            fontSize = style.GetProperty("fontSize"),
                            bodyFontSize = style.GetProperty("bodyFontSize"),
                            fontSizeToBodyRatio = style.GetProperty("fontSizeToBodyRatio"),
                            boldRatio = style.GetProperty("boldRatio"),
                            italicRatio = style.GetProperty("italicRatio"),
                            lineCount = style.GetProperty("lineCount"),
                        },
                        location = new
                        {
                            verticalPosition = location?.GetProperty("verticalPosition") ?? default,
                            sameNormalizedTextPageCount = location?.GetProperty("sameNormalizedTextPageCount") ?? default,
                            sameNormalizedTextFirstPage = location?.GetProperty("sameNormalizedTextFirstPage") ?? default,
                            sameNormalizedTextLastPage = location?.GetProperty("sameNormalizedTextLastPage") ?? default,
                        },
                    };
                }).ToArray();
                var user = JsonSerializer.Serialize(new
                {
                    protocolVersion = "v5-function-conditioned-exact-end-pointer-2",
                    anchors = new[] { new { anchor = anchorId, occurrences = occurrenceRows } },
                });
                var request = new V5FreeHeadingRequestV1("v5-function-conditioned-exact-end-pointer-2", SystemPrompt, user,
                    Hash(user), Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
                var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
                var worstResponse = WorstCaseResponseBytes(anchorId, occurrenceRows.Select(value => value.occurrence).ToArray());
                Assert.True(worstResponse < ResponseByteCap, $"H2C worst-case response exceeds cap:{source.DocumentId}:{anchorId}");
                documentRows.Add(new RequestRow(source.DocumentId, pack.PackId, anchorId, anchorAlias, plan.SourceSha256,
                    plan.SourceUniverseSha256, Hash(f1Bytes), Hash(f1Response), Hash(g2aBytes), g2aRoot.GetProperty("rawResponseSha256").GetString()!,
                    request.UserMessageSha256, request.UserMessageUtf8Bytes, body.Hash, body.Bytes, pack.MaxCompletionTokens,
                    occurrenceRows.Length, worstResponse, occurrenceRows.Select(value => (string)value.occurrence!).ToArray()));
            }
            Assert.Equal(has.Length, documentRows.Count);
            perDocument.Add(new
            {
                documentId = source.DocumentId,
                packId = pack.PackId,
                sourceSha256 = plan.SourceSha256,
                sourceUniverseSha256 = plan.SourceUniverseSha256,
                f1CaptureSha256 = Hash(f1Bytes),
                g2aRawCaptureSha256 = Hash(g2aBytes),
                f1OwnedOccurrences = owned.Count,
                g2aIssued = g2aDecisions.Count,
                g2aHas = has.Length,
                h2cRequests = documentRows.Count,
                maxUserBytes = documentRows.Max(value => value.UserBytes),
                maxProviderBodyBytes = documentRows.Max(value => value.ProviderBodyBytes),
                maxWorstCaseResponseBytes = documentRows.Max(value => value.WorstCaseResponseBytes),
            });
            rows.AddRange(documentRows);
        }

        Assert.Equal(5, perDocument.Count);
        Assert.Equal(31, rows.Count);
        var maxBody = rows.Max(value => value.ProviderBodyBytes);
        var maxUser = rows.Max(value => value.UserBytes);
        var maxOutput = rows.Max(value => value.WorstCaseResponseBytes);
        FreezeArtifact.AssertJson(OutputRoot, "h2c-exact-end-pointer-preflight.v2.json", new
        {
            schemaVersion = "v5-p6th2c-exact-end-pointer-preflight-v2",
            status = "FULL_G2A_HAS_REQUESTS_FROZEN_NOT_AUTHORIZED_NO_GOLD_READ_PROMPT_EXAMPLE_HANDLES_REMOVED",
            authorities = new
            {
                g2aPreflightSha256 = Hash(File.ReadAllText(TestRepository.Path(G2APreflightPath))),
                allFiveFrozenG2AResponsesHashVerified = true,
                allFiveFunctionLedgersAccepted = true,
                hasAnchorsDerivedOnlyFromFrozenG2A = true,
                sourceFactsReadFromFrozenCanonicalSnapshots = true,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                sharedRuntime = "UNCHANGED",
            },
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                temperature = 0,
                route = "OPENROUTER_ALIBABA_PINNED",
                protocolVersion = "v5-function-conditioned-exact-end-pointer-2",
                completeSourceTail = true,
                systemPromptSha256 = Hash(SystemPrompt),
                sharedProviderBodyBuilder = nameof(PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled),
                includesOnlyOwnedPackOccurrencesAfterAnchor = true,
                contextOnlyAtomsVisibleOrSelectable = false,
                output = "ONE_EXACT_BOUNDARY_POINTER_AND_FIRST_OUTSIDE_ROLE_PER_ANCHOR",
                noCompetingCandidateMenu = true,
                maxProviderBodyBytes = maxBody,
                maxUserMessageBytes = maxUser,
                maxWorstCaseResponseBytes = maxOutput,
                responseByteCap = ResponseByteCap,
                worstCaseResponseSerialization = "ACTUAL_JSON_WITH_EVERY_ISSUED_OCCURRENCE_IN_HEADING_MEMBERS_AND_NULL_OUTSIDE_SENTINEL",
            },
            responseContract = new
            {
                requiredFields = new[] { "anchor", "headingMembers", "endOccurrence", "firstOutsideOccurrence", "firstOutsideRole" },
                headingMembersMustBeContiguousPrefixStartingAtAnchor = true,
                endOccurrenceMustEqualLastHeadingMember = true,
                outsideMustBeImmediateSuccessor = true,
                noVisibleSuccessor = "firstOutsideOccurrence=null; firstOutsideRole=NO_VISIBLE_SUCCESSOR",
                allowedOutsideRoles = new[] { "NEW_HEADING", "BODY_CONTENT", "PAGE_FURNITURE", "TABLE_OR_STRUCTURED_CONTENT", "OTHER_NON_HEADING", "NO_VISIBLE_SUCCESSOR" },
                exactPropertiesOnly = true,
                parserAuthority = "TEST_ONLY_VALIDATION;_NO_RUNTIME_PROMOTION",
            },
            neutralFacts = new
            {
                style = new[] { "fontSize", "bodyFontSize", "fontSizeToBodyRatio", "boldRatio", "italicRatio", "lineCount" },
                location = new[] { "page", "verticalPosition", "sameNormalizedTextPageCount", "sameNormalizedTextFirstPage", "sameNormalizedTextLastPage" },
                semanticOrExpectedBoundaryLabels = false,
                layoutBlockIdentity = false,
                goldDerivedFacts = false,
            },
            population = new { documents = perDocument, requestCount = rows.Count, maxEdgesEquivalent = rows.Sum(value => Math.Max(0, value.IssuedOccurrenceCount - 1)) },
            requestUniverse = rows,
            execution = new
            {
                providerCalls = 0,
                maximumFuturePrimaryCalls = rows.Count,
                retry = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                status = "AWAITING_SEPARATE_H2C_PROVIDER_AUTHORIZATION",
            },
            nextGate = "REVIEW_FROZEN_DIRECT_END_POINTER_REQUESTS;_SEPARATE_AUTHORIZATION_REQUIRED_BEFORE_PROVIDER_EXECUTION",
        });
    }

    private static int WorstCaseResponseBytes(string anchor, IReadOnlyList<string> occurrenceIds)
    {
        var roles = new[] { "NEW_HEADING", "BODY_CONTENT", "PAGE_FURNITURE", "TABLE_OR_STRUCTURED_CONTENT", "OTHER_NON_HEADING" };
        var sizes = new List<int>();
        for (var memberCount = 1; memberCount < occurrenceIds.Count; memberCount++)
        {
            foreach (var role in roles)
            {
                sizes.Add(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new
                {
                    decisions = new[] { new
                    {
                        anchor,
                        headingMembers = occurrenceIds.Take(memberCount).ToArray(),
                        endOccurrence = occurrenceIds[memberCount - 1],
                        firstOutsideOccurrence = occurrenceIds[memberCount],
                        firstOutsideRole = role,
                    } },
                })));
            }
        }
        sizes.Add(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new
        {
            decisions = new[] { new { anchor, headingMembers = occurrenceIds, endOccurrence = occurrenceIds[^1], firstOutsideOccurrence = (string?)null, firstOutsideRole = "NO_VISIBLE_SUCCESSOR" } },
        })));
        return sizes.Max();
    }

    private static bool TryParseEndPointer(string raw, IReadOnlyList<string> issuedOccurrences, string anchor)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(raw) > ResponseByteCap) return false;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != 1) return false;
            var decision = decisions[0];
            if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Count() != 5 ||
                decision.GetProperty("anchor").ValueKind != JsonValueKind.String || decision.GetProperty("anchor").GetString() != anchor ||
                decision.GetProperty("headingMembers").ValueKind != JsonValueKind.Array ||
                decision.GetProperty("endOccurrence").ValueKind != JsonValueKind.String ||
                !decision.TryGetProperty("firstOutsideOccurrence", out var outside) ||
                decision.GetProperty("firstOutsideRole").ValueKind != JsonValueKind.String) return false;

            var members = decision.GetProperty("headingMembers").EnumerateArray().ToArray();
            if (members.Length == 0 || members.Any(value => value.ValueKind != JsonValueKind.String) || members.Length > issuedOccurrences.Count) return false;
            var memberIds = members.Select(value => value.GetString()!).ToArray();
            if (!memberIds.SequenceEqual(issuedOccurrences.Take(memberIds.Length), StringComparer.Ordinal) || memberIds[0] != anchor ||
                decision.GetProperty("endOccurrence").GetString() != memberIds[^1]) return false;

            var outsideRole = decision.GetProperty("firstOutsideRole").GetString();
            if (memberIds.Length == issuedOccurrences.Count)
                return outside.ValueKind == JsonValueKind.Null && outsideRole == "NO_VISIBLE_SUCCESSOR";

            var allowed = outsideRole is "NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING";
            return outside.ValueKind == JsonValueKind.String && outside.GetString() == issuedOccurrences[memberIds.Length] && allowed;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

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

    private static Dictionary<string, string> ParseG2A(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("p6th2c-g2a-ledger-root-invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2) throw new InvalidDataException("p6th2c-g2a-ledger-row-invalid");
            var primary = row.GetProperty("primary").GetString()!;
            var anchor = row.GetProperty("anchor").GetString()!;
            if (anchor is not ("HAS_STRUCTURAL_EXTENT" or "NO_STRUCTURAL_EXTENT") || !result.TryAdd(primary, anchor)) throw new InvalidDataException("p6th2c-g2a-ledger-value-invalid");
        }
        return result;
    }

    private static string ReadStringOrAlternative(JsonElement element, string first, string second) =>
        element.TryGetProperty(first, out var value) ? value.GetString()! : element.GetProperty(second).GetString()!;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
