using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Freeze all five G2A full-pack requests from immutable F1 ledgers, without Gold or provider execution.</summary>
public sealed class V5P6TG2AFullPackPopulationPreflightTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string OutputRoot = Root + "/p6tg2a-full-pack-population-preflight";
    private const string SystemPrompt = """
        Decide anchor existence only. Each issued primary occurrence has an upstream ESTABLISHES_STRUCTURE eligibility signal, but that signal is not proof that a valid local structural heading extent begins at this primary.

        For every issued O#, return exactly one anchor: HAS_STRUCTURAL_EXTENT if at least one valid local structural heading extent begins at that primary; otherwise NO_STRUCTURAL_EXTENT. Do not choose or describe any extent. Do not infer an answer from context-only items.

        Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","anchor":"HAS_STRUCTURAL_EXTENT"},{"primary":"O28","anchor":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and anchor. Do not output source text, candidate IDs, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
        """;

    private static readonly Source[] Sources =
    [
        new("SRC-089", SourcePdfCorpus.Src089, "p6tf1-preflight/retry-src089-result.v1.json", F1Kind.ResultRow, true),
        new("SRC-041", SourcePdfCorpus.Src041, "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("SRC-095", SourcePdfCorpus.Src095, "p6tf1-preflight/result.v1.json", F1Kind.ResultRows, true),
        new("DOC-0252", SourcePdfCorpus.Doc0252, "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
        new("DOC-0256", SourcePdfCorpus.Doc0256, "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Kind.RawCapture, false),
    ];

    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Kind Kind, bool F1UsedCorrespondences);
    private enum F1Kind { RawCapture, ResultRow, ResultRows }
    private sealed record Prepared(string DocumentId, PdfCandidateAuthorityDocumentPlan Plan,
        PdfCandidateAuthorityPreparedPack Pack, PdfFunctionMembershipPreparedPackF1 F1,
        OccurrenceFunctionResult F1Result,
        string F1RawPath, string F1RawSha256, string F1ResponseSha256,
        string F1FinishReason,
        IReadOnlyList<IssuedPrimary> Primaries, string UserMessage, string UserHash,
        int UserBytes, byte[] ProviderPayload, string ProviderHash, int ProviderBytes);
    private sealed record IssuedPrimary(string Occurrence, string Alias, int OwnedIndex, int Page, int Ordinal);

    [Fact]
    public void P6TG2A_freezes_full_pack_requests_for_all_five_documents_before_any_provider_or_Gold_use()
    {
        var prepared = Sources.Select(Prepare).ToArray();
        Assert.Equal(5, prepared.Length);
        Assert.All(prepared, row =>
        {
            Assert.Equal(96, row.F1.Request.Occurrences.Count);
            Assert.Equal(96, row.Pack.OwnedAliases.Count);
            Assert.Equal(row.Pack.OwnedAliases.Count, row.Pack.OwnedAliases.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(row.Primaries.Count, row.Primaries.Select(value => value.Occurrence).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(row.Primaries.Count, row.Primaries.Select(value => value.Alias).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(row.Primaries.Count, row.Primaries.Select(value => value.OwnedIndex).Distinct().Count());
        });

        // Existing canaries are parity witnesses for this canonical G2A composer and carrier.
        var p089 = prepared.Single(value => value.DocumentId == "SRC-089");
        using (var prior = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/p6tg2a-anchor-existence-preflight/anchor-existence-preflight.v1.json"))))
        {
            var callPlan = prior.RootElement.GetProperty("callPlan");
            Assert.Equal(p089.UserHash, callPlan.GetProperty("userMessageSha256").GetString());
            Assert.Equal(p089.ProviderHash, callPlan.GetProperty("providerBodySha256").GetString());
            Assert.Equal(p089.Primaries.Count, callPlan.GetProperty("primaryCount").GetInt32());
            var oldIssued = callPlan.GetProperty("primaryOccurrences").EnumerateArray()
                .Select(value => (value.GetProperty("occurrence").GetString(), value.GetProperty("alias").GetString())).ToArray();
            Assert.Equal(p089.Primaries.Select(value => (value.Occurrence, value.Alias)), oldIssued);
        }
        var p041 = prepared.Single(value => value.DocumentId == "SRC-041");
        using (var prior = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/p6te-src041-e-challenge/g2a-request-manifest.v1.json"))))
        {
            Assert.Equal(p041.UserHash, prior.RootElement.GetProperty("userMessageSha256").GetString());
            Assert.Equal(p041.ProviderHash, prior.RootElement.GetProperty("providerRequestSha256").GetString());
            Assert.Equal(p041.ProviderBytes, prior.RootElement.GetProperty("providerRequestBytes").GetInt32());
            Assert.Equal(p041.Primaries.Count, prior.RootElement.GetProperty("issuedF1Establishes").GetInt32());
        }

        FreezeArtifact.AssertJson(OutputRoot, "g2a-full-pack-preflight.v1.json", new
        {
            schemaVersion = "v5-p6tg2a-full-pack-population-preflight-v1",
            status = "PREPARED_NOT_AUTHORIZED",
            sequence = new[] { "F1_FULL", "G2A_FULL_PACK_PREFLIGHT", "G2A_RAW_CAPTURE", "RAW_ONLY_G2A_X_H2_CENSUS", "GOLD_AUDIT", "H2_QUALIFICATION" },
            treatment = new
            {
                model = "qwen/qwen3.7-flash",
                provider = "alibaba",
                reasoning = new { enabled = true, effort = "OMITTED" },
                temperature = 0,
                route = "OPENROUTER_ALIBABA_PINNED",
            },
            cohort = prepared.Select(row => new
            {
                documentId = row.DocumentId,
                packId = row.Pack.PackId,
                sourceSha256 = row.Plan.SourceSha256,
                sourceUniverseSha256 = row.Plan.SourceUniverseSha256,
                f1 = new
                {
                    rawCapturePath = row.F1RawPath,
                    rawCaptureSha256 = row.F1RawSha256,
                    acceptedRawResponseSha256 = row.F1ResponseSha256,
                    semanticRequestSha256 = row.F1.Request.UserMessageSha256,
                    expectedTotalLedger = 96,
                    returnedTotalLedger = row.F1Result.Decisions.Count,
                    finishReason = row.F1FinishReason,
                    establishesStructure = row.F1Result.Decisions.Count(value => value.Function == OccurrenceFunction.EstablishesStructure),
                    representsStructure = row.F1Result.Decisions.Count(value => value.Function == OccurrenceFunction.RepresentsStructure),
                    other = row.F1Result.Decisions.Count(value => value.Function == OccurrenceFunction.Other),
                },
                ownedPack = new { occurrenceCount = row.Pack.OwnedAliases.Count, ownedAliases = row.Pack.OwnedAliases },
                issuedPrimaries = row.Primaries.Select(value => new
                {
                    occurrence = value.Occurrence,
                    alias = value.Alias,
                    ownedIndex = value.OwnedIndex,
                    ordinal = value.Ordinal,
                    page = value.Page,
                    function = "ESTABLISHES_STRUCTURE",
                }).ToArray(),
                g2a = new
                {
                    protocolVersion = "v5-function-conditioned-anchor-existence-1",
                    expectedLedgerCardinality = row.Primaries.Count,
                    systemPromptSha256 = Hash(SystemPrompt),
                    userMessageSha256 = row.UserHash,
                    userMessageUtf8Bytes = row.UserBytes,
                    providerBodySha256 = row.ProviderHash,
                    providerBodyBytes = row.ProviderBytes,
                    requestSha256 = row.ProviderHash,
                    maxCompletionTokens = row.Pack.MaxCompletionTokens,
                    status = "PREPARED_NOT_AUTHORIZED",
                },
            }).ToArray(),
            invariants = new
            {
                completeFiveDocumentCohortFrozenBeforeProvider = true,
                g2aOnlyForF1EstablishesOccurrences = true,
                allF1OwnedAliasesRemainAvailableAsNonSelectableLocalContext = true,
                noCandidateIdsOrExtents = true,
                noGoldOrSourceReview = true,
                h2NotComposedUntilAllG2ARawLedgersAreValid = true,
            },
            execution = new
            {
                status = "AWAITING_SEPARATE_PROVIDER_AUTHORIZATION",
                providerCalls = 0,
                maximumFuturePrimaryCalls = 5,
                retry = 0,
                repair = false,
                fallback = false,
                goldRead = false,
                goldMutation = "NONE",
                runtimeChanged = false,
                sharedRuntime = "UNCHANGED",
            },
            conclusion = "FIVE_G2A_FULL_PACK_REQUESTS_FROZEN;_NO_PROVIDER_EXECUTION;_H2_REQUESTS_NOT_YET_COMPOSED",
        });
    }

    private static Prepared Prepare(Source source)
    {
        var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(
            TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json"), source.DocumentId);
        var capturePath = TestRepository.Path($"{Root}/{source.F1Path}");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        var root = capture.RootElement;
        var f1Row = source.Kind switch
        {
            F1Kind.RawCapture => root,
            F1Kind.ResultRow => root.GetProperty("row"),
            F1Kind.ResultRows => root.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidOperationException("unknown-f1-kind"),
        };
        var packId = f1Row.GetProperty("packId").GetString()!;
        var pack = plan.Packs.Single(value => value.PackId == packId);
        Assert.Equal(96, pack.OwnedAliases.Count);
        var correspondences = source.F1UsedCorrespondences
            ? BuildCorrespondences(pack)
            : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        var f1 = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack, correspondences);
        var frozenF1RequestHash = f1Row.TryGetProperty("semanticRequestHash", out var semanticHash)
            ? semanticHash.GetString()
            : f1Row.GetProperty("userMessageSha256").GetString();
        Assert.Equal(frozenF1RequestHash, f1.Request.UserMessageSha256);
        Assert.Equal(96, f1.Request.Occurrences.Count);
        var response = f1Row.GetProperty("rawResponse").GetString()!;
        var finishReason = f1Row.GetProperty("finishReason").GetString()!;
        Assert.Equal("stop", finishReason);
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(f1, response);
        Assert.Equal(96, parsed.Decisions.Count);
        var atoms = plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var occurrenceByAlias = f1.Request.Occurrences.ToDictionary(value => value.Atom.Alias, value => value.Id, StringComparer.Ordinal);
        var ownedIndexByAlias = pack.OwnedAliases.Select((alias, index) => (alias, index)).ToDictionary(value => value.alias, value => value.index, StringComparer.Ordinal);
        var functions = parsed.Decisions.ToDictionary(value => value.OccurrenceId, value => value.Function, StringComparer.Ordinal);
        var primaries = f1.Request.Occurrences.Where(value => functions[value.Id] == OccurrenceFunction.EstablishesStructure)
            .OrderBy(value => value.Atom.Ordinal).ThenBy(value => value.Atom.Alias, StringComparer.Ordinal)
            .Select(value => new IssuedPrimary(value.Id, value.Atom.Alias, ownedIndexByAlias[value.Atom.Alias], value.Atom.Page, value.Atom.Ordinal)).ToArray();
        var ownedAtoms = pack.OwnedAliases.Select(alias => atoms[alias]).ToArray();
        var primaryRows = primaries.Select(primary =>
        {
            var atom = atoms[primary.Alias];
            object? Neighbor(int index)
            {
                if (index < 0 || index >= pack.OwnedAliases.Count) return null;
                var neighbor = atoms[pack.OwnedAliases[index]];
                return new { occurrence = occurrenceByAlias[neighbor.Alias], page = neighbor.Page, text = neighbor.Text, selectable = false };
            }
            return new
            {
                primary = primary.Occurrence,
                page = atom.Page,
                text = atom.Text,
                upstreamFunction = "ESTABLISHES_STRUCTURE",
                previous = Neighbor(primary.OwnedIndex - 1),
                next = Neighbor(primary.OwnedIndex + 1),
            };
        }).ToArray();
        var user = JsonSerializer.Serialize(new
        {
            protocolVersion = "v5-function-conditioned-anchor-existence-1",
            occurrences = primaryRows,
        });
        var request = new V5FreeHeadingRequestV1("v5-function-conditioned-anchor-existence-1", SystemPrompt, user,
            Hash(user), Encoding.UTF8.GetByteCount(SystemPrompt), Encoding.UTF8.GetByteCount(user));
        var body = PdfCandidateAuthorityQualificationAdapter.BuildProviderBodyReasoningEnabled(request, pack.MaxCompletionTokens);
        return new Prepared(source.DocumentId, plan, pack, f1, parsed, Rel(capturePath), Hash(File.ReadAllBytes(capturePath)), Hash(Encoding.UTF8.GetBytes(response)), finishReason,
            primaries, user, request.UserMessageSha256, request.UserMessageUtf8Bytes, body.PayloadBytes, body.Hash, body.Bytes);
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
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[key] = list;
        }
        return result;
    }

    private static string Rel(string path) => Path.GetRelativePath(TestRepository.Root(), path).Replace('\\', '/');
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
