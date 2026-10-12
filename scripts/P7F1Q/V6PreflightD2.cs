using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// Issue #9 D2 preflight (offline, no provider). For each document and each shard size, runs the hierarchical reconciliation
// with a scripted honest model and measures every node: exact input bytes, exact accepted output bytes, levels, requests,
// carries and violations. Also computes the exact UTF-8 size of the largest valid item in canonical form (ASCII aliases, the
// longest alias in the document, the largest allowed number of references and both optional references).
internal static class V6PreflightD2
{
    static readonly int[] ShardSizes = [32, 48, 60, 80];
    const int Context = 3, FanIn = 8, PassUpCap = 60, OutputBudgetBytes = 16384, HeaderBytes = 256;

    sealed record DocRef(string Id, string Path);

    public static void Run(string docsJsonPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var docs = JsonSerializer.Deserialize<List<DocRef>>(File.ReadAllText(docsJsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var summary = new List<object>();
        foreach (var d in docs)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var perSize = ShardSizes.Select(s => Measure(d, s)).ToArray();
                File.WriteAllText(Path.Combine(outDir, d.Id + ".json"), JsonSerializer.Serialize(new { document = d.Id, sizes = perSize }, new JsonSerializerOptions { WriteIndented = true }));
                summary.Add(new { id = d.Id, status = "OK", seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
                Console.WriteLine($"{d.Id} OK {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception e)
            {
                summary.Add(new { id = d.Id, status = "ERROR", error = e.GetType().Name + ": " + e.Message });
                Console.WriteLine($"{d.Id} ERROR {e.GetType().Name}: {e.Message}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    static object Measure(DocRef d, int shard)
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(d.Path);
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        var cands = legacy.AllLines.Where(l => l.Page is not null).Select(l => new P7F1V6Hier.Cand(l.Alias, l.Page!.Value, l.Text)).ToArray();
        var maxAlias = cands.Max(c => c.Alias.Length);
        var itemMax = ExactItemMaxBytes(maxAlias);
        var opts = new P7F1V6Hier.Options("Trích xuất các heading chính", legacy.SourceSha256, shard, Context, FanIn, PassUpCap, null, OutputBudgetBytes);
        var sw = Stopwatch.StartNew();
        var run = P7F1V6Hier.Run(cands, opts, Honest);
        var nodes = run.Nodes;
        int levels = nodes.Max(n => n.Level);
        object Kind(string k) => Stats(nodes.Where(n => n.Kind == k).ToArray());
        return new
        {
            shardSize = shard, candidates = cands.Length, shards = (int)Math.Ceiling(cands.Length / (double)shard), maxAliasChars = maxAlias,
            itemMaxBytes = itemMax, shardOutputBoundBytes = shard * itemMax + HeaderBytes, visibleBudgetBytes = OutputBudgetBytes,
            provablyWithinBudget = shard * itemMax + HeaderBytes <= OutputBudgetBytes,
            requests = nodes.Count, levels, violations = run.Violations.Count, violationSample = run.Violations.Take(3).ToArray(),
            coverageAllDecided = run.Final.Count == cands.Length && run.Final.Values.All(v => v.Decision != "MISSING"),
            selected = run.Final.Values.Count(v => v.Decision == "SELECT"), confirmationsTotal = run.Final.Values.Sum(v => v.Confirmations),
            local = Kind("Local"), reconcile = Kind("Reconcile"), synthesis = Kind("Synthesis"), global = Kind("Global"),
            maxOutputBytesAccepted = nodes.Where(n => n.Status == "ACCEPTED").DefaultIfEmpty().Max(n => n?.OutputBytes ?? 0),
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), transcriptSha256 = run.TranscriptSha256,
        };
    }

    /// <summary>The scripted honest model. Local nodes decide every owned id (SELECT when the alias index is even). Reconcile, synthesis
    /// and global nodes confirm every id they were shown. Synthesis passes up its first selections, leaving room for the two boundary
    /// windows so that the passUp cap is never exceeded.</summary>
    static string Honest(string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        string Quoted(IEnumerable<string> ids) => string.Join(",", ids.Select(i => $"\"{i}\""));
        string Node(string items, string passUp = "", string confirmed = "") =>
            $"{{\"protocolVersion\":\"P7_F1_V6_HIER_NODE_V2\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"items\":[{items}]"
            + (passUp == "" ? "" : $",\"passUp\":[{passUp}]") + (confirmed == "" ? "" : $",\"confirmed\":[{confirmed}]") + "}";
        if (kind == P7F1V6Hier.Kind.Local)
        {
            var items = r.GetProperty("owned").EnumerateArray().Select(o =>
            {
                var a = o.GetProperty("sourceAlias").GetString()!;
                var even = int.Parse(a[1..5]) % 2 == 0;
                return $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"{(even ? "SELECT" : "NOT_SELECT")}\",\"evidenceRefs\":[\"{a}\"]}}";
            });
            return Node(string.Join(",", items));
        }
        var shownIds = r.TryGetProperty("shown", out var shown) ? shown.EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray() : [];
        if (kind == P7F1V6Hier.Kind.Reconcile)
            return Node("", confirmed: Quoted(r.GetProperty("owned").EnumerateArray().Select(o => o.GetProperty("id").GetString()!)));
        if (kind == P7F1V6Hier.Kind.Synthesis)
        {
            var up = shown.EnumerateArray().Where(s => s.GetProperty("current").GetString() == "SELECT")
                .Take(PassUpCap - 2 * Context).Select(s => s.GetProperty("id").GetString()!);
            return Node("", passUp: Quoted(up), confirmed: Quoted(shownIds));
        }
        return Node("", confirmed: Quoted(shownIds));
    }

    /// <summary>Exact UTF-8 bytes of the largest canonical item: every reference at the longest alias, and both optional references.</summary>
    static int ExactItemMaxBytes(int aliasChars)
    {
        var alias = new string('A', aliasChars);
        var refs = string.Join(",", Enumerable.Repeat($"\"{alias}\"", 8));
        var item = $"{{\"id\":\"xxxx\",\"decision\":\"NOT_SELECT\",\"evidenceRefs\":[{refs}],\"parentId\":\"xxxx\",\"continuesFromId\":\"xxxx\"}}";
        return Encoding.UTF8.GetByteCount(item) + 1;   // one separating comma
    }

    static object Stats(P7F1V6Hier.NodeRecord[] ns)
    {
        if (ns.Length == 0) return new { n = 0 };
        var inp = ns.Select(n => (long)n.InputBytes).OrderBy(x => x).ToArray();
        var outp = ns.Select(n => (long)n.OutputBytes).OrderBy(x => x).ToArray();
        long P(long[] s, double q) => s[(int)Math.Min(s.Length - 1, Math.Ceiling(q * s.Length) - 1)];
        return new { n = ns.Length, inputMax = inp[^1], inputP50 = P(inp, 0.5), inputP95 = P(inp, 0.95), outputMax = outp[^1],
            rejected = ns.Count(n => n.Status != "ACCEPTED") };
    }
}
