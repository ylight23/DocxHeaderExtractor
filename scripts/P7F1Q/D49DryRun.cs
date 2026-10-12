using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.V5Qualification.P7;
using F = DocxHeaderExtractor.V5Qualification.P7.P7F1V6DirectTwoStage.SelectFormat;

// Issue #9 D4.9 (offline): dry-run of variants A (V3, decisions and groups in one reply), B (two-stage, object decisions) and C (two-stage,
// compact codes) on SRC-095 with scripted models, and a representation-change replay of the recorded D4.8 replies. Nothing is sent. The
// oracle reads the Gold to play a perfect model for capacity only; it is not a model result and no Gold value enters a provider request.
internal static class D49DryRun
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonElement Payload(P7F1V6Protocol.PreparedRequest req)
    {
        using var doc = JsonDocument.Parse(req.Body);
        using var p = JsonDocument.Parse(doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        return p.RootElement.Clone();
    }

    static string[] Targets(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("targets").EnumerateArray().Select(x => x.GetProperty("alias").GetString()!).ToArray();
    static string Stage(P7F1V6Protocol.PreparedRequest req) => Payload(req).GetProperty("stage").GetString()!;

    static string Compact(IReadOnlyList<string> t, ISet<string> sel)
    {
        var codes = t.Select(a => sel.Contains(a) ? 'S' : 'N').ToArray();
        var blocks = new JsonArray();
        for (int s = 0; s < t.Count; s += 8)
        {
            var len = Math.Min(8, t.Count - s);
            blocks.Add(new JsonObject { ["start"] = s, ["firstAlias"] = t[s], ["lastAlias"] = t[s + len - 1], ["codes"] = new string(codes, s, len) });
        }
        return new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.SelectCompactVersion, ["status"] = "COMPLETE", ["targetCount"] = t.Count, ["blocks"] = blocks,
            ["selectedEvidence"] = new JsonArray(Enumerable.Range(0, t.Count).Where(i => codes[i] == 'S')
                .Select(i => (JsonNode)new JsonObject { ["targetIndex"] = i, ["evidenceRefs"] = new JsonArray(t[i]) }).ToArray()),
        }.ToJsonString();
    }

    static string Objects(IReadOnlyList<string> t, ISet<string> sel) => new JsonObject
    {
        ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
        ["decisions"] = new JsonArray(t.Select(a => (JsonNode)(sel.Contains(a)
            ? new JsonObject { ["alias"] = a, ["decision"] = "SELECT", ["evidenceRefs"] = new JsonArray(a) }
            : new JsonObject { ["alias"] = a, ["decision"] = "NOT_SELECT" })).ToArray()),
    }.ToJsonString();

    static string Group(IReadOnlyList<string> t, List<List<string>> claims)
    {
        var owned = t.ToHashSet(StringComparer.Ordinal);
        return new JsonObject
        {
            ["protocolVersion"] = P7F1V6DirectTwoStage.GroupVersion, ["status"] = "COMPLETE",
            ["groups"] = new JsonArray(claims.Where(c => c.Any(owned.Contains)).Select(c => (JsonNode)new JsonObject { ["parts"] = new JsonArray(c.Select(a => (JsonNode)a).ToArray()) }).ToArray()),
        }.ToJsonString();
    }

    static string V3(IReadOnlyList<string> t, ISet<string> sel, List<List<string>> claims)
    {
        var owned = t.ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6DirectV2.ResponseVersion, status = "COMPLETE",
            decisions = t.Select(a => new { alias = a, decision = sel.Contains(a) ? "SELECT" : "NOT_SELECT", evidenceRefs = new[] { a } }),
            headings = claims.Where(c => c.Any(a => owned.Contains(a) && sel.Contains(a))).Select(c => new { parts = c }),
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    sealed record Seen(string Stage, int Bytes, double Reserve);

    static object Report(string name, P7F1V6DirectV2.RunOutcome run, string? grouping, P7F1V6DirectV2.Budget budget, List<Seen> seen) => new
    {
        simulation = name,
        status = run.Status, finalization = run.Finalization, grouping, assurance = run.Assurance,
        calls = budget.Calls, reservedUsd = Math.Round(budget.ReservedUsd, 6), refusals = budget.Refusals.GroupBy(r => r).Select(g => new { code = g.Key, count = g.Count() }),
        headings = run.Headings.Count,
        byStage = seen.GroupBy(s => s.Stage).Select(g => new { stage = g.Key, calls = g.Count(), minBytes = g.Min(x => x.Bytes), maxBytes = g.Max(x => x.Bytes), reserveUsd = Math.Round(g.Sum(x => x.Reserve), 6) }),
    };

    public static void Run(string repoRoot, string artifactDir)
    {
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        using var g = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, gold.Path)));
        var claims = g.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray()
            .Select(c => c.GetProperty("sourceParts").EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).ToList()).ToList();
        var atoms = claims.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);
        var instruction = P7F1V6DirectD45.Instruction;
        var sims = new List<object>();

        P7F1V6DirectV2.ReplyModel Record(List<Seen> seen, P7F1V6DirectV2.ReplyModel m) => (unit, round, req) =>
        { seen.Add(new Seen(Stage(req), req.Bytes, P7F1V6Direct.AttemptUsd(req.Bytes))); return m(unit, round, req); };

        // A: V3, decisions and groups in one reply.
        { var b = P7F1V6DirectD45.NewBudget(); var seen = new List<Seen>();
          var run = P7F1V6DirectV2.Run(instruction, u, Record(seen, (unit, r, req) => V3(Targets(req), atoms, claims)), b, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit);
          sims.Add(Report("A_V3_ORACLE", run, null, b, seen)); }

        foreach (var format in new[] { F.Objects, F.Compact })
        {
            var label = format == F.Objects ? "B_OBJECTS" : "C_COMPACT";
            P7F1V6DirectV2.ReplyModel Oracle(ISet<string> sel) => (unit, r, req) =>
                Stage(req) == "GROUP" ? Group(Targets(req), claims) : format == F.Compact ? Compact(Targets(req), sel) : Objects(Targets(req), sel);

            { var b = P7F1V6DirectD45.NewBudget(); var seen = new List<Seen>();
              var o = P7F1V6DirectTwoStage.Run(format, instruction, u, Record(seen, Oracle(atoms)), b, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit);
              sims.Add(Report(label + "_ORACLE", o.Run, o.Grouping, b, seen)); }
            { var b = P7F1V6DirectD45.NewBudget(); var seen = new List<Seen>();
              var o = P7F1V6DirectTwoStage.Run(format, instruction, u, Record(seen, Oracle(none)), b, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit);
              sims.Add(Report(label + "_ALL_NEGATIVE", o.Run, o.Grouping, b, seen)); }
            { var b = P7F1V6DirectD45.NewBudget(); var seen = new List<Seen>();
              var oracle = Oracle(atoms);
              var o = P7F1V6DirectTwoStage.Run(format, instruction, u, Record(seen, (unit, r, req) => r == 1 && Stage(req) == "SEGMENT"
                  ? JsonSerializer.Serialize(new { protocolVersion = P7F1V6DirectTwoStage.SelectVersion(format), status = "NEEDS_EVIDENCE", retrieve = new[] { new { kind = "SEARCH", text = "Section" } } })
                  : oracle(unit, r, req)), b, P7F1V6DirectD45.Overlap, P7F1V6DirectD45.Unit);
              sims.Add(Report(label + "_RETRIEVAL_HEAVY", o.Run, o.Grouping, b, seen)); }
        }

        var segments = (u.Aliases.Count + P7F1V6DirectD45.Unit - 1) / P7F1V6DirectD45.Unit;
        var report = new
        {
            artifact = "p7.f1.v6.d4.9-dryrun.v1",
            providerCalls = 0,
            document = P7F1V6DirectD45.Document,
            task = P7F1V6DirectD45.TaskId,
            unit = P7F1V6DirectD45.Unit,
            budgetUnderStudy = new { P7F1V6DirectD45.MaxCalls, P7F1V6DirectD45.CeilingUsd, P7F1V6DirectD45.MaxRequestBytes },
            prompts = new
            {
                selectObjects = P7F1V6DirectTwoStage.SelectObjectsPromptSha256,
                selectCompact = P7F1V6DirectTwoStage.SelectCompactPromptSha256,
                group = P7F1V6DirectTwoStage.GroupPromptSha256,
                v3Combined = P7F1V6DirectV2.SystemPromptSha256,
            },
            callCountModel = new
            {
                segments,
                minimumTwoStage = $"{segments} SELECT + ceil(selected/48) FINALIZE + ceil(selected/48) GROUP; with the Gold-size selection (109 atoms): {segments} + 3 + 3 = {segments + 6}",
                worstTwoStage = $"every unit uses 3 rounds: 3*{segments} SELECT + 3*{segments} RESOLVE + 3*25 FINALIZE + 3*ceil(selected/48) GROUP",
            },
            simulations = sims,
            notes = new[]
            {
                "Scripted models only; the oracle reads the Gold to play a perfect model so capacity can be checked. Not a model result.",
                "Two-stage adds GROUP calls on top of SELECT and FINALIZE; whether 100 calls and $1.00 are enough depends on how many atoms Qwen selects and how often it asks for evidence.",
            },
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d4.9-dryrun.v1.json"), JsonSerializer.Serialize(report, Indented) + "\n", new UTF8Encoding(false));
        foreach (var s in sims) Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>Representation-change replay: the decisions of each recorded D4.8 SEGMENT reply, rewritten as a B reply (objects without
    /// headings) and a C reply (compact codes; a target with no decision gets no code), checked by the new parsers. This tests the parsers on
    /// real decision sets. It says nothing about how Qwen would answer a B or C request.</summary>
    public static void Replay(string repoRoot, string runDir, string outPath)
    {
        var gold = P7F1V6DirectD45.ReadGoldPin(repoRoot);
        var u = P7F1V6Universe.Rebuild(repoRoot, P7F1V6DirectD45.Document, gold.SourceSha256, out var why) ?? throw new InvalidOperationException(why);
        var lines = u.Lines;
        using var sum = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(runDir, "run-summary.json")));
        var rows = new List<object>();
        foreach (var e in sum.RootElement.GetProperty("ledger").EnumerateArray())
        {
            var unit = e.GetProperty("UnitId").GetString()!;
            if (!unit.StartsWith("SEG-", StringComparison.Ordinal) || e.GetProperty("Round").GetInt32() != 1) continue;
            var n = int.Parse(unit[4..]);
            var core = Enumerable.Range(n * 48, Math.Min(48, lines.Count - n * 48)).ToArray();
            var decide = core.Select(i => lines[i].Alias).ToArray();
            var shown = Enumerable.Range(Math.Max(0, n * 48 - 20), Math.Min(lines.Count, n * 48 + 48 + 20) - Math.Max(0, n * 48 - 20))
                .ToDictionary(i => lines[i].Alias, i => i, StringComparer.Ordinal);
            var raw = File.ReadAllText(Path.Combine(runDir, "raw", e.GetProperty("RawStoreKey").GetString()! + ".json"));
            var content = JsonNode.Parse(JsonNode.Parse(raw)!["choices"]![0]!["message"]!["content"]!.GetValue<string>())!.AsObject();
            var decisions = (content["decisions"] as JsonArray ?? new JsonArray()).Select(d => d!.AsObject()).ToList();

            var b = new JsonObject
            {
                ["protocolVersion"] = P7F1V6DirectTwoStage.SelectObjectsVersion, ["status"] = "COMPLETE",
                ["decisions"] = new JsonArray(decisions.Select(d => (JsonNode)JsonNode.Parse(d.ToJsonString())!).ToArray()),
            }.ToJsonString();
            var byAlias = decisions.GroupBy(d => d["alias"]!.GetValue<string>()).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var codes = new StringBuilder();
            var evidence = new JsonArray();
            for (int i = 0; i < decide.Length; i++)
            {
                if (!byAlias.TryGetValue(decide[i], out var d)) break;             // no decision: no code, and the block comes out short
                var v = d["decision"]!.GetValue<string>();
                codes.Append(v == "SELECT" ? 'S' : v == "NOT_SELECT" ? 'N' : 'U');
                if (v == "SELECT") evidence.Add(new JsonObject { ["targetIndex"] = i, ["evidenceRefs"] = JsonNode.Parse((d["evidenceRefs"] ?? new JsonArray()).ToJsonString()) });
            }
            var blocks = new JsonArray();
            var cs = codes.ToString();
            for (int s = 0; s < decide.Length; s += 8)
            {
                var len = Math.Min(8, decide.Length - s);
                if (s >= cs.Length) break;
                blocks.Add(new JsonObject { ["start"] = s, ["firstAlias"] = decide[s], ["lastAlias"] = decide[s + len - 1], ["codes"] = cs.Substring(s, Math.Min(len, cs.Length - s)) });
            }
            var c = new JsonObject
            {
                ["protocolVersion"] = P7F1V6DirectTwoStage.SelectCompactVersion, ["status"] = "COMPLETE", ["targetCount"] = decide.Length,
                ["blocks"] = blocks, ["selectedEvidence"] = evidence,
            }.ToJsonString();
            var rb = P7F1V6DirectTwoStage.ParseSelect(F.Objects, b, shown, decide);
            var rc = P7F1V6DirectTwoStage.ParseSelect(F.Compact, c, shown, decide);
            rows.Add(new { unit, recordedDecisions = decisions.Count, objectsVerdict = rb.Ok ? "OK" : rb.Failure, compactVerdict = rc.Ok ? "OK" : rc.Failure });
            Console.WriteLine($"{unit} decisions {decisions.Count}: B {(rb.Ok ? "OK" : rb.Failure)} | C {(rc.Ok ? "OK" : rc.Failure)}");
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            artifact = "p7.f1.v6.d4.9-representation-replay.v1",
            label = "PARSER CONSISTENCY ONLY: recorded D4.8 decisions rewritten as B and C replies; not evidence of Qwen behaviour with B or C requests",
            rows,
        }, Indented) + "\n", new UTF8Encoding(false));
    }
}
