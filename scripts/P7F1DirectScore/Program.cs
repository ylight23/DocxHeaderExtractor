using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// P7-F1 V4 (Issue #7) independent binary heading-membership evaluator. Runs only on frozen inputs pinned by SHA-256:
//   direct  <bound-predictions.json> <bound-sha256> <gold-freeze.json> <gold-freeze-sha256> <new-score.json>
//   control <f1q-plan.json> <capture-root> <raw-manifest-sha256> <gold-freeze.json> <gold-freeze-sha256> <new-score.json>
//   paired  <score-a.json> <score-b.json> <new-comparison.json>
// Universe: adjudicated Gold rows (goldStatus LABELED). Positive = Gold ESTABLISHES_STRUCTURE; negative = every other
// adjudicated label (old REPRESENTS/OTHER alike). EXCLUDED_* rows, unissued parser headings and unseen pages are never negatives.
// Prediction per row: POS | NEG | UNRESOLVED | MISSING. Only POS counts as a selection; recall is over ALL Gold positives,
// so UNRESOLVED and MISSING positives are false negatives; coverage and abstention are reported separately.
// The control conversion (old ESTABLISHES -> POS, REPRESENTS/OTHER -> NEG, INSUFFICIENT_EVIDENCE -> UNRESOLVED) lives only here.
const string Version = "P7_F1_BINARY_HEADING_EVALUATOR_V1";
var json = new JsonSerializerOptions { WriteIndented = true };
string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
byte[] Pinned(string path, string sha) { var b = File.ReadAllBytes(path); if (Sha(b) != sha) throw new InvalidOperationException("PINNED_INPUT_DRIFT:" + Path.GetFileName(path)); return b; }
void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }

string output; JsonObject report;
switch (args.Length > 0 ? args[0] : "")
{
    case "direct" when args.Length == 6:
    {
        output = args[5];
        using var bound = JsonDocument.Parse(Pinned(args[1], args[2]));
        Need(bound.RootElement.GetProperty("goldRead").GetBoolean() == false, "BOUND_PREDICTIONS_READ_GOLD");
        var pred = new Dictionary<string, string>(StringComparer.Ordinal); var requestStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in bound.RootElement.GetProperty("requests").EnumerateArray())
        {
            var doc = q.GetProperty("document").GetString()!; var status = q.GetProperty("status").GetString()!;
            requestStatus[status] = requestStatus.GetValueOrDefault(status) + 1;
            if (status != "ACCEPTED_COMPLETE") continue; // INCOMPLETE / failed / missing requests yield MISSING, never negatives
            foreach (var s in q.GetProperty("selected").EnumerateArray()) Put(pred, doc, s.GetProperty("sourceAlias").GetString()!, "POS");
            foreach (var u in q.GetProperty("unresolved").EnumerateArray()) Put(pred, doc, u.GetProperty("sourceAlias").GetString()!, "UNRESOLVED");
            foreach (var n in q.GetProperty("negativeEligible").EnumerateArray()) Put(pred, doc, n.GetString()!, "NEG");
        }
        report = Score(pred, args[3], args[4], "P7_F1_DIRECT_HEADING_V1", new JsonObject
        {
            ["boundPredictionsSha256"] = args[2], ["planSha256"] = bound.RootElement.GetProperty("planSha256").GetString(),
            ["rawManifestSha256"] = bound.RootElement.GetProperty("rawManifestSha256").GetString(),
            ["requestStatuses"] = JsonSerializer.SerializeToNode(requestStatus),
        });
        break;
    }
    case "control" when args.Length == 7:
    {
        output = args[6];
        var root = args[2];
        Pinned(Path.Combine(root, "manifest.json"), args[3]);
        using var plan = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        var pred = new Dictionary<string, string>(StringComparer.Ordinal); var requestStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in plan.RootElement.GetProperty("requests").EnumerateArray())
        {
            var doc = q.GetProperty("document").GetString()!;
            var aliasById = q.GetProperty("issued").EnumerateArray().ToDictionary(i => i.GetProperty("occurrence").GetString()!, i => i.GetProperty("sourceAlias").GetString()!);
            var dir = Path.Combine(root, "requests", q.GetProperty("handle").GetString()!.Replace('|', '.'));
            var accepted = Directory.Exists(dir) ? Directory.GetDirectories(dir, "attempt-*").Order(StringComparer.Ordinal)
                .FirstOrDefault(a => JsonNode.Parse(File.ReadAllBytes(Path.Combine(a, "request-receipt.json")))!["status"]!.GetValue<string>() == "ACCEPTED") : null;
            var status = accepted is null ? "NOT_ACCEPTED" : "ACCEPTED";
            requestStatus[status] = requestStatus.GetValueOrDefault(status) + 1;
            if (accepted is null) continue;
            foreach (var r in JsonNode.Parse(File.ReadAllBytes(Path.Combine(accepted, "validation.json")))!["rows"]!.AsArray())
            {
                if (!r!["valid"]!.GetValue<bool>()) continue;
                var d = r["decision"]!; var alias = aliasById[d["occurrence"]!.GetValue<string>()];
                var p = d["assessment"]!.GetValue<string>() == "INSUFFICIENT_EVIDENCE" ? "UNRESOLVED"
                    : d["function"]!.GetValue<string>() == "ESTABLISHES_STRUCTURE" ? "POS" : "NEG";
                Put(pred, doc, alias, p);
            }
        }
        report = Score(pred, args[4], args[5], "P7_F1Q_V2_CONVERTED_IN_EVALUATOR", new JsonObject
        {
            ["planSha256"] = Sha(File.ReadAllBytes(args[1])), ["rawManifestSha256"] = args[3], ["requestStatuses"] = JsonSerializer.SerializeToNode(requestStatus),
            ["conversion"] = "ESTABLISHES_STRUCTURE->POS; REPRESENTS_STRUCTURE/OTHER->NEG; INSUFFICIENT_EVIDENCE->UNRESOLVED; not accepted->MISSING",
        });
        break;
    }
    case "paired" when args.Length == 4:
    {
        output = args[3];
        report = Paired(args[1], args[2]);
        break;
    }
    default: throw new ArgumentException("direct|control|paired, see header");
}
if (File.Exists(output)) throw new InvalidOperationException("SCORE_EXISTS");
var bytes = JsonSerializer.SerializeToUtf8Bytes(report, json);
using (var f = new FileStream(output, FileMode.CreateNew)) f.Write(bytes);
Console.WriteLine($"{Path.GetFileName(output)} sha256={Sha(bytes)}");

static void Put(Dictionary<string, string> pred, string doc, string alias, string value)
{
    var key = doc + "|" + alias;
    if (pred.TryGetValue(key, out var old) && old != value) throw new InvalidOperationException("CONFLICTING_PREDICTION:" + key);
    pred[key] = value;
}

JsonObject Score(Dictionary<string, string> pred, string goldFreezePath, string goldFreezeSha, string arm, JsonObject provenance)
{
    using var freeze = JsonDocument.Parse(Pinned(goldFreezePath, goldFreezeSha));
    var rows = new List<(string Doc, string Alias, string Page, string Stratum, string Text, bool Gold, string Pred)>();
    var excluded = 0;
    foreach (var d in freeze.RootElement.GetProperty("documents").EnumerateArray())
    {
        var id = d.GetProperty("id").GetString()!;
        using var g = JsonDocument.Parse(Pinned(Path.Combine(Path.GetDirectoryName(goldFreezePath)!, d.GetProperty("goldFile").GetString()!), d.GetProperty("goldSha256").GetString()!));
        foreach (var l in g.RootElement.GetProperty("labels").EnumerateArray())
        {
            if (l.GetProperty("goldStatus").GetString() != "LABELED") { excluded++; continue; }
            var alias = l.GetProperty("sourceAlias").GetString()!;
            rows.Add((id, alias, l.GetProperty("page").GetInt32().ToString(), l.GetProperty("pageStratum").GetString()!, l.GetProperty("text").GetString()!,
                l.GetProperty("label").GetString() == "ESTABLISHES_STRUCTURE", pred.GetValueOrDefault(id + "|" + alias) ?? "MISSING"));
        }
    }
    JsonObject M(IEnumerable<(string Doc, string Alias, string Page, string Stratum, string Text, bool Gold, string Pred)> src)
    {
        var l = src.ToArray();
        int tp = l.Count(r => r.Gold && r.Pred == "POS"), fp = l.Count(r => !r.Gold && r.Pred == "POS"), pos = l.Count(r => r.Gold);
        int tn = l.Count(r => !r.Gold && r.Pred == "NEG"), unres = l.Count(r => r.Pred == "UNRESOLVED"), miss = l.Count(r => r.Pred == "MISSING");
        double? p = tp + fp == 0 ? null : (double)tp / (tp + fp), rc = pos == 0 ? null : (double)tp / pos;
        return new JsonObject
        {
            ["rows"] = l.Length, ["goldPositives"] = pos, ["tp"] = tp, ["fp"] = fp, ["fnAllGoldPositives"] = pos - tp, ["tn"] = tn,
            ["unresolved"] = unres, ["unresolvedGoldPositives"] = l.Count(r => r.Gold && r.Pred == "UNRESOLVED"), ["missing"] = miss,
            ["precision"] = R(p), ["precisionWilson95"] = Wilson(tp, tp + fp), ["recall"] = R(rc), ["recallWilson95"] = Wilson(tp, pos),
            ["f1"] = p is null || rc is null || p + rc == 0 ? null : R(2 * p * rc / (p + rc)),
            ["resolvedCoverage"] = l.Length == 0 ? null : R((double)l.Count(r => r.Pred is "POS" or "NEG") / l.Length),
            ["status"] = pos < 10 ? "INCONCLUSIVE_SPARSE_POSITIVES" : "EVALUABLE",
        };
    }
    var overall = M(rows);
    return new JsonObject
    {
        ["version"] = Version, ["arm"] = arm, ["goldFreezeSha256"] = goldFreezeSha, ["provenance"] = provenance,
        ["universe"] = "adjudicated Gold rows (LABELED); EXCLUDED_* not scored", ["excludedRows"] = excluded,
        ["overall"] = overall,
        ["gates"] = new JsonObject
        {
            ["headingPrecision>=0.90"] = overall["precision"] is { } pp && pp.GetValue<double>() >= 0.90,
            ["headingRecall>=0.90"] = overall["recall"] is { } rr && rr.GetValue<double>() >= 0.90,
            ["resolvedCoverage>=0.99"] = overall["resolvedCoverage"] is { } cc && cc.GetValue<double>() >= 0.99,
            ["note"] = "first-attempt and final acceptance are request-level and reported from raw receipts by the run report",
        },
        ["byDocument"] = new JsonObject(rows.GroupBy(r => r.Doc).OrderBy(g => g.Key).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)M(g)))),
        ["byPageStratum"] = new JsonObject(rows.GroupBy(r => r.Stratum).OrderBy(g => g.Key).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)M(g)))),
        ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)new JsonObject { ["doc"] = r.Doc, ["alias"] = r.Alias, ["page"] = int.Parse(r.Page), ["gold"] = r.Gold ? "POS" : "NEG", ["pred"] = r.Pred }).ToArray()),
        ["errors"] = new JsonArray(rows.Where(r => r.Gold != (r.Pred == "POS")).Select(r => (JsonNode?)new JsonObject
            { ["doc"] = r.Doc, ["alias"] = r.Alias, ["page"] = int.Parse(r.Page), ["text"] = r.Text.Length > 120 ? r.Text[..120] : r.Text,
              ["gold"] = r.Gold ? "POS" : "NEG", ["pred"] = r.Pred, ["kind"] = r.Gold ? "FALSE_NEGATIVE" : "FALSE_POSITIVE" }).ToArray()),
        ["providerCallsDuringScoring"] = 0, ["goldMutation"] = "NONE",
    };
}

JsonObject Paired(string aPath, string bPath)
{
    var a = JsonNode.Parse(File.ReadAllBytes(aPath))!; var b = JsonNode.Parse(File.ReadAllBytes(bPath))!;
    Need(a["goldFreezeSha256"]!.GetValue<string>() == b["goldFreezeSha256"]!.GetValue<string>(), "GOLD_FREEZE_DIFFERS");
    Dictionary<string, (string Gold, string Pred)> Rows(JsonNode s)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var r in s["rows"]!.AsArray())
            Need(d.TryAdd(r!["doc"]!.GetValue<string>() + "|" + r["alias"]!.GetValue<string>(), (r["gold"]!.GetValue<string>(), r["pred"]!.GetValue<string>())), "DUPLICATE_ROW_KEY");
        return d;
    }
    var ra = Rows(a); var rb = Rows(b);
    // Identical scored universe: same keys, no duplicates, same Gold label per key.
    Need(ra.Count == rb.Count && ra.Keys.All(rb.ContainsKey), "SCORED_UNIVERSE_DIFFERS");
    Need(ra.All(p => rb[p.Key].Gold == p.Value.Gold), "GOLD_LABEL_DIFFERS");
    bool Correct((string Gold, string Pred) x) => x.Gold == "POS" ? x.Pred == "POS" : x.Pred == "NEG";
    int w2r = ra.Keys.Count(k => !Correct(ra[k]) && Correct(rb[k])), r2w = ra.Keys.Count(k => Correct(ra[k]) && !Correct(rb[k]));
    var docs = ra.Keys.Select(k => k.Split('|')[0]).Distinct().Order(StringComparer.Ordinal).ToArray();
    (int Tp, int Fp, int Pos) Counts(Dictionary<string, (string Gold, string Pred)> r, IEnumerable<string> ds)
    {
        var set = ds.ToHashSet(); var l = r.Where(p => set.Contains(p.Key.Split('|')[0])).Select(p => p.Value).ToArray();
        return (l.Count(x => x.Gold == "POS" && x.Pred == "POS"), l.Count(x => x.Gold == "NEG" && x.Pred == "POS"), l.Count(x => x.Gold == "POS"));
    }
    double F1((int Tp, int Fp, int Pos) c) => c.Tp == 0 ? 0 : 2.0 * c.Tp / (2 * c.Tp + c.Fp + (c.Pos - c.Tp));
    double Prec((int Tp, int Fp, int Pos) c) => c.Tp + c.Fp == 0 ? 0 : (double)c.Tp / (c.Tp + c.Fp);
    double Rec((int Tp, int Fp, int Pos) c) => c.Pos == 0 ? 0 : (double)c.Tp / c.Pos;
    // Document-cluster paired bootstrap (resample documents with replacement), fixed seed for reproducibility.
    var rng = new Random(20261011); const int B = 10000;
    var dF1 = new double[B]; var dP = new double[B]; var dR = new double[B];
    for (var i = 0; i < B; i++)
    {
        var sample = Enumerable.Range(0, docs.Length).Select(_ => docs[rng.Next(docs.Length)]).ToArray();
        (int, int, int) Sum(Dictionary<string, (string Gold, string Pred)> r) => sample.Select(d => Counts(r, [d])).Aggregate((0, 0, 0), (s, c) => (s.Item1 + c.Tp, s.Item2 + c.Fp, s.Item3 + c.Pos));
        var ca = Sum(ra); var cb = Sum(rb);
        dF1[i] = F1(cb) - F1(ca); dP[i] = Prec(cb) - Prec(ca); dR[i] = Rec(cb) - Rec(ca);
    }
    JsonObject Ci(double[] x) { var s = x.Order().ToArray(); return new JsonObject { ["low"] = R(s[(int)(0.025 * B)]), ["high"] = R(s[(int)(0.975 * B) - 1]) }; }
    var perDoc = docs.Select(d => (d, delta: F1(Counts(rb, [d])) - F1(Counts(ra, [d])))).ToArray();
    int better = perDoc.Count(x => x.delta > 1e-12), worse = perDoc.Count(x => x.delta < -1e-12);
    var all = Counts(ra, docs); var allB = Counts(rb, docs);
    return new JsonObject
    {
        ["version"] = Version + "_PAIRED", ["armA"] = a["arm"]!.GetValue<string>(), ["armB"] = b["arm"]!.GetValue<string>(),
        ["scoreASha256"] = Sha(File.ReadAllBytes(aPath)), ["scoreBSha256"] = Sha(File.ReadAllBytes(bPath)), ["rows"] = ra.Count, ["documents"] = docs.Length,
        ["armAF1"] = R(F1(all)), ["armBF1"] = R(F1(allB)), ["armAPrecision"] = R(Prec(all)), ["armBPrecision"] = R(Prec(allB)), ["armARecall"] = R(Rec(all)), ["armBRecall"] = R(Rec(allB)),
        ["rowMcNemar"] = new JsonObject { ["wrongToRight"] = w2r, ["rightToWrong"] = r2w, ["exactTwoSidedP"] = R(McNemar(w2r, r2w)), ["note"] = "rows within a page/document are correlated" },
        ["documentSignTestOnF1"] = new JsonObject { ["better"] = better, ["worse"] = worse, ["ties"] = docs.Length - better - worse, ["exactTwoSidedP"] = R(McNemar(better, worse)) },
        ["documentClusterBootstrap"] = new JsonObject { ["resamples"] = B, ["seed"] = 20261011, ["deltaF1Ci95"] = Ci(dF1), ["deltaPrecisionCi95"] = Ci(dP), ["deltaRecallCi95"] = Ci(dR) },
        ["perDocumentF1Delta"] = new JsonObject(perDoc.Select(x => KeyValuePair.Create(x.d, (JsonNode?)R(x.delta)))),
    };
}

static double McNemar(int b, int c)
{
    var n = b + c; if (n == 0) return 1.0;
    double tail = 0, logHalf = n * Math.Log(0.5);
    for (var i = 0; i <= Math.Min(b, c); i++) { double lc = 0; for (var j = 1; j <= i; j++) lc += Math.Log(n - i + j) - Math.Log(j); tail += Math.Exp(lc + logHalf); }
    return Math.Min(1.0, 2 * tail);
}
static double? R(double? v) => v is null ? null : Math.Round(v.Value, 4);
static JsonObject? Wilson(int k, int n)
{
    if (n == 0) return null;
    const double z = 1.959963984540054; double p = (double)k / n, d = 1 + z * z / n;
    double c = (p + z * z / (2 * n)) / d, h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
    return new JsonObject { ["low"] = R(Math.Max(0, c - h)), ["high"] = R(Math.Min(1, c + h)) };
}
