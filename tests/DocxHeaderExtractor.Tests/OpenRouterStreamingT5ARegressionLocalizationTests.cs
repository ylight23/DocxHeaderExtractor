using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free T5A localization of none-vs-medium regression by parent ordinal.</summary>
public sealed class OpenRouterStreamingT5ARegressionLocalizationTests
{
    private const string Root = "eval/a99-closed-loop/request-architecture-v2";
    private const string T3c = Root + "/openrouter-streaming-t3c-efficiency-heavy-leaf-audit.v1.json";
    private const string P05Manifest = Root + "/v4r2-p05-accepted-response-manifest.v1.json";
    private const string OutputJson = Root + "/openrouter-streaming-t5a-selective-medium-regression-localization.v1.json";
    private const string OutputMd = Root + "/openrouter-streaming-t5a-selective-medium-regression-localization.v1.md";
    private static readonly (string Id, string Pdf)[] Documents = [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];

    private sealed record ArmRow(int Ordinal, string DocumentId, string RequestFile, string ResponseFile, string? ContentFile, int PromptTokens, int CompletionTokens, int ReasoningTokens, int WallClockMs, int FirstContentMs, string SemanticRequestHash, string ProviderEnvelopeHash, int OwnedAliasCount = 0, int VisibleAtomCount = 0, int PrimaryInputBytes = 0, int ClaimCount = 0, int MemberClaimCount = 0);
    private sealed record Candidate(int Ordinal, string Identity, string Function, bool Member);
    private sealed record Metric(int Tp, int Fp, int Fn, int MemberClaims);

    [Fact]
    public void Freeze_t5a_regression_localization_provider_free()
    {
        using var t3c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(T3c)));
        var none = t3c.RootElement.GetProperty("rows").EnumerateArray().Select(x => new ArmRow(
            x.GetProperty("ordinal").GetInt32(), x.GetProperty("documentId").GetString()!,
            $"{Root}/{(x.GetProperty("acceptedRun").GetString() == "initial" ? "openrouter-streaming-t3b-reasoning-none-full-cohort-20260928T091634Z" : "openrouter-streaming-t3b-reasoning-none-full-cohort-continuation-003-031-20260928T091819Z")}/{x.GetProperty("acceptedRequestFile").GetString()}",
            $"{Root}/{(x.GetProperty("acceptedRun").GetString() == "initial" ? "openrouter-streaming-t3b-reasoning-none-full-cohort-20260928T091634Z" : "openrouter-streaming-t3b-reasoning-none-full-cohort-continuation-003-031-20260928T091819Z")}/{x.GetProperty("acceptedContentFile").GetString()}", x.GetProperty("acceptedContentFile").GetString()!,
            x.GetProperty("promptTokens").GetInt32(), x.GetProperty("completionTokens").GetInt32(), x.GetProperty("reasoningTokens").GetInt32(),
            x.GetProperty("wallClockMs").GetInt32(), x.GetProperty("firstContentMs").GetInt32(), x.GetProperty("semanticRequestHash").GetString()!, x.GetProperty("providerEnvelopeHash").GetString()!, x.GetProperty("ownedAliasCount").GetInt32(), x.GetProperty("visibleAtomCount").GetInt32(), x.GetProperty("primaryInputBytes").GetInt32(), x.GetProperty("claimCount").GetInt32(), x.GetProperty("memberClaimCount").GetInt32())).ToArray();
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(P05Manifest)));
        var medium = manifest.RootElement.GetProperty("entries").EnumerateArray().Select(x => new ArmRow(
            x.GetProperty("parentOrdinal").GetInt32(), x.GetProperty("documentId").GetString()!,
            x.GetProperty("requestFile").GetString()!, x.GetProperty("responseFile").GetString()!, null, 0, 0, 0, 0, 0,
            x.GetProperty("semanticRequestHash").GetString()!, x.GetProperty("providerEnvelopeHash").GetString()!)).ToArray();
        var t3cRows = none.ToDictionary(x => x.Ordinal);
        var output = new List<object>();
        var allRows = new List<(string DocumentId, object Row)>();
        foreach (var (id, pdf) in Documents)
        {
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms;
            var goldPath = TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json");
            var gold = ExactScorer.ReadGold(goldPath, ExactScorer.Universe.For("PDF", TestRepository.Path(pdf)));
            var goldIds = gold.Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);
            var noneRows = none.Where(x => x.DocumentId == id).ToArray();
            var mediumRows = medium.Where(x => x.DocumentId == id).ToArray();
            var noneCandidates = Bind(noneRows, atoms, Root, "none");
            var mediumCandidates = Bind(mediumRows, atoms, Root, "medium");
            var ownership = BuildOwnership(noneRows, atoms, Root);
            var byOrdinal = new List<object>();
            foreach (var ordinal in noneRows.Select(x => x.Ordinal).OrderBy(x => x))
            {
                var n = Metrics(noneCandidates.Where(x => x.Ordinal == ordinal), goldIds, ownership, ordinal);
                var m = Metrics(mediumCandidates.Where(x => x.Ordinal == ordinal), goldIds, ownership, ordinal);
                var feature = t3cRows[ordinal];
                var tpGain = m.Tp - n.Tp;
                var fpDelta = m.Fp - n.Fp;
                var fnRecovered = n.Fn - m.Fn;
                var net = tpGain - Math.Max(fpDelta, 0);
                var group = net > 0 && tpGain > 0 ? "BENEFIT_FROM_MEDIUM" : net < 0 ? "HARMED_BY_MEDIUM" : "NEUTRAL";
                byOrdinal.Add(new { ordinal, documentId = id, none = n, medium = m, delta = new { tpGain, fpDelta, fnRecovered, netGain = net }, features = new { ownedAliasCount = feature.OwnedAliasCount, visibleAtomCount = feature.VisibleAtomCount, primaryInputBytes = feature.PrimaryInputBytes, promptTokens = feature.PromptTokens, completionTokens = feature.CompletionTokens, reasoningTokens = feature.ReasoningTokens, claimCount = feature.ClaimCount, memberClaimCount = feature.MemberClaimCount, memberClaimRate = feature.ClaimCount == 0 ? 0 : Math.Round((double)feature.MemberClaimCount / feature.ClaimCount, 4), firstContentMs = feature.FirstContentMs, wallClockMs = feature.WallClockMs, semanticRequestHash = feature.SemanticRequestHash }, classification = group });
            }
            output.Add(new { documentId = id, goldClaims = goldIds.Count, noneHeadline = Metrics(noneCandidates, goldIds, ownership, null), mediumHeadline = Metrics(mediumCandidates, goldIds, ownership, null), byOrdinal });
        }
        var rows = output.SelectMany(x => (IEnumerable<object>)x.GetType().GetProperty("byOrdinal")!.GetValue(x)!).ToArray();
        var benefit = rows.Count(x => (string)x.GetType().GetProperty("classification")!.GetValue(x)! == "BENEFIT_FROM_MEDIUM");
        var harmed = rows.Count(x => (string)x.GetType().GetProperty("classification")!.GetValue(x)! == "HARMED_BY_MEDIUM");
        var neutral = rows.Length - benefit - harmed;
        var json = new
        {
            artifactKind = "a99_openrouter_streaming_t5a_regression_localization",
            status = "T5A_PROVIDER_FREE_REGRESSION_LOCALIZATION_COMPLETE",
            candidate = "T3B_P05_STREAMING_REASONING_NONE_VS_V4R2_P05_MEDIUM",
            providerCalls = 0, goldRead = true, productionPromotion = false,
            runtimeRuleStatus = "DIAGNOSTIC_ONLY_GOLD_DEPENDENT_NOT_A_RUNTIME_TRIGGER",
            lineage = new { t3c = new { path = T3c, sha256 = Hash(T3c) }, p05Manifest = new { path = P05Manifest, sha256 = Hash(P05Manifest) } },
            methodology = new { parentOrdinalAggregation = "adaptive children aggregate to parent ordinal", ownerAssignment = "whole-owned-alias identity mapping; multipart/unassigned identities are reported by scorer authority", metric = "netGain = TP_gain - max(FP_delta,0)", extraCompletionTokensAvailable = false },
            summary = new { ordinals = rows.Length, benefitFromMedium = benefit, neutral, harmedByMedium = harmed }, documents = output,
            ranking = rows.OrderByDescending(x => (int)x.GetType().GetProperty("delta")!.GetValue(x)!.GetType().GetProperty("netGain")!.GetValue(x.GetType().GetProperty("delta")!.GetValue(x))!).Select(x => x).ToArray(),
            interpretation = "T5A localizes where medium changes scored outcomes; it does not establish a pre-Gold escalation signal. Do not use ordinal, document ID, or Gold residual identity as runtime logic. T5B must test generic observable predictors."
        };
        FreezeArtifact.AssertJson(Root, "openrouter-streaming-t5a-selective-medium-regression-localization.v1.json", json);
        var md = $"# T5A selective medium regression localization\n\nStatus: `T5A_PROVIDER_FREE_REGRESSION_LOCALIZATION_COMPLETE`\n\nProvider calls: `0`\n\nThis is an offline Gold-dependent diagnostic, not a runtime escalation rule. Medium/none are compared per parent ordinal; adaptive children are aggregated to parent 030.\n\n- Benefit from medium: `{benefit}` ordinals\n- Neutral: `{neutral}` ordinals\n- Harmed by medium: `{harmed}` ordinals\n\nMetric: `netGain = TP_gain - max(FP_delta, 0)`. Extra completion-token efficiency is unavailable at per-parent medium lineage granularity and is therefore not inferred.\n\nNext: derive generic pre-Gold predictors from request/none-output features, then run only a bounded selected medium cohort.\n";
        FreezeArtifact.AssertText(Root, "openrouter-streaming-t5a-selective-medium-regression-localization.v1.md", md);
    }

    private static List<Candidate> Bind(IReadOnlyList<ArmRow> rows, IReadOnlyList<SemanticSourceAtom> atoms, string root, string arm)
    {
        var result = new List<Candidate>();
        foreach (var row in rows)
        {
            using var request = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(row.RequestFile)));
            var user = request.RootElement.GetProperty("messages").EnumerateArray().First(x => x.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            var split = user.LastIndexOf("SCHEMA=", StringComparison.Ordinal); if (split < 0) continue;
            while (split > 0 && (user[split - 1] == '\n' || user[split - 1] == '\r')) split--;
            var owned = OwnedAliases(user[..split]);
            var responsePath = row.ResponseFile;
            using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(responsePath)));
            JsonElement content;
            if (arm == "none") content = raw.RootElement;
            else if (raw.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() is { } s)
            {
                try { content = JsonDocument.Parse(s).RootElement; }
                catch (JsonException)
                {
                    var first = s.IndexOf('{'); var last = s.LastIndexOf('}');
                    if (first < 0 || last <= first) continue;
                    try { content = JsonDocument.Parse(s[first..(last + 1)]).RootElement; }
                    catch (JsonException) { continue; }
                }
            }
            else content = default;
            if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("headings", out var headings)) continue;
            foreach (var entry in headings.EnumerateArray())
            {
                var decoded = SemanticFunctionMembershipContractV1.Decode(entry); if (decoded.Proposals.Count != 1) continue;
                var p = decoded.Proposals[0]; if (p.SourceParts is null || p.SourceParts.Any(x => !owned.Contains(x.SourceAlias))) continue;
                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, p.SourceParts); if (!canonical.IsCanonical) continue;
                var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts)); if (!bound.IsBound) continue;
                var fn = entry.GetProperty("semanticFunction").GetString()!;
                result.Add(new(row.Ordinal, bound.Identity, fn, SemanticFunctionMembershipContractV1.IsMember(fn)));
            }
        }
        return result;
    }
    private static Dictionary<string, int> BuildOwnership(IReadOnlyList<ArmRow> rows, IReadOnlyList<SemanticSourceAtom> atoms, string root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            using var request = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(row.RequestFile)));
            var user = request.RootElement.GetProperty("messages").EnumerateArray().First(x => x.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            var split = user.LastIndexOf("SCHEMA=", StringComparison.Ordinal); if (split < 0) continue;
            while (split > 0 && (user[split - 1] == '\n' || user[split - 1] == '\r')) split--;
            var owned = OwnedAliases(user[..split]);
            foreach (var atom in atoms.Where(a => owned.Contains(a.Alias))) result.TryAdd(ExactScorer.Identity([new ExactScorer.Span(atom.Alias, 0, atom.Text.Length)]), row.Ordinal);
        }
        return result;
    }
    private static Metric Metrics(IEnumerable<Candidate> candidates, HashSet<string> gold, Dictionary<string, int> ownership, int? ordinal)
    {
        var all = candidates.Where(x => x.Member).GroupBy(x => x.Identity, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var tp = all.Count(x => gold.Contains(x.Identity)); var fp = all.Length - tp;
        var ownedGold = ordinal is null ? gold : gold.Where(x => ownership.TryGetValue(x, out var owner) && owner == ordinal.Value).ToHashSet(StringComparer.Ordinal);
        var fn = ownedGold.Count(x => all.All(y => y.Identity != x));
        return new Metric(tp, fp, fn, all.Length);
    }
    private static HashSet<string> OwnedAliases(string prefix)
    {
        var m = Regex.Match(prefix, "\\\"ownedSourceAliases\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
        if (!m.Success) return new HashSet<string>(StringComparer.Ordinal);
        return Regex.Matches(m.Groups[1].Value, "\\\"([^\\\"]+)\\\"").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }
    private static string Hash(string path) => CanonicalArtifactHash.OfTextFile(TestRepository.Path(path));
}
