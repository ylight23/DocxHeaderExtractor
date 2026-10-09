using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Post-freeze scorer for plan V3 (P7_F1Q_V2). Reads Gold only after the V3 raw manifest is pinned. Compares
/// V3 (retry-inclusive and first-attempt-only) with Control and the frozen V1/V2 arms on identical rows,
/// and runs the deterministic layout-claim audit on every F1Q arm's decisions. Abstentions and missing rows are
/// never OTHER; rows of rejected requests are diagnostic only.
/// </summary>
internal static class F1QScorerV3
{
    private const string Est = "ESTABLISHES_STRUCTURE", Rep = "REPRESENTS_STRUCTURE";
    private static readonly string[] Labels = [Est, Rep, "OTHER"];
    internal sealed record Pred(string Assessment, string? Function, string Role, string Interpretation, string[] Refs, string Audit);
    internal sealed record Row(string Case, string Document, string Occurrence, string Alias, int Page, string Text, string Slice, string Gold, Dictionary<string, Pred?> P);

    public static byte[] Run(string planPath, string rootV3, string manifestShaV3, string rootV1, string pilotGoldDir, string d3RawDir, string fullSourceDir)
    {
        var manifest = File.ReadAllBytes(Path.Combine(rootV3, "manifest.json"));
        Need(SpatialCanonical.Hash(manifest) == manifestShaV3, "RAW_FREEZE_REQUIRED");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var arms = new[] { "Control", "F1QNoTools", "F1QTools", "F1QToolsMandatory", "F1QEvidenceV2", "F1QEvidenceV2_FirstAttempt" };

        // Gold (opened here only).
        var goldManifestBytes = File.ReadAllBytes(Path.Combine(pilotGoldDir, "gold-manifest.v2.json"));
        Need(SpatialCanonical.Hash(goldManifestBytes) == "614b2e09e7568446d343dbab18f4f9d405007657d6f5a665fb1538909de073f7", "PILOT_GOLD_MANIFEST_DRIFT");
        var policy = File.ReadAllBytes(Path.Combine(pilotGoldDir, "evaluation-policy.v1.json"));
        using var gm = JsonDocument.Parse(goldManifestBytes);
        var pilotGold = gm.RootElement.GetProperty("documents").EnumerateArray().ToDictionary(d => d.GetProperty("document").GetString()!, d =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(pilotGoldDir, d.GetProperty("goldFile").GetString()!));
            Need(SpatialCanonical.Hash(bytes) == d.GetProperty("goldSha256").GetString(), "PILOT_GOLD_DRIFT");
            return P7PilotGoldReader.Read(bytes, policy).ReviewedRows.ToDictionary(r => r.Alias, r => r.SemanticFunction, StringComparer.Ordinal);
        });
        var authored = new[] { "SRC-089", "SRC-095" }.ToDictionary(id => id, id =>
        {
            using var g = JsonDocument.Parse(File.ReadAllBytes($"eval/a99-closed-loop/gold/{id}.gold.json"));
            return g.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray().SelectMany(c => c.GetProperty("boundParts").EnumerateArray())
                .Select(p => p.GetProperty("sourceAlias").GetString()!).ToHashSet(StringComparer.Ordinal);
        });

        // Historical D3 Control
        using var d3 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d3RawDir, "manifest.json")));
        var d3Accepted = d3.RootElement.GetProperty("attempts").EnumerateArray()
            .Where(a => a.GetProperty("status").GetString() == "ACCEPTED" && a.GetProperty("callHandle").GetString()!.EndsWith(".F1.CONTROL", StringComparison.Ordinal))
            .ToDictionary(a => a.GetProperty("bodySha256").GetString()!, a => a.GetProperty("directory").GetString()!);

        var rows = new List<Row>(); var requestStats = new List<object>();
        foreach (var c in plan.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("case").GetString()!; var document = c.GetProperty("document").GetString()!;
            var tools = Tools(document, c.GetProperty("sourceSha256").GetString()!, c.GetProperty("evidenceStoreSha256").GetString()!, fullSourceDir);
            var preds = arms.ToDictionary(a => a, _ => new Dictionary<string, Pred>(StringComparer.Ordinal));
            var issued = new List<(string Id, string Alias, int Page, string Text)>();
            var caseRequests = plan.RootElement.GetProperty("requests").EnumerateArray().Where(q => q.GetProperty("case").GetString() == name).ToArray();
            foreach (var q in caseRequests)
            {
                using var body = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(rootV3, "initial-bodies", q.GetProperty("bodyFile").GetString()!)));
                using var user = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                issued.AddRange(user.RootElement.GetProperty("issuedOccurrences").EnumerateArray().Select(o =>
                    (o.GetProperty("id").GetString()!, o.GetProperty("sourceAlias").GetString()!, o.GetProperty("page").GetInt32(), o.GetProperty("text").GetString()!)));
            }
            var aliasById = issued.ToDictionary(i => i.Id, i => i.Alias, StringComparer.Ordinal);
            foreach (var q in caseRequests)
            {
                var dir = Path.Combine(rootV3, "requests", q.GetProperty("handle").GetString()!.Replace('|', '.'));
                var attempts = Enumerable.Range(1, 2).Select(k => Path.Combine(dir, $"attempt-{k}")).Where(Directory.Exists).ToArray();
                var statuses = attempts.Select(a => JsonNode.Parse(File.ReadAllBytes(Path.Combine(a, "request-receipt.json")))!).ToArray();
                for (var k = 0; k < attempts.Length; k++)
                {
                    var status = statuses[k]["status"]!.GetValue<string>();
                    if (status != "ACCEPTED") continue;
                    Load(Path.Combine(attempts[k], "validation.json"), preds["F1QEvidenceV2"], tools, aliasById);
                    if (k == 0) Load(Path.Combine(attempts[k], "validation.json"), preds["F1QEvidenceV2_FirstAttempt"], tools, aliasById);
                }
                requestStats.Add(new
                {
                    handle = q.GetProperty("handle").GetString(),
                    attempts = statuses.Select((s, k) => new
                    {
                        attempt = k + 1, status = s["status"]!.GetValue<string>(), failureCode = s["failureCode"]?.GetValue<string>(),
                        modelTurns = s["turns"]!.AsArray().Count, costUsd = s["costUsd"]!.GetValue<decimal>(),
                        promptTokens = s["turns"]!.AsArray().Sum(t => t!["promptTokens"]!.GetValue<long>()),
                        completionTokens = s["turns"]!.AsArray().Sum(t => t!["completionTokens"]!.GetValue<long>()),
                        reasoningTokens = s["turns"]!.AsArray().Sum(t => t!["reasoningTokens"]!.GetValue<long>()),
                        toolCalls = s["toolCallsByName"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<int>()),
                        invalidToolCalls = s["invalidToolCalls"]!.GetValue<int>(),
                        providers = s["turns"]!.AsArray().Select(t => t!["provider"]?.GetValue<string>()).Distinct().ToArray(),
                    }),
                });
            }
            foreach (var arm in new[] { "F1QNoTools", "F1QTools", "F1QToolsMandatory", "Control" })
            {
                var dir = Path.Combine(rootV1, "requests", $"{name}.{arm}");
                if (arm == "Control" && c.GetProperty("historicalControl").ValueKind == JsonValueKind.String)
                {
                    Need(d3Accepted.TryGetValue(c.GetProperty("historicalControl").GetString()!, out var hdir), "HISTORICAL_CONTROL_NOT_BOUND");
                    using var parsed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d3RawDir, hdir!, "parsed-decision.json")));
                    foreach (var d in parsed.RootElement.GetProperty("decisions").EnumerateArray())
                        preds[arm][d.GetProperty("occurrence").GetString()!] = new("SUPPORTED", d.GetProperty("function").GetString(), "", "", [], "NOT_APPLICABLE");
                    continue;
                }
                if (!Directory.Exists(dir) || JsonNode.Parse(File.ReadAllBytes(Path.Combine(dir, "request-receipt.json")))!["status"]!.GetValue<string>() != "ACCEPTED") continue;
                if (arm == "Control")
                {
                    var v = JsonNode.Parse(File.ReadAllBytes(Path.Combine(dir, "validation.json")))!;
                    foreach (var r in v["rows"]!.AsArray())
                        preds[arm][r!["occurrence"]!.GetValue<string>()] = new("SUPPORTED", r["decision"]!["function"]!.GetValue<string>(), "", "", [], "NOT_APPLICABLE");
                }
                else Load(Path.Combine(dir, "validation.json"), preds[arm], tools, aliasById);
            }
            foreach (var i in issued)
            {
                string slice, gold;
                if (pilotGold.TryGetValue(document, out var pg)) { if (!pg.TryGetValue(i.Alias, out var g)) continue; slice = "PILOT_3_CLASS"; gold = g; }
                else { slice = "AUTHORED_BINARY_ESTABLISHES"; gold = authored[document].Contains(i.Alias) ? Est : "NOT_ESTABLISHES"; }
                rows.Add(new(name, document, i.Id, i.Alias, i.Page, i.Text, slice, gold, arms.ToDictionary(a => a, a => preds[a].GetValueOrDefault(i.Id))));
            }
        }

        var pilot = rows.Where(r => r.Slice == "PILOT_3_CLASS").ToArray();
        var binary = rows.Where(r => r.Slice == "AUTHORED_BINARY_ESTABLISHES").ToArray();
        Need(pilot.Length == 213 && binary.Length == 288, "SCORING_UNIVERSE_DRIFT");
        static string Bucket(Pred? p) => p is null ? "MISSING" : p.Assessment == "INSUFFICIENT_EVIDENCE" ? "INSUFFICIENT_EVIDENCE" : p.Function!;
        static bool Right(Row r, Pred? p) => p is { Assessment: "SUPPORTED" } && (r.Slice == "PILOT_3_CLASS" ? p.Function == r.Gold : (p.Function == Est) == (r.Gold == Est));
        object Metrics(IEnumerable<Row> src, string arm)
        {
            var list = src.ToArray(); var sup = list.Where(r => r.P[arm] is { Assessment: "SUPPORTED" }).ToArray();
            int tp = sup.Count(r => r.Gold == Est && r.P[arm]!.Function == Est), fp = sup.Count(r => r.Gold != Est && r.P[arm]!.Function == Est), fn = sup.Count(r => r.Gold == Est && r.P[arm]!.Function != Est);
            var golds = list.Select(r => r.Gold).Distinct().Order().ToArray();
            return new
            {
                universe = list.Length, resolved = sup.Length, abstained = list.Count(r => r.P[arm] is { Assessment: "INSUFFICIENT_EVIDENCE" }), missing = list.Count(r => r.P[arm] is null),
                correct = sup.Count(r => Right(r, r.P[arm])),
                establishes = new { tp, fp, fn, precision = tp + fp == 0 ? (double?)null : Math.Round((double)tp / (tp + fp), 4), recallResolved = tp + fn == 0 ? (double?)null : Math.Round((double)tp / (tp + fn), 4) },
                confusion = golds.ToDictionary(g => g, g => Labels.Append("INSUFFICIENT_EVIDENCE").Append("MISSING").ToDictionary(p => p, p => list.Count(r => r.Gold == g && Bucket(r.P[arm]) == p))),
                layoutClaimAudit = list.Where(r => r.P[arm] is not null).GroupBy(r => r.P[arm]!.Audit).ToDictionary(g => g.Key, g => new { decisions = g.Count(), correct = g.Count(r => Right(r, r.P[arm])) }),
            };
        }
        object Paired(IEnumerable<Row> src, string a, string b)
        {
            var both = src.Where(r => r.P[a] is { Assessment: "SUPPORTED" } && r.P[b] is { Assessment: "SUPPORTED" }).ToArray();
            return new { evaluable = both.Length, wrongToRight = both.Count(r => !Right(r, r.P[a]) && Right(r, r.P[b])), rightToWrong = both.Count(r => Right(r, r.P[a]) && !Right(r, r.P[b])),
                bothRight = both.Count(r => Right(r, r.P[a]) && Right(r, r.P[b])), bothWrong = both.Count(r => !Right(r, r.P[a]) && !Right(r, r.P[b])) };
        }
        object Slice(Row[] s) => new
        {
            strict = arms.ToDictionary(a => a, a => Metrics(s, a)),
            perDocument = s.GroupBy(r => r.Document).Select(g => new { document = g.Key, metrics = arms.ToDictionary(a => a, a => Metrics(g, a)) }),
            paired = new { controlToV2 = Paired(s, "Control", "F1QEvidenceV2"), toolsToV2 = Paired(s, "F1QTools", "F1QEvidenceV2"),
                mandatoryToV2 = Paired(s, "F1QToolsMandatory", "F1QEvidenceV2"), noToolsToV2 = Paired(s, "F1QNoTools", "F1QEvidenceV2") },
        };
        object Ledger(Row r) => new
        {
            r.Case, r.Occurrence, r.Alias, r.Page, r.Text, r.Gold, control = Bucket(r.P["Control"]), tools = Bucket(r.P["F1QTools"]), mandatory = Bucket(r.P["F1QToolsMandatory"]),
            v2 = Bucket(r.P["F1QEvidenceV2"]), v2Role = r.P["F1QEvidenceV2"]?.Role, v2Interpretation = r.P["F1QEvidenceV2"]?.Interpretation,
            v2Refs = r.P["F1QEvidenceV2"]?.Refs, v2Audit = r.P["F1QEvidenceV2"]?.Audit,
        };
        var allDecisions = arms.ToDictionary(a => a, a => rows.Select(r => r.P[a]).Where(p => p is not null).ToArray());
        return SpatialCanonical.Bytes(new
        {
            version = "P7_F1Q_V3_POST_FREEZE_SCORE_V1", mode = "F1_ISOLATED_NOT_END_TO_END",
            rawManifestSha256 = manifestShaV3, planSha256 = SpatialCanonical.Hash(File.ReadAllBytes(planPath)), claimAudit = P7F1QClaimAudit.Version,
            requests = requestStats,
            pilot3Class = Slice(pilot), authoredBinary = Slice(binary),
            abstentions = arms.ToDictionary(a => a, a => allDecisions[a].Count(p => p!.Assessment == "INSUFFICIENT_EVIDENCE")),
            decisionsCitingToolEvidence = arms.ToDictionary(a => a, a => allDecisions[a].Count(p => p!.Refs.Any(x => x.StartsWith('E')))),
            layoutClaimAuditAll = arms.ToDictionary(a => a, a => allDecisions[a].GroupBy(p => p!.Audit).ToDictionary(g => g.Key, g => g.Count())),
            v2Errors = rows.Where(r => !Right(r, r.P["F1QEvidenceV2"])).Select(Ledger),
            v2Contradicted = rows.Where(r => r.P["F1QEvidenceV2"]?.Audit == "CONTRADICTED").Select(r => new { r.Case, r.Occurrence, r.Text, role = r.P["F1QEvidenceV2"]!.Role, interpretation = r.P["F1QEvidenceV2"]!.Interpretation,
                checks = P7F1QClaimAudit.Check(r.P["F1QEvidenceV2"]!.Role, r.P["F1QEvidenceV2"]!.Interpretation, r.Alias, ToolsCache[r.Document]).Checks }),
            focusCases = rows.Where(r => r.Case == "D05-PACK_001" && new[] { "O1", "O5", "O6", "O7", "O39", "O40", "O41", "O45", "O46", "O47" }.Contains(r.Occurrence)).Select(Ledger),
            src089FrontMatter = rows.Where(r => r.Document == "SRC-089" && r.Page == 1).Take(19).Select(Ledger),
            src095Contents = rows.Where(r => r.Case == "SRC-095-PACK_002" && (r.Page is 3 or 4 || new[] { "O45", "O56", "O57", "O96" }.Contains(r.Occurrence))).Select(Ledger),
            src095Title = rows.Where(r => r.Case == "SRC-095-PACK_001" && new[] { "O15", "O41" }.Contains(r.Occurrence)).Select(Ledger),
            outsideScopePilotRowsStatus = "UNKNOWN_UNSCORED", abstentionsBecomeOTHER = false, failuresBecomeOTHER = false,
            goldMutation = "NONE", providerCallsDuringScoring = 0, productionChanged = false, generalization = "NOT_ESTABLISHED_SMALL_COHORT",
        });
    }

    private static readonly Dictionary<string, P7F1QEvidenceTools> ToolsCache = new(StringComparer.Ordinal);

    private static P7F1QEvidenceTools Tools(string document, string sourceSha, string storeSha, string fullSourceDir)
    {
        if (ToolsCache.TryGetValue(document, out var cached)) return cached;
        PdfSourceEvidenceStore store;
        if (document.StartsWith("PDF-", StringComparison.Ordinal))
        {
            var parsed = PdfSourceAdapter.BuildWithDetails(Path.Combine(fullSourceDir, document, "source.pdf"));
            store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        }
        else
        {
            using var u = JsonDocument.Parse(File.ReadAllBytes("eval/a99-closed-loop/pdf-source-determinism-v2/universe-hashes.v1.json"));
            var pdf = u.RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("documentId").GetString() == document).GetProperty("pdf").GetString()!;
            var parsed = PdfSourceAdapter.BuildWithDetails(pdf);
            store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        }
        Need(store.SourceSha256 == sourceSha && store.StoreSha256 == storeSha, "AUDIT_STORE_DRIFT:" + document);
        var first = store.Entries[0].SourceAlias;
        return ToolsCache[document] = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["AUDIT"] = first }, 2);
    }

    private static void Load(string validationPath, Dictionary<string, Pred> into, P7F1QEvidenceTools tools, IReadOnlyDictionary<string, string> aliasById)
    {
        var v = JsonNode.Parse(File.ReadAllBytes(validationPath))!;
        foreach (var r in v["rows"]!.AsArray())
        {
            if (!r!["valid"]!.GetValue<bool>()) continue;
            var d = r["decision"]!; var id = d["occurrence"]!.GetValue<string>();
            string role = d["observedRole"]!.GetValue<string>(), interp = d["interpretation"]!.GetValue<string>();
            into[id] = new(d["assessment"]!.GetValue<string>(), d["function"]?.GetValue<string>(), role, interp,
                d["evidenceRefs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(), P7F1QClaimAudit.Check(role, interp, aliasById[id], tools).Status);
        }
    }
    private static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }
}
