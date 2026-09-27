using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// LLM_SEMANTIC_PILOT_V1, the analysis of the committed raw score (58854f7), against the deterministic held-out score of
/// the same document and Gold. Every residual is put in a family by a rule computed from the source (which page band the
/// atom sits in, what its text is shaped like) and from the Gold's own review pattern - not by a hand-written list of
/// identities. Nothing here re-scores or changes a prompt, a model, the facts or the binder.
/// </summary>
public sealed partial class LlmSemanticPilotV1AnalysisTests
{
    private const string Root = LlmSemanticPilotV1Tests.Root;

    /// <summary>
    /// The deterministic held-out score of the same document and Gold, the engine that produced it, and the two page bands
    /// of the source that carry no structure of their own: its contents list and its index (0 where the document has none).
    /// </summary>
    private static readonly (string Id, string Engine, string Score, (int From, int To) Contents, int IndexFrom)[] Documents =
    [
        ("SRC-089", "GENERIC_AUDIT_ENGINE_V1.4", "eval/a99-closed-loop/generic-audit-v1_4-held-out/SRC-089.blind-score.json", (0, 0), 0),
        ("SRC-095", "GENERIC_AUDIT_ENGINE_V1.3", "eval/a99-closed-loop/generic-audit-v1_3-held-out/SRC-095.blind-score.json", (2, 4), 54),
    ];

    [GeneratedRegex(@"^(Bishop Standards Track Page|RFC 9114 HTTP/3 June)")] private static partial Regex Rfc9114Furniture();
    [GeneratedRegex(@"^(Table|Figure|Chart|Exhibit) [A-Z]?\d")] private static partial Regex Caption();

    private static Dictionary<string, int> PageOfAlias(string id)
    {
        var rows = File.ReadAllLines(TestRepository.Path($"eval/a99-closed-loop/source-review-v1/{id}/atom-glyph-facts.tsv"))
            .Select(l => l.Split('\t'));
        return rows.ToDictionary(r => r[0], r => int.Parse(r[1]), StringComparer.Ordinal);
    }

    private static int PageOf(IReadOnlyDictionary<string, int> pages, string identity)
    {
        var first = identity.Split('|')[0];
        return pages.GetValueOrDefault(first[..first.LastIndexOf(':')], -1);
    }

    private static IEnumerable<string> AliasesOf(string identity) =>
        identity.Split('|').Select(part => part[..part.LastIndexOf(':')]);

    /// <summary>
    /// The family of a claim the model proposed that the Gold does not hold. The reviewer's own reading of the source decides
    /// first - a Gold claim's occurrence, or a non-heading the review named and why - then the page band, then the shape of
    /// the text. The model's semanticRole is the last resort, and says so in the name.
    /// </summary>
    private static string FalsePositiveFamily(string identity, string text, string role, int page, (int From, int To) contents, int indexFrom,
        IReadOnlySet<string> goldAliases, IReadOnlyDictionary<string, string> reviewedNonHeadings)
    {
        // The same occurrence as a Gold heading, claimed with a different extent - not another heading.
        if (AliasesOf(identity).Any(goldAliases.Contains)) return "GOLD_HEADING_WRONG_EXTENT";
        if (AliasesOf(identity).Select(a => reviewedNonHeadings.GetValueOrDefault(a)).FirstOrDefault(p => p is not null) is { } pattern)
            return $"REVIEWED_NON_HEADING:{pattern}";
        if (Rfc9114Furniture().IsMatch(text)) return "PAGE_FURNITURE";
        if (contents.From > 0 && page >= contents.From && page <= contents.To) return "CONTENTS_ENTRY";
        if (indexFrom > 0 && page >= indexFrom) return "INDEX_ENTRY";
        if (Caption().IsMatch(text)) return "CAPTION";
        return $"MODEL_ROLE_{role.ToUpperInvariant().Replace('-', '_')}";
    }

    /// <summary>What the source-only review named as a set-apart non-heading, by alias: its verdict and pattern.</summary>
    private static Dictionary<string, string> ReviewedNonHeadings(string id)
    {
        using var items = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/source-review-v1/{id}/review-items.json")));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.RootElement.GetProperty("items").EnumerateArray()
                     .Where(i => i.GetProperty("verdict").GetString() == "NON_HEADING"))
        foreach (var part in item.GetProperty("parts").EnumerateArray())
            map[part.GetProperty("sourceAlias").GetString()!] = item.GetProperty("pattern").GetString()!;
        return map;
    }

    [Fact]
    public void Freeze_the_analysis()
    {
        using var score = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/score.v1.json")));
        var rows = new List<object>();

        foreach (var (id, engine, deterministicPath, contents, indexFrom) in Documents)
        {
            var document = score.RootElement.GetProperty("documents").EnumerateArray().Single(d => d.GetProperty("documentId").GetString() == id);
            var residuals = document.GetProperty("residuals");
            var pages = PageOfAlias(id);

            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"eval/a99-closed-loop/gold/{id}.gold.json")));
            var goldClaims = gold.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().ToArray();
            var goldAliases = goldClaims.SelectMany(c => c.GetProperty("sourceParts").EnumerateArray()
                .Select(p => p.GetProperty("sourceAlias").GetString()!)).ToHashSet(StringComparer.Ordinal);
            var reviewedNonHeadings = ReviewedNonHeadings(id);
            var patternOf = goldClaims.ToDictionary(c => c.GetProperty("identity").GetString()!, c => c.GetProperty("pattern").GetString()!, StringComparer.Ordinal);

            var falsePositives = residuals.GetProperty("nonGoldTrue").EnumerateArray().Select(m =>
            {
                var h = m.GetProperty("hypothesis");
                var identity = h.GetProperty("identity").GetString()!;
                var text = h.GetProperty("Text").GetString()!;
                var role = h.GetProperty("Evidence")[0].GetString()!.Replace("semanticRole=", "");
                return FalsePositiveFamily(identity, text, role, PageOf(pages, identity), contents, indexFrom, goldAliases, reviewedNonHeadings);
            }).GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
              .ToDictionary(g => g.Key, g => g.Count());

            var missed = residuals.GetProperty("goldNotEngineTrue").EnumerateArray().Select(m => new
            {
                bucket = m.GetProperty("bucket").GetString()!,
                pattern = patternOf[m.GetProperty("goldIdentity").GetString()!],
            }).GroupBy(m => (m.pattern, m.bucket)).OrderBy(g => g.Key.pattern, StringComparer.Ordinal)
              .Select(g => new { g.Key.pattern, g.Key.bucket, claims = g.Count() }).ToArray();

            using var deterministic = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(deterministicPath)));
            var them = deterministic.RootElement.GetProperty("headline");
            var us = document.GetProperty("headline");
            double D(JsonElement e, string k) => e.GetProperty(k).GetDouble();
            int I(JsonElement e, string k) => e.GetProperty(k).GetInt32();

            rows.Add(new
            {
                documentId = id,
                goldClaims = I(us, "goldClaims"),
                llm = new
                {
                    model = "qwen/qwen3.7-flash",
                    truePositives = I(us, "truePositives"), falsePositives = I(us, "falsePositives"), falseNegatives = I(us, "falseNegatives"),
                    precision = D(us, "truePrecision"), recall = D(us, "trueRecall"), f1 = D(us, "f1"),
                    proposals = document.GetProperty("uniqueBoundProposals").GetInt32(),
                    unbindable = document.GetProperty("refusals"),
                    invalidResponses = document.GetProperty("invalidResponses").GetInt32(),
                    declinedIsHeadingFalse = document.GetProperty("declinedIsHeadingFalse").GetInt32(),
                },
                deterministic = new
                {
                    engine,
                    truePositives = I(them, "truePositives"), falsePositives = I(them, "falsePositives"), falseNegatives = I(them, "falseNegatives"),
                    precision = D(them, "truePrecision"), recall = D(them, "trueRecall"), f1 = D(them, "f1"),
                    score = new { path = deterministicPath, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(deterministicPath)) },
                },
                exactByGoldPattern = document.GetProperty("byGoldPattern"),
                missedByPattern = missed,
                falsePositiveFamilies = falsePositives,
                falsePositiveNote = "GOLD_HEADING_WRONG_EXTENT counts a proposal that opens a Gold claim at the right alias but does not cover its parts: the same heading, scored both as a miss and as a false positive",
            });
        }

        FreezeArtifact.AssertJson(Root, "analysis.v1.json", new
        {
            artifactKind = "a99_llm_semantic_pilot_analysis",
            study = "LLM_SEMANTIC_PILOT_V1",
            modelProviderVlmCallsInThisAnalysis = 0,
            rawScore = new { path = $"{Root}/score.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Root}/score.v1.json")), commit = "58854f7" },
            comparability = "each row compares the LLM route and the deterministic engine on the same document, the same Gold and the same scorer (GENERIC_EXACT_SCORER_V1, exact claim identity); the deterministic figure is that document's committed held-out score, never recomputed",
            reviewState = "the production contract answers isHeading true or false and has no NEEDS_REVIEW state, so the deterministic engine's review fail-safe has no counterpart here: a claim the model is unsure of is either proposed or absent",
            documents = rows,
            findings = new[]
            {
                "SRC-089: the assembly the deterministic engine could not do, the LLM does. 23 of 26 article headings ('Article N.' bold plus its regular-weight title, wrapping onto lower-case lines) and 5 of 5 chapter headings (label plus title line) are exact multipart claims, against 0 of 26 and 0 of 5 for V1.4. Its 3 remaining article misses drop the last wrapped line of a three-line title",
                "SRC-089: the four colon-ended clause labels (S089_Q3, the user's TRUE) are unproposed by both routes: nothing in the source sets them apart, and the LLM did not read them as structure either",
                "SRC-095: recall rose (0.913 against V1.3's 0.864 - every numbered section, including the 6 with their number set apart, and the 6 matter sections) but precision fell to 0.370 against 0.967: the model returns navigation as structure",
                "SRC-095: the largest false-positive family is the table of contents (87 claims) and the index (24); page furniture adds 29. The deterministic engine has explicit rules for all three (gap 8's pointer test, the furniture test) and produced 3 false positives in total",
                "both documents: the model names these families correctly in semanticRole (running-header, page-footer, index-entry, footnote, signature-label) and still returns them as headings, so the loss is in what the contract asks of it, not in whether it recognizes them",
                "both documents: the identity distinctions the user decided are not reproduced - 'RFC 9114 HTTP/3' proposed as one title where the Gold holds 'HTTP/3' alone (S095_Q1), and the decree title proposed with its footnote mark '(*)' (S089_Q1)",
                "the eight SRC-095 index group letters (S095_Q2) are unproposed, as under V1.3, which sent them to review",
                "transport and contract held: 0 invalid responses over 25 requests, 0 claims declined, and 8 of 262 proposals unbindable (7 out of the owned segment, 1 out of source order)",
            },
            limitations = new[]
            {
                "two documents, one model, one run, no repeats: nothing here measures variance",
                "the Golds were authored by the same reviewer who designed the deterministic engine; the LLM route had no part in them",
                "placement was not run, so hierarchy is still not measured",
                "the pilot spent 26 of 30 authorized calls; no cohort run is authorized",
            },
        });
    }
}
