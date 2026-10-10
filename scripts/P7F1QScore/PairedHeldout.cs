using System.Text.Json;
using DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Pre-registered paired comparison of two held-out arms scored on the same frozen Gold (layout-aware chunking
/// experiment, 2026-10-11): per-arm accuracy and per-class P/R/F1, paired wrong->right / right->wrong over the shared
/// scored rows, exact two-sided McNemar (binomial on the discordant pairs), per-document deltas. A difference is
/// called only when p &lt; 0.05. Reads only the two score files; no provider call, no Gold mutation.
/// </summary>
internal static class F1QHeldoutPairedComparison
{
    public static byte[] Run(string scoreAPath, string scoreBPath, string labelA, string labelB)
    {
        using var a = JsonDocument.Parse(File.ReadAllBytes(scoreAPath));
        using var b = JsonDocument.Parse(File.ReadAllBytes(scoreBPath));
        Need(a.RootElement.GetProperty("goldFreezeSha256").GetString() == b.RootElement.GetProperty("goldFreezeSha256").GetString(), "GOLD_FREEZE_DIFFERS");
        var na = a.RootElement.GetProperty("overall").GetProperty("normalizedFinal").GetProperty("occurrences").GetInt32();
        var nb = b.RootElement.GetProperty("overall").GetProperty("normalizedFinal").GetProperty("occurrences").GetInt32();
        Need(na == nb, "SCORED_UNIVERSE_DIFFERS");
        var wrongA = Wrong(a); var wrongB = Wrong(b);
        int wrongToRight = wrongA.Keys.Count(k => !wrongB.ContainsKey(k)), rightToWrong = wrongB.Keys.Count(k => !wrongA.ContainsKey(k));
        int bothWrong = wrongA.Keys.Count(wrongB.ContainsKey), bothRight = na - wrongToRight - rightToWrong - bothWrong;
        var p = McNemarExact(wrongToRight, rightToWrong);
        var docs = a.RootElement.GetProperty("byDocument").EnumerateObject().Select(d => d.Name).Order(StringComparer.Ordinal).ToArray();
        var report = new
        {
            version = "P7_F1Q_HELDOUT_PAIRED_COMPARISON_V1",
            armA = new { label = labelA, scoreFileSha256 = SpatialCanonical.Hash(File.ReadAllBytes(scoreAPath)), planSha256 = a.RootElement.GetProperty("planSha256").GetString(), summary = Summary(a) },
            armB = new { label = labelB, scoreFileSha256 = SpatialCanonical.Hash(File.ReadAllBytes(scoreBPath)), planSha256 = b.RootElement.GetProperty("planSha256").GetString(), summary = Summary(b) },
            goldFreezeSha256 = a.RootElement.GetProperty("goldFreezeSha256").GetString(), scoredRows = na,
            paired = new { wrongToRight, rightToWrong, bothWrong, bothRight, mcnemarExactTwoSidedP = Math.Round(p, 6),
                verdict = p < 0.05 ? (wrongToRight > rightToWrong ? "B_BETTER_SIGNIFICANT" : "A_BETTER_SIGNIFICANT") : "NO_SIGNIFICANT_DIFFERENCE" },
            transitions = wrongA.Keys.Union(wrongB.Keys).Select(k => (k, from: wrongA.GetValueOrDefault(k)?.Pred ?? "CORRECT", to: wrongB.GetValueOrDefault(k)?.Pred ?? "CORRECT",
                    gold: (wrongA.GetValueOrDefault(k) ?? wrongB[k])!.Value.Gold))
                .Where(t => t.from != t.to).GroupBy(t => $"{t.gold}: {t.from} -> {t.to}").OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            byDocument = docs.Select(d => new { document = d, accuracyA = Acc(a, d), accuracyB = Acc(b, d), delta = Math.Round(Acc(b, d) - Acc(a, d), 4),
                wrongToRight = wrongA.Keys.Count(k => k.StartsWith(d + "|", StringComparison.Ordinal) && !wrongB.ContainsKey(k)),
                rightToWrong = wrongB.Keys.Count(k => k.StartsWith(d + "|", StringComparison.Ordinal) && !wrongA.ContainsKey(k)) }),
            rule = "PRE_REGISTERED_5CAAA82_DIFFERENCE_CALLED_ONLY_IF_MCNEMAR_P_LT_0_05_HELDOUT_NOW_USED_FOR_METHOD_COMPARISON",
            providerCalls = 0, goldMutation = "NONE",
        };
        return JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
    }

    static Dictionary<string, (string Gold, string Pred)?> Wrong(JsonDocument s) => s.RootElement.GetProperty("errors").EnumerateArray()
        .ToDictionary(e => e.GetProperty("doc").GetString() + "|" + e.GetProperty("alias").GetString(),
            e => ((string Gold, string Pred)?)(e.GetProperty("gold").GetString()!, e.GetProperty("predicted").GetString()!), StringComparer.Ordinal);

    static object Summary(JsonDocument s)
    {
        var o = s.RootElement.GetProperty("overall").GetProperty("normalizedFinal"); var c = o.GetProperty("classes");
        object Cls(string k) => new { precision = c.GetProperty(k).GetProperty("precision"), recall = c.GetProperty(k).GetProperty("recall"), f1 = c.GetProperty(k).GetProperty("f1") };
        var r = s.RootElement.GetProperty("requests");
        return new { correct = o.GetProperty("correct").GetInt32(), scored = o.GetProperty("occurrences").GetInt32(),
            accuracy = Math.Round((double)o.GetProperty("correct").GetInt32() / o.GetProperty("occurrences").GetInt32(), 4),
            establishes = Cls("ESTABLISHES_STRUCTURE"), represents = Cls("REPRESENTS_STRUCTURE"), other = Cls("OTHER"),
            requests = r.GetProperty("total").GetInt32(), firstAttemptAccepted = r.GetProperty("acceptedFirstAttemptNormalized").GetInt32(),
            costUsd = r.GetProperty("reportedCostUsd").GetDecimal() };
    }

    static double Acc(JsonDocument s, string doc)
    {
        var d = s.RootElement.GetProperty("byDocument").GetProperty(doc);
        return Math.Round((double)d.GetProperty("correct").GetInt32() / d.GetProperty("occurrences").GetInt32(), 4);
    }

    // Exact two-sided McNemar: binomial(n = b + c, 0.5), doubled smaller tail, capped at 1.
    internal static double McNemarExact(int b, int c)
    {
        var n = b + c; if (n == 0) return 1.0;
        var k = Math.Min(b, c); double tail = 0, logHalfN = n * Math.Log(0.5);
        for (var i = 0; i <= k; i++) tail += Math.Exp(LogChoose(n, i) + logHalfN);
        return Math.Min(1.0, 2 * tail);
    }

    static double LogChoose(int n, int k) { double s = 0; for (var i = 1; i <= k; i++) s += Math.Log(n - k + i) - Math.Log(i); return s; }

    static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }
}
