using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Pre-registered held-out scorer (Issue #6 Decision 3), frozen before the provider run. Per-occurrence F1 on the
/// user-approved, frozen Gold of the source-only selected pages. Missing rows and abstentions are never OTHER and
/// count against recall. Reports STRICT_RAW and NORMALIZED contract views, first-attempt and retry-inclusive.
/// Sampled pages are not full-document recall; slices with fewer than 10 Gold positives are INCONCLUSIVE.
/// </summary>
internal static class F1QHeldoutScorer
{
    private const string Est = "ESTABLISHES_STRUCTURE", Rep = "REPRESENTS_STRUCTURE", Oth = "OTHER";
    private static readonly string[] Labels = [Est, Rep, Oth];
    public const int SparseThreshold = 10;
    internal sealed record Row(string Doc, string Stratum, string Family, string PageStratum, string Alias, string Gold,
        string? Final, string? First, string? Raw);

    public static byte[] Run(string planPath, string root, string manifestSha, string goldFreezePath, string goldFreezeSha, string selectionPath, string auditPath)
    {
        Need(SpatialCanonical.Hash(File.ReadAllBytes(Path.Combine(root, "manifest.json"))) == manifestSha, "RAW_FREEZE_REQUIRED");
        var goldFreezeBytes = File.ReadAllBytes(goldFreezePath);
        Need(SpatialCanonical.Hash(goldFreezeBytes) == goldFreezeSha, "GOLD_FREEZE_DRIFT");
        using var goldFreeze = JsonDocument.Parse(goldFreezeBytes);
        var goldApproval = goldFreeze.RootElement.GetProperty("status").GetString();
        Need(goldApproval is "USER_APPROVED_GOLD_FROZEN" or "GOLD_FROZEN_AI_REVIEWED_USER_DELEGATED", "GOLD_NOT_APPROVED");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planPath));
        using var selection = JsonDocument.Parse(File.ReadAllBytes(selectionPath));
        using var audit = JsonDocument.Parse(File.ReadAllBytes(auditPath));
        var family = audit.RootElement.GetProperty("finalCohort").EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!,
            f => audit.RootElement.GetProperty("candidates").EnumerateArray().Concat(audit.RootElement.GetProperty("replacements").EnumerateArray().SelectMany(r => r.GetProperty("tried").EnumerateArray()))
                .First(c => c.GetProperty("id").GetString() == f.GetProperty("id").GetString()).GetProperty("family").GetString()!.StartsWith("SEEN") ? "SEEN_FAMILY" : "UNSEEN_FAMILY");
        var stratumOf = selection.RootElement.GetProperty("documents").EnumerateArray().ToDictionary(d => d.GetProperty("id").GetString()!, d => d.GetProperty("stratum").GetString()!);

        // Predictions by alias, per view.
        var final = new Dictionary<string, string>(StringComparer.Ordinal); var first = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        int requests = 0, acceptedFirst = 0, acceptedFinal = 0, rawFirst = 0, rawFinal = 0, normalizedUsed = 0; var failures = new SortedDictionary<string, int>();
        decimal cost = 0;
        // Prediction maps are keyed by document|alias: source aliases (L0004:S0, ...) repeat across documents.
        // (Keyed by alias alone, later documents overwrote earlier ones - found 2026-10-11 on the first held-out score.)
        foreach (var q in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            requests++;
            var document = q.GetProperty("document").GetString()!;
            var aliasById = q.GetProperty("issued").EnumerateArray().ToDictionary(i => i.GetProperty("occurrence").GetString()!, i => i.GetProperty("sourceAlias").GetString()!);
            var dir = Path.Combine(root, "requests", q.GetProperty("handle").GetString()!.Replace('|', '.'));
            var attempts = Enumerable.Range(1, 2).Select(k => Path.Combine(dir, $"attempt-{k}")).Where(Directory.Exists).ToArray();
            bool doneFinal = false, doneRaw = false;
            for (var k = 0; k < attempts.Length; k++)
            {
                var receipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(attempts[k], "request-receipt.json")))!;
                cost += receipt["costUsd"]!.GetValue<decimal>();
                var status = receipt["status"]!.GetValue<string>();
                if (status != "ACCEPTED") { var code = receipt["failureCode"]?.GetValue<string>() ?? status; failures[code] = failures.GetValueOrDefault(code) + 1; }
                var rawPath = Path.Combine(attempts[k], "validation.raw.json");
                var rawAccepted = File.Exists(rawPath) && JsonNode.Parse(File.ReadAllBytes(rawPath))!["strictAccepted"]!.GetValue<bool>();
                if (File.Exists(Path.Combine(attempts[k], "normalization.json")) &&
                    JsonNode.Parse(File.ReadAllBytes(Path.Combine(attempts[k], "normalization.json")))!["applied"]!.GetValue<bool>() && status == "ACCEPTED") normalizedUsed++;
                if (status == "ACCEPTED" && !doneFinal)
                {
                    doneFinal = true; acceptedFinal++; if (k == 0) acceptedFirst++;
                    foreach (var (alias, fn) in Decisions(Path.Combine(attempts[k], "validation.json"), aliasById)) { final[Key(document, alias)] = fn; if (k == 0) first[Key(document, alias)] = fn; }
                }
                if (rawAccepted && !doneRaw)
                {
                    doneRaw = true; rawFinal++; if (k == 0) rawFirst++;
                    foreach (var (alias, fn) in Decisions(rawPath, aliasById)) raw[Key(document, alias)] = fn;
                }
            }
        }

        var rows = new List<Row>(); var excluded = new List<object>();
        foreach (var d in goldFreeze.RootElement.GetProperty("documents").EnumerateArray())
        {
            var id = d.GetProperty("id").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(goldFreezePath)!, d.GetProperty("goldFile").GetString()!));
            Need(SpatialCanonical.Hash(bytes) == d.GetProperty("goldSha256").GetString(), "GOLD_FILE_DRIFT:" + id);
            using var g = JsonDocument.Parse(bytes);
            foreach (var l in g.RootElement.GetProperty("labels").EnumerateArray())
            {
                var alias = l.GetProperty("sourceAlias").GetString()!;
                var goldStatus = l.TryGetProperty("goldStatus", out var gs) ? gs.GetString()! : "LABELED";
                if (goldStatus.StartsWith("EXCLUDED_", StringComparison.Ordinal))
                {   // Declared before the run (source-quality audit): kept in requests, never scored, reported separately.
                    excluded.Add(new { document = id, alias, goldStatus, predicted = final.GetValueOrDefault(Key(id, alias)) ?? "MISSING" }); continue;
                }
                var label = l.GetProperty("label").GetString()!;
                Need(Labels.Contains(label), "GOLD_LABEL_INVALID:" + id + alias);
                rows.Add(new(id, stratumOf[id], family[id], l.GetProperty("pageStratum").GetString()!, alias, label,
                    final.GetValueOrDefault(Key(id, alias)), first.GetValueOrDefault(Key(id, alias)), raw.GetValueOrDefault(Key(id, alias))));
            }
        }

        object Metrics(IEnumerable<Row> src, Func<Row, string?> view)
        {
            var list = src.ToArray(); var n = list.Length;
            var resolved = list.Count(r => view(r) is not null && view(r) != "INSUFFICIENT_EVIDENCE");
            object Class(string c)
            {
                int tp = list.Count(r => r.Gold == c && view(r) == c), fp = list.Count(r => r.Gold != c && view(r) == c), pos = list.Count(r => r.Gold == c);
                double? p = tp + fp == 0 ? null : (double)tp / (tp + fp), rc = pos == 0 ? null : (double)tp / pos;
                return new
                {
                    goldPositives = pos, predictedPositives = tp + fp, tp, fp, fnAllGoldPositives = pos - tp,
                    precision = Round(p), precisionWilson95 = Wilson(tp, tp + fp), recall = Round(rc), recallWilson95 = Wilson(tp, pos),
                    f1 = p is null || rc is null || p + rc == 0 ? (double?)null : Round(2 * p * rc / (p + rc)),
                    status = pos < SparseThreshold ? "INCONCLUSIVE_SPARSE" : "EVALUABLE",
                };
            }
            var classes = Labels.ToDictionary(c => c, Class);
            var f1s = Labels.Select(c => JsonSerializer.SerializeToElement(classes[c]).GetProperty("f1")).Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetDouble()).ToArray();
            return new
            {
                occurrences = n, resolved, coverage = n == 0 ? null : Round((double)resolved / n), abstained = list.Count(r => view(r) == "INSUFFICIENT_EVIDENCE"),
                missing = list.Count(r => view(r) is null), correct = list.Count(r => view(r) == r.Gold),
                classes, macroF1 = f1s.Length == 3 ? Round(f1s.Average()) : null,
                confusion = Labels.ToDictionary(gl => gl, gl => Labels.Append("INSUFFICIENT_EVIDENCE").Append("MISSING").ToDictionary(p => p, p => list.Count(r => r.Gold == gl && (view(r) ?? "MISSING") == p))),
            };
        }
        object Views(IEnumerable<Row> src) => new { normalizedFinal = Metrics(src, r => r.Final), normalizedFirstAttempt = Metrics(src, r => r.First), strictRawFinal = Metrics(src, r => r.Raw) };
        var est = JsonSerializer.SerializeToElement(Metrics(rows, r => r.Final)).GetProperty("classes").GetProperty(Est);
        double P(string k) => est.GetProperty(k).ValueKind == JsonValueKind.Number ? est.GetProperty(k).GetDouble() : 0;
        var coverage = rows.Count == 0 ? 0 : (double)rows.Count(r => r.Final is not null && r.Final != "INSUFFICIENT_EVIDENCE") / rows.Count;
        var gate = new
        {
            establishesPrecision = new { value = Round(P("precision")), threshold = 0.90, pass = P("precision") >= 0.90 },
            establishesRecallAllGoldPositives = new { value = Round(P("recall")), threshold = 0.90, pass = P("recall") >= 0.90 },
            resolvedCoverage = new { value = Round(coverage), threshold = 0.99, pass = coverage >= 0.99 },
            firstAttemptAcceptance = new { value = Round((double)acceptedFirst / requests), threshold = 0.95, pass = (double)acceptedFirst / requests >= 0.95 },
            finalAcceptance = new { value = Round((double)acceptedFinal / requests), threshold = 0.99, pass = (double)acceptedFinal / requests >= 0.99 },
        };
        var all = JsonSerializer.SerializeToElement(gate).EnumerateObject().All(p => p.Value.GetProperty("pass").GetBoolean());
        return SpatialCanonical.Bytes(new
        {
            version = "P7_F1Q_HELDOUT_SCORE_V1", mode = "PER_OCCURRENCE_F1_ON_SAMPLED_PAGES_NOT_FULL_DOCUMENT_RECALL",
            rawManifestSha256 = manifestSha, goldFreezeSha256 = goldFreezeSha,
            goldApproval = goldApproval == "USER_APPROVED_GOLD_FROZEN" ? "USER_APPROVED" : "AI_REVIEWED_UNDER_USER_DELEGATION_NOT_INDEPENDENT_HUMAN_REVIEW",
            planSha256 = SpatialCanonical.Hash(File.ReadAllBytes(planPath)),
            requests = new { total = requests, acceptedFirstAttemptNormalized = acceptedFirst, acceptedFinalNormalized = acceptedFinal,
                acceptedFirstAttemptStrictRaw = rawFirst, acceptedFinalStrictRaw = rawFinal, acceptedViaFenceNormalization = normalizedUsed,
                failureCodesAllAttempts = failures, reportedCostUsd = cost },
            primaryGate = gate, primaryGatePass = all,
            overall = Views(rows),
            byDocument = rows.GroupBy(r => r.Doc).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => Metrics(g, r => r.Final)),
            byStratum = rows.GroupBy(r => r.Stratum).ToDictionary(g => g.Key, g => Metrics(g, r => r.Final)),
            byFamily = rows.GroupBy(r => r.Family).ToDictionary(g => g.Key, g => Metrics(g, r => r.Final)),
            byPageStratum = rows.GroupBy(r => r.PageStratum).ToDictionary(g => g.Key, g => Metrics(g, r => r.Final)),
            excludedSourceCorrupted = excluded, excludedCount = excluded.Count,
            errors = rows.Where(r => r.Final != r.Gold).Select(r => new { r.Doc, r.Alias, r.PageStratum, r.Gold, predicted = r.Final ?? "MISSING" }),
            sparseRule = $"slices with fewer than {SparseThreshold} Gold positives for a class are INCONCLUSIVE for that class",
            missingAndAbstentionAreNotOTHER = true, goldMutation = "NONE", providerCallsDuringScoring = 0, productionPromotion = "BLOCKED",
        });
    }

    internal static string Key(string document, string alias) => document + "|" + alias;

    private static IEnumerable<(string Alias, string Function)> Decisions(string validationPath, IReadOnlyDictionary<string, string> aliasById)
    {
        var v = JsonNode.Parse(File.ReadAllBytes(validationPath))!;
        foreach (var r in v["rows"]!.AsArray())
        {
            if (!r!["valid"]!.GetValue<bool>()) continue;
            var d = r["decision"]!;
            yield return (aliasById[d["occurrence"]!.GetValue<string>()],
                d["assessment"]!.GetValue<string>() == "INSUFFICIENT_EVIDENCE" ? "INSUFFICIENT_EVIDENCE" : d["function"]!.GetValue<string>());
        }
    }

    private static object? Wilson(int k, int n)
    {
        if (n == 0) return null;
        const double z = 1.959963984540054; double p = (double)k / n, d = 1 + z * z / n;
        double c = (p + z * z / (2 * n)) / d, h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return new { low = Round(Math.Max(0, c - h)), high = Round(Math.Min(1, c + h)) };
    }
    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 4);
    private static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }
}
