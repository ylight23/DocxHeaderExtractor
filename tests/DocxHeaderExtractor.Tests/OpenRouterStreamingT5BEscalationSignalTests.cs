using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free T5B: derive a generic (pre-Gold) escalation signal for the none-vs-medium
/// regression localized by T5A, and replay candidate policies over the already-accepted
/// reasoning-none (T3B) and reasoning-medium (P05) responses.  No provider calls.
/// <para>
/// The pre-Gold signals are computed from the request and the none reply alone: how many
/// member claims were emitted, and how many the harness refused at canonicalize/bind and why.
/// Gold is opened only afterwards, to score each policy.  Ordinal and document identity are
/// never a policy input.
/// </para>
/// </summary>
public sealed class OpenRouterStreamingT5BEscalationSignalTests
{
    private const string Root = "eval/a99-closed-loop/request-architecture-v2";
    private const string T3c = Root + "/openrouter-streaming-t3c-efficiency-heavy-leaf-audit.v1.json";
    private const string T5a = Root + "/openrouter-streaming-t5a-selective-medium-regression-localization.v1.json";
    private const string P05Manifest = Root + "/v4r2-p05-accepted-response-manifest.v1.json";
    private const string NoneInitial = "openrouter-streaming-t3b-reasoning-none-full-cohort-20260928T091634Z";
    private const string NoneContinuation = "openrouter-streaming-t3b-reasoning-none-full-cohort-continuation-003-031-20260928T091819Z";
    private static readonly (string Id, string Pdf)[] Documents =
        [("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095)];

    /// <summary>How the harness turns quoted parts into coordinates before binding.</summary>
    private enum Repair
    {
        /// <summary>The byte-exact canonicalizer before CASE B2 (the T3B/T4 scoring harness).</summary>
        Strict,
        /// <summary>
        /// A quote that is refused only because it carries leading/trailing whitespace the atom
        /// does not have, and whose trimmed form IS the whole atom, is the whole atom.  This is the
        /// production canonicalizer since CASE B2.
        /// </summary>
        BoundaryWhitespaceWholeAtom,
    }

    private sealed record Leaf(int Ordinal, string DocumentId, string RequestFile, string ResponseFile, bool NoneArm, int CompletionTokens, int ReasoningTokens);
    private sealed record Candidate(int Ordinal, string Identity, bool Member);
    private sealed record Refusal(int Ordinal, string Status, bool WhitespaceOnly);
    private sealed record BindResult(List<Candidate> Candidates, List<Refusal> Refusals, Dictionary<int, int> EmittedMember);
    private sealed record Score(int Tp, int Fp, int Fn, double Precision, double Recall, double F1);

    [Fact]
    public void Freeze_t5b_escalation_signal_provider_free()
    {
        using var t3c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(T3c)));
        var none = t3c.RootElement.GetProperty("rows").EnumerateArray().Select(x =>
        {
            var run = x.GetProperty("acceptedRun").GetString() == "initial" ? NoneInitial : NoneContinuation;
            return new Leaf(x.GetProperty("ordinal").GetInt32(), x.GetProperty("documentId").GetString()!,
                $"{Root}/{run}/{x.GetProperty("acceptedRequestFile").GetString()}",
                $"{Root}/{run}/{x.GetProperty("acceptedContentFile").GetString()}", true,
                x.GetProperty("completionTokens").GetInt32(), x.GetProperty("reasoningTokens").GetInt32());
        }).ToArray();
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(P05Manifest)));
        var medium = manifest.RootElement.GetProperty("entries").EnumerateArray().Select(x =>
        {
            var responseFile = x.GetProperty("responseFile").GetString()!;
            using var raw = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(responseFile)));
            var usage = raw.RootElement.TryGetProperty("usage", out var u) ? u : default;
            var completion = usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("completion_tokens").GetInt32() : 0;
            var reasoning = usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("completion_tokens_details", out var d) &&
                d.TryGetProperty("reasoning_tokens", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            return new Leaf(x.GetProperty("parentOrdinal").GetInt32(), x.GetProperty("documentId").GetString()!,
                x.GetProperty("requestFile").GetString()!, responseFile, false, completion, reasoning);
        }).ToArray();

        var docs = new List<object>();
        var perOrdinal = new List<Dictionary<string, object?>>();
        // policy -> document -> (tp, fp, fn)
        var policyTotals = new Dictionary<string, List<(string Doc, Score Score, int Escalated, int ExtraCompletion)>>();
        var cohortSize = none.Length;
        var noneCompletion = none.Sum(x => x.CompletionTokens);
        var mediumCompletion = medium.Sum(x => x.CompletionTokens);

        foreach (var (id, pdf) in Documents)
        {
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(pdf)).Atoms;
            var gold = ExactScorer.ReadGold(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json"),
                ExactScorer.Universe.For("PDF", TestRepository.Path(pdf))).Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);
            var noneLeaves = none.Where(x => x.DocumentId == id).ToArray();
            var mediumLeaves = medium.Where(x => x.DocumentId == id).ToArray();
            var ordinals = noneLeaves.Select(x => x.Ordinal).OrderBy(x => x).ToArray();
            var aliasOwner = AliasOwnership(noneLeaves);

            var arms = new Dictionary<string, BindResult>
            {
                ["none.strict"] = Bind(noneLeaves, atoms, Repair.Strict),
                ["none.ws"] = Bind(noneLeaves, atoms, Repair.BoundaryWhitespaceWholeAtom),
                ["medium.strict"] = Bind(mediumLeaves, atoms, Repair.Strict),
                ["medium.ws"] = Bind(mediumLeaves, atoms, Repair.BoundaryWhitespaceWholeAtom),
            };

            // Multipart-aware Gold ownership: a Gold identity belongs to the leaf that owns its
            // aliases; one that spans leaves is attributed to the leaf owning its first alias.
            var goldOwner = new Dictionary<string, int>(StringComparer.Ordinal);
            var crossLeafGold = 0;
            foreach (var g in gold)
            {
                var owners = IdentityAliases(g).Select(a => aliasOwner.TryGetValue(a, out var o) ? o : -1).ToArray();
                if (owners.Length == 0 || owners[0] < 0) continue;
                if (owners.Distinct().Count() > 1) crossLeafGold++;
                goldOwner[g] = owners[0];
            }

            foreach (var ordinal in ordinals)
            {
                var noneLeaf = noneLeaves.Single(x => x.Ordinal == ordinal);
                var mediumTokens = mediumLeaves.Where(x => x.Ordinal == ordinal).Sum(x => x.CompletionTokens);
                var refusals = arms["none.strict"].Refusals.Where(x => x.Ordinal == ordinal).ToArray();
                var emitted = arms["none.strict"].EmittedMember.GetValueOrDefault(ordinal);
                var row = new Dictionary<string, object?>
                {
                    ["documentId"] = id,
                    ["ordinal"] = ordinal,
                    // --- pre-Gold signals (request + none reply only) ---
                    ["noneEmittedMemberClaims"] = emitted,
                    ["noneRefusedMemberClaims"] = refusals.Length,
                    ["noneRefusedWhitespaceOnly"] = refusals.Count(x => x.WhitespaceOnly),
                    ["noneRefusalStatuses"] = refusals.GroupBy(x => x.Status).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count()),
                    ["noneRefusalRate"] = emitted == 0 ? 0 : Math.Round((double)refusals.Length / emitted, 4),
                    ["noneCompletionTokens"] = noneLeaf.CompletionTokens,
                    ["mediumCompletionTokens"] = mediumTokens,
                    ["mediumRefusedMemberClaims"] = arms["medium.strict"].Refusals.Count(x => x.Ordinal == ordinal),
                    // --- Gold-scored, multipart-aware (diagnostic only) ---
                    ["noneStrict"] = LeafScore(arms["none.strict"], gold, goldOwner, ordinal),
                    ["noneWhitespaceRepaired"] = LeafScore(arms["none.ws"], gold, goldOwner, ordinal),
                    ["mediumStrict"] = LeafScore(arms["medium.strict"], gold, goldOwner, ordinal),
                    ["mediumWhitespaceRepaired"] = LeafScore(arms["medium.ws"], gold, goldOwner, ordinal),
                };
                perOrdinal.Add(row);
            }

            // Policies.  Every trigger reads only the none reply's harness outcome, never Gold,
            // ordinal or document identity.
            var refusedOrdinals = arms["none.strict"].Refusals.Select(x => x.Ordinal).ToHashSet();
            var refusedAfterWs = arms["none.ws"].Refusals.Select(x => x.Ordinal).ToHashSet();
            // NOT_OWNED is the model citing a halo atom; the owner leaf decides it, so it says
            // nothing about this leaf's own decisions.  Only refusals on owned aliases count.
            var ownedRefusal = arms["none.strict"].Refusals.Where(x => x.Status != "NOT_OWNED").Select(x => x.Ordinal).ToHashSet();
            // Exploratory only: the threshold was chosen after seeing the T5A table, so it is
            // Gold-informed and must be validated on held-out documents before any use.
            var lowEmission = ordinals.Where(o => arms["none.strict"].EmittedMember.GetValueOrDefault(o) <= 2).ToHashSet();
            var policies = new (string Name, string NoneArm, string MediumArm, HashSet<int> Escalate)[]
            {
                ("P0_NONE_STRICT", "none.strict", "medium.strict", []),
                ("P1_MEDIUM_STRICT_ALL", "none.strict", "medium.strict", ordinals.ToHashSet()),
                ("P2_NONE_BOUNDARY_WS_REPAIR", "none.ws", "medium.ws", []),
                ("P3_ESCALATE_ON_ANY_NONE_REFUSAL", "none.strict", "medium.strict", refusedOrdinals),
                ("P4_WS_REPAIR_THEN_ESCALATE_ON_RESIDUAL_REFUSAL", "none.ws", "medium.ws", refusedAfterWs),
                ("P5_MEDIUM_WS_REPAIR_ALL", "none.ws", "medium.ws", ordinals.ToHashSet()),
                ("P6_ESCALATE_ON_OWNED_ALIAS_REFUSAL", "none.strict", "medium.strict", ownedRefusal),
                ("P7_WS_REPAIR_THEN_ESCALATE_ON_RESIDUAL_OWNED_REFUSAL", "none.ws", "medium.ws",
                    arms["none.ws"].Refusals.Where(x => x.Status != "NOT_OWNED").Select(x => x.Ordinal).ToHashSet()),
                ("X8_EXPLORATORY_P6_PLUS_LOW_EMISSION_LE2_GOLD_INFORMED", "none.strict", "medium.strict", ownedRefusal.Union(lowEmission).ToHashSet()),
            };
            foreach (var (name, noneArm, mediumArm, escalate) in policies)
            {
                var spliced = arms[noneArm].Candidates.Where(x => !escalate.Contains(x.Ordinal))
                    .Concat(arms[mediumArm].Candidates.Where(x => escalate.Contains(x.Ordinal)));
                var extra = mediumLeaves.Where(x => escalate.Contains(x.Ordinal)).Sum(x => x.CompletionTokens);
                if (!policyTotals.TryGetValue(name, out var list)) policyTotals[name] = list = [];
                list.Add((id, DocScore(spliced, gold), escalate.Count, extra));
            }

            docs.Add(new { documentId = id, goldClaims = gold.Count, crossLeafGoldIdentities = crossLeafGold, unownedGoldIdentities = gold.Count - goldOwner.Count });
        }

        var policyRows = policyTotals.Select(p =>
        {
            var tp = p.Value.Sum(x => x.Score.Tp); var fp = p.Value.Sum(x => x.Score.Fp); var fn = p.Value.Sum(x => x.Score.Fn);
            var escalated = p.Value.Sum(x => x.Escalated);
            return new
            {
                policy = p.Key,
                aggregate = Make(tp, fp, fn),
                byDocument = p.Value.ToDictionary(x => x.Doc, x => x.Score),
                escalatedLeaves = escalated,
                escalatedShareOfCohort = Math.Round((double)escalated / cohortSize, 4),
                extraMediumCompletionTokens = p.Value.Sum(x => x.ExtraCompletion),
            };
        }).ToArray();

        var byName = policyRows.ToDictionary(x => x.policy);
        var p2 = byName["P2_NONE_BOUNDARY_WS_REPAIR"];
        var medAll = byName["P1_MEDIUM_STRICT_ALL"];
        var noneStrict = byName["P0_NONE_STRICT"];
        bool Gate(Score aggregate, IReadOnlyDictionary<string, Score> byDocument, double share) =>
            aggregate.F1 >= medAll.aggregate.F1 &&
            byDocument["SRC-089"].F1 >= medAll.byDocument["SRC-089"].F1 - 0.02 &&
            byDocument["SRC-095"].F1 >= noneStrict.byDocument["SRC-095"].F1 - 0.01 &&
            share <= 0.30;

        var json = new
        {
            artifactKind = "a99_openrouter_streaming_t5b_escalation_signal",
            status = "T5B_PROVIDER_FREE_SIGNAL_DERIVATION_COMPLETE",
            providerCalls = 0,
            goldRead = true,
            goldUsedAsPolicyInput = false,
            productionPromotion = false,
            lineage = new
            {
                t3c = new { path = T3c, sha256 = Hash(T3c) },
                t5a = new { path = T5a, sha256 = Hash(T5a) },
                p05Manifest = new { path = P05Manifest, sha256 = Hash(P05Manifest) },
            },
            methodology = new
            {
                signals = "computed from the request's owned aliases and the none reply through the production decoder + canonicalizer + binder; Gold opened only for scoring",
                repairVariant = "BOUNDARY_WHITESPACE_WHOLE_ATOM: a quote refused as TEXT_NOT_IN_ATOM whose Trim() equals the named atom's text byte-exactly is treated as the whole atom; any other refused quote stays refused",
                goldOwnership = "multipart-aware: Gold identity owned by the leaf owning its first alias (T5A dropped multipart identities from per-ordinal FN)",
                replay = "medium arm is the already-accepted P05 reasoning-medium response for the same parent ordinal; spliced per policy, no new calls",
                gate = "aggregate F1 >= P05-medium-all; SRC-089 F1 within 0.02 of medium; SRC-095 F1 within 0.01 of none-strict; escalated share <= 0.30",
            },
            cohort = new { leaves = cohortSize, noneCompletionTokens = noneCompletion, mediumCompletionTokens = mediumCompletion },
            documents = docs,
            policies = policyRows.Select(p => new { p.policy, p.aggregate, p.byDocument, p.escalatedLeaves, p.escalatedShareOfCohort, p.extraMediumCompletionTokens, passesGate = Gate(p.aggregate, p.byDocument, p.escalatedShareOfCohort) }).ToArray(),
            perOrdinal,
        };
        FreezeArtifact.AssertJson(Root, "openrouter-streaming-t5b-escalation-signal.v1.json", json);

        var table = string.Join("\n", policyRows.Select(p =>
            $"| {p.policy} | {p.aggregate.Tp} | {p.aggregate.Fp} | {p.aggregate.Fn} | {p.aggregate.F1:0.0000} | {p.byDocument["SRC-089"].F1:0.0000} | {p.byDocument["SRC-095"].F1:0.0000} | {p.escalatedLeaves}/{cohortSize} | {p.extraMediumCompletionTokens} | {(Gate(p.aggregate, p.byDocument, p.escalatedShareOfCohort) ? "PASS" : "FAIL")} |"));
        var md = $"""
            # T5B escalation signal derivation (provider-free)

            Status: `T5B_PROVIDER_FREE_SIGNAL_DERIVATION_COMPLETE` · provider calls `0` · Gold used as policy input `false`

            Medium arm = already-accepted P05 reasoning-medium responses, spliced per leaf (replay, no new calls).
            Gate: aggregate F1 >= full-medium; SRC-089 F1 within 0.02 of medium; SRC-095 F1 within 0.01 of none; escalated <= 30%.

            | policy | TP | FP | FN | F1 | SRC-089 F1 | SRC-095 F1 | escalated | extra medium completion tokens | gate |
            |---|---:|---:|---:|---:|---:|---:|---:|---:|---|
            {table}

            ## Findings

            1. Most of the SRC-089 none-vs-medium loss is not missing reasoning. Under reasoning=none the model
               emits the same multipart claims as medium but prefixes continuation parts' `verbatimText` with a
               space; the canonicalizer refuses them as `TextNotInAtom`. SRC-089 ordinal 2: 7 claims emitted,
               4 refused, all 4 recovered by trimming boundary whitespace when the trimmed quote is the whole atom.
            2. T5A undercounted per-leaf FN: it assigned only single-alias Gold identities. Multipart-aware
               ownership assigns every Gold identity (0 cross-leaf, 0 unowned).
            3. `NOT_OWNED` refusals (model cites a halo atom) fire on neutral leaves and are not a signal.
               Refusal on an owned alias is: P6/P7 escalate 6/5 leaves and exceed full-medium aggregate F1.
            4. SRC-089 ordinal 5 (+3 TP under medium) has no refusal signal. The only probe that catches it
               (<= 2 emitted member claims, Gold-informed threshold) also catches SRC-095 ordinal 30, where medium
               adds 12 FP, and cancels the gain. No generic pre-Gold signal for it was found.
            5. Boundary-whitespace repair also binds 2 genuine FPs on SRC-095 ordinal 18 that strict refusal had hidden.

            ## Decision needed before T5C live

            P7 is the best generic policy but fails only the SRC-089-closeness gate (ordinal 5). A live T5C rerun
            of P7's leaves would re-measure the same replayed outcome, so it is not run until the gate or the
            policy is decided. The whitespace repair is a shared-contract canonicalizer change and needs its own
            approval and full suite before production.
            """;
        FreezeArtifact.AssertText(Root, "openrouter-streaming-t5b-escalation-signal.v1.md", md.ReplaceLineEndings("\n") + "\n");

        // The finding this artifact freezes: the none->medium SRC-089 loss is harness refusal of
        // boundary-whitespace quotes, recoverable at zero provider calls.
        Assert.True(p2.aggregate.Tp > noneStrict.aggregate.Tp);
    }

    private static BindResult Bind(IReadOnlyList<Leaf> leaves, IReadOnlyList<SemanticSourceAtom> atoms, Repair repair)
    {
        var byAlias = new Dictionary<string, SemanticSourceAtom>(StringComparer.Ordinal);
        foreach (var atom in atoms) byAlias.TryAdd(atom.Alias, atom);
        var candidates = new List<Candidate>();
        var refusals = new List<Refusal>();
        var emitted = new Dictionary<int, int>();
        foreach (var leaf in leaves)
        {
            var owned = OwnedAliases(leaf.RequestFile);
            var content = ReadContent(leaf);
            if (content is null || !content.Value.TryGetProperty("headings", out var headings)) continue;
            foreach (var entry in headings.EnumerateArray())
            {
                var fn = entry.TryGetProperty("semanticFunction", out var f) ? f.GetString() ?? "" : "";
                var member = SemanticFunctionMembershipContractV1.IsMember(fn);
                if (member) emitted[leaf.Ordinal] = emitted.GetValueOrDefault(leaf.Ordinal) + 1;
                var decoded = SemanticFunctionMembershipContractV1.Decode(entry);
                if (decoded.Proposals.Count != 1) { if (member) refusals.Add(new(leaf.Ordinal, "DECODE", false)); continue; }
                var parts = decoded.Proposals[0].SourceParts;
                if (parts is null || parts.Any(x => !owned.Contains(x.SourceAlias))) { if (member) refusals.Add(new(leaf.Ordinal, "NOT_OWNED", false)); continue; }

                var whitespaceOnly = parts.Any(p => p.VerbatimText is { } v && v != v.Trim() &&
                    byAlias.TryGetValue(p.SourceAlias, out var a) && v.Trim() == a.Text);
                // The production canonicalizer now repairs this shape (CASE B2).  Strict reproduces
                // the byte-exact canonicalizer these responses were first scored under: such a quote
                // cannot occur in an atom shorter than itself, so it was TextNotInAtom.
                if (repair == Repair.Strict && whitespaceOnly)
                {
                    if (member) refusals.Add(new(leaf.Ordinal, nameof(SemanticSourcePartsStatus.TextNotInAtom), true));
                    continue;
                }

                var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, parts);
                if (!canonical.IsCanonical) { if (member) refusals.Add(new(leaf.Ordinal, canonical.Status.ToString(), whitespaceOnly)); continue; }
                var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
                if (!bound.IsBound) { if (member) refusals.Add(new(leaf.Ordinal, "BIND_" + bound.Status, whitespaceOnly)); continue; }
                candidates.Add(new(leaf.Ordinal, bound.Identity, member));
            }
        }
        return new(candidates, refusals, emitted);
    }

    private static JsonElement? ReadContent(Leaf leaf)
    {
        var text = File.ReadAllText(TestRepository.Path(leaf.ResponseFile));
        using var raw = JsonDocument.Parse(text);
        if (leaf.NoneArm) return raw.RootElement.Clone();
        if (raw.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() is not { } s) return null;
        try { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }
        catch (JsonException)
        {
            var first = s.IndexOf('{'); var last = s.LastIndexOf('}');
            if (first < 0 || last <= first) return null;
            try { using var d = JsonDocument.Parse(s[first..(last + 1)]); return d.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
    }

    private static Dictionary<string, int> AliasOwnership(IReadOnlyList<Leaf> leaves)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
            foreach (var alias in OwnedAliases(leaf.RequestFile)) result.TryAdd(alias, leaf.Ordinal);
        return result;
    }

    private static HashSet<string> OwnedAliases(string requestFile)
    {
        using var request = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(requestFile)));
        var user = request.RootElement.GetProperty("messages").EnumerateArray()
            .First(x => x.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        var split = user.LastIndexOf("SCHEMA=", StringComparison.Ordinal);
        var prefix = split < 0 ? user : user[..split];
        var m = Regex.Match(prefix, "\"ownedSourceAliases\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
        if (!m.Success) return new HashSet<string>(StringComparer.Ordinal);
        return Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    // Identity format: "alias:start-end|alias:start-end".  Aliases themselves contain ':'.
    private static IEnumerable<string> IdentityAliases(string identity) =>
        identity.Split('|').Select(s => s[..s.LastIndexOf(':')]);

    private static object LeafScore(BindResult arm, HashSet<string> gold, Dictionary<string, int> goldOwner, int ordinal)
    {
        var claims = arm.Candidates.Where(x => x.Ordinal == ordinal && x.Member).Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);
        var tp = claims.Count(gold.Contains);
        var owned = gold.Where(g => goldOwner.TryGetValue(g, out var o) && o == ordinal).ToArray();
        return new { tp, fp = claims.Count - tp, fn = owned.Count(g => !claims.Contains(g)), ownedGold = owned.Length };
    }

    private static Score DocScore(IEnumerable<Candidate> candidates, HashSet<string> gold)
    {
        var claims = candidates.Where(x => x.Member).Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);
        var tp = claims.Count(gold.Contains);
        return Make(tp, claims.Count - tp, gold.Count(g => !claims.Contains(g)));
    }

    private static Score Make(int tp, int fp, int fn)
    {
        var p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        var f = p + r == 0 ? 0 : 2 * p * r / (p + r);
        return new Score(tp, fp, fn, Math.Round(p, 4), Math.Round(r, 4), Math.Round(f, 4));
    }

    private static string Hash(string path) => CanonicalArtifactHash.OfTextFile(TestRepository.Path(path));
}
