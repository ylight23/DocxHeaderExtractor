using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V6Qualification.P7;

// Issue #9 D1 preflight (offline, no provider). Measures what a V6 run would send and receive on real documents, using the
// driver itself with a scripted honest model, plus a whole-document read census with the read tools. Writes one JSON per
// document and a summary. Sizes are exact UTF-8 bytes. Token counts are not measured here (no tokenizer on this machine):
// tokens <= bytes is a valid upper bound for UTF-8 text, so bytes are reported as the token upper bound.
internal static class V6Preflight
{
    const int ChunkLines = 48;               // the layout-aware chunk size used by V2–V5
    const int SegmentCandidates = 200;       // a segmented reconcile would send this many candidates per request
    const decimal PromptUsdPerToken = 0.0000002m, CompletionUsdPerToken = 0.0000008m;   // the same pricing bounds as the runner
    const int MaxTokens = 32768;
    const int ReconcileItemBytesBound = 80 + 8 * 16 + 300;   // id, decision, 8 evidence refs, a 300-character note

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
                var m = Measure(d);
                File.WriteAllText(Path.Combine(outDir, d.Id + ".json"), JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
                summary.Add(new { id = d.Id, status = "OK", seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
                Console.WriteLine($"{d.Id} OK {sw.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception e)
            {
                summary.Add(new { id = d.Id, status = "ERROR", error = e.GetType().Name + ": " + e.Message, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) });
                Console.WriteLine($"{d.Id} ERROR {e.GetType().Name}: {e.Message}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    static object Measure(DocRef d)
    {
        var pdfBytes = File.ReadAllBytes(d.Path);
        var sourceSha = Convert.ToHexStringLower(SHA256.HashData(pdfBytes));
        var parsed = PdfSourceAdapter.BuildWithDetails(d.Path);
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        var lines = legacy.AllLines;
        var placed = lines.Where(l => l.Page is not null).ToArray();
        var pages = placed.Select(l => l.Page!.Value).Distinct().Order().ToArray();
        var longLines = lines.Count(l => Encoding.UTF8.GetByteCount(l.Text) > P7F1V6Tools.MaxResultBytes);
        var packs = pages.SelectMany(pg => placed.Where(l => l.Page == pg).Chunk(ChunkLines).Select((c, k) =>
            new P7F1V6Driver.Pack($"p{pg}-c{k + 1}", c.Select(l => (l.Alias, l.Page!.Value, l.Text)).ToArray()))).ToArray();

        // Honest scripted run through the real driver: every payload and output is measured from the transcript.
        var tools = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, lines);
        var instr = "Trích xuất các heading chính";
        var run = P7F1V6Driver.Run(new(instr, tools, sourceSha, packs), (phase, req, payload, results) =>
            phase == "interpret" ? P7F1V6Driver.Turn.Final(Interp(req)) : P7F1V6Driver.Turn.Final(Respond(payload, Even)));

        var events = run.Transcript.Select(e => JsonDocument.Parse(JsonSerializer.Serialize(e)).RootElement).ToArray();
        long interpretBytes = 0; var packBytes = new List<long>(); long reconcileBytes = 0; long outputBytesReconcile = 0;
        var packOutputBytes = new List<long>();
        foreach (var e in events)
        {
            var kind = e.GetProperty("kind").GetString(); var phase = e.GetProperty("phase").GetString()!;
            if (kind == "PAYLOAD")
            {
                var bytes = e.GetProperty("data").GetProperty("bytes").GetInt64();
                if (phase == "interpret") interpretBytes = bytes;
                else if (phase == "reconcile") reconcileBytes = bytes;
                else packBytes.Add(bytes);
            }
            if (kind == "FINAL")
            {
                var bytes = e.GetProperty("data").GetProperty("bytes").GetInt64();
                if (phase == "reconcile") outputBytesReconcile = bytes; else if (phase.StartsWith("pack-", StringComparison.Ordinal)) packOutputBytes.Add(bytes);
            }
        }

        // Whole-document read census: read every granted page with paging, then complete every partial line with read_line_span.
        var census = ReadCensus(legacy, lines);

        // Reconcile strategies. Whole: one request with every candidate (as the driver sends). Segmented: the same overview plus
        // SegmentCandidates candidates per request; sizes computed from the same payload construction.
        var overview = P7F1V6Core.Overview(lines);
        var overviewBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(overview, P7F1QEvidenceTools.WireJson));
        var candidates = packs.SelectMany(p => p.Subjects).ToArray();
        var segBytes = candidates.Chunk(SegmentCandidates).Select(seg => SegmentBytes(seg, overview)).ToArray();

        var totalPayloadBytes = interpretBytes + packBytes.Sum() + reconcileBytes;
        var worstOutputBytesReconcile = candidates.Length * ReconcileItemBytesBound + 200;
        var costBytesUpperBound = (totalPayloadBytes * PromptUsdPerToken) + (packBytes.Count + 2) * MaxTokens * CompletionUsdPerToken;

        return new
        {
            version = "P7_F1_V6_PREFLIGHT_V1", document = d.Id, path = Path.GetFileName(d.Path), sourceSha256 = sourceSha,
            source = new { pages = pages.Length, maxPage = pages.LastOrDefault(), lines = lines.Count, placedLines = placed.Length,
                unplacedLines = lines.Count - placed.Length, atoms = parsed.Snapshot.Atoms.Count, candidates = candidates.Length,
                packs = packs.Length, linesOverCap = longLines, maxLineBytes = lines.Count == 0 ? 0 : lines.Max(l => Encoding.UTF8.GetByteCount(l.Text)) },
            interpretation = new { requestBytes = interpretBytes, tokensUpperBound = interpretBytes },
            packs = new { count = packBytes.Count, requestBytes = Stats(packBytes), outputBytes = Stats(packOutputBytes), maxRequestBytes = packBytes.DefaultIfEmpty(0).Max() },
            reconcile = new { candidates = candidates.Length, requestBytes = reconcileBytes, tokensUpperBound = reconcileBytes, overviewBytes,
                worstOutputBytes = worstOutputBytesReconcile, observedOutputBytes = outputBytesReconcile,
                exceedsMaxTokensByBytes = worstOutputBytesReconcile > MaxTokens,
                segmented = new { segmentCandidates = SegmentCandidates, segments = segBytes.Length, requestBytes = Stats(segBytes.Select(x => (long)x).ToList()), maxRequestBytes = segBytes.DefaultIfEmpty(0).Max() } },
            tools = census,
            transcript = new { events = events.Length, sha256 = run.TranscriptSha256, rawItems = run.Raw.Items.Count, rawVerified = run.Raw.Verify() },
            coverage = new { issued = candidates.Length, evaluated = run.Resolution?.Finals.Count(f => f.State != "MISSING") ?? 0,
                missing = run.Resolution?.Finals.Count(f => f.State == "MISSING") ?? 0, runStatus = run.RunStatus, assessment = run.Status?.Assessment },
            cost = new { promptBytes = totalPayloadBytes, costUsdUpperBoundBytes = Math.Round(costBytesUpperBound, 6),
                runnerFormulaUsdLargestRequest = Math.Round(new F1QRunCaps().WorstCaseUsd((int)Math.Min(int.MaxValue, packBytes.DefaultIfEmpty(0).Max()), MaxTokens), 6),
                note = "runner formula assumes bytes/2 prompt tokens; bytes is the proven upper bound" },
        };
    }

    static object Stats(List<long> xs)
    {
        if (xs.Count == 0) return new { min = 0, p50 = 0, p95 = 0, max = 0, n = 0 };
        var s = xs.OrderBy(v => v).ToArray();
        long P(double q) => s[(int)Math.Min(s.Length - 1, Math.Ceiling(q * s.Length) - 1)];
        return new { min = s[0], p50 = P(0.50), p95 = P(0.95), max = s[^1], n = s.Length };
    }

    /// <summary>Exact bytes of one segmented reconcile request: the same payload shape as the driver's reconcile, for a subset of candidates.</summary>
    static long SegmentBytes((string Alias, int Page, string Text)[] seg, object overview)
    {
        var payload = JsonSerializer.Serialize(new
        {
            protocolVersion = P7F1V6Core.DecisionVersion, requestId = "RECONCILE-SEGMENT", instruction = "Trích xuất các heading chính",
            instructionSha256 = P7F1V6Core.InstructionSha256("Trích xuất các heading chính"), overview,
            candidates = seg.Select(s => new { id = "xxxx", sourceAlias = s.Alias, page = s.Page, text = s.Text, provisional = "NONE" }).ToArray(),
        });
        return Encoding.UTF8.GetByteCount(payload);
    }

    static string Interp(string req) =>
        $"{{\"protocolVersion\":\"P7_F1_V6_INTERPRET_V1\",\"requestId\":\"{req}\",\"status\":\"COMPLETE\",\"interpretation\":\"Scripted.\"}}";

    static bool Even(string alias) => int.Parse(alias[1..5]) % 2 == 0;

    static string Respond(string payload, Func<string, bool> even)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var req = root.GetProperty("requestId").GetString();
        var items = root.TryGetProperty("subjects", out var s) ? s : root.GetProperty("candidates");
        var decisions = items.EnumerateArray().Select(x =>
        {
            var a = x.GetProperty("sourceAlias").GetString()!;
            return $"{{\"id\":\"{x.GetProperty("id").GetString()}\",\"decision\":\"{(even(a) ? "SELECT" : "NOT_SELECT")}\",\"evidenceRefs\":[\"{a}\"]}}";
        });
        return $"{{\"protocolVersion\":\"P7_F1_V6_DECISIONS_V1\",\"requestId\":\"{req}\",\"status\":\"COMPLETE\",\"decisions\":[{string.Join(",", decisions)}]}}";
    }

    /// <summary>Reads every page with paging, then completes every partial line with read_line_span. Counts calls and bytes.</summary>
    static object ReadCensus(P7F1QEvidenceTools legacy, IReadOnlyList<F1QSourceLine> lines)
    {
        var t = new P7F1V6Tools(legacy.SourceSha256, legacy.SourceAliasUniverseSha256, legacy.EvidenceStoreSha256, lines);
        int calls = 0; long bytes = 0; int errors = 0;
        foreach (var pg in t.Pages)
        {
            int offset = 0;
            while (true)
            {
                var r = t.GetPageText($"C.p{pg}.{offset}", "CENSUS", pg, offset, 50);
                calls++; bytes += r.Content.Length; if (r.Status != "OK") { errors++; break; }
                var res = JsonDocument.Parse(r.Content).RootElement.GetProperty("result");
                if (!res.GetProperty("hasMore").GetBoolean()) break;
                offset = res.GetProperty("nextOffset").GetInt32();
            }
        }
        var partial = t.Scope.PartialViews.ToArray();
        int spanCalls = 0; long spanBytes = 0; int spanErrors = 0;
        foreach (var alias in partial)
        {
            int offset = 0; int guard = 0;
            while (guard++ < 100000)
            {
                var r = t.ReadLineSpan($"C.s.{alias}.{offset}", "CENSUS", alias, offset, 20000);
                spanCalls++; spanBytes += r.Content.Length; if (r.Status != "OK") { spanErrors++; break; }
                var res = JsonDocument.Parse(r.Content).RootElement.GetProperty("result");
                if (!res.GetProperty("hasMore").GetBoolean()) break;
                offset = res.GetProperty("nextOffset").GetInt32();
            }
        }
        var incomplete = t.Scope.IncompleteEvidence.Count;
        return new { pageCalls = calls, pageBytes = bytes, pageErrors = errors, partialLines = partial.Length, spanCalls, spanBytes, spanErrors,
            incompleteAfterCensus = incomplete, observedAll = t.Scope.ReturnedToModel.Count, lines = lines.Count };
    }
}
