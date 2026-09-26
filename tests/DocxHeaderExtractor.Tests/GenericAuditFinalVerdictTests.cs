using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The end of the generic audit study (user, 2026-09-26: after the SRC-089 held-out, the cross-document summary, the
/// hierarchy evaluation and the final verdict). Every number is read from a committed artifact - the raw held-out scores,
/// their residual classifications, the V1.4 development table and the canonical Gold registry - never typed by hand.
/// Nothing here re-scores, re-classifies or tunes: the held-out scores stay as committed at their reveals.
/// </summary>
public sealed class GenericAuditFinalVerdictTests
{
    private const string Dir = "eval/a99-closed-loop/final-verdict-v1";
    private const string A = "eval/a99-closed-loop/";

    private sealed record HeldOut(string Id, string Engine, string Facts, string Genre, string Score, string Classification, string RevealCommit);

    private static readonly HeldOut[] Chain =
    [
        new("SRC-029", "GENERIC_AUDIT_ENGINE_V1", "PDF_SOURCE_FACTS_V1", "procurement (standard bidding document)", A + "generic-audit-v1/SRC-029.blind-score.v1.json", A + "generic-audit-v1/SRC-029.residual-classification.v1.json", "SRC029_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-041", "GENERIC_AUDIT_ENGINE_V1.1", "PDF_SOURCE_FACTS_V1", "financial (statements)", A + "generic-audit-v1_1/SRC-041.blind-score.v1_1.json", A + "generic-audit-v1_1/SRC-041.residual-classification.v1_1.json", "SRC041_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-042", "GENERIC_AUDIT_ENGINE_V1.2", "PDF_SOURCE_FACTS_V1", "financial (statements)", A + "generic-audit-v1_2/SRC-042.blind-score.v1_2.json", A + "generic-audit-v1_2/SRC-042.residual-classification.v1_2.json", "SRC042_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-044", "GENERIC_AUDIT_ENGINE_V1.2", "PDF_SOURCE_FACTS_V1", "financial (statements)", A + "generic-audit-v1_2/SRC-044.blind-score.v1_2.json", A + "generic-audit-v1_2/SRC-044.residual-classification.v1_2.json", "SRC044_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-053", "GENERIC_AUDIT_ENGINE_V1.2", "PDF_SOURCE_FACTS_V1", "financial (information statement)", A + "generic-audit-v1_2/SRC-053.blind-score.v1_2.json", A + "generic-audit-v1_2/SRC-053.residual-classification.v1_2.json", "SRC053_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-054", "GENERIC_AUDIT_ENGINE_V1.2", "PDF_SOURCE_FACTS_V2", "financial (information statement)", A + "generic-audit-v1_2-pdf-facts-v2/SRC-054.blind-score.json", A + "generic-audit-v1_2-pdf-facts-v2/SRC-054.residual-classification.json", "SRC054_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-095", "GENERIC_AUDIT_ENGINE_V1.3", "PDF_SOURCE_FACTS_V3", "technical standard (RFC)", A + "generic-audit-v1_3-held-out/SRC-095.blind-score.json", A + "generic-audit-v1_3-held-out/SRC-095.residual-classification.json", "SRC095_BLIND_GENERALIZATION_AUDIT_V1"),
        new("SRC-089", "GENERIC_AUDIT_ENGINE_V1.4", "PDF_SOURCE_FACTS_V3", "legal (decree, translated)", A + "generic-audit-v1_4-held-out/SRC-089.blind-score.json", A + "generic-audit-v1_4-held-out/SRC-089.residual-classification.json", "SRC089_BLIND_GENERALIZATION_AUDIT_V1"),
    ];

    /// <summary>The one held-out the source facts made unmeasurable (SRC-053: size 1 and no bold on every atom under V1 facts).</summary>
    private const string MeasurementInvalid = "SRC-053";

    private static (int Tp, int Fp, int Fn, int Gold, double F1) Headline(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
        var root = doc.RootElement;
        if (root.TryGetProperty("membership", out var m)) // SRC-029's V1 score layout
            return (m.GetProperty("truePositives").GetInt32(), m.GetProperty("falsePositives").GetInt32(), m.GetProperty("falseNegatives").GetInt32(),
                root.GetProperty("gold").GetProperty("claims").GetInt32(), m.GetProperty("f1").GetDouble());
        var h = root.GetProperty("headline");
        return (h.GetProperty("truePositives").GetInt32(), h.GetProperty("falsePositives").GetInt32(), h.GetProperty("falseNegatives").GetInt32(),
            h.GetProperty("goldClaims").GetInt32(), h.GetProperty("f1").GetDouble());
    }

    private static Dictionary<string, int> Buckets(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(path)));
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in doc.RootElement.GetProperty("byBucket").EnumerateObject())
        {
            // V1's layout names known gaps per id (B_KNOWN_B1) and counts review noise; count claims and non-Gold TRUEs only.
            var key = bucket.Name.StartsWith("B_KNOWN", StringComparison.Ordinal) ? "B_KNOWN" : bucket.Name;
            var n = bucket.Value.EnumerateObject().Where(p => p.Name is "goldNotEngineTrue" or "nonGoldTrue" or "GOLD_NOT_ENGINE_TRUE" or "NON_GOLD_TRUE").Sum(p => p.Value.GetInt32());
            result[key] = result.GetValueOrDefault(key) + n;
        }
        return result;
    }

    private static double Round(double v) => Math.Round(v, 4);

    private static object Micro(IEnumerable<HeldOut> rows)
    {
        var h = rows.Select(r => Headline(r.Score)).ToArray();
        int tp = h.Sum(x => x.Tp), fp = h.Sum(x => x.Fp), fn = h.Sum(x => x.Fn);
        double p = tp + fp == 0 ? 0 : (double)tp / (tp + fp), r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return new { documents = h.Length, goldClaims = h.Sum(x => x.Gold), truePositives = tp, falsePositives = fp, falseNegatives = fn, precision = Round(p), recall = Round(r), f1 = p + r == 0 ? 0 : Round(2 * p * r / (p + r)) };
    }

    [Fact]
    public void Summarize_across_documents()
    {
        var rows = Chain.Select(r =>
        {
            var h = Headline(r.Score);
            return new
            {
                documentId = r.Id,
                study = r.RevealCommit,
                engine = r.Engine,
                sourceFacts = r.Facts,
                genre = r.Genre,
                goldClaims = h.Gold,
                truePositives = h.Tp,
                falsePositives = h.Fp,
                falseNegatives = h.Fn,
                f1 = h.F1,
                buckets = Buckets(r.Classification),
                measurementValid = r.Id != MeasurementInvalid,
                score = new { path = r.Score, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(r.Score)) },
            };
        }).ToArray();

        using var v14 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(GenericAuditEngineV14Tests.Root + "/GENERIC_AUDIT_ENGINE_V1.4.freeze.json")));
        FreezeArtifact.AssertJson(Dir, "cross-document-summary.v1.json", new
        {
            artifactKind = "a99_generic_audit_cross_document_summary",
            modelProviderVlmCalls = 0,
            scorer = GenericAudit.V1_1.ExactScorer.ScorerId,
            rule = "each held-out is scored once, blind, against the Gold frozen before its reveal, by the engine version frozen before its pre-registration; the next version is developed on it afterwards. The chain below is those raw scores - one engine version each, never re-scored",
            heldOutChain = rows,
            micro = new
            {
                allRaw = Micro(Chain),
                excludingTheInvalidMeasurement = Micro(Chain.Where(r => r.Id != MeasurementInvalid)),
                note = "micro totals mix engine versions V1 to V1.4 - a summary of the study, not the accuracy of any one version",
            },
            byGenre = Chain.GroupBy(r => r.Genre.Split(' ')[0]).Select(g => new { genre = g.Key, documents = g.Select(r => r.Id).ToArray(), micro = Micro(g) }).ToArray(),
            bucketsTotal = Chain.SelectMany(r => Buckets(r.Classification)).GroupBy(kv => kv.Key).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value)),
            latestEngineDevelopmentFit = new
            {
                engine = "GENERIC_AUDIT_ENGINE_V1.4",
                role = "DEVELOPMENT - every row was developed on or revealed before V1.4 froze; not a generalization result",
                rows = JsonSerializer.Deserialize<object>(v14.RootElement.GetProperty("development").GetProperty("rows").GetRawText()),
            },
        });
    }

    [Fact]
    public void Evaluate_hierarchy()
    {
        var registry = CanonicalGoldRegistry.Entries;
        var authorities = registry.Select(e =>
        {
            using var gold = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldAuthoredSourceTests.AuthoredRoot}/{e.AuthorityId}.gold.json")));
            var root = gold.RootElement;
            var occurrence = root.GetProperty("occurrence");
            return new
            {
                authorityId = e.AuthorityId,
                hierarchyEvaluable = root.GetProperty("declaredCapabilities").GetProperty("hierarchyEvaluable").GetBoolean(),
                occurrenceLevel = occurrence.ValueKind != JsonValueKind.Null,
            };
        }).ToArray();
        var engineFields = typeof(GenericAudit.V1_4.SemanticHypothesis).GetProperties().Select(p => p.Name).ToArray();

        FreezeArtifact.AssertJson(Dir, "hierarchy-evaluation.v1.json", new
        {
            artifactKind = "a99_generic_audit_hierarchy_evaluation",
            modelProviderVlmCalls = 0,
            question = "does the pipeline place each heading at the right level under the right parent?",
            gold = new
            {
                authorities = authorities.Length,
                declaringHierarchy = authorities.Where(a => a.hierarchyEvaluable).Select(a => a.authorityId).ToArray(),
                declaringHierarchyWithOccurrences = authorities.Where(a => a.hierarchyEvaluable && a.occurrenceLevel).Select(a => a.authorityId).ToArray(),
                heldOutGoldDeclaringHierarchy = Chain.Where(r => authorities.Single(a => a.authorityId == r.Id).hierarchyEvaluable).Select(r => r.Id).ToArray(),
            },
            engine = new
            {
                hypothesisFields = engineFields,
                emitsLevel = engineFields.Any(f => f.Contains("Level", StringComparison.Ordinal)),
                emitsParent = engineFields.Any(f => f.Contains("Parent", StringComparison.Ordinal)),
            },
            metrics = new[] { "LEVEL_ACCURACY", "PARENT_ACCURACY", "FULL_PATH_ACCURACY" }.Select(m => new { metric = m, result = "NOT_EVALUABLE", correct = 0, denominator = 0 }).ToArray(),
            result = "NOT_EVALUABLE: the generic engine emits no level and no parent, and no Gold with occurrences declares a hierarchy (the one authority that declares it holds a count, not occurrences). Only occurrence membership and its semantic axes were ever measured",
            nearestMeasuredEvidence = "the scope and primary-function axes on exact matches, reported per held-out score (axesOnTruePositives); they classify a heading's kind, not its place in a tree",
            needed = new[]
            {
                "a Gold hierarchy per occurrence (level and parent claim), authored on the source and approved like membership",
                "an engine output for level and parent, frozen before a held-out",
                "a scorer for level, parent and full path over exact-matched claims",
            },
        });
    }

    [Fact]
    public void Record_the_final_verdict()
    {
        var valid = Chain.Where(r => r.Id != MeasurementInvalid).Select(r => (r.Id, r.Genre, Headline(r.Score).F1)).ToArray();
        FreezeArtifact.AssertJson(Dir, "final-verdict.v1.json", new
        {
            artifactKind = "a99_generic_audit_final_verdict",
            decidedBy = "the protocol the user set on 2026-09-26: V1.4 the last development cycle; SRC-089 the last held-out; a gate failing on real generalization gaps goes to the verdict with its limitation stated",
            modelProviderVlmCalls = 0,
            inputs = new
            {
                summary = new { path = $"{Dir}/cross-document-summary.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/cross-document-summary.v1.json")) },
                hierarchy = new { path = $"{Dir}/hierarchy-evaluation.v1.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/hierarchy-evaluation.v1.json")) },
            },
            verdict = "NOT_GENERALIZED",
            statement = "the generic heading audit does not generalize across document genres. It is accurate inside the family it was developed on - financial statements whose headings are typographically marked (held-out F1 0.94 to 0.98) - and every new genre found conventions it did not read: body-size table labels (0.80), RFC numbering and back matter (0.91), decree articles bold only on their number (0). Hierarchy was never measurable",
            findings = new object[]
            {
                new { id = "F1", finding = "within-family accuracy", evidence = valid.Where(v => v.Genre.StartsWith("financial (statements)", StringComparison.Ordinal)).Select(v => new { v.Id, f1 = v.F1 }).ToArray() },
                new { id = "F2", finding = "no generalization to new genres: each held-out of a new kind exposed new generic gaps (bucket B_NEW) that the previous development could not have seen", evidence = valid.Where(v => !v.Genre.StartsWith("financial (statements)", StringComparison.Ordinal)).Select(v => new { v.Id, v.Genre, f1 = v.F1 }).ToArray() },
                new { id = "F3", finding = "the ontology held: no held-out residual since V1.1 needed a new semantic category (C = 0); one document-specific residual (D) in the V1.1 held-out, none after", evidence = "bucketsTotal in the summary" },
                new { id = "F4", finding = "the source-fact layer can invalidate a measurement: SRC-053 read size 1 and no bold on every atom under PDF_SOURCE_FACTS_V1 (raw F1 0, kept); V2 and V3 fixed it and V3 read SRC-089 correctly", evidence = "SRC-053 classification; PDF_SOURCE_FACTS_V2/V3 freezes; SRC-089 measurementValidity" },
                new { id = "F5", finding = "where the engine fails it mostly finds the heading's place but not its extent - a number without its title, a heading's lines apart - and it rarely invents headings: precision stayed at or above 0.96 on every valid held-out since V1.2 except SRC-054 (0.78, body-size tables) and SRC-089 (0 exact matches, 32 of 36 Gold headings found partially)", evidence = "per held-out headline and goldBuckets" },
                new { id = "F6", finding = "hierarchy is NOT_EVALUABLE: no level or parent is emitted and no occurrence Gold declares a hierarchy", evidence = "hierarchy-evaluation.v1.json" },
            },
            limitations = new[]
            {
                "eight held-outs, five of one family; each engine version met one or two held-outs; the reviewer who wrote the source-only Golds also designed the engine (not double-blind); every Gold decision without precedent was the user's",
                "micro totals across the chain mix engine versions and are a summary of the study, not the accuracy of V1.4",
                "V1.4's development fit is measured on documents it was developed on",
                "occurrence membership and semantic axes only; no hierarchy, no visual binding",
            },
            notClaimed = new[] { "99% accuracy", "cross-genre generalization", "hierarchy accuracy", "production readiness beyond a review-assisted audit of marked financial documents" },
        });
    }
}
