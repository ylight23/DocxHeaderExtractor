using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;
using DocxHeaderExtractor.V5Qualification.P7;

// Gate C0 (D3.5a): offline replay of the public document RFC 9113 through the same dispatch, audit and parser path as a live run, with the
// mock port. Nothing here opens a connection. c0-pin writes the pins; c0-replay refuses if the source, candidate, universe or manifest hash
// differs from the pins. The document is an IETF RFC, published publicly; the corpus owner's public-status record is still a separate gate.
internal static class C0Replay
{
    const string Rel = "todo10_8/heading_corpus_100/07_system_generated/094_RFC9113_HTTP_2.pdf";
    const string Instruction = "Trích xuất các heading chính";
    const string PinsFile = "p7.f1.v6.d3.5a-c0-pins.v1.json";
    const string ReplayFile = "p7.f1.v6.d3.5a-c0-replay.v1.json";
    const string DryRunFile = "p7.f1.v6.d3.5a-dryrun.v1.json";
    const string Inventory = "p7.f1.v6.d3.5a-endpoints-readonly.raw.json";
    const string Manifest = "p7.f1.v6.d3.1-protocol-manifest.v1.json";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Pin(string repoRoot, string artifactDir)
    {
        var (cands, legacy) = Load(repoRoot);
        var pins = new
        {
            artifact = "p7.f1.v6.d3.5a-c0-pins.v1",
            document = Rel,
            sourceSha256 = Sha(File.ReadAllBytes(Path.Combine(repoRoot, Rel))),
            universeSha256 = legacy.SourceAliasUniverseSha256,
            candidatesSha256 = P7F1V6Hier.CandidatesSha256(cands),
            candidateCount = cands.Count,
            manifestSha256 = Sha(File.ReadAllBytes(Path.Combine(artifactDir, Manifest))),
            evidenceStoreSha256 = legacy.EvidenceStoreSha256,
        };
        File.WriteAllText(Path.Combine(artifactDir, PinsFile), JsonSerializer.Serialize(pins, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"pins written: candidates {pins.candidatesSha256[..12]}, source {pins.sourceSha256[..12]}, {cands.Count} candidates");
    }

    public static void Replay(string repoRoot, string artifactDir, string safeRoot)
    {
        var pins = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifactDir, PinsFile))).RootElement;
        var (cands, legacy) = Load(repoRoot);
        var sourceBytes = File.ReadAllBytes(Path.Combine(repoRoot, Rel));
        var observed = new P7F1V6Preflight.PinSet(Sha(sourceBytes), legacy.SourceAliasUniverseSha256, P7F1V6Hier.CandidatesSha256(cands),
            Sha(File.ReadAllBytes(Path.Combine(artifactDir, Manifest))));
        var pinned = new P7F1V6Preflight.PinSet(pins.GetProperty("sourceSha256").GetString()!, pins.GetProperty("universeSha256").GetString()!,
            pins.GetProperty("candidatesSha256").GetString()!, pins.GetProperty("manifestSha256").GetString()!);
        var mismatches = new List<string>();
        if (observed.SourceSha256 != pinned.SourceSha256) mismatches.Add("SOURCE_SHA");
        if (observed.UniverseSha256 != pinned.UniverseSha256) mismatches.Add("UNIVERSE_SHA");
        if (observed.CandidatesSha256 != pinned.CandidatesSha256) mismatches.Add("CANDIDATES_SHA");
        if (observed.ManifestSha256 != pinned.ManifestSha256) mismatches.Add("MANIFEST_SHA");
        if (mismatches.Count > 0)
        {
            Console.WriteLine("C0 REFUSED: pinned values differ: " + string.Join(", ", mismatches));
            Environment.ExitCode = 2;
            return;
        }

        var inventoryPath = Path.Combine(artifactDir, Inventory);
        var inventorySha = Sha(File.ReadAllBytes(inventoryPath));
        var source = P7F1V6SourceGate.Snapshot(repoRoot, Rel, pinned.SourceSha256);
        var store = P7F1V6SafeRawStore.CreateRun(safeRoot);

        // The preflight is built from evidence, in mock mode: it reports every gate, and the mock path can reach only a mock port.
        var mockPreflight = P7F1V6Preflight.Result.Build(new P7F1V6Preflight.Evidence(P7F1V6Preflight.RunMode.Mock, "c0-replay", pinned, observed,
            source, SourcePublicConfirmed: false, inventoryPath, inventorySha, InventoryFreshDeclared: true, store.RunDirectory,
            C0ArtifactPath: null, AuthorisationPath: null, DocumentRelativePath: Rel));

        var opts = new P7F1V6Hier.Options(Instruction, legacy.SourceSha256, 60, 3, 8, 60, null, 16384, "NORMAL", 131072, 2, 20, 8,
            SourceUniverseSha256: pinned.UniverseSha256, ParserVersion: P7F1QEvidenceTools.VersionV3, EvidenceStoreVersion: legacy.EvidenceStoreSha256,
            ExpectedCandidatesSha256: pinned.CandidatesSha256, RequirePinned: true, SafeStore: store, RequireSafeStore: true);
        var (run, state, port) = P7F1V6MockRun.Run(cands, opts, mockPreflight);

        var disk = store.VerifyOnDisk();
        var replay = new
        {
            artifact = "p7.f1.v6.d3.5a-c0-replay.v1",
            document = Rel,
            unit = "page 1 of the document (one shard)",
            documentPublicity = "IETF RFC 9113, a published document; the corpus owner's public-status record is not yet on file",
            status = run.RunStatus,
            runnerStopReason = state.StopReason,
            protocolResolved = run.Summary.ProtocolResolved,
            pins = new { candidatesSha256 = pinned.CandidatesSha256, sourceSha256 = pinned.SourceSha256, universeSha256 = pinned.UniverseSha256, manifestSha256 = pinned.ManifestSha256 },
            pinsMatched = true,
            mode = "MOCK",
            providerCalls = 0,
            mockCalls = port.Calls,
            attempts = state.Attempts,
            reservedUsd = Math.Round(state.ReservedUsd, 6),
            occurrences = run.Summary.Occurrences,
            decided = run.Summary.Decided,
            maxEnvelopeBytes = run.Summary.MaxEnvelopeBytes,
            rawItemsInSafeStore = run.Raw.Items.Count,
            rawItemsOnDiskVerified = run.Raw.Items.Keys.Count(k => store.ReadVerified(k) is not null),
            safeStoreDamagedItems = disk.Count,
            safeStoreRunDirectoryName = Path.GetFileName(store.RunDirectory),
            violations = run.Violations.Count,
            sourceGate = new { allowed = source.Allowed, code = source.Code },
        };
        File.WriteAllText(Path.Combine(artifactDir, ReplayFile), JsonSerializer.Serialize(replay, Indented) + "\n", new UTF8Encoding(false));

        // The final gate report: the evidence now includes this replay, and the live gates are reported in the same way.
        var c0Passed = run.RunStatus == "PROTOCOL_RESOLVED" && state.StopReason is null && disk.Count == 0;
        var liveEvidence = new P7F1V6Preflight.Evidence(P7F1V6Preflight.RunMode.Live, "c0-replay", pinned, observed, source,
            SourcePublicConfirmed: false, inventoryPath, inventorySha, InventoryFreshDeclared: true, store.RunDirectory,
            C0ArtifactPath: Path.Combine(artifactDir, ReplayFile), AuthorisationPath: null, DocumentRelativePath: Rel);
        var live = P7F1V6Preflight.Result.Build(liveEvidence);
        WriteDryRun(artifactDir, port.Requests, live, c0Passed, store.RunDirectory);
        Console.WriteLine($"C0 replay (mock): {run.RunStatus}, mock calls {port.Calls}, attempts {state.Attempts}, provider calls 0, damaged items {disk.Count}");
        Console.WriteLine($"live preflight allowed: {live.Allowed}; failed gates: {string.Join(", ", live.Gates.Where(g => !g.Passed).Select(g => g.Name))}");
    }

    /// <summary>The dry-run (D3.5a): every request the replay built, with bytes, hash and worst-case cost, and the live preflight gate results.
    /// Nothing is sent.</summary>
    static void WriteDryRun(string artifactDir, IReadOnlyList<P7F1V6Protocol.PreparedRequest> requests, P7F1V6Preflight.Result live, bool c0Passed,
        string safeRunDir)
    {
        var lines = P7F1V6Runner.DryRun(requests);
        var structural = P7F1V6Runner.MaxCalls * P7F1V6Runner.WorstAttemptUsd(P7F1V6Protocol.EnvelopeCapBytes);
        var dry = new
        {
            artifact = "p7.f1.v6.d3.5a-dryrun.v1",
            document = Rel,
            provider = "none: dry-run, no request sent",
            mode = "MOCK requests recorded; live gates evaluated from evidence",
            route = new { allow = "alibaba", ignore = P7F1V6Protocol.DefaultIgnore, allowFallbacks = false, requireParameters = true },
            caps = new { maxCalls = P7F1V6Runner.MaxCalls, maxRetries = P7F1V6Runner.MaxRetries, ceilingUsd = (double)P7F1V6Runner.CeilingUsd,
                payloadCapBytes = P7F1V6Protocol.PayloadCapBytes, envelopeCapBytes = P7F1V6Protocol.EnvelopeCapBytes, visibleResponseCapBytes = P7F1V6Protocol.VisibleResponseCapBytes },
            plannedRequests = lines.Select(l => new { l.Index, l.EnvelopeBytes, l.Sha256, worstUsd = Math.Round(l.WorstUsd, 6),
                reserveAfterUsd = Math.Round(l.ReserveAfterUsd, 6), withinCeiling = l.WithinCeiling }),
            worstReserveUsd = lines.Count == 0 ? 0 : Math.Round(lines[^1].ReserveAfterUsd, 6),
            structuralSixAttemptReserveUsd = Math.Round(structural, 6),
            costGate = new { ceilingUsd = (double)P7F1V6Runner.CeilingUsd, passed = structural <= (double)P7F1V6Runner.CeilingUsd },
            c0Replay = c0Passed ? "PASSED (mock)" : "NOT PASSED",
            liveGates = live.Gates.Select(g => new { g.Name, g.Passed, g.Reason }),
            liveAllowed = live.Allowed,
            safeStoreRunDirectoryName = Path.GetFileName(safeRunDir),
            costNote = "Worst case per attempt: prompt tokens bounded by UTF-8 bytes, completion at 65,536. Not the billed amount; endpoint reasoning and token accounting are not verified.",
        };
        File.WriteAllText(Path.Combine(artifactDir, DryRunFile), JsonSerializer.Serialize(dry, Indented) + "\n", new UTF8Encoding(false));
    }

    /// <summary>D4 size audit (offline): sizes the DIRECT-F1 requests over every line of RFC 9113 with the approved instruction, single-context
    /// and segmented. Nothing is sent; the sizes are byte upper bounds on tokens (no tokenizer is available here).</summary>
    public static void D4SizeAudit(string repoRoot, string artifactDir)
    {
        var (_, legacy) = Load(repoRoot);
        var lines = legacy.AllLines
            .Select(l => new P7F1V6Direct.Line(l.Alias, l.Page ?? 0, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToList();
        var sourceSha = Sha(File.ReadAllBytes(Path.Combine(repoRoot, Rel)));
        var maxAlias = lines.Max(l => l.Alias.Length);
        var perReply = P7F1V6Direct.DecisionsPerReply(maxAlias);
        var (single, segmented) = P7F1V6Direct.Compare(Instruction, sourceSha, lines);

        // Boundary exposure (PROXY, not Gold). RFC 9113 has no approved Gold, so no error rate can be measured. These numbers show where
        // the segmented cores meet and how much context each side has. A proxy heading is a line whose text starts with a numbering pattern.
        var overlap = P7F1V6Direct.DefaultOverlapLines;
        var heading = new System.Text.RegularExpressions.Regex(@"^\d+(\.\d+)*\.?\s+\S", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var proxy = lines.Select((l, i) => (l, i)).Where(x => x.l.Text.Length <= 100 && heading.IsMatch(x.l.Text)).Select(x => x.i).ToList();
        var boundaries = Enumerable.Range(1, Math.Max(0, (lines.Count + perReply - 1) / perReply - 1))
            .Select(k => k * perReply).Where(b => b < lines.Count)
            .Select(b => new
            {
                boundaryLine = b,
                leftContextLines = Math.Min(overlap, b),
                rightContextLines = Math.Min(overlap, lines.Count - b),
                proxyHeadingsInOverlapZone = proxy.Count(i => i >= b - overlap && i < b + overlap),
                nearestProxyHeadingDistance = proxy.Count == 0 ? (int?)null : proxy.Min(i => Math.Abs(i - b)),
            }).ToList();
        var boundaryAudit = new
        {
            status = "PROXY_NOT_GOLD",
            overlapLines = overlap,
            boundaries = boundaries.Count,
            proxyHeadingLines = proxy.Count,
            proxyHeadingsInAnyOverlapZone = proxy.Count(i => boundaries.Any(b => i >= b.boundaryLine - overlap && i < b.boundaryLine + overlap)),
            boundaryDetail = boundaries,
            limitation = "Without an approved Gold for RFC 9113, whether 20 lines of overlap are enough cannot be measured. Only exposure is shown. Context retrieval (asking for more lines) is not implemented in D4.1.",
        };

        var audit = new
        {
            artifact = "p7.f1.v6.d4-size-audit.v1",
            document = Rel,
            documentSha256 = sourceSha,
            providerCalls = 0,
            instruction = Instruction,
            lineCount = lines.Count,
            pages = lines.Select(l => l.Page).Distinct().Count(),
            linesWithLayout = lines.Count(l => l.Layout is not null),
            maxAliasChars = maxAlias,
            replyCapBytes = P7F1V6Direct.VisibleResponseCapBytes,
            worstDecisionBytes = P7F1V6Direct.WorstDecisionBytes(maxAlias),
            decisionsPerReply = perReply,
            replyPagesNeeded = (lines.Count + perReply - 1) / perReply,
            maxTokens = P7F1V6Protocol.MaxTokens,
            promptTokenBudget = P7F1V6Direct.PromptTokenBudget,
            endpointContextTokens = P7F1V6Direct.EndpointContextTokens,
            tokenBoundNote = "Token counts are not measured. Every token covers at least one UTF-8 byte, so the request byte count bounds the prompt tokens from above. Cost uses that bound and the tier the bound reaches.",
            singleContext = single,
            segmented,
            boundaryExposure = boundaryAudit,
        };
        File.WriteAllText(Path.Combine(artifactDir, "p7.f1.v6.d4-size-audit.v1.json"), JsonSerializer.Serialize(audit, Indented) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"d4 size audit: {lines.Count} lines, {perReply} decisions per reply, single {single.Requests} requests (covered {single.Covered}, max {single.MaxRequestBytes} bytes, ${single.WorstUsd:F4}), segmented {segmented.Requests} requests (covered {segmented.Covered}, max {segmented.MaxRequestBytes} bytes, ${segmented.WorstUsd:F4})");
    }

    /// <summary>D4.1 canary T1 (offline): writes the exact HTTP body and its audit record. Nothing is sent.</summary>
    public static void D4CanaryArtifact(string artifactDir, string generatedAtCommit)
    {
        var sha = P7F1V6DirectCanary.WriteArtifact(artifactDir, generatedAtCommit);
        Console.WriteLine($"canary T1 request written: sha256 {sha}, not sent");
    }

    static (List<P7F1V6Hier.Cand> Cands, P7F1QEvidenceTools Legacy) Load(string repoRoot)
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(Path.Combine(repoRoot, Rel));
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        var legacy = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        // The unit replayed is page 1 of the RFC: one shard of at most 60 candidates, so the live call structure (four calls) fits the six-call limit.
        // The full document needs about fifty shards and is not a candidate for a six-call run; it is not replayed here.
        var cands = legacy.AllLines.Where(l => l.Page is 1)
            .Select(l => new P7F1V6Hier.Cand(l.Alias, l.Page!.Value, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToList();
        return (cands, legacy);
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}
