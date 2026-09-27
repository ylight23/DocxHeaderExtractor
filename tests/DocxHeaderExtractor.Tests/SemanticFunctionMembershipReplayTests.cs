using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Offline-only measurement of the ceiling of a single semantic-membership authority. It consumes
/// the immutable raw V2 and V3 replies, derives membership solely from the model-written role,
/// then sends the unchanged source parts through the existing canonicalizer and exact binder.
/// No source fact, Gold value, document identity, page, diagnostic, or candidate ranking participates
/// in membership projection. Gold opens only after the candidate set is complete, for scoring.
/// </summary>
public sealed class SemanticFunctionMembershipReplayTests
{
    private const string Dir = "eval/a99-closed-loop/semantic-function-membership-replay-v1";
    private const string V2 = "eval/a99-closed-loop/llm-semantic-pilot-v1";
    private const string V3 = "eval/a99-closed-loop/llm-semantic-arm-v3-exclusion-v1";

    private static readonly (string Id, string Pdf)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf),
    ];

    // This is the whole experiment: a frozen, role-string-only legacy bridge to the proposed
    // closed function ontology. "heading" deliberately has no bridge; accepting it would retain
    // the free-form escape that the proposed contract forbids. It is therefore measurable as
    // UNMAPPABLE rather than silently treated as structure.
    private static readonly IReadOnlyDictionary<string, SemanticFunction> RoleMap =
        new Dictionary<string, SemanticFunction>(StringComparer.OrdinalIgnoreCase)
        {
            ["article"] = SemanticFunction.StructuralUnit,
            ["article-heading"] = SemanticFunction.StructuralUnit,
            ["chapter"] = SemanticFunction.StructuralUnit,
            ["section"] = SemanticFunction.StructuralUnit,
            ["section-heading"] = SemanticFunction.StructuralUnit,
            ["subsection"] = SemanticFunction.StructuralUnit,
            ["subsubsection"] = SemanticFunction.StructuralUnit,
            ["definition-term"] = SemanticFunction.StructuralUnit,

            ["document-title"] = SemanticFunction.DocumentIdentity,
            ["document-subtitle"] = SemanticFunction.DocumentIdentity,
            ["subtitle"] = SemanticFunction.DocumentIdentity,

            ["running-header"] = SemanticFunction.NonStructural,
            ["running-footer"] = SemanticFunction.NonStructural,
            ["page-header"] = SemanticFunction.NonStructural,
            ["page-footer"] = SemanticFunction.NonStructural,
            ["page-number"] = SemanticFunction.NonStructural,
            ["page_furniture"] = SemanticFunction.NonStructural,
            ["footnote"] = SemanticFunction.NonStructural,
            ["source-note"] = SemanticFunction.NonStructural,
            ["index-entry"] = SemanticFunction.NonStructural,
            ["toc-entry"] = SemanticFunction.NonStructural,
            ["table-of-contents-entry"] = SemanticFunction.NonStructural,
            ["table-caption"] = SemanticFunction.NonStructural,
            ["table-header"] = SemanticFunction.NonStructural,
            ["table-label"] = SemanticFunction.NonStructural,
            ["figure-caption"] = SemanticFunction.NonStructural,
            ["figure-label"] = SemanticFunction.NonStructural,
            ["caption"] = SemanticFunction.NonStructural,
            ["signature-label"] = SemanticFunction.NonStructural,
            ["signature-value"] = SemanticFunction.NonStructural,
        };

    private enum SemanticFunction
    {
        Unmappable,
        StructuralUnit,
        DocumentIdentity,
        NonStructural,
    }

    private sealed record BoundCandidate(
        string Identity,
        ExactScorer.Span[] Spans,
        string Text,
        string Role,
        SemanticFunction Function,
        bool BaselineMembership,
        string PartsFingerprint);

    [Fact]
    public async Task Freeze_the_offline_semantic_function_membership_replay()
    {
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_PILOT_RUN") is "1" or "true" or "TRUE");
        Assert.False(Environment.GetEnvironmentVariable("A99_LLM_ARM_RUN") is "1" or "true" or "TRUE");

        var lanes = new[]
        {
            new Lane("V2_RAW_REPLAY", V2, SemanticRequestVersion.V2_ATTENTION_FREE),
            new Lane("V3_RAW_REPLAY", V3, SemanticRequestVersion.V3_ATTENTION_FREE_EXCLUSION_CONSISTENCY),
        };
        var laneResults = new List<object>();
        foreach (var lane in lanes)
            laneResults.Add(await ReplayLane(lane));

        FreezeArtifact.AssertJson(Dir, "replay-score.v1.json", new
        {
            artifactKind = "a99_offline_semantic_function_membership_replay",
            study = "SEMANTIC_FUNCTION_MEMBERSHIP_REPLAY_V1",
            modelProviderCalls = 0,
            mapping = new
            {
                mappingId = "LEGACY_SEMANTIC_ROLE_TO_FUNCTION_V1",
                frozenBeforeScore = true,
                membershipRule = "STRUCTURAL_UNIT and DOCUMENT_IDENTITY are members; NON_STRUCTURAL and UNMAPPABLE are not",
                unmappableRule = "Any role absent from the closed bridge, including generic free-form 'heading', projects to not-a-heading.",
                entries = RoleMap.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new { semanticRole = x.Key, semanticFunction = x.Value.ToString(), derivedMembership = IsMember(x.Value) }).ToArray(),
            },
            invariants = new
            {
                rawResponses = "immutable committed run files, hash-pinned below",
                sourceParts = "decoded from each raw entry without mutation",
                verbatimText = "decoded and bound without mutation",
                aliasBinding = "existing SemanticSourcePartCanonicalizer then SemanticSourcePartBinder",
                claimBoundaries = "the replay changes no part, span, order, or occurrence selector",
                identity = "binder identity is carried through unchanged",
                hierarchy = "not run in either original arm and not introduced by replay",
                onlyChange = "semanticRole -> mapped semanticFunction -> derived membership",
                noMembershipInputs = new[] { "Gold", "document ID", "page number", "source heuristic", "diagnostic", "candidate ranking", "post-model text filter" },
            },
            lanes = laneResults,
        });
    }

    private sealed record Lane(string Name, string Root, SemanticRequestVersion Version);

    private static async Task<object> ReplayLane(Lane lane)
    {
        var runPath = TestRepository.Path($"{lane.Root}/run.v1.json");
        var scorePath = TestRepository.Path($"{lane.Root}/score.v1.json");
        using var run = JsonDocument.Parse(File.ReadAllText(runPath));
        using var committedScore = JsonDocument.Parse(File.ReadAllText(scorePath));
        var ledger = run.RootElement.GetProperty("ledger").EnumerateArray().ToArray();
        var rows = new List<object>();

        foreach (var (id, pdf) in Documents)
        {
            using var capture = new RequestCapturingClassifier();
            await ComposeRequests(pdf, capture, lane.Version);
            var calls = ledger.Where(e => e.GetProperty("DocumentId").GetString() == id).ToArray();
            Assert.Equal(calls.Length, capture.Requests.Count);
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(
                TestRepository.Path(pdf), PdfSourceFactsVersion.V3_RobustGlyphStatistics).Atoms;
            var candidates = ReadAndBind(calls, capture.Requests, atoms);

            // Candidate projection is now fixed. Only after that point may the scorer open Gold.
            var baseline = Score(id, pdf, candidates.Where(c => c.BaselineMembership));
            var replay = Score(id, pdf, candidates.Where(c => IsMember(c.Function)));
            var expected = committedScore.RootElement.GetProperty("documents").EnumerateArray()
                .Single(d => d.GetProperty("documentId").GetString() == id).GetProperty("headline");
            Assert.Equal(expected.GetProperty("truePositives").GetInt32(), baseline.Score.TruePositives);
            Assert.Equal(expected.GetProperty("falsePositives").GetInt32(), baseline.Score.FalsePositives.Length);
            Assert.Equal(expected.GetProperty("falseNegatives").GetInt32(), baseline.Score.Rows.Length - baseline.Score.TruePositives);

            var baselineIds = candidates.Where(c => c.BaselineMembership).Select(c => c.Identity).ToHashSet(StringComparer.Ordinal);
            var replayIds = candidates.Where(c => IsMember(c.Function)).Select(c => c.Identity).ToHashSet(StringComparer.Ordinal);
            Assert.True(replayIds.IsSubsetOf(baselineIds), "This frozen cohort has no isHeading=false proposal; replay must only derive membership away.");
            rows.Add(new
            {
                documentId = id,
                rawCalls = calls.Length,
                rawResponseSha256 = calls.Select(c => Sha(c.GetProperty("Response").GetString() ?? "")).ToArray(),
                sourcePartInvariant = new
                {
                    boundCandidates = candidates.Count,
                    distinctPartFingerprints = candidates.Select(c => c.PartsFingerprint).Distinct(StringComparer.Ordinal).Count(),
                    replayDoesNotRewriteParts = true,
                    replayDoesNotRewriteBinderIdentity = true,
                },
                baseline = Describe(baseline.Score),
                replay = Describe(replay.Score),
                delta = Delta(baseline.Score, replay.Score),
                membershipMovement = new
                {
                    acceptedToRejected = baselineIds.Except(replayIds, StringComparer.Ordinal).Count(),
                    rejectedToAccepted = replayIds.Except(baselineIds, StringComparer.Ordinal).Count(),
                    retained = replayIds.Count,
                },
                unmappableSemanticStates = candidates.Where(c => c.Function == SemanticFunction.Unmappable)
                    .GroupBy(c => c.Role).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new { semanticRole = g.Key, entries = g.Count(), derivedMembership = false }).ToArray(),
                contradictions = new
                {
                    before = candidates.Count(c => c.BaselineMembership && c.Function == SemanticFunction.NonStructural),
                    after = candidates.Count(c => IsMember(c.Function) && c.Function == SemanticFunction.NonStructural),
                    definition = "a model-proposed member whose mapped primary function is NON_STRUCTURAL",
                },
                src095FalsePositiveBreakdown = id == "SRC-095"
                    ? new { baseline = FalsePositiveBreakdown(id, baseline.Score), replay = FalsePositiveBreakdown(id, replay.Score) }
                    : null,
                src089AssemblyChecks = id == "SRC-089"
                    ? Src089Checks(baseline.Score, replay.Score, candidates, baselineIds, replayIds)
                    : null,
            });
        }

        return new
        {
            lane = lane.Name,
            requestVersion = lane.Version.ToString(),
            rawRun = new { path = $"{lane.Root}/run.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(runPath) },
            baselineScore = new { path = $"{lane.Root}/score.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(scorePath) },
            documents = rows,
        };
    }

    private static async Task ComposeRequests(string pdf, RequestCapturingClassifier capture, SemanticRequestVersion version)
    {
        var experiment = version == SemanticRequestVersion.V2_ATTENTION_FREE
            ? CanonicalSemanticExperiment.Baseline
            : CanonicalSemanticExperiment.Baseline with { RequestVersion = version };
        await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(pdf), capture, CancellationToken.None,
            experiment: experiment, profile: PdfSemanticAuthorityProfile.StructuredSourceParts,
            runPlacement: false, sourceFacts: PdfSourceFactsVersion.V3_RobustGlyphStatistics);
    }

    private static List<BoundCandidate> ReadAndBind(
        IReadOnlyList<JsonElement> calls,
        IReadOnlyList<CapturedRequest> requests,
        IReadOnlyList<SemanticSourceAtom> atoms)
    {
        var contract = SemanticCoordinateContract.PdfStructuredSourceParts;
        var result = new List<BoundCandidate>();
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            Assert.Equal(Sha(requests[index].UserMessage), call.GetProperty("RequestSha256").GetString());
            var responseText = call.GetProperty("Response").GetString();
            if (responseText is null) continue;
            var schemaAt = requests[index].UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
            using var packet = JsonDocument.Parse(requests[index].UserMessage[..schemaAt]);
            var owned = packet.RootElement.GetProperty("sourceEvidence").EnumerateArray()
                .Where(item => item.GetProperty("owned").GetBoolean())
                .Select(item => item.GetProperty("alias").GetString()!).ToHashSet(StringComparer.Ordinal);
            using var response = JsonDocument.Parse(responseText);
            if (contract.Validate(response.RootElement).Count > 0 ||
                !response.RootElement.TryGetProperty("headings", out var headings))
                continue;

            foreach (var entry in headings.EnumerateArray())
            {
                var decoded = contract.Decode(entry);
                if (decoded.Proposals.Count != 1) continue;
                var proposal = decoded.Proposals[0];
                var parts = proposal.SourceParts!;
                if (parts.Any(part => !owned.Contains(part.SourceAlias))) continue;
                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, parts);
                if (!canonical.IsCanonical) continue;
                var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (!bound.IsBound) continue;
                var role = entry.TryGetProperty("semanticRole", out var property) && property.ValueKind == JsonValueKind.String
                    ? property.GetString()! : "(absent)";
                var function = RoleMap.GetValueOrDefault(role, SemanticFunction.Unmappable);
                var baseline = !entry.TryGetProperty("isHeading", out var flag) || flag.ValueKind != JsonValueKind.False;
                var spans = bound.Parts.Select(part => new ExactScorer.Span(part.Alias, part.Start, part.End)).ToArray();
                result.Add(new BoundCandidate(bound.Identity, spans, string.Join(" ", bound.Parts.Select(part => part.Text)),
                    role, function, baseline, Sha(string.Join("|", spans.Select(span => $"{span.Alias}:{span.Start}-{span.End}")))));
            }
        }
        return result;
    }

    private sealed record Scored(ExactScorer.Score Score, IReadOnlyList<ExactScorer.Hypothesis> Hypotheses);

    private static Scored Score(string id, string pdf, IEnumerable<BoundCandidate> candidates)
    {
        var hypotheses = candidates.GroupBy(c => c.Identity, StringComparer.Ordinal).Select(group => group.First())
            .Select((candidate, index) => new ExactScorer.Hypothesis(index, candidate.Text, "TRUE", [], null, null, [], "TITLE", null,
                [$"semanticRole={candidate.Role}", $"semanticFunction={candidate.Function}"], candidate.Identity, null, candidate.Spans))
            .ToArray();
        var goldPath = TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json");
        var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(pdf));
        var gold = ExactScorer.ReadGold(goldPath, universe);
        return new(ExactScorer.Compute(gold, hypotheses), hypotheses);
    }

    private static bool IsMember(SemanticFunction function) =>
        function is SemanticFunction.StructuralUnit or SemanticFunction.DocumentIdentity;

    private static object Describe(ExactScorer.Score score) => score.Headline();

    private static object Delta(ExactScorer.Score before, ExactScorer.Score after)
    {
        static (int Tp, int Fp, int Fn, double Precision, double Recall, double F1) Metrics(ExactScorer.Score score)
        {
            var tp = score.TruePositives;
            var fp = score.FalsePositives.Length;
            var fn = score.Rows.Length - tp;
            var precision = tp == 0 ? 0 : Math.Round((double)tp / (tp + fp), 4);
            var recall = score.Rows.Length == 0 ? 0 : Math.Round((double)tp / score.Rows.Length, 4);
            var f1 = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 4);
            return (tp, fp, fn, precision, recall, f1);
        }

        var a = Metrics(before);
        var b = Metrics(after);
        return new
        {
            truePositives = b.Tp - a.Tp,
            falsePositives = b.Fp - a.Fp,
            falseNegatives = b.Fn - a.Fn,
            precision = Math.Round(b.Precision - a.Precision, 4),
            recall = Math.Round(b.Recall - a.Recall, 4),
            f1 = Math.Round(b.F1 - a.F1, 4),
        };
    }

    private static object FalsePositiveBreakdown(string id, ExactScorer.Score score)
    {
        var pages = LlmSemanticPilotV1AnalysisTests.PageOfAlias(id);
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")));
        var goldAliases = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .SelectMany(claim => claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        var reviewed = LlmSemanticPilotV1AnalysisTests.ReviewedNonHeadings(id);
        return score.FalsePositives.Select(hypothesis =>
        {
            var role = hypothesis.Evidence.First(item => item.StartsWith("semanticRole=", StringComparison.Ordinal))["semanticRole=".Length..];
            return LlmSemanticPilotV1AnalysisTests.FalsePositiveFamily(hypothesis.Identity!, hypothesis.Text, role,
                LlmSemanticPilotV1AnalysisTests.PageOf(pages, hypothesis.Identity!), (2, 4), 54, goldAliases, reviewed);
        }).GroupBy(family => family).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
          .ToDictionary(group => group.Key, group => group.Count());
    }

    private static object Src089Checks(
        ExactScorer.Score baseline,
        ExactScorer.Score replay,
        IReadOnlyList<BoundCandidate> candidates,
        IReadOnlySet<string> baselineIds,
        IReadOnlySet<string> replayIds)
    {
        const string Articles = "ARTICLE_HEADING";
        const string Chapters = "S089_Q2_CHAPTER_LABEL_OVER_TITLE";
        const string Title = "S089_Q1_DECREE_TITLE_BLOCK";
        const string Clauses = "S089_Q3_COLON_CLAUSE_LABEL";
        return new
        {
            articleExact = PatternExact(baseline, replay, Articles),
            chapterExact = PatternExact(baseline, replay, Chapters),
            titleBlock = new
            {
                exact = PatternExact(baseline, replay, Title),
                baselinePartFingerprints = TouchingFingerprints(baseline, candidates, baselineIds, Title),
                replayPartFingerprints = TouchingFingerprints(replay, candidates, replayIds, Title),
            },
            clauseLabels = new
            {
                exact = PatternExact(baseline, replay, Clauses),
                replayIsSubsetOfBaseline = replayIds.All(baselineIds.Contains),
                note = "Projection only removes membership; it cannot synthesize a new claim or boundary.",
            },
        };
    }

    private static object PatternExact(ExactScorer.Score baseline, ExactScorer.Score replay, string pattern)
    {
        int Exact(ExactScorer.Score score) => score.Rows.Count(row => GoldPattern(row.Claim.Identity) == pattern && row.Bucket == "EXACT_TRUE");
        int Gold(ExactScorer.Score score) => score.Rows.Count(row => GoldPattern(row.Claim.Identity) == pattern);
        return new { gold = Gold(baseline), baseline = Exact(baseline), replay = Exact(replay) };
    }

    // Used only after scoring for the SRC-089 report. The dictionary is from frozen Gold and has no
    // path into RoleMap or candidate projection.
    private static readonly Lazy<Dictionary<string, string>> Src089PatternByIdentity = new(() =>
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("eval/a99-closed-loop/gold/SRC-089.gold.json")));
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
            .ToDictionary(claim => claim.GetProperty("identity").GetString()!, claim => claim.GetProperty("pattern").GetString()!, StringComparer.Ordinal);
    });

    private static string GoldPattern(string identity) => Src089PatternByIdentity.Value.GetValueOrDefault(identity, "(none)");

    private static string[] TouchingFingerprints(
        ExactScorer.Score score,
        IReadOnlyList<BoundCandidate> candidates,
        IReadOnlySet<string> ids,
        string pattern)
    {
        var spans = score.Rows.Where(row => GoldPattern(row.Claim.Identity) == pattern)
            .SelectMany(row => row.Claim.Spans).ToArray();
        return candidates.Where(candidate => ids.Contains(candidate.Identity) && candidate.Spans.Any(span => spans.Any(gold =>
                span.Alias == gold.Alias && span.Start < gold.End && gold.Start < span.End)))
            .Select(candidate => candidate.PartsFingerprint).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
