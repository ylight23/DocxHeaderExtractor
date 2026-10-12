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
        // Scripted decisions only: no network. Local decides by alias parity, reconcile and synthesis confirm what they were shown.
        var run = P7F1V6Hier.Run(cands, opts, (P7F1V6Hier.PreparedNodeModel)((n, k, p, r) => { calls++; return Scripted(n, k, p); }));
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
        Console.WriteLine($"C0 replay: {run.RunStatus}, calls {calls}, provider calls 0, damaged items {disk.Count}");
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
