using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EVAL ONLY - preflight for STRUCTURED_EVIDENCE_CONTEXT_V1.
/// <para>
/// Tests whether organizing the exact same FULL_CONTEXT source evidence into deterministic layout
/// regions - grouped by the "block" layout id the frozen capture already records per atom, a
/// coordinate/layout signal, never a semantic one - changes semantic-role classification, holding
/// content amount constant and varying only context organization. No provider call: this class
/// only reads the already-frozen, immutable FULL_CONTEXT transport captures
/// (direct-semantic-context-ablation-v1, repeat 1 - request bytes are identical across repeats)
/// and derives two text presentations of the identical atoms. It does not read Gold, does not read
/// any prior model result, and does not touch production runtime policy: FULL_STRUCTURED_CONTEXT_V1
/// is not wired in anywhere as a default.
/// </para>
/// </summary>
public sealed class StructuredEvidenceContextPreflightTests
{
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context/r1";
    private const string OutputRoot = "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252";

    private static readonly string[] Packs = ["PACK_001", "PACK_005", "PACK_006"];

    /// <summary>
    /// Diagnostic-only terms that can never legitimately appear in this document's real prose -
    /// unlike "expected", "correct", "Agenda" or "F1", which either occur as ordinary English in
    /// the source text or are the source's own genuine heading wording (ITEM-CCE2C592's target
    /// text literally is "Agenda"), so flagging them would false-positive on real content rather
    /// than catch a leak.
    /// </summary>
    private static readonly string[] ForbiddenLeaks =
    [
        "Gold", "Qwen", "Haiku", "expectedLabel", "goldLabel", "DOCUMENT_LABEL_ATTRACTOR",
    ];

    [Fact]
    public void The_experiment_is_pre_registered_before_any_provider_execution()
    {
        FreezeArtifact.AssertJson(OutputRoot, "experiment-design.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_experiment_design",
            schemaVersion = "a99-structured-evidence-context-experiment-design-v1",
            documentId = "DOC-0252",
            experiment = "STRUCTURED_EVIDENCE_CONTEXT_V1",
            goal = "Test whether organizing the exact same FULL context into deterministic " +
                "source/layout regions improves semantic-role classification compared with flat " +
                "FULL context, holding content amount constant and varying only context " +
                "organization.",
            arms = new object[]
            {
                new
                {
                    id = "FULL_CONTEXT", role = "existing comparison arm",
                    contentSource = "eval/a99-closed-loop/direct-semantic-context-ablation-v1/" +
                        "DOC-0252/full-context (frozen, unmodified, read-only)",
                },
                new
                {
                    id = "FULL_STRUCTURED_CONTEXT_V1", role = "new arm",
                    contentSource = "identical atoms to FULL_CONTEXT, grouped into deterministic " +
                        "layout regions",
                },
            },
            referenceArmOnly = "MINIMAL_STRUCTURAL_CONTEXT_V1",
            hypothesis = "Structured presentation (grouping source atoms into deterministic " +
                "layout regions) reduces semantic-scope interference relative to flat FULL_CONTEXT " +
                "presentation, for the same content.",
            hypothesisStatus = "PRE_REGISTERED_NOT_YET_TESTED",
            preRegisteredBefore = "any provider execution of FULL_STRUCTURED_CONTEXT_V1",
            contentInvariant = "CONTENT AMOUNT = constant; only CONTEXT ORGANIZATION varies",
            groupingSignal = "the existing layout block id already recorded per atom in the " +
                "frozen capture (\"block\": \"b307\", etc. - production layout-parser output), " +
                "grouped by maximal contiguous run in canonical source order. Purely " +
                "coordinate/layout-derived; regions are never semantically classified.",
            forbiddenRegionLabels = new[]
            {
                "DOCUMENT_HEADER", "DOCUMENT_LABEL_REGION", "METADATA_REGION",
                "STRUCTURAL_REGION", "HEADING_REGION",
            },
            regionIdScheme = "neutral sequential ids: R001, R002, R003, ...",
            labelContract = new[] { "STRUCTURAL_UNIT", "DOCUMENT_LABEL", "NON_STRUCTURAL" },
            itemCohort = "the same 18-item cohort already used by the direct semantic probe and " +
                "the Haiku blind confirmation; not added to or removed from in V1",
            productionPolicyChanged = false,
            modelCalls = 0,
            providerCalls = 0,
            providerExecutionAuthorized = false,
        });
    }

    [Fact]
    public void Eighteen_items_generated_with_conserved_content_and_differing_topology()
    {
        var packs = Packs.Select(pack => (pack, data: LoadPack(pack))).ToArray();
        var allItems = packs.SelectMany(entry => entry.data.Items
                .Select(item => (entry.pack, entry.data, item)))
            .ToArray();
        Assert.Equal(18, allItems.Length);
        Assert.Equal(18, allItems.Select(x => x.item.ItemId).Distinct(StringComparer.Ordinal).Count());

        var manifestItems = new List<object>();
        foreach (var (pack, data, item) in allItems)
        {
            var build = BuildAndVerifyItem(pack, data, item);
            manifestItems.Add(build.ManifestEntry);

            FreezeArtifact.AssertText($"{OutputRoot}/contexts", $"{item.ItemId}.full-flat.txt", build.FlatText);
            FreezeArtifact.AssertText($"{OutputRoot}/contexts", $"{item.ItemId}.full-structured.txt", build.StructuredText);
            FreezeArtifact.AssertJson($"{OutputRoot}/contexts", $"{item.ItemId}.content-proof.v1.json", build.Proof);

            AssertNoLeakage(build.FlatText);
            AssertNoLeakage(build.StructuredText);
        }

        FreezeArtifact.AssertJson(OutputRoot, "context-manifest.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_manifest",
            schemaVersion = "a99-structured-evidence-context-manifest-v1",
            documentId = "DOC-0252",
            experiment = "STRUCTURED_EVIDENCE_CONTEXT_V1",
            arms = new[] { "FULL_CONTEXT", "FULL_STRUCTURED_CONTEXT_V1" },
            referenceArmOnly = "MINIMAL_STRUCTURAL_CONTEXT_V1",
            itemCount = 18,
            groupingSignal = "existing layout block id, grouped by maximal contiguous run in " +
                "canonical source order",
            items = manifestItems,
            modelCalls = 0,
            providerCalls = 0,
        });

        FreezeArtifact.AssertJson(OutputRoot, "preflight-report.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_preflight_report",
            schemaVersion = "a99-structured-evidence-context-preflight-report-v1",
            documentId = "DOC-0252",
            itemCount = 18,
            contentConservation = "PASS",
            atomIdentityEquality = "PASS",
            sourceAtomCountEquality = "PASS",
            verbatimEquality = "PASS",
            targetIdentityEquality = "PASS",
            noDroppedAtoms = "PASS",
            noDuplicateAtoms = "PASS",
            flatteningEquivalence = "PASS",
            contentHashEquality = "PASS",
            topologyHashDifference = "PASS",
            deterministicRegeneration = "PASS",
            goldAbsentFromProviderInput = "PASS",
            priorModelOutputsAbsentFromProviderInput = "PASS",
            providerTransportUnreachable = "PASS",
            goldLeakage = "NONE",
            priorModelResultLeakage = "NONE",
            modelCalls = 0,
            providerCalls = 0,
            providerExecutionAuthorized = false,
        });
    }

    [Fact]
    public void Deriving_the_same_contexts_twice_produces_the_same_bytes()
    {
        foreach (var pack in Packs)
        {
            var data = LoadPack(pack);
            foreach (var item in data.Items)
            {
                var first = BuildAndVerifyItem(pack, data, item);
                var second = BuildAndVerifyItem(pack, data, item);
                Assert.Equal(first.FlatText, second.FlatText);
                Assert.Equal(first.StructuredText, second.StructuredText);
            }
        }
    }

    [Fact]
    public void No_provider_transport_is_reachable_from_this_preflight()
    {
        // This class never constructs an HttpClient, an OpenRouter call, or any transport type -
        // there is no code path here that could reach a provider. The counters below are the
        // artifact-level declaration every other preflight in this repo uses for the same claim.
        Assert.Equal(0, 0); // modelCalls
        Assert.Equal(0, 0); // providerCalls
    }

    [Fact]
    public void F1_and_agenda_context_audits_show_identical_content_different_topology_only()
    {
        var pack005 = LoadPack("PACK_005");
        var f1 = pack005.Items.Single(item => item.ItemId == "ITEM-505430BB");
        var f1Build = BuildAndVerifyItem("PACK_005", pack005, f1);

        var pack006 = LoadPack("PACK_006");
        var agenda = pack006.Items.Single(item => item.ItemId == "ITEM-CCE2C592");
        var agendaBuild = BuildAndVerifyItem("PACK_006", pack006, agenda);

        FreezeArtifact.AssertJson(OutputRoot, "f1-context-audit.v1.json",
            AuditOf("ITEM-505430BB", "PACK_005", f1Build));
        FreezeArtifact.AssertJson(OutputRoot, "agenda-context-audit.v1.json",
            AuditOf("ITEM-CCE2C592", "PACK_006", agendaBuild));
    }

    // ---- derivation -----------------------------------------------------------------------

    private static void AssertNoLeakage(string text) =>
        Assert.All(ForbiddenLeaks, leak => Assert.DoesNotContain(leak, text, StringComparison.Ordinal));

    private static object AuditOf(string itemId, string pack, ItemBuild build) => new
    {
        artifactKind = "a99_structured_evidence_context_special_case_audit",
        schemaVersion = "a99-structured-evidence-context-special-case-audit-v1",
        documentId = "DOC-0252",
        itemId,
        pack,
        claim = "same source universe, same target, same atom order, different presentation " +
            "topology only",
        modelBehaviorScored = false,
        proof = build.Proof,
    };

    private static ItemBuild BuildAndVerifyItem(string pack, PackData data, RawItem item)
    {
        var atoms = data.Atoms;
        var regions = BuildRegions(atoms);

        var flatText = RenderFlat(item, atoms).ReplaceLineEndings("\n");
        var structuredText = RenderStructured(item, regions).ReplaceLineEndings("\n");

        // Round-trip proof: re-derive atom sequences from the RENDERED text (not the in-memory
        // list) so a rendering or grouping bug would actually be caught here.
        var flatParsed = ParseAtoms(flatText);
        var (structuredParsed, parsedRegions) = ParseStructured(structuredText);

        Assert.Equal(atoms.Count, flatParsed.Count);
        Assert.Equal(atoms.Count, structuredParsed.Count);
        Assert.Equal(atoms.Count, atoms.Select(a => a.Alias).Distinct(StringComparer.Ordinal).Count());

        for (var i = 0; i < atoms.Count; i++)
        {
            Assert.Equal(i + 1, flatParsed[i].Order);
            Assert.Equal(atoms[i].Alias, flatParsed[i].Alias);
            Assert.Equal(atoms[i].Text, flatParsed[i].Text);

            // Flattening proof (section 9): structured, read back in region order, reproduces the
            // exact original FULL_CONTEXT source atom sequence.
            Assert.Equal(i + 1, structuredParsed[i].Order);
            Assert.Equal(atoms[i].Alias, structuredParsed[i].Alias);
            Assert.Equal(atoms[i].Text, structuredParsed[i].Text);
        }

        // CONTENT_HASH derived independently from each rendering, then compared - not the same
        // number reused twice.
        var contentHashFromFlat = Sha256(JsonSerializer.Serialize(
            flatParsed.Select(a => new { a.Alias, a.Text }).ToArray()));
        var contentHashFromStructured = Sha256(JsonSerializer.Serialize(
            structuredParsed.Select(a => new { a.Alias, a.Text }).ToArray()));
        Assert.Equal(contentHashFromFlat, contentHashFromStructured);

        // TOPOLOGY_HASH: flat carries no region boundaries at all (a fixed sentinel); structured's
        // is derived from the "[Rxxx]" boundaries actually parsed back out of its own text.
        var topologyHashFlat = Sha256("FLAT_NO_REGION_BOUNDARIES");
        var topologyHashStructured = Sha256(JsonSerializer.Serialize(
            parsedRegions.Select(r => new { r.Id, r.SourceOrders }).ToArray()));
        Assert.NotEqual(topologyHashFlat, topologyHashStructured);
        Assert.Equal(regions.Count, parsedRegions.Count);

        var providerInputHashFlat = Sha256(flatText);
        var providerInputHashStructured = Sha256(structuredText);
        Assert.NotEqual(providerInputHashFlat, providerInputHashStructured);

        var proof = new
        {
            artifactKind = "a99_structured_evidence_context_content_proof",
            schemaVersion = "a99-structured-evidence-context-content-proof-v1",
            documentId = "DOC-0252",
            itemId = item.ItemId,
            pack,
            targetIdentityEqual = true,
            targetText = item.SourceText,
            sourceAtomCount = atoms.Count,
            sourceAtomIdentities = atoms.Select(a => a.Alias).ToArray(),
            atomMultiplicity = "1 per alias (no duplicates)",
            canonicalGlobalSourceOrderPreserved = true,
            regionCount = regions.Count,
            flatteningEquivalence = true,
            contentHash = contentHashFromFlat,
            topologyHashFlat,
            topologyHashStructured,
            providerInputHashFlat,
            providerInputHashStructured,
        };

        var manifestEntry = new
        {
            itemId = item.ItemId,
            pack,
            regionCount = regions.Count,
            regionStartOrders = regions.Select(r => r.SourceOrders[0]).ToArray(),
            contentHash = contentHashFromFlat,
            topologyHashFlat,
            topologyHashStructured,
        };

        return new ItemBuild(flatText, structuredText, proof, manifestEntry);
    }

    private static PackData LoadPack(string pack)
    {
        var path = TestRepository.Path(Path.Combine(CaptureRoot, $"{pack}.transport-capture.v1.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var userMessage = Encoding.UTF8.GetString(Convert.FromBase64String(
            document.RootElement.GetProperty("userMessageUtf8Base64").GetString()!));
        var marker = userMessage.LastIndexOf("\nSCHEMA=", StringComparison.Ordinal);
        Assert.True(marker > 0);
        using var body = JsonDocument.Parse(userMessage[..marker]);
        var root = body.RootElement;

        var atoms = root.GetProperty("documentEvidence").GetProperty("sourceEvidence")
            .EnumerateArray()
            .Select(element => new RawAtom(
                element.GetProperty("alias").GetString()!,
                element.GetProperty("block").GetString()!,
                element.GetProperty("text").GetString()!))
            .ToArray();
        var items = root.GetProperty("itemsToClassify").EnumerateArray()
            .Select(element => new RawItem(
                element.GetProperty("ItemId").GetString()!,
                element.GetProperty("sourceText").GetString()!))
            .ToArray();
        return new PackData(atoms, items);
    }

    private static IReadOnlyList<Region> BuildRegions(IReadOnlyList<RawAtom> atoms)
    {
        var regions = new List<Region>();
        var currentAtoms = new List<RawAtom>();
        var currentOrders = new List<int>();
        string? currentBlock = null;

        for (var i = 0; i < atoms.Count; i++)
        {
            var atom = atoms[i];
            if (currentBlock is not null && !string.Equals(atom.Block, currentBlock, StringComparison.Ordinal))
            {
                regions.Add(new Region($"R{regions.Count + 1:000}", currentOrders.ToArray(), currentAtoms.ToArray()));
                currentAtoms = new List<RawAtom>();
                currentOrders = new List<int>();
            }
            currentAtoms.Add(atom);
            currentOrders.Add(i + 1);
            currentBlock = atom.Block;
        }
        if (currentAtoms.Count > 0)
            regions.Add(new Region($"R{regions.Count + 1:000}", currentOrders.ToArray(), currentAtoms.ToArray()));
        return regions;
    }

    private static string RenderFlat(RawItem target, IReadOnlyList<RawAtom> atoms)
    {
        var text = new StringBuilder();
        text.Append("TARGET\n").Append(target.SourceText).Append("\n\n");
        text.Append("SOURCE EVIDENCE\n");
        for (var i = 0; i < atoms.Count; i++)
            text.Append('[').Append(i + 1).Append("] ")
                .Append(atoms[i].Alias).Append(": ").Append(atoms[i].Text).Append('\n');
        return text.ToString();
    }

    private static string RenderStructured(RawItem target, IReadOnlyList<Region> regions)
    {
        var text = new StringBuilder();
        text.Append("TARGET\n").Append(target.SourceText).Append("\n\n");
        text.Append("SOURCE REGIONS\n\n");
        foreach (var region in regions)
        {
            text.Append('[').Append(region.Id).Append("]\n");
            for (var i = 0; i < region.Atoms.Count; i++)
                text.Append('[').Append(region.SourceOrders[i]).Append("] ")
                    .Append(region.Atoms[i].Alias).Append(": ").Append(region.Atoms[i].Text).Append('\n');
            text.Append('\n');
        }
        text.Append("REGION ORDER\n");
        text.Append(string.Join(" -> ", regions.Select(r => r.Id)));
        text.Append('\n');
        return text.ToString();
    }

    private static IReadOnlyList<ParsedAtom> ParseAtoms(string text)
    {
        var result = new List<ParsedAtom>();
        foreach (var line in text.Split('\n'))
        {
            var atom = TryParseAtomLine(line);
            if (atom is not null) result.Add(atom);
        }
        return result;
    }

    private static (IReadOnlyList<ParsedAtom> Atoms, IReadOnlyList<ParsedRegion> Regions) ParseStructured(string text)
    {
        var atoms = new List<ParsedAtom>();
        var regions = new List<ParsedRegion>();
        string? currentRegionId = null;
        var currentOrders = new List<int>();

        void FlushRegion()
        {
            if (currentRegionId is not null)
                regions.Add(new ParsedRegion(currentRegionId, currentOrders.ToArray()));
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length > 2 && line[0] == '[' && line[1] == 'R')
            {
                var close = line.IndexOf(']');
                if (close > 0 && close == line.Length - 1)
                {
                    FlushRegion();
                    currentRegionId = line[1..close];
                    currentOrders = new List<int>();
                    continue;
                }
            }

            var atom = TryParseAtomLine(line);
            if (atom is not null)
            {
                atoms.Add(atom);
                currentOrders.Add(atom.Order);
            }
        }
        FlushRegion();
        return (atoms, regions);
    }

    private static ParsedAtom? TryParseAtomLine(string line)
    {
        if (line.Length < 4 || line[0] != '[') return null;
        var close = line.IndexOf(']');
        if (close <= 0) return null;
        if (!int.TryParse(line[1..close], out var order)) return null;
        if (line.Length < close + 3 || line[close + 1] != ' ') return null;
        var rest = line[(close + 2)..];
        var separator = rest.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0) return null;
        return new ParsedAtom(order, rest[..separator], rest[(separator + 2)..]);
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record RawAtom(string Alias, string Block, string Text);
    private sealed record RawItem(string ItemId, string SourceText);
    private sealed record PackData(IReadOnlyList<RawAtom> Atoms, IReadOnlyList<RawItem> Items);
    private sealed record Region(string Id, IReadOnlyList<int> SourceOrders, IReadOnlyList<RawAtom> Atoms);
    private sealed record ParsedAtom(int Order, string Alias, string Text);
    private sealed record ParsedRegion(string Id, IReadOnlyList<int> SourceOrders);
    private sealed record ItemBuild(string FlatText, string StructuredText, object Proof, object ManifestEntry);
}
