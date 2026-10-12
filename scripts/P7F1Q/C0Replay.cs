using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// Gate C0 (D3.4): offline replay of document 073 with the pinned candidate and source hashes. No network call is made by this code.
// c0-pin writes the pins file; c0-replay checks the current code against the pins before it runs, and refuses on any mismatch.
internal static class C0Replay
{
    const string Rel = "todo10_8/heading_corpus_100/05_bien_ban_hop/073_FORTIS_GC_Minutes_Mar_2026.pdf";
    const string Instruction = "Trích xuất các heading chính";

    public static void Pin(string repoRoot, string artifactDir)
    {
        var (cands, legacy) = Load(repoRoot);
        var pins = new
        {
            artifact = "p7.f1.v6.d3.4-c0-pins.v1",
            document = "073",
            relativePath = Rel,
            sourceSha256 = Sha(File.ReadAllBytes(Path.Combine(repoRoot, Rel))),
            candidatesSha256 = P7F1V6Hier.CandidatesSha256(cands),
            candidateCount = cands.Count,
            sourceUniverseSha256 = legacy.SourceAliasUniverseSha256,
            evidenceStoreSha256 = legacy.EvidenceStoreSha256,
            manifestFile = "p7.f1.v6.d3.1-protocol-manifest.v1.json",
            manifestSha256 = Sha(File.ReadAllBytes(Path.Combine(artifactDir, "p7.f1.v6.d3.1-protocol-manifest.v1.json"))),
            frozenSnapshotCommit = "696d2e56c6ee57bed858349fac75c38f80878e1b",
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d3.4-c0-pins.v1.json"), JsonSerializer.Serialize(pins, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine("pins written: candidates " + pins.candidatesSha256[..12] + ", source " + pins.sourceSha256[..12]);
    }

    public static void Replay(string repoRoot, string artifactDir, string safeRoot)
    {
        var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifactDir, "p7.f1.v6.d3.4-c0-pins.v1.json"))).RootElement;
        var (cands, legacy) = Load(repoRoot);
        var sourceSha = Sha(File.ReadAllBytes(Path.Combine(repoRoot, Rel)));
        var candidatesSha = P7F1V6Hier.CandidatesSha256(cands);
        var manifestSha = Sha(File.ReadAllBytes(Path.Combine(artifactDir, "p7.f1.v6.d3.1-protocol-manifest.v1.json")));
        var mismatches = new List<string>();
        if (sourceSha != pins.GetProperty("sourceSha256").GetString()) mismatches.Add("SOURCE_SHA");
        if (candidatesSha != pins.GetProperty("candidatesSha256").GetString()) mismatches.Add("CANDIDATES_SHA");
        if (manifestSha != pins.GetProperty("manifestSha256").GetString()) mismatches.Add("MANIFEST_SHA");
        if (mismatches.Count > 0)
        {
            Console.WriteLine("C0 REFUSED: pinned values differ: " + string.Join(", ", mismatches));
            Environment.ExitCode = 2;
            return;
        }
        var gate = P7F1V6SourceGate.Check(repoRoot, Rel, sourceSha);
        var store = P7F1V6SafeRawStore.CreateRun(safeRoot);
        var opts = new P7F1V6Hier.Options(Instruction, legacy.SourceSha256, 60, 3, 8, 60, null, 16384, "NORMAL", 131072, 2, 20, 8,
            SourceUniverseSha256: legacy.SourceAliasUniverseSha256, ParserVersion: P7F1QEvidenceTools.VersionV3, EvidenceStoreVersion: legacy.EvidenceStoreSha256,
            ExpectedCandidatesSha256: candidatesSha, RequirePinned: true, SafeStore: store, RequireSafeStore: true);
        var calls = 0;
        var planned = new List<P7F1V6Protocol.PreparedRequest>();
        // Scripted decisions only: no network. Local decides by alias parity, reconcile and synthesis confirm what they were shown.
        var run = P7F1V6Hier.Run(cands, opts, (P7F1V6Hier.PreparedNodeModel)((n, k, p, r) => { calls++; planned.Add(r); return Scripted(n, k, p); }));
        var disk = store.VerifyOnDisk();
        var result = new
        {
            artifact = "p7.f1.v6.d3.4-c0-replay.v1",
            status = run.RunStatus,
            protocolResolved = run.Summary.ProtocolResolved,
            pins = new { candidatesSha256 = candidatesSha, sourceSha256 = sourceSha, manifestSha256 = manifestSha },
            pinsMatched = true,
            sourceGate = new { allowed = gate.Allowed, code = gate.Code },
            providerCalls = 0,
            scriptedModelCalls = calls,
            occurrences = run.Summary.Occurrences,
            decided = run.Summary.Decided,
            maxEnvelopeBytes = run.Summary.MaxEnvelopeBytes,
            rawItemsInSafeStore = run.Raw.Items.Count,
            rawItemsOnDiskVerified = run.Raw.Items.Keys.Count(k => store.ReadVerified(k) is not null),
            safeStoreDamagedItems = disk.Count,
            safeStoreDirectoryName = Path.GetFileName(store.RunDirectory),
            violations = run.Violations.Count,
            noteOnSourceGate = "The source gate checks the prefix and the pinned hash. It does not establish that the document is public.",
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d3.4-c0-replay.v1.json"), JsonSerializer.Serialize(result, Indented) + "\n", new UTF8Encoding(false));
        WriteDryRun(artifactDir, planned, candidatesSha, sourceSha, manifestSha, legacy, gate.Code, run.Summary.ProtocolResolved, store.RunDirectory);
        Console.WriteLine($"C0 replay: {run.RunStatus}, calls {calls}, provider calls 0, damaged items {disk.Count}");
    }

    /// <summary>The C1 dry-run (D3.5): every request the replay built, with bytes, hash, worst-case cost and every gate result. Nothing is sent.</summary>
    static void WriteDryRun(string artifactDir, List<P7F1V6Protocol.PreparedRequest> planned, string candidatesSha, string sourceSha,
        string manifestSha, P7F1QEvidenceTools legacy, string sourceGateCode, bool c0Passed, string safeRunDir)
    {
        var pins = new P7F1V6Runner.PinSet(sourceSha, legacy.SourceAliasUniverseSha256, candidatesSha, manifestSha);
        var inventory = new[]
        {
            new P7F1V6Protocol.EndpointRecord("alibaba", "qwen/qwen3.7-flash", 1000000, 65536),
            new P7F1V6Protocol.EndpointRecord("alibaba/us-east-1", "qwen/qwen3.7-flash", 1000000, 65536),
        };
        // Public status and a fresh inventory read are not established here: the gates report them as not met.
        var inputs = new P7F1V6Runner.Inputs(pins, pins, sourceGateCode, PublicConfirmed: false, inventory, InventoryFresh: false,
            SafeStore: true, C0Passed: c0Passed, LiveAuthorised: false);
        var gates = P7F1V6Runner.Gates(inputs);
        var lines = P7F1V6Runner.DryRun(planned);
        var dry = new
        {
            artifact = "p7.f1.v6.d3.5-dryrun.v1",
            document = "073",
            provider = "none: dry-run, no request sent",
            route = new { allow = "alibaba", ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            caps = new { maxCalls = P7F1V6Runner.MaxCalls, maxRetries = P7F1V6Runner.MaxRetries, ceilingUsd = (double)P7F1V6Runner.CeilingUsd,
                payloadCapBytes = P7F1V6Protocol.PayloadCapBytes, envelopeCapBytes = P7F1V6Protocol.EnvelopeCapBytes, visibleResponseCapBytes = P7F1V6Protocol.VisibleResponseCapBytes },
            plannedRequests = lines.Select(l => new { l.Index, l.EnvelopeBytes, l.Sha256, worstUsd = Math.Round(l.WorstUsd, 6),
                reserveAfterUsd = Math.Round(l.ReserveAfterUsd, 6), withinCeiling = l.WithinCeiling }),
            worstReserveUsd = lines.Count == 0 ? 0 : Math.Round(lines[^1].ReserveAfterUsd, 6),
            gates = gates.Select(g => new { g.Name, g.Passed, g.Reason }),
            structuralMaxReserveUsd = Math.Round(P7F1V6Runner.MaxCalls * P7F1V6Runner.WorstAttemptUsd(P7F1V6Protocol.EnvelopeCapBytes), 6),
            costGate = new { ceilingUsd = (double)P7F1V6Runner.CeilingUsd, passed = P7F1V6Runner.MaxCalls * P7F1V6Runner.WorstAttemptUsd(P7F1V6Protocol.EnvelopeCapBytes) <= (double)P7F1V6Runner.CeilingUsd,
                basis = "six attempts, each at the envelope cap, completion 65,536; includes one retry in the count" },
            allGatesPassed = P7F1V6Runner.AllPassed(gates) && P7F1V6Runner.MaxCalls * P7F1V6Runner.WorstAttemptUsd(P7F1V6Protocol.EnvelopeCapBytes) <= (double)P7F1V6Runner.CeilingUsd,
            safeStoreRunDirectoryName = Path.GetFileName(safeRunDir),
            costNote = "Worst case per attempt: prompt tokens bounded by UTF-8 bytes, completion at 65,536. Not the billed amount; endpoint reasoning and token accounting are not verified.",
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d3.5-dryrun.v1.json"), JsonSerializer.Serialize(dry, Indented) + "\n", new UTF8Encoding(false));
    }

    static (List<P7F1V6Hier.Cand> Cands, P7F1QEvidenceTools Legacy) Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, Rel);
        var parsed = PdfSourceAdapter.BuildWithDetails(path);
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        var cands = legacy.AllLines.Where(l => l.Page is not null)
            .Select(l => new P7F1V6Hier.Cand(l.Alias, l.Page!.Value, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToList();
        return (cands, legacy);
    }

    static string Scripted(string nodeId, P7F1V6Hier.Kind kind, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        if (kind == P7F1V6Hier.Kind.Interpretation)
            return JsonSerializer.Serialize(new { protocolVersion = "P7_F1_V6_INTERPRET_V1", requestId = "INTERPRET", status = "COMPLETE",
                interpretation = "Task: " + r.GetProperty("originalUserInstruction").GetString() }, Indented);
        if (kind == P7F1V6Hier.Kind.Local)
        {
            var items = r.GetProperty("owned").EnumerateArray().Select(o =>
            {
                var a = o.GetProperty("sourceAlias").GetString()!;
                var even = int.Parse(a[1..5]) % 2 == 0;
                return $"{{\"id\":\"{o.GetProperty("id").GetString()}\",\"decision\":\"{(even ? "SELECT" : "NOT_SELECT")}\",\"evidenceRefs\":[\"{a}\"]}}";
            });
            return $"{{\"protocolVersion\":\"{P7F1V6Hier.NodeVersion}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"items\":[{string.Join(",", items)}]}}";
        }
        var ids = (kind == P7F1V6Hier.Kind.Reconcile ? r.GetProperty("owned") : r.GetProperty("shown")).EnumerateArray()
            .Select(o => "\"" + o.GetProperty("id").GetString() + "\"");
        return $"{{\"protocolVersion\":\"{P7F1V6Hier.NodeVersion}\",\"nodeId\":\"{nodeId}\",\"status\":\"COMPLETE\",\"confirmed\":[{string.Join(",", ids)}]}}";
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
