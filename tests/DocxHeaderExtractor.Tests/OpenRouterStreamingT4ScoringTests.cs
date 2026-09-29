using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>Scores frozen T3B reasoning-none streamed responses after the T3C execution audit.</summary>
public sealed class OpenRouterStreamingT4ScoringTests
{
    private const string Root = "eval/a99-closed-loop/request-architecture-v2";
    private const string T3bPreflight = Root + "/openrouter-streaming-t3b-p05-reasoning-none-full-cohort-preflight.v1.json";
    private const string T3bSummary = Root + "/openrouter-streaming-t3b-reasoning-none-combined-summary.v1.json";
    private const string T3cAudit = Root + "/openrouter-streaming-t3c-efficiency-heavy-leaf-audit.v1.json";
    private const string P05MediumScore = Root + "/v4r2-p05-score.v1.json";
    private const string ScorerVersion = "SEMANTIC_FUNCTION_V4_EXACT_BIND_SCORER_V1";
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", SourcePdfCorpus.Src089),
        ("SRC-095", SourcePdfCorpus.Src095),
    ];

    private sealed record Candidate(
        int Ordinal,
        string Identity,
        ExactScorer.Span[] Spans,
        string Text,
        string Function,
        bool Member);

    [Fact]
    public void Freeze_t4_t3b_reasoning_none_gold_score()
    {
        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(T3bPreflight)));
        Assert.Equal("T3B_P05_STREAMING_REASONING_NONE_FULL_31_PACED", preflight.RootElement.GetProperty("candidateId").GetString());
        Assert.Equal("none", preflight.RootElement.GetProperty("pins").GetProperty("reasoningEffort").GetString());
        Assert.True(preflight.RootElement.GetProperty("pins").GetProperty("stream").GetBoolean());
        Assert.True(preflight.RootElement.GetProperty("semanticIdentity").GetProperty("semanticRequestHashUnchangedFromP05").GetBoolean());
        Assert.True(preflight.RootElement.GetProperty("semanticIdentity").GetProperty("goldClosed").GetBoolean());

        using var t3c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(T3cAudit)));
        Assert.Equal("T3C_PROVIDER_FREE_AUDIT_COMPLETE", t3c.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, t3c.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(t3c.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal(31, t3c.RootElement.GetProperty("summary").GetProperty("acceptedLeaves").GetInt32());
        Assert.Equal(31, t3c.RootElement.GetProperty("summary").GetProperty("usableAfterTransportRetry").GetInt32());

        var auditRows = t3c.RootElement.GetProperty("rows").EnumerateArray().Select(row => new
        {
            Ordinal = row.GetProperty("ordinal").GetInt32(),
            DocumentId = row.GetProperty("documentId").GetString()!,
            SemanticRequestHash = row.GetProperty("semanticRequestHash").GetString()!,
            ProviderEnvelopeHash = row.GetProperty("providerEnvelopeHash").GetString()!,
            AcceptedRun = row.GetProperty("acceptedRun").GetString()!,
            AcceptedRequestFile = row.GetProperty("acceptedRequestFile").GetString()!,
            AcceptedContentFile = row.GetProperty("acceptedContentFile").GetString()!,
            PromptTokens = row.GetProperty("promptTokens").GetInt32(),
            CompletionTokens = row.GetProperty("completionTokens").GetInt32(),
            ReasoningTokens = row.GetProperty("reasoningTokens").GetInt32(),
            WallClockMs = row.GetProperty("wallClockMs").GetInt32(),
            FirstContentMs = row.GetProperty("firstContentMs").GetInt32(),
            TransportRetryCountFromLogs = row.GetProperty("transportRetryCountFromLogs").GetInt32(),
        }).OrderBy(row => row.Ordinal).ToArray();
        Assert.Equal(31, auditRows.Length);

        var rows = new List<object>();
        var documentSummaries = new Dictionary<string, (int Gold, int Tp, int Fp, int Fn, double F1)>(StringComparer.Ordinal);
        foreach (var (id, pdf) in Documents)
        {
            var documentRows = auditRows.Where(row => row.DocumentId == id).ToArray();
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms;
            var decisions = Bind(documentRows, atoms, out var refusals, out var invalidResponses, out var ordinalOwnership);

            var goldPath = TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json");
            var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(pdf));
            var gold = ExactScorer.ReadGold(goldPath, universe);
            var goldIds = gold.Select(claim => claim.Identity).ToHashSet(StringComparer.Ordinal);
            var members = decisions.Where(d => d.Member).GroupBy(d => d.Identity, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            var hypotheses = members.Select((d, i) => new ExactScorer.Hypothesis(i, d.Text, "TRUE", [], null, null, [], "TITLE", null,
                [$"semanticFunction={d.Function}"], d.Identity, null, d.Spans)).ToArray();
            var score = ExactScorer.Compute(gold, hypotheses);
            using var goldJson = JsonDocument.Parse(File.ReadAllText(goldPath));
            var patterns = goldJson.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .ToDictionary(c => c.GetProperty("identity").GetString()!, c => c.GetProperty("pattern").GetString()!, StringComparer.Ordinal);

            var perOrdinal = documentRows.Select(row =>
            {
                var rowDecisions = decisions.Where(d => d.Ordinal == row.Ordinal).ToArray();
                var rowMembers = rowDecisions.Where(d => d.Member).GroupBy(d => d.Identity, StringComparer.Ordinal).Select(g => g.First()).ToArray();
                var rowTp = rowMembers.Count(d => goldIds.Contains(d.Identity));
                var rowFp = rowMembers.Count(d => !goldIds.Contains(d.Identity));
                var ownedGold = gold.Where(g => ordinalOwnership.TryGetValue(g.Identity, out var owner) && owner == row.Ordinal).ToArray();
                var rowFn = ownedGold.Count(g => !members.Any(m => m.Identity == g.Identity));
                return new
                {
                    ordinal = row.Ordinal,
                    row.DocumentId,
                    promptTokens = row.PromptTokens,
                    completionTokens = row.CompletionTokens,
                    reasoningTokens = row.ReasoningTokens,
                    firstContentMs = row.FirstContentMs,
                    wallClockMs = row.WallClockMs,
                    transportRetries = row.TransportRetryCountFromLogs,
                    uniqueBoundDecisions = rowDecisions.Select(d => d.Identity).Distinct(StringComparer.Ordinal).Count(),
                    memberClaims = rowMembers.Length,
                    truePositives = rowTp,
                    falsePositives = rowFp,
                    ownedGoldClaims = ownedGold.Length,
                    falseNegativesByOwnedGold = rowFn,
                    semanticRequestHash = row.SemanticRequestHash,
                    providerEnvelopeHash = row.ProviderEnvelopeHash,
                };
            }).ToArray();

            rows.Add(new
            {
                documentId = id,
                gold = new
                {
                    path = $"eval/a99-closed-loop/gold/{id}.gold.json",
                    sha256 = CanonicalArtifactHash.OfTextFile(goldPath),
                    authorityId = goldJson.RootElement.GetProperty("authorityId").GetString(),
                    userFinalApproval = goldJson.RootElement.GetProperty("approval").GetProperty("userFinalApproval").GetBoolean(),
                },
                rawCalls = documentRows.Length,
                invalidResponses,
                refusals,
                uniqueBoundDecisions = decisions.Select(d => d.Identity).Distinct(StringComparer.Ordinal).Count(),
                uniqueMemberClaims = hypotheses.Length,
                headline = score.Headline(),
                residuals = score.Residuals(),
                exactByGoldPattern = patterns.GroupBy(x => x.Value).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new
                {
                    gold = g.Count(),
                    exact = g.Count(x => score.Rows.Any(r => r.Claim.Identity == x.Key && r.Bucket == "EXACT_TRUE")),
                }),
                src089 = id == "SRC-089" ? new
                {
                    articleExact = Exact(patterns, score, "ARTICLE_HEADING"),
                    chapterExact = Exact(patterns, score, "S089_Q2_CHAPTER_LABEL_OVER_TITLE"),
                    titleBlockExact = Exact(patterns, score, "S089_Q1_DECREE_TITLE_BLOCK"),
                    clauseLabelsExact = Exact(patterns, score, "S089_Q3_COLON_CLAUSE_LABEL"),
                } : null,
                src095 = id == "SRC-095" ? new
                {
                    falsePositiveFamilies = FalsePositiveFamilies(id, score, goldJson.RootElement),
                    falsePositiveBreakdown = FalsePositiveBreakdown(id, score, goldJson.RootElement),
                    numberedSectionRecall = PatternRecall(patterns, score, ["NUMBERED_SECTION", "NUMBERED_SECTION_SPLIT"]),
                    rfcTitleIdentifierExact = Exact(patterns, score, "S095_Q1_RFC_TITLE_BLOCK"),
                    indexGroupLettersExact = Exact(patterns, score, "S095_Q2_INDEX_GROUP_LETTER"),
                } : null,
                byOrdinal = perOrdinal,
                bySemanticFunction = decisions.GroupBy(d => new { goldHeading = goldIds.Contains(d.Identity), d.Function })
                    .OrderBy(g => g.Key.goldHeading ? 0 : 1).ThenBy(g => g.Key.Function, StringComparer.Ordinal)
                    .Select(g => new { g.Key.goldHeading, semanticFunction = g.Key.Function, occurrences = g.Select(x => x.Identity).Distinct(StringComparer.Ordinal).Count() }).ToArray(),
                functionConfusionMatrix = new
                {
                    semanticFunctions = SemanticFunctionMembershipContractV1.Functions,
                    goldHeading = SemanticFunctionMembershipContractV1.Functions.ToDictionary(function => function,
                        function => decisions.Where(d => d.Function == function && goldIds.Contains(d.Identity)).Select(d => d.Identity).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal),
                    nonGold = SemanticFunctionMembershipContractV1.Functions.ToDictionary(function => function,
                        function => decisions.Where(d => d.Function == function && !goldIds.Contains(d.Identity)).Select(d => d.Identity).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal),
                    goldUnemitted = goldIds.Count(identity => !decisions.Any(d => d.Identity == identity)),
                },
            });

            using var headlineJson = JsonDocument.Parse(JsonSerializer.Serialize(score.Headline(), FreezeArtifact.Json));
            documentSummaries[id] = (
                headlineJson.RootElement.GetProperty("goldClaims").GetInt32(),
                headlineJson.RootElement.GetProperty("truePositives").GetInt32(),
                headlineJson.RootElement.GetProperty("falsePositives").GetInt32(),
                headlineJson.RootElement.GetProperty("falseNegatives").GetInt32(),
                headlineJson.RootElement.GetProperty("f1").GetDouble());
        }

        using var p05Score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(P05MediumScore)));
        var p05ByDocument = p05Score.RootElement.GetProperty("documents").EnumerateArray()
            .ToDictionary(d => d.GetProperty("DocumentId").GetString()!, d => d.GetProperty("Headline").Clone(), StringComparer.Ordinal);

        var delta = Documents.Select(doc =>
        {
            var none = documentSummaries[doc.Id];
            var medium = p05ByDocument[doc.Id];
            return new
            {
                documentId = doc.Id,
                none = new { gold = none.Gold, tp = none.Tp, fp = none.Fp, fn = none.Fn, f1 = none.F1 },
                medium = new
                {
                    gold = medium.GetProperty("goldClaims").GetInt32(),
                    tp = medium.GetProperty("truePositives").GetInt32(),
                    fp = medium.GetProperty("falsePositives").GetInt32(),
                    fn = medium.GetProperty("falseNegatives").GetInt32(),
                    f1 = medium.GetProperty("f1").GetDouble(),
                },
                delta = new
                {
                    tp = none.Tp - medium.GetProperty("truePositives").GetInt32(),
                    fp = none.Fp - medium.GetProperty("falsePositives").GetInt32(),
                    fn = none.Fn - medium.GetProperty("falseNegatives").GetInt32(),
                    f1 = Math.Round(none.F1 - medium.GetProperty("f1").GetDouble(), 4),
                },
            };
        }).ToArray();

        var totalNoneTp = documentSummaries.Values.Sum(x => x.Tp);
        var totalNoneFp = documentSummaries.Values.Sum(x => x.Fp);
        var totalNoneFn = documentSummaries.Values.Sum(x => x.Fn);
        var totalMediumTp = p05ByDocument.Values.Sum(x => x.GetProperty("truePositives").GetInt32());
        var totalMediumFp = p05ByDocument.Values.Sum(x => x.GetProperty("falsePositives").GetInt32());
        var totalMediumFn = p05ByDocument.Values.Sum(x => x.GetProperty("falseNegatives").GetInt32());

        FreezeArtifact.AssertJson(Root, "openrouter-streaming-t4-t3b-reasoning-none-score.v1.json", new
        {
            artifactKind = "a99_openrouter_streaming_t4_t3b_reasoning_none_score",
            status = "T4_GOLD_SCORE_COMPLETE",
            study = "OPENROUTER_STREAMING_TRANSPORT_V1_REASONING_NONE",
            candidateId = "T3B_P05_STREAMING_REASONING_NONE_FULL_31_PACED",
            scoringAuthority = T3cAudit,
            scorer = new
            {
                version = ScorerVersion,
                exactMatcher = ExactScorer.ScorerId,
                sourceSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path("tests/DocxHeaderExtractor.Tests/OpenRouterStreamingT4ScoringTests.cs")),
                providerCallsDuringScoring = 0,
            },
            lineage = new
            {
                t3bPreflight = new { path = T3bPreflight, sha256 = Hash(T3bPreflight) },
                t3bSummary = new { path = T3bSummary, sha256 = Hash(T3bSummary) },
                t3cAudit = new { path = T3cAudit, sha256 = Hash(T3cAudit) },
                p05MediumComparator = new { path = P05MediumScore, sha256 = Hash(P05MediumScore) },
            },
            protocol = new
            {
                semanticRequestHashesUnchangedFromP05 = true,
                packing = "P05_RESOURCE_BOUNDED_SOURCE_PACKING_V1",
                model = "qwen/qwen3.7-flash",
                providerRoute = "Alibaba",
                reasoning = "none",
                stream = true,
                usageInclude = true,
                responseFormat = "json_object",
                goldOpenedAfterExecutionFreeze = true,
                providerCalls = 0,
                productionPromotion = false,
            },
            execution = new
            {
                acceptedLeaves = 31,
                acceptedProviderCalls = 31,
                observedTransportRetryEvents = t3c.RootElement.GetProperty("summary").GetProperty("observedTransportRetryEvents").GetInt32(),
                observedTotalProviderAttemptsIncludingTransportErrors = t3c.RootElement.GetProperty("summary").GetProperty("observedTotalProviderAttemptsIncludingTransportErrors").GetInt32(),
                semanticRecoveryCalls = 0,
                mediumFallbackCalls = 0,
                totalPromptTokens = t3c.RootElement.GetProperty("summary").GetProperty("totalPromptTokens").GetInt32(),
                totalCompletionTokens = t3c.RootElement.GetProperty("summary").GetProperty("totalCompletionTokens").GetInt32(),
                totalReasoningTokens = t3c.RootElement.GetProperty("summary").GetProperty("totalReasoningTokens").GetInt32(),
            },
            aggregateDeltaVsP05Medium = new
            {
                none = new { tp = totalNoneTp, fp = totalNoneFp, fn = totalNoneFn, f1 = F1(totalNoneTp, totalNoneFp, totalNoneFn) },
                medium = new { tp = totalMediumTp, fp = totalMediumFp, fn = totalMediumFn, f1 = F1(totalMediumTp, totalMediumFp, totalMediumFn) },
                delta = new
                {
                    tp = totalNoneTp - totalMediumTp,
                    fp = totalNoneFp - totalMediumFp,
                    fn = totalNoneFn - totalMediumFn,
                    f1 = Math.Round(F1(totalNoneTp, totalNoneFp, totalNoneFn) - F1(totalMediumTp, totalMediumFp, totalMediumFn), 4),
                },
            },
            documentDeltaVsP05Medium = delta,
            documents = rows,
            goldRead = true,
            providerCalls = 0,
            productionPromotion = false,
        });
    }

    private static List<Candidate> Bind(
        IReadOnlyList<dynamic> rows,
        IReadOnlyList<SemanticSourceAtom> atoms,
        out Dictionary<string, int> refusals,
        out int invalidResponses,
        out Dictionary<string, int> ordinalOwnershipByGoldIdentity)
    {
        var result = new List<Candidate>();
        refusals = new(StringComparer.Ordinal);
        invalidResponses = 0;
        ordinalOwnershipByGoldIdentity = new(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var runDir = row.AcceptedRun == "initial"
                ? "openrouter-streaming-t3b-reasoning-none-full-cohort-20260928T091634Z"
                : "openrouter-streaming-t3b-reasoning-none-full-cohort-continuation-003-031-20260928T091819Z";
            var requestPath = TestRepository.Path($"{Root}/{runDir}/{row.AcceptedRequestFile}");
            var contentPath = TestRepository.Path($"{Root}/{runDir}/{row.AcceptedContentFile}");
            using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
            var userMessage = request.RootElement.GetProperty("messages").EnumerateArray()
                .First(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            var split = userMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
            using var packet = JsonDocument.Parse(userMessage[..split]);
            var owned = packet.RootElement.GetProperty("sourceEvidence").EnumerateArray()
                .Where(x => x.GetProperty("owned").GetBoolean())
                .Select(x => x.GetProperty("alias").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var ownedIdentity in BuildWholeAliasOwnedIdentities(owned, atoms))
                ordinalOwnershipByGoldIdentity.TryAdd(ownedIdentity, row.Ordinal);

            using var response = JsonDocument.Parse(File.ReadAllText(contentPath));
            if (SemanticFunctionMembershipContractV1.ValidateJson(response.RootElement).Count > 0)
            {
                invalidResponses++;
                continue;
            }

            foreach (var entry in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                var decoded = SemanticFunctionMembershipContractV1.Decode(entry);
                if (decoded.Proposals.Count != 1) { Count(refusals, "DECODE"); continue; }
                var p = decoded.Proposals[0];
                if (p.SourceParts is null) { Count(refusals, "DECODE"); continue; }
                if (p.SourceParts.Any(x => !owned.Contains(x.SourceAlias))) { Count(refusals, "OUT_OF_OWNED_SEGMENT"); continue; }
                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, p.SourceParts);
                if (!canonical.IsCanonical) { Count(refusals, canonical.Status.ToString()); continue; }
                var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (!bound.IsBound) { Count(refusals, bound.Status.ToString()); continue; }
                var function = entry.GetProperty("semanticFunction").GetString()!;
                result.Add(new Candidate(row.Ordinal, bound.Identity, bound.Parts.Select(x => new ExactScorer.Span(x.Alias, x.Start, x.End)).ToArray(),
                    string.Join(" ", bound.Parts.Select(x => x.Text)), function, SemanticFunctionMembershipContractV1.IsMember(function)));
            }
        }
        return result;
    }

    private static IEnumerable<string> BuildWholeAliasOwnedIdentities(HashSet<string> owned, IReadOnlyList<SemanticSourceAtom> atoms)
    {
        foreach (var atom in atoms)
        {
            if (!owned.Contains(atom.Alias)) continue;
            yield return ExactScorer.Identity([new ExactScorer.Span(atom.Alias, 0, atom.Text.Length)]);
        }
    }

    private static void Count(Dictionary<string, int> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;
    private static object Exact(Dictionary<string, string> patterns, ExactScorer.Score score, string pattern) => new { gold = patterns.Count(x => x.Value == pattern), exact = patterns.Count(x => x.Value == pattern && score.Rows.Any(r => r.Claim.Identity == x.Key && r.Bucket == "EXACT_TRUE")) };
    private static object PatternRecall(Dictionary<string, string> patterns, ExactScorer.Score score, string[] wanted) { var ids = patterns.Where(x => wanted.Contains(x.Value, StringComparer.Ordinal)).Select(x => x.Key).ToArray(); var exact = ids.Count(id => score.Rows.Any(r => r.Claim.Identity == id && r.Bucket == "EXACT_TRUE")); return new { gold = ids.Length, exact, recall = Math.Round((double)exact / ids.Length, 4) }; }
    private static Dictionary<string, int> FalsePositiveFamilies(string id, ExactScorer.Score score, JsonElement gold) { var pages = ResidualFamilies.PageOfAlias(id); var goldAliases = gold.GetProperty("occurrence").GetProperty("claims").EnumerateArray().SelectMany(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!)).ToHashSet(StringComparer.Ordinal); var reviewed = ResidualFamilies.ReviewedNonHeadings(id); return score.FalsePositives.Select(h => ResidualFamilies.FalsePositiveFamily(h.Identity!, h.Text, h.Evidence[0]["semanticFunction=".Length..], ResidualFamilies.PageOf(pages, h.Identity!), (2, 4), 54, goldAliases, reviewed)).GroupBy(x => x).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()); }
    private static object FalsePositiveBreakdown(string id, ExactScorer.Score score, JsonElement gold)
    {
        var families = FalsePositiveFamilies(id, score, gold);
        int Sum(Func<string, bool> predicate) => families.Where(x => predicate(x.Key)).Sum(x => x.Value);
        var contents = Sum(x => x == "CONTENTS_ENTRY" || x.Contains("CONTENTS_ENTRY", StringComparison.Ordinal));
        var index = Sum(x => x == "INDEX_ENTRY" || x.Contains("INDEX_ENTRY", StringComparison.Ordinal));
        var furniture = Sum(x => x == "PAGE_FURNITURE" || x.Contains("PAGE_FURNITURE", StringComparison.Ordinal));
        var captions = Sum(x => x == "CAPTION" || x.Contains("CAPTION", StringComparison.Ordinal));
        var table = Sum(x => x.Contains("TABLE", StringComparison.Ordinal));
        var wrongExtent = Sum(x => x == "GOLD_HEADING_WRONG_EXTENT");
        return new { contentsEntries = contents, indexEntries = index, pageFurniture = furniture, captions, tableStructure = table, goldHeadingWrongExtent = wrongExtent, other = score.FalsePositives.Length - contents - index - furniture - captions - table - wrongExtent };
    }
    private static double F1(int tp, int fp, int fn)
    {
        var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return Math.Round(precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall), 4);
    }
    private static string Hash(string path) => CanonicalArtifactHash.OfTextFile(TestRepository.Path(path));
    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
