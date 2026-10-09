using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// Post-freeze F1 scorer. Reachable only after the raw manifest exists (pinned by sha). Strict outputs and
/// diagnostic rows from rejected responses are scored separately. Abstentions and missing requests are
/// never converted to OTHER. Pilot Gold: 3-class on 213 adjudicated rows (430 others stay UNKNOWN).
/// SRC-089/095 authored Gold: binary ESTABLISHES membership over every issued occurrence, because its truth
/// definition is ALL_TRUE_HEADING_OCCURRENCES; REPRESENTS vs OTHER is not adjudicated there.
/// </summary>
internal static class F1QScorer
{
    private const string Est = "ESTABLISHES_STRUCTURE", Rep = "REPRESENTS_STRUCTURE", Oth = "OTHER";
    private static readonly string[] Labels = [Est, Rep, Oth];

    internal sealed record Pred(string Assessment, string? Function, string Role, string Interpretation, string[] Refs);
    internal sealed record Row(string Case, string Document, string Occurrence, string Alias, int Page, string Text, string Slice, string? Gold,
        Dictionary<string, Pred?> Strict, Dictionary<string, Pred?> Diagnostic);

    public static byte[] Run(string planPath, string captureRoot, string manifestSha, string pilotGoldDir, string d3RawDir)
    {
        var manifestBytes = File.ReadAllBytes(Path.Combine(captureRoot, "manifest.json"));
        Need(SpatialCanonical.Hash(manifestBytes) == manifestSha, "RAW_FREEZE_REQUIRED");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planPath));
        var bodies = Path.Combine(captureRoot, "initial-bodies");
        var arms = new[] { "Control", "F1QNoTools", "F1QTools", "F1QToolsMandatory" };
        var rows = new List<Row>();
        var requestStats = new List<object>();

        // Historical Control (D01-D05): accepted D3 raw, bound by exact body sha.
        using var d3 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d3RawDir, "manifest.json")));
        var d3Accepted = d3.RootElement.GetProperty("attempts").EnumerateArray()
            .Where(a => a.GetProperty("status").GetString() == "ACCEPTED" && a.GetProperty("callHandle").GetString()!.EndsWith(".F1.CONTROL", StringComparison.Ordinal))
            .ToDictionary(a => a.GetProperty("bodySha256").GetString()!, a => a.GetProperty("directory").GetString()!);

        // Gold
        var goldManifestBytes = File.ReadAllBytes(Path.Combine(pilotGoldDir, "gold-manifest.v2.json"));
        Need(SpatialCanonical.Hash(goldManifestBytes) == "614b2e09e7568446d343dbab18f4f9d405007657d6f5a665fb1538909de073f7", "PILOT_GOLD_MANIFEST_DRIFT");
        var policy = File.ReadAllBytes(Path.Combine(pilotGoldDir, "evaluation-policy.v1.json"));
        Need(SpatialCanonical.Hash(policy) == "88f9885fa39832cfbf8915758f68637a215b8e0ae2a0d5adad5633b80236125c", "PILOT_POLICY_DRIFT");
        using var goldManifest = JsonDocument.Parse(goldManifestBytes);
        var pilotGold = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var doc in goldManifest.RootElement.GetProperty("documents").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(pilotGoldDir, doc.GetProperty("goldFile").GetString()!));
            Need(SpatialCanonical.Hash(bytes) == doc.GetProperty("goldSha256").GetString(), "PILOT_GOLD_DRIFT");
            var gold = P7PilotGoldReader.Read(bytes, policy);
            pilotGold[doc.GetProperty("document").GetString()!] = gold.ReviewedRows.ToDictionary(r => r.Alias, r => r.SemanticFunction, StringComparer.Ordinal);
        }
        var authoredGold = new Dictionary<string, (HashSet<string> Aliases, List<(string Alias, int Start, int End, string Text)> Parts, string Sha)>();
        foreach (var id in new[] { "SRC-089", "SRC-095" })
        {
            var path = $"eval/a99-closed-loop/gold/{id}.gold.json"; var bytes = File.ReadAllBytes(path);
            using var g = JsonDocument.Parse(bytes);
            Need(g.RootElement.GetProperty("approval").GetProperty("truthDefinition").GetString() == "ALL_TRUE_HEADING_OCCURRENCES", "AUTHORED_GOLD_TRUTH_DEFINITION");
            var parts = g.RootElement.GetProperty("occurrence").GetProperty("claims").EnumerateArray()
                .SelectMany(c => c.GetProperty("boundParts").EnumerateArray())
                .Select(p => (p.GetProperty("sourceAlias").GetString()!, p.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                    p.GetProperty("utf16Span").GetProperty("end").GetInt32(), p.GetProperty("text").GetString()!)).ToList();
            authoredGold[id] = (parts.Select(p => p.Item1).ToHashSet(StringComparer.Ordinal), parts, SpatialCanonical.Hash(bytes));
        }

        foreach (var c in plan.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("case").GetString()!; var document = c.GetProperty("document").GetString()!;
            using var initial = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(bodies, $"{name}.F1QNoTools.initial-body.json")));
            using var user = JsonDocument.Parse(initial.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var issued = user.RootElement.GetProperty("issuedOccurrences").EnumerateArray()
                .Select(o => (Id: o.GetProperty("id").GetString()!, Alias: o.GetProperty("sourceAlias").GetString()!, Page: o.GetProperty("page").GetInt32(), Text: o.GetProperty("text").GetString()!)).ToArray();
            var strict = arms.ToDictionary(a => a, _ => new Dictionary<string, Pred>(StringComparer.Ordinal));
            var diagnostic = arms.ToDictionary(a => a, _ => new Dictionary<string, Pred>(StringComparer.Ordinal));
            foreach (var arm in arms)
            {
                var dir = Path.Combine(captureRoot, "requests", $"{name}.{arm}");
                if (arm == "Control" && c.GetProperty("historicalControl").ValueKind == JsonValueKind.String)
                {
                    var bodySha = c.GetProperty("historicalControl").GetString()!;
                    Need(d3Accepted.TryGetValue(bodySha, out var hdir), "HISTORICAL_CONTROL_NOT_BOUND:" + name);
                    using var parsed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d3RawDir, hdir!, "parsed-decision.json")));
                    foreach (var d in parsed.RootElement.GetProperty("decisions").EnumerateArray())
                        strict[arm][d.GetProperty("occurrence").GetString()!] = new("SUPPORTED", d.GetProperty("function").GetString(), "", "", []);
                    requestStats.Add(new { @case = name, arm, status = "ACCEPTED", source = "HISTORICAL_D3_RAW", directory = hdir });
                    continue;
                }
                if (!Directory.Exists(dir)) { requestStats.Add(new { @case = name, arm, status = "NOT_EXECUTED" }); continue; }
                var receipt = JsonNode.Parse(File.ReadAllBytes(Path.Combine(dir, "request-receipt.json")))!;
                var status = receipt["status"]!.GetValue<string>();
                var validationPath = Path.Combine(dir, "validation.json");
                if (File.Exists(validationPath))
                {
                    var v = JsonNode.Parse(File.ReadAllBytes(validationPath))!;
                    foreach (var r in v["rows"]!.AsArray())
                    {
                        if (!r!["valid"]!.GetValue<bool>()) continue;
                        var d = r["decision"]!;
                        var p = new Pred(d["assessment"]!.GetValue<string>(), d["function"]?.GetValue<string>(), d["observedRole"]!.GetValue<string>(),
                            d["interpretation"]!.GetValue<string>(), d["evidenceRefs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
                        (status == "ACCEPTED" ? strict : diagnostic)[arm][d["occurrence"]!.GetValue<string>()] = p;
                    }
                }
                var turns = receipt["turns"]!.AsArray();
                requestStats.Add(new
                {
                    @case = name, arm, status, failureCode = receipt["failureCode"]?.GetValue<string>(), modelTurns = turns.Count,
                    httpAttempts = turns.Sum(t => t!["attempts"]!.GetValue<int>()), costUsd = receipt["costUsd"]!.GetValue<decimal>(),
                    promptTokens = turns.Sum(t => t!["promptTokens"]!.GetValue<long>()), completionTokens = turns.Sum(t => t!["completionTokens"]!.GetValue<long>()),
                    reasoningTokens = turns.Sum(t => t!["reasoningTokens"]!.GetValue<long>()),
                    toolCalls = receipt["toolCallsByName"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<int>()),
                    invalidToolCalls = receipt["invalidToolCalls"]!.GetValue<int>(),
                    providers = turns.Select(t => t!["provider"]?.GetValue<string>()).Distinct().ToArray(),
                    finishReasons = turns.Select(t => t!["finishReason"]?.GetValue<string>()).ToArray(),
                });
            }
            string slice; Func<string, string?> goldOf;
            if (pilotGold.TryGetValue(document, out var pg)) { slice = "PILOT_3_CLASS"; goldOf = a => pg.GetValueOrDefault(a); }
            else
            {
                var ag = authoredGold[document];
                var byAlias = issued.ToDictionary(i => i.Alias, i => i.Text, StringComparer.Ordinal);
                foreach (var part in ag.Parts.Where(p => byAlias.ContainsKey(p.Alias)))
                    Need(byAlias[part.Alias].Length >= part.End && byAlias[part.Alias][part.Start..part.End] == part.Text, "AUTHORED_GOLD_UNIVERSE_DRIFT:" + part.Alias);
                slice = "AUTHORED_BINARY_ESTABLISHES"; goldOf = a => ag.Aliases.Contains(a) ? Est : "NOT_ESTABLISHES";
            }
            foreach (var i in issued)
                rows.Add(new(name, document, i.Id, i.Alias, i.Page, i.Text, slice, goldOf(i.Alias),
                    arms.ToDictionary(a => a, a => strict[a].GetValueOrDefault(i.Id)), arms.ToDictionary(a => a, a => diagnostic[a].GetValueOrDefault(i.Id))));
        }

        var pilot = rows.Where(r => r.Slice == "PILOT_3_CLASS" && r.Gold is not null).ToArray();
        var binary = rows.Where(r => r.Slice == "AUTHORED_BINARY_ESTABLISHES").ToArray();
        Need(pilot.Length == 213, "PILOT_DENOMINATOR_DRIFT");
        object ArmMetrics(IEnumerable<Row> source, string arm, bool threeClass, bool useDiagnostic = false)
        {
            var list = source.ToArray();
            Pred? P(Row r) => useDiagnostic ? r.Diagnostic[arm] : r.Strict[arm];
            var supported = list.Where(r => P(r) is { Assessment: "SUPPORTED" }).ToArray();
            int tp = supported.Count(r => r.Gold == Est && P(r)!.Function == Est), fp = supported.Count(r => r.Gold != Est && P(r)!.Function == Est);
            int fnSupported = supported.Count(r => r.Gold == Est && P(r)!.Function != Est);
            int abstainedEst = list.Count(r => r.Gold == Est && P(r) is { Assessment: "INSUFFICIENT_EVIDENCE" });
            int missingEst = list.Count(r => r.Gold == Est && P(r) is null);
            return new
            {
                universe = list.Length, supported = supported.Length,
                abstained = list.Count(r => P(r) is { Assessment: "INSUFFICIENT_EVIDENCE" }), missing = list.Count(r => P(r) is null),
                correct = threeClass ? supported.Count(r => r.Gold == P(r)!.Function) : supported.Count(r => (r.Gold == Est) == (P(r)!.Function == Est)),
                confusion = threeClass
                    ? Labels.ToDictionary(g => g, g => Labels.Append("INSUFFICIENT_EVIDENCE").Append("MISSING").ToDictionary(p => p,
                        p => list.Count(r => r.Gold == g && Bucket(P(r)) == p)))
                    : new[] { Est, "NOT_ESTABLISHES" }.ToDictionary(g => g, g => Labels.Append("INSUFFICIENT_EVIDENCE").Append("MISSING").ToDictionary(p => p,
                        p => list.Count(r => r.Gold == g && Bucket(P(r)) == p))),
                establishes = new
                {
                    tp, fp, fnAmongSupported = fnSupported, goldPositivesAbstained = abstainedEst, goldPositivesMissing = missingEst,
                    precision = tp + fp == 0 ? (double?)null : Math.Round((double)tp / (tp + fp), 4),
                    recallAmongSupported = tp + fnSupported == 0 ? (double?)null : Math.Round((double)tp / (tp + fnSupported), 4),
                    recallAllGoldPositives = tp + fnSupported + abstainedEst + missingEst == 0 ? (double?)null : Math.Round((double)tp / (tp + fnSupported + abstainedEst + missingEst), 4),
                },
            };
        }
        static string Bucket(Pred? p) => p is null ? "MISSING" : p.Assessment == "INSUFFICIENT_EVIDENCE" ? "INSUFFICIENT_EVIDENCE" : p.Function!;
        bool Right(Row r, Pred? p) => p is { Assessment: "SUPPORTED" } && (r.Slice == "PILOT_3_CLASS" ? p.Function == r.Gold : (p.Function == Est) == (r.Gold == Est));
        object Paired(IEnumerable<Row> source, string a, string b)
        {
            var both = source.Where(r => r.Strict[a] is { Assessment: "SUPPORTED" } && r.Strict[b] is { Assessment: "SUPPORTED" }).ToArray();
            return new
            {
                evaluable = both.Length, bothRight = both.Count(r => Right(r, r.Strict[a]) && Right(r, r.Strict[b])),
                wrongToRight = both.Count(r => !Right(r, r.Strict[a]) && Right(r, r.Strict[b])),
                rightToWrong = both.Count(r => Right(r, r.Strict[a]) && !Right(r, r.Strict[b])),
                bothWrong = both.Count(r => !Right(r, r.Strict[a]) && !Right(r, r.Strict[b])),
                abstainedInB = source.Count(r => r.Strict[a] is not null && r.Strict[b] is { Assessment: "INSUFFICIENT_EVIDENCE" }),
            };
        }
        object Slice(Row[] slice, bool threeClass) => new
        {
            strict = arms.ToDictionary(a => a, a => ArmMetrics(slice, a, threeClass)),
            diagnosticFromRejectedResponses = arms.ToDictionary(a => a, a => ArmMetrics(slice, a, threeClass, true)),
            paired = new { controlToNoTools = Paired(slice, "Control", "F1QNoTools"), controlToTools = Paired(slice, "Control", "F1QTools"), noToolsToTools = Paired(slice, "F1QNoTools", "F1QTools"),
                controlToMandatory = Paired(slice, "Control", "F1QToolsMandatory"), toolsToMandatory = Paired(slice, "F1QTools", "F1QToolsMandatory") },
            perDocument = slice.GroupBy(r => r.Document).Select(g => new { document = g.Key, metrics = arms.ToDictionary(a => a, a => ArmMetrics(g, a, threeClass)) }),
        };
        object Ledger(Row r) => new
        {
            r.Case, r.Occurrence, r.Alias, r.Page, r.Text, r.Gold,
            control = Bucket(r.Strict["Control"]), noTools = Bucket(r.Strict["F1QNoTools"]), tools = Bucket(r.Strict["F1QTools"]),
            noToolsInterpretation = r.Strict["F1QNoTools"]?.Interpretation, toolsInterpretation = r.Strict["F1QTools"]?.Interpretation,
            toolsRole = r.Strict["F1QTools"]?.Role, toolsRefs = r.Strict["F1QTools"]?.Refs,
            mandatory = Bucket(r.Strict["F1QToolsMandatory"]), mandatoryInterpretation = r.Strict["F1QToolsMandatory"]?.Interpretation,
            mandatoryRole = r.Strict["F1QToolsMandatory"]?.Role, mandatoryRefs = r.Strict["F1QToolsMandatory"]?.Refs,
            diagnosticMandatory = r.Strict["F1QToolsMandatory"] is null ? Bucket(r.Diagnostic["F1QToolsMandatory"]) : null,
            diagnosticTools = r.Strict["F1QTools"] is null ? Bucket(r.Diagnostic["F1QTools"]) : null,
            diagnosticNoTools = r.Strict["F1QNoTools"] is null ? Bucket(r.Diagnostic["F1QNoTools"]) : null,
        };
        var focus = new[] { ("D05-PACK_001", new[] { "O1", "O5", "O39", "O45", "O46", "O47" }) };
        return SpatialCanonical.Bytes(new
        {
            version = "P7_F1Q_POST_FREEZE_SCORE_V1", mode = "F1_ISOLATED_NOT_END_TO_END",
            rawManifestSha256 = manifestSha, planSha256 = SpatialCanonical.Hash(File.ReadAllBytes(planPath)),
            pilotGoldManifestSha256 = SpatialCanonical.Hash(goldManifestBytes), authoredGold = authoredGold.ToDictionary(p => p.Key, p => p.Value.Sha),
            requests = requestStats,
            pilot3Class = Slice(pilot, true), authoredBinary = Slice(binary, false),
            toolGroundedDecisions = arms.ToDictionary(a => a, a => rows.Count(r => r.Strict[a]?.Refs.Any(x => x.StartsWith('E')) == true)),
            pilotDisagreements = pilot.Where(r => !Right(r, r.Strict["F1QToolsMandatory"]) || !Right(r, r.Strict["F1QTools"]) || !Right(r, r.Strict["F1QNoTools"]) || !Right(r, r.Strict["Control"])).Select(Ledger),
            authoredDisagreements = binary.Where(r => !Right(r, r.Strict["F1QToolsMandatory"]) || !Right(r, r.Strict["F1QTools"]) || !Right(r, r.Strict["F1QNoTools"]) || !Right(r, r.Strict["Control"])).Select(Ledger),
            focusCases = focus.SelectMany(f => rows.Where(r => r.Case == f.Item1 && f.Item2.Contains(r.Occurrence))).Select(Ledger),
            src089FrontMatter = rows.Where(r => r.Document == "SRC-089" && r.Page == 1).Take(16).Select(Ledger),
            src095Navigation = rows.Where(r => r.Document == "SRC-095" && (r.Strict.Values.Any(p => p?.Function == Rep) || P7F1QEvidenceTools.RepeatKey(r.Text) != r.Text.ToLowerInvariant().Trim())).Take(60).Select(Ledger),
            outsideScopePilotRows = rows.Count(r => r.Slice == "PILOT_3_CLASS" && r.Gold is null), outsideScopeStatus = "UNKNOWN_UNSCORED",
            abstentionsBecomeOTHER = false, failuresBecomeOTHER = false, diagnosticRowsAreStrictSuccess = false,
            controlSources = new { pilot = "HISTORICAL_D3_ACCEPTED_RAW_BODY_SHA_BOUND", authored = "FRESH_FROZEN_PRODUCTION_CONTROL_BODY" },
            goldMutation = "NONE", providerCallsDuringScoring = 0, productionChanged = false, generalization = "NOT_ESTABLISHED_SMALL_COHORT",
        });
    }

    private static void Need(bool ok, string code) { if (!ok) throw new InvalidOperationException(code); }
}
