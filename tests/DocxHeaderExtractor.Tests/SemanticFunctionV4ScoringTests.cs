using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>Scores the immutable V4 responses once, after the combined lineage gate opens Gold.</summary>
public sealed class SemanticFunctionV4ScoringTests
{
    private const string Root = "eval/a99-closed-loop/semantic-function-single-authority-v4";
    private const string Preflight = Root + "/preflight.v1.json";
    private const string Manifest = Root + "/combined-run-manifest.v1.json";
    private const string Attempt1 = Root + "/run.v1.json";
    private const string Continuation = Root + "/continuation.v1.json";
    private const string ScorerVersion = "SEMANTIC_FUNCTION_V4_EXACT_BIND_SCORER_V1";
    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf),
    ];
    private static readonly CanonicalSemanticExperiment V4 = CanonicalSemanticExperiment.Baseline with
    { RequestVersion = SemanticRequestVersion.V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY };

    private sealed record Candidate(string Identity, ExactScorer.Span[] Spans, string Text, string Function, bool Member);

    [Fact]
    public async Task Freeze_the_v4_exact_bind_score_once()
    {
        // All lineage and protocol gates run before the first Gold path is constructed.
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Manifest)));
        var root = manifest.RootElement;
        Assert.Equal("COMPLETE_25_OF_25", root.GetProperty("status").GetString());
        var attempts = root.GetProperty("attempts");
        Assert.Equal(25, attempts.GetProperty("successful").GetInt32());
        Assert.Equal(0, attempts.GetProperty("pending").GetInt32());
        Assert.Equal(0, attempts.GetProperty("duplicateSuccessful").GetInt32());
        Assert.Equal(0, attempts.GetProperty("unexpected").GetInt32());
        Assert.False(root.GetProperty("invariants").GetProperty("goldRead").GetBoolean());
        Assert.Equal(Hash(Preflight), root.GetProperty("lineage").GetProperty("preflightSha256").GetString());
        Assert.Equal(Hash(Attempt1), root.GetProperty("lineage").GetProperty("attempt1Sha256").GetString());
        Assert.Equal(Hash(Continuation), root.GetProperty("lineage").GetProperty("continuationSha256").GetString());

        using var preflight = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Preflight)));
        Assert.Equal("V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY", preflight.RootElement.GetProperty("requestVersion").GetString());
        Assert.Equal("e996bef4346efff9b0544f34777192d5b59c7e7f75a749dbf91cbe9f0d5df93b", preflight.RootElement.GetProperty("prompt").GetProperty("sha256").GetString());
        Assert.Equal("7d8ae805c0373dad3795d819cd5b0229b9421c28600d5d6b001e818f839225db", preflight.RootElement.GetProperty("schema").GetProperty("sha256").GetString());
        Assert.Equal("V3_RobustGlyphStatistics", preflight.RootElement.GetProperty("factsVersion").GetString());
        Assert.Equal("FIXED_OWNED_COUNT_120", preflight.RootElement.GetProperty("packing").GetProperty("policy").GetString());
        Assert.Equal("qwen/qwen3.7-flash", preflight.RootElement.GetProperty("model").GetProperty("identity").GetString());

        var calls = SuccessfulCalls();
        Assert.Equal(25, calls.Count); Assert.Equal(25, calls.Select(c => c.Hash).Distinct(StringComparer.Ordinal).Count());
        var rows = new List<object>();
        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), capture, CancellationToken.None,
                experiment: V4, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
                packingPolicy: SemanticEvidencePackingPolicies.FixedOwnedCount120,
                sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics, runPlacement: false);
            var documentCalls = calls.Where(c => c.Document == id).ToArray();
            Assert.Equal(capture.Requests.Count, documentCalls.Length);
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf), PdfSourceFactsVersion.V3_RobustGlyphStatistics).Atoms;
            var decisions = Bind(documentCalls, capture.Requests, atoms, out var refusals, out var invalidResponses);

            // Gate passed: Gold opens here, and nowhere above.
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

            rows.Add(new
            {
                documentId = id,
                gold = new { path = $"eval/a99-closed-loop/gold/{id}.gold.json", sha256 = CanonicalArtifactHash.OfTextFile(goldPath), authorityId = goldJson.RootElement.GetProperty("authorityId").GetString(), userFinalApproval = goldJson.RootElement.GetProperty("approval").GetProperty("userFinalApproval").GetBoolean() },
                rawCalls = documentCalls.Length,
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
                functionConfusion = decisions.GroupBy(d => new { goldHeading = goldIds.Contains(d.Identity), d.Function })
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
        }

        FreezeArtifact.AssertJson(Root, "score.v1.json", new
        {
            artifactKind = "a99_semantic_function_v4_exact_bind_score",
            study = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY",
            scorer = new { version = ScorerVersion, sourceSha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path("tests/DocxHeaderExtractor.Tests/SemanticFunctionV4ScoringTests.cs")), exactMatcher = ExactScorer.ScorerId },
            modelProviderCalls = 0,
            combinedManifest = new { path = Manifest, sha256 = Hash(Manifest), status = "COMPLETE_25_OF_25" },
            rawLineage = new { preflightSha256 = Hash(Preflight), attempt1Sha256 = Hash(Attempt1), continuationSha256 = Hash(Continuation) },
            protocol = new { requestVersion = "V4_SEMANTIC_FUNCTION_SINGLE_AUTHORITY", promptSha256 = "e996bef4346efff9b0544f34777192d5b59c7e7f75a749dbf91cbe9f0d5df93b", schemaSha256 = "7d8ae805c0373dad3795d819cd5b0229b9421c28600d5d6b001e818f839225db", factsVersion = "V3_RobustGlyphStatistics", packing = "FIXED_OWNED_COUNT_120", model = "qwen/qwen3.7-flash" },
            invariants = new { sourcePartsMutated=false, claimsMergedOrSplit=false, legacySemanticRoleUsed=false, heuristicOrPostFilterUsed=false, documentSpecificMembershipRuleUsed=false, taxonomyChangedAfterGold=false },
            baselineV2 = new { SRC_089 = new { f1=0.7567, articles="23/26", chapters="5/5" }, SRC_095 = new { f1=0.5266, recall=0.9126, tocFalsePositives=87 } },
            documents = rows,
        });
    }

    private sealed record RawCall(string Document, string Hash, string Response);
    private static List<RawCall> SuccessfulCalls()
    {
        var result = new List<RawCall>();
        foreach (var path in new[] { Attempt1, Continuation })
        {
            using var artifact = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
            foreach (var call in artifact.RootElement.GetProperty("ledger").EnumerateArray())
                if (call.GetProperty("Error").ValueKind == JsonValueKind.Null && call.GetProperty("Response").ValueKind == JsonValueKind.String)
                    result.Add(new(call.GetProperty("DocumentId").GetString()!, call.GetProperty("RequestSha256").GetString()!, call.GetProperty("Response").GetString()!));
        }
        return result;
    }

    private static List<Candidate> Bind(IReadOnlyList<RawCall> calls, IReadOnlyList<CapturedRequest> requests,
        IReadOnlyList<SemanticSourceAtom> atoms, out Dictionary<string,int> refusals, out int invalidResponses)
    {
        var result = new List<Candidate>(); refusals = new(StringComparer.Ordinal); invalidResponses = 0;
        for (var i=0;i<calls.Count;i++)
        {
            Assert.Equal(Sha(requests[i].UserMessage), calls[i].Hash);
            var split=requests[i].UserMessage.IndexOf("\nSCHEMA=",StringComparison.Ordinal);
            using var packet=JsonDocument.Parse(requests[i].UserMessage[..split]);
            var owned=packet.RootElement.GetProperty("sourceEvidence").EnumerateArray().Where(x=>x.GetProperty("owned").GetBoolean()).Select(x=>x.GetProperty("alias").GetString()!).ToHashSet(StringComparer.Ordinal);
            using var response=JsonDocument.Parse(calls[i].Response);
            if(SemanticFunctionMembershipContractV1.ValidateJson(response.RootElement).Count>0){invalidResponses++;continue;}
            foreach(var entry in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                var decoded=SemanticFunctionMembershipContractV1.Decode(entry); if(decoded.Proposals.Count!=1){Count(refusals,"DECODE");continue;}
                var p=decoded.Proposals[0]; if(p.SourceParts is null){Count(refusals,"DECODE");continue;} if(p.SourceParts.Any(x=>!owned.Contains(x.SourceAlias))){Count(refusals,"OUT_OF_OWNED_SEGMENT");continue;}
                var canonical=SemanticSourcePartCanonicalizer.Canonicalize(atoms,p.SourceParts); if(!canonical.IsCanonical){Count(refusals,canonical.Status.ToString());continue;}
                var bound=SemanticSourcePartBinder.Bind(atoms,new SemanticSourcePartsProposal(canonical.Parts)); if(!bound.IsBound){Count(refusals,bound.Status.ToString());continue;}
                var function=entry.GetProperty("semanticFunction").GetString()!;
                result.Add(new(bound.Identity,bound.Parts.Select(x=>new ExactScorer.Span(x.Alias,x.Start,x.End)).ToArray(),string.Join(" ",bound.Parts.Select(x=>x.Text)),function,SemanticFunctionMembershipContractV1.IsMember(function)));
            }
        }
        return result;
    }
    private static void Count(Dictionary<string,int> map,string key)=>map[key]=map.GetValueOrDefault(key)+1;
    private static object Exact(Dictionary<string,string> patterns,ExactScorer.Score score,string pattern)=>new { gold=patterns.Count(x=>x.Value==pattern), exact=patterns.Count(x=>x.Value==pattern&&score.Rows.Any(r=>r.Claim.Identity==x.Key&&r.Bucket=="EXACT_TRUE")) };
    private static object PatternRecall(Dictionary<string,string> patterns,ExactScorer.Score score,string[] wanted){var ids=patterns.Where(x=>wanted.Contains(x.Value,StringComparer.Ordinal)).Select(x=>x.Key).ToArray();var exact=ids.Count(id=>score.Rows.Any(r=>r.Claim.Identity==id&&r.Bucket=="EXACT_TRUE"));return new { gold=ids.Length,exact,recall=Math.Round((double)exact/ids.Length,4) };}
    private static Dictionary<string,int> FalsePositiveFamilies(string id,ExactScorer.Score score,JsonElement gold){var pages=LlmSemanticPilotV1AnalysisTests.PageOfAlias(id);var goldAliases=gold.GetProperty("occurrence").GetProperty("claims").EnumerateArray().SelectMany(c=>c.GetProperty("sourceParts").EnumerateArray().Select(p=>p.GetProperty("sourceAlias").GetString()!)).ToHashSet(StringComparer.Ordinal);var reviewed=LlmSemanticPilotV1AnalysisTests.ReviewedNonHeadings(id);return score.FalsePositives.Select(h=>LlmSemanticPilotV1AnalysisTests.FalsePositiveFamily(h.Identity!,h.Text,h.Evidence[0]["semanticFunction=".Length..],LlmSemanticPilotV1AnalysisTests.PageOf(pages,h.Identity!),(2,4),54,goldAliases,reviewed)).GroupBy(x=>x).OrderByDescending(g=>g.Count()).ToDictionary(g=>g.Key,g=>g.Count());}
    private static object FalsePositiveBreakdown(string id,ExactScorer.Score score,JsonElement gold)
    {
        var families = FalsePositiveFamilies(id, score, gold);
        int Sum(Func<string, bool> predicate) => families.Where(x => predicate(x.Key)).Sum(x => x.Value);
        var contents = Sum(x => x == "CONTENTS_ENTRY");
        var index = Sum(x => x == "INDEX_ENTRY");
        var furniture = Sum(x => x == "PAGE_FURNITURE" || x.Contains("PAGE_FURNITURE", StringComparison.Ordinal));
        var captions = Sum(x => x == "CAPTION" || x.Contains("CAPTION", StringComparison.Ordinal));
        var table = Sum(x => x.Contains("TABLE", StringComparison.Ordinal));
        var wrongExtent = Sum(x => x == "GOLD_HEADING_WRONG_EXTENT");
        return new
        {
            contentsEntries = contents,
            indexEntries = index,
            pageFurniture = furniture,
            captions,
            tableStructure = table,
            goldHeadingWrongExtent = wrongExtent,
            other = score.FalsePositives.Length - contents - index - furniture - captions - table - wrongExtent,
        };
    }
    private static string Hash(string path)=>CanonicalArtifactHash.OfTextFile(TestRepository.Path(path));
    private static string Sha(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
