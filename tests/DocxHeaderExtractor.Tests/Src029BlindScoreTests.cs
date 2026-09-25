using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC029_BLIND_SCORE_PROTOCOL_V1 - the scorer, frozen by hash in the protocol before the engine's blind
/// proposals (dc98144) are opened. It joins those proposals with the frozen 374-claim Gold and writes the
/// raw score once, with A99_SRC029_SCORE=1; the score is committed as written and never rewritten.
/// <para>
/// Matching is claim-level and exact: a hypothesis matches a Gold claim only when its ordered source-part
/// tuple, bound by the production binder, has the same identity. A hypothesis that covers part of a
/// claim, or a claim plus more, is a miss at claim level and a part-level diagnostic. NEEDS_REVIEW is
/// never counted as a heading in precision or recall; it has its own capture rate.
/// </para>
/// </summary>
public sealed class Src029BlindScoreTests
{
    internal const string Root = "eval/a99-closed-loop/generic-audit-v1";
    internal const string Proposals = Root + "/SRC-029.proposals.v1.json";
    internal const string Protocol = Root + "/SRC-029.score-protocol.v1.json";
    internal const string GoldPath = "eval/a99-closed-loop/gold/SRC-029.gold.json";
    internal const string ScorerFile = "tests/DocxHeaderExtractor.Tests/Src029BlindScoreTests.cs";

    private sealed record Hypothesis(
        int Index, string[] Aliases, string Text, string State, string[] Functions, string? Primary, string? Scope,
        string[] Roles, string TitleRelation, string RepeatStatus, string? InformationType, string[] Evidence,
        string? Identity, string? UnboundReason, (string Alias, int Start, int End)[] Spans);

    private sealed record Claim(
        string Identity, string Text, string[] Aliases, bool HasVerbatimPart, string[] Functions, string? Primary,
        string? Scope, string[] Roles, string TitleRelation, string RepeatStatus, string? InformationType,
        (string Alias, int Start, int End)[] Spans, string Pattern);

    [Fact]
    public void Score_the_blind_proposals_against_gold()
    {
        if (Environment.GetEnvironmentVariable("A99_SRC029_SCORE") != "1") return;

        // Everything the protocol pinned must be what is on disk now.
        using var protocol = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Protocol)));
        var pinned = protocol.RootElement;
        Assert.Equal(pinned.GetProperty("scorer").GetProperty("sha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(ScorerFile)));
        Assert.Equal(pinned.GetProperty("gold").GetProperty("authoredGoldSha256").GetString(), CanonicalArtifactHash.OfTextFile(TestRepository.Path(GoldPath)));
        Assert.Equal(pinned.GetProperty("gold").GetProperty("registryGoldSha256").GetString(), CanonicalGoldRegistry.Entry("SRC-029").GoldSha256);
        Assert.Equal(pinned.GetProperty("engineBaseline").GetProperty("proposalGitBlob").GetString(), GitBlob(TestRepository.Path(Proposals)));

        var result = Score();
        FreezeArtifact.AssertJson(Root, "SRC-029.blind-score.v1.json", result);
    }

    /// <summary>After the reveal: the committed score is still what the pinned inputs produce.</summary>
    [Fact]
    public void The_committed_score_is_reproducible()
    {
        if (!File.Exists(TestRepository.Path($"{Root}/SRC-029.blind-score.v1.json"))) return; // not revealed yet
        FreezeArtifact.AssertJson(Root, "SRC-029.blind-score.v1.json", Score());
    }

    private static object Score()
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Src029SourceReviewTests.Pdf)).Atoms;
        var claims = ReadGold();
        var hypotheses = ReadProposals(atoms);

        var claimByIdentity = claims.ToDictionary(c => c.Identity, StringComparer.Ordinal);
        var exactByIdentity = hypotheses.Where(h => h.Identity is not null)
            .GroupBy(h => h.Identity!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        static bool Overlaps((string Alias, int Start, int End)[] a, (string Alias, int Start, int End)[] b) =>
            a.Any(x => b.Any(y => x.Alias == y.Alias && x.Start < y.End && y.Start < x.End));

        // ---- Gold side: every claim in exactly one bucket ---------------------------------------
        var rows = claims.Select(c =>
        {
            var exact = exactByIdentity.GetValueOrDefault(c.Identity);
            var overlapping = hypotheses.Where(h => h.Identity != c.Identity && Overlaps(h.Spans, c.Spans)).ToArray();
            string bucket;
            if (exact is { Length: > 0 }) bucket = "EXACT_" + exact[0].State;
            else if (overlapping.Any(h => h.State == "TRUE")) bucket = "PARTIAL_TRUE";
            else if (overlapping.Any(h => h.State == "NEEDS_REVIEW")) bucket = "PARTIAL_NEEDS_REVIEW";
            else if (overlapping.Length > 0) bucket = "PARTIAL_FALSE";
            else bucket = "UNPROPOSED";
            return (Claim: c, Exact: exact?[0], Overlapping: overlapping, Bucket: bucket);
        }).ToArray();
        int Count(string bucket) => rows.Count(r => r.Bucket == bucket);

        var tp = Count("EXACT_TRUE");
        var trueHypotheses = hypotheses.Where(h => h.State == "TRUE").ToArray();
        var reviewHypotheses = hypotheses.Where(h => h.State == "NEEDS_REVIEW").ToArray();
        var fp = trueHypotheses.Where(h => h.Identity is null || !claimByIdentity.ContainsKey(h.Identity)).ToArray();
        var reviewOutside = reviewHypotheses.Where(h => h.Identity is null || !claimByIdentity.ContainsKey(h.Identity)).ToArray();
        bool TouchesGold(Hypothesis h) => claims.Any(c => Overlaps(h.Spans, c.Spans));
        var notTrue = claims.Count - tp;
        double Ratio(int a, int b) => b == 0 ? 0 : Math.Round((double)a / b, 4);
        var precision = Ratio(tp, trueHypotheses.Length);
        var recall = Ratio(tp, claims.Count);

        // ---- axes, on exact matches only ---------------------------------------------------------
        object Axes(IEnumerable<(Claim Claim, Hypothesis? Exact, Hypothesis[] Overlapping, string Bucket)> matched)
        {
            var pairs = matched.Select(r => (Gold: r.Claim, Engine: r.Exact!)).ToArray();
            object Axis(Func<Claim, string> gold, Func<Hypothesis, string> engine) => new
            {
                disagreements = pairs.Count(p => gold(p.Gold) != engine(p.Engine)),
                pairs = pairs.Where(p => gold(p.Gold) != engine(p.Engine))
                    .GroupBy(p => $"gold {gold(p.Gold)} <- engine {engine(p.Engine)}")
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
            };
            static string Set(string[] values) => string.Join("+", values.Order(StringComparer.Ordinal));
            static string Scalar(string? value) => value ?? "null";
            return new
            {
                compared = pairs.Length,
                semanticFunctions = Axis(c => Set(c.Functions), h => Set(h.Functions)),
                primaryFunction = Axis(c => Scalar(c.Primary), h => Scalar(h.Primary)),
                scope = Axis(c => Scalar(c.Scope), h => Scalar(h.Scope)),
                occurrenceRoles = Axis(c => Set(c.Roles), h => Set(h.Roles)),
                titleRelation = Axis(c => c.TitleRelation, h => h.TitleRelation),
                repeatStatus = Axis(c => c.RepeatStatus, h => h.RepeatStatus),
                informationType = Axis(c => Scalar(c.InformationType), h => Scalar(h.InformationType)),
            };
        }

        object Brief(Hypothesis h) => new
        {
            h.Index, h.Aliases, h.Text, state = h.State, h.Roles, h.Evidence, identity = h.Identity, h.UnboundReason,
        };

        return new
        {
            artifactKind = "a99_generic_audit_blind_score",
            study = "SRC029_BLIND_GENERALIZATION_AUDIT_V1",
            protocol = new { path = Protocol, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Protocol)) },
            proposals = new { path = Proposals, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(Proposals)), gitBlob = GitBlob(TestRepository.Path(Proposals)) },
            gold = new { path = GoldPath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(GoldPath)), claims = claims.Count },
            modelProviderVlmCalls = 0,
            hypotheses = new
            {
                listed = hypotheses.Count,
                TRUE = trueHypotheses.Length,
                NEEDS_REVIEW = reviewHypotheses.Length,
                FALSE_listed = hypotheses.Count(h => h.State == "FALSE"),
                unbindable = hypotheses.Count(h => h.Identity is null),
                duplicateIdentities = exactByIdentity.Count(g => g.Value.Length > 1),
            },
            headline = new
            {
                goldHeadingsEngineTrue = tp,
                goldHeadingsEngineNeedsReview = Count("EXACT_NEEDS_REVIEW"),
                goldHeadingsEngineFalse = Count("EXACT_FALSE") + Count("UNPROPOSED"),
                goldHeadingsPartialOnly = Count("PARTIAL_TRUE") + Count("PARTIAL_NEEDS_REVIEW") + Count("PARTIAL_FALSE"),
            },
            membership = new
            {
                truePositives = tp,
                falsePositives = fp.Length,
                falseNegatives = notTrue,
                precision,
                recall,
                f1 = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 4),
            },
            goldBuckets = new[] { "EXACT_TRUE", "EXACT_NEEDS_REVIEW", "EXACT_FALSE", "PARTIAL_TRUE", "PARTIAL_NEEDS_REVIEW", "PARTIAL_FALSE", "UNPROPOSED" }
                .ToDictionary(b => b, Count),
            review = new
            {
                reviewCaptureRate = Ratio(Count("EXACT_NEEDS_REVIEW"), notTrue),
                reviewCaptureRateIncludingPartial = Ratio(Count("EXACT_NEEDS_REVIEW") + Count("PARTIAL_NEEDS_REVIEW"), notTrue),
                nonGoldNeedsReview = reviewOutside.Length,
                nonGoldNeedsReviewTouchingGold = reviewOutside.Count(TouchesGold),
                reviewNoiseOutsideGold = reviewOutside.Count(h => !TouchesGold(h)),
            },
            nonGoldTrue = new
            {
                total = fp.Length,
                touchingGold = fp.Count(TouchesGold),
                disjointFromGold = fp.Count(h => !TouchesGold(h)),
            },
            composite = new
            {
                goldMultipartClaims = claims.Count(c => c.Spans.Length > 1),
                exactCompositeMatches = rows.Count(r => r.Bucket.StartsWith("EXACT_", StringComparison.Ordinal) && r.Claim.Spans.Length > 1),
                goldClaimsWithVerbatimPart = claims.Count(c => c.HasVerbatimPart),
                verbatimClaimBuckets = rows.Where(r => r.Claim.HasVerbatimPart).GroupBy(r => r.Bucket)
                    .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            axisDisagreementsOnTruePositives = Axes(rows.Where(r => r.Bucket == "EXACT_TRUE")),
            axisDisagreementsOnExactNeedsReview = Axes(rows.Where(r => r.Bucket == "EXACT_NEEDS_REVIEW")),
            residuals = new
            {
                goldNotEngineTrue = rows.Where(r => r.Bucket != "EXACT_TRUE").Select(r => new
                {
                    bucket = r.Bucket,
                    goldIdentity = r.Claim.Identity,
                    goldText = r.Claim.Text,
                    goldPattern = r.Claim.Pattern,
                    goldAliases = r.Claim.Aliases,
                    goldPartsCoveredByEngine = r.Claim.Spans.Count(s => r.Overlapping.Concat(r.Exact is null ? [] : [r.Exact]).Any(h => Overlaps(h.Spans, [s]))),
                    goldParts = r.Claim.Spans.Length,
                    engine = r.Exact is not null ? new[] { Brief(r.Exact) } : r.Overlapping.Select(Brief).ToArray(),
                }).ToArray(),
                nonGoldTrue = fp.Select(h => new { hypothesis = Brief(h), touchesGold = TouchesGold(h) }).ToArray(),
                nonGoldNeedsReview = reviewOutside.Select(h => new { hypothesis = Brief(h), touchesGold = TouchesGold(h) }).ToArray(),
            },
        };
    }

    private static IReadOnlyList<Claim> ReadGold()
    {
        using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GoldPath)));
        return gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().Select(c =>
        {
            var axes = c.GetProperty("semanticAxes");
            var parts = c.GetProperty("sourceParts").EnumerateArray().ToArray();
            return new Claim(
                c.GetProperty("identity").GetString()!,
                c.GetProperty("approvedWording").GetString()!,
                parts.Select(p => p.GetProperty("sourceAlias").GetString()!).ToArray(),
                parts.Any(p => p.GetProperty("selectionMode").GetString() == "VERBATIM_TEXT"),
                Strings(axes.GetProperty("semanticFunctions")),
                Nullable(axes.GetProperty("primaryFunction")),
                Nullable(axes.GetProperty("scope")),
                Strings(axes.GetProperty("occurrenceRoles")),
                axes.GetProperty("titleRelation").GetString()!,
                axes.GetProperty("repeatStatus").GetString()!,
                Nullable(axes.GetProperty("informationType")),
                c.GetProperty("boundParts").EnumerateArray().Select(p => (p.GetProperty("sourceAlias").GetString()!,
                    p.GetProperty("utf16Span").GetProperty("start").GetInt32(), p.GetProperty("utf16Span").GetProperty("end").GetInt32())).ToArray(),
                c.GetProperty("pattern").GetString()!);
        }).ToArray();
    }

    /// <summary>
    /// Every listed hypothesis, its aliases bound as WHOLE_ALIAS parts in the order given. Occurrences the
    /// engine did not list are FALSE by the proposal artifact's own rule and need no row.
    /// </summary>
    private static IReadOnlyList<Hypothesis> ReadProposals(IReadOnlyList<SemanticSourceAtom> atoms)
    {
        using var frozen = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(Proposals)));
        return frozen.RootElement.GetProperty("hypotheses").EnumerateArray().Select((h, index) =>
        {
            var aliases = Strings(h.GetProperty("Aliases"));
            var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(
                aliases.Select(a => new SemanticSourcePart(a, "WHOLE_ALIAS")).ToArray()));
            return new Hypothesis(
                index, aliases, h.GetProperty("Text").GetString()!, h.GetProperty("ProposedIsHeading").GetString()!,
                Strings(h.GetProperty("SemanticFunctions")), Nullable(h.GetProperty("PrimaryFunction")), Nullable(h.GetProperty("Scope")),
                Strings(h.GetProperty("OccurrenceRoles")), h.GetProperty("TitleRelation").GetString()!, h.GetProperty("RepeatStatus").GetString()!,
                Nullable(h.GetProperty("InformationType")), Strings(h.GetProperty("Evidence")),
                binding.IsBound ? binding.Identity : null, binding.IsBound ? null : binding.Status.ToString(),
                binding.IsBound
                    ? binding.Parts.Select(p => (p.Alias, p.Start, p.End)).ToArray()
                    : aliases.Where(a => atoms.Any(x => x.Alias == a)).Select(a => (a, 0, atoms.First(x => x.Alias == a).Text.Length)).ToArray());
        }).ToArray();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string? Nullable(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    /// <summary>The git blob id of a text file as committed (LF line endings) - pins a file without reading its meaning.</summary>
    internal static string GitBlob(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(path).ReplaceLineEndings("\n"));
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. bytes]));
    }
}
