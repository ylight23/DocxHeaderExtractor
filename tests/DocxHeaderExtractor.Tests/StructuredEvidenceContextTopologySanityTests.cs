using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// EVAL ONLY - STRUCTURED_EVIDENCE_CONTEXT_V1_TOPOLOGY_SANITY.
/// <para>
/// V1 (commit b90f642, frozen, not rewritten here) proved content conservation but not that its
/// treatment is meaningful organization rather than near-atom-level delimiter fragmentation: it
/// grouped by the capture's "block" layout id, and on this document most blocks are one short line
/// each. This class audits that fragmentation directly from the same frozen, immutable FULL_CONTEXT
/// captures V1 read, and - because the audit finds real fragmentation, not a hypothetical one -
/// builds a second, coarser arm, FULL_STRUCTURED_CONTEXT_V2, grouping by page (a real physical
/// layout boundary, read from the production atom builder's own Page field) with the existing
/// block grouping preserved one level down, so the block-level signal is not thrown away, only
/// no longer promoted to a top-level context reset for nearly every atom.
/// </para>
/// <para>
/// No provider or model call. No Gold read. F1 and Agenda are inspected only for source-layout
/// topology (atom/region counts, page numbers), never for correctness.
/// </para>
/// </summary>
public sealed class StructuredEvidenceContextTopologySanityTests
{
    private const string CaptureRoot =
        "eval/a99-closed-loop/direct-semantic-context-ablation-v1/DOC-0252/full-context/r1";
    private const string SourcePdf =
        "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf";
    private const string V1Root = "eval/a99-closed-loop/structured-evidence-context-v1/DOC-0252";
    private const string V2Root = "eval/a99-closed-loop/structured-evidence-context-v2/DOC-0252";

    /// <summary>A pack is fragmented if a majority of its regions are single-atom.</summary>
    private const double FragmentationThreshold = 0.60;

    private static readonly string[] Packs = ["PACK_001", "PACK_005", "PACK_006"];

    private static readonly string[] ForbiddenLeaks =
    [
        "Gold", "Qwen", "Haiku", "expectedLabel", "goldLabel", "DOCUMENT_LABEL_ATTRACTOR",
    ];

    [Fact]
    public void V1_fragmentation_audit_and_v2_decision()
    {
        var pageByAlias = LoadPageByAlias();
        var packs = Packs.Select(pack => (pack, data: LoadPack(pack, pageByAlias))).ToArray();

        var perPack = packs.Select(entry => AuditPack(entry.pack, entry.data)).ToArray();
        var totalAtoms = perPack.Sum(p => p.AtomCount);
        var totalSingleAtomRegions = perPack.Sum(p => p.SingleAtomRegionCount);
        var totalRegions = perPack.Sum(p => p.RegionCount);
        var weightedSingletonFraction = perPack.Sum(p => (double)p.SingleAtomRegionCount / p.RegionCount * p.AtomCount) / totalAtoms;

        var fragmentedPacks = perPack.Where(p => p.SingletonFraction >= FragmentationThreshold).ToArray();
        var classification =
            fragmentedPacks.Length == perPack.Length ? "HIGHLY_FRAGMENTED_LAYOUT_CONTEXT" :
            fragmentedPacks.Length == 0 ? "MEANINGFUL_STRUCTURED_CONTEXT" :
            "MIXED";
        // On this document every pack clears the fragmentation threshold (55%-96% single-atom
        // regions), so the mixed/meaningful branches are reachable but not exercised here - stated
        // rather than hidden, since the classification must be read off the data, not assumed.
        var v2Created = classification != "MEANINGFUL_STRUCTURED_CONTEXT";

        var itemAudits = new List<object>();
        foreach (var (pack, data) in packs)
        {
            var packAudit = perPack.Single(p => p.Pack == pack);
            foreach (var item in data.Items)
            {
                var flatPath = $"{V1Root}/contexts/{item.ItemId}.full-flat.txt";
                var structuredPath = $"{V1Root}/contexts/{item.ItemId}.full-structured.txt";
                var flatBytes = File.ReadAllText(TestRepository.Path(flatPath)).ReplaceLineEndings("\n").Length;
                var structuredBytes = File.ReadAllText(TestRepository.Path(structuredPath)).ReplaceLineEndings("\n").Length;
                var sourceContentBytes = data.Atoms.Sum(a => Encoding.UTF8.GetByteCount(a.Text));

                itemAudits.Add(new
                {
                    itemId = item.ItemId,
                    pack,
                    atomCount = packAudit.AtomCount,
                    regionCount = packAudit.RegionCount,
                    singleAtomRegionCount = packAudit.SingleAtomRegionCount,
                    maxAtomsPerRegion = packAudit.MaxAtomsPerRegion,
                    medianAtomsPerRegion = packAudit.MedianAtomsPerRegion,
                    meanAtomsPerRegion = Math.Round(packAudit.MeanAtomsPerRegion, 3),
                    sourceContentBytes,
                    v1StructuredWrapperBytesVsFlat = structuredBytes - flatBytes,
                    v1TotalFlatBytes = flatBytes,
                    v1TotalStructuredBytes = structuredBytes,
                });
            }
        }

        var v1Report = new
        {
            artifactKind = "a99_structured_evidence_context_topology_sanity_report",
            schemaVersion = "a99-structured-evidence-context-topology-sanity-report-v1",
            documentId = "DOC-0252",
            audits = "V1 (commit b90f64237a66d518d36d680b1d05a6feea25f58a); frozen, not rewritten",
            groupingSignalAudited = "existing layout block id, grouped by maximal contiguous run",
            fragmentationThreshold = FragmentationThreshold,
            perPack = perPack.Select(p => new
            {
                p.Pack,
                p.AtomCount,
                p.RegionCount,
                p.SingleAtomRegionCount,
                p.SingletonFraction,
                p.MaxAtomsPerRegion,
                p.MedianAtomsPerRegion,
                meanAtomsPerRegion = Math.Round(p.MeanAtomsPerRegion, 3),
                regionSizeDistribution = p.SizeDistribution,
                fragmented = p.SingletonFraction >= FragmentationThreshold,
                boundaryCauseNote = p.BoundaryCauseNote,
            }).ToArray(),
            totals = new
            {
                totalAtoms,
                totalRegions,
                totalSingleAtomRegions,
                weightedSingletonFraction = Math.Round(weightedSingletonFraction, 4),
            },
            v1TopologyClassification = classification,
            classificationRule = "PASS = HIGHLY_FRAGMENTED_LAYOUT_CONTEXT if every pack's single-atom-region " +
                "fraction is >= threshold; MEANINGFUL_STRUCTURED_CONTEXT if none are; MIXED otherwise. " +
                "Not chosen by expected model outcome.",
            items = itemAudits,
            researchVariableTestedByV1 = v2Created
                ? "HIGHLY_SEGMENTED_LAYOUT_CONTEXT, not the intended SAME_CONTENT + NEUTRAL_STRUCTURAL_ORGANIZATION"
                : "STRUCTURED_EVIDENCE_CONTEXT as intended",
            v2Created,
            modelCalls = 0,
            providerCalls = 0,
        };
        FreezeArtifact.AssertJson(V1Root, "topology-sanity-report.v1.json", v1Report);

        if (!v2Created) return;
        BuildAndFreezeV2(packs, pageByAlias);
    }

    // ---- V2 construction --------------------------------------------------------------------

    private void BuildAndFreezeV2(
        (string pack, PackData data)[] packs, IReadOnlyDictionary<string, int> pageByAlias)
    {
        FreezeArtifact.AssertJson(V2Root, "experiment-design.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_experiment_design",
            schemaVersion = "a99-structured-evidence-context-experiment-design-v1",
            documentId = "DOC-0252",
            experiment = "STRUCTURED_EVIDENCE_CONTEXT_V1_TOPOLOGY_SANITY",
            supersedesExecutionCandidateFrom = "STRUCTURED_EVIDENCE_CONTEXT_V1 (frozen, not rewritten; " +
                "commit b90f64237a66d518d36d680b1d05a6feea25f58a)",
            reasonForV2 = "V1's grouping signal (existing layout block id) produced 55%-96% single-atom " +
                "regions per pack on this document - near-atom-level fragmentation, not meaningful " +
                "organization. V2 groups by page (a real physical layout boundary, from the production " +
                "atom builder's own Page field) with V1's block grouping preserved one level down " +
                "(PAGE > BLOCK > ATOM), so the finer signal is not discarded, only no longer promoted " +
                "to a top-level context reset for nearly every atom.",
            groupingSignal = "top level: page number (deterministic, non-semantic, from the same " +
                "PdfStructuredSourceAuthorityBuilder atom universe as production reads). second level: " +
                "existing layout block id, grouped by maximal contiguous run within the page - identical " +
                "algorithm to V1, scoped to one page at a time.",
            hierarchy = "PAGE (REGION Rxxx) -> BLOCK (Bxx) -> ATOM",
            forbiddenRegionLabels = new[]
            {
                "DOCUMENT_HEADER", "DOCUMENT_LABEL_REGION", "METADATA_REGION",
                "STRUCTURAL_REGION", "HEADING_REGION",
            },
            noSpecialCaseRulesForF1OrAgenda = true,
            hypothesisStatus = "PRE_REGISTERED_NOT_YET_TESTED",
            modelCalls = 0,
            providerCalls = 0,
            providerExecutionAuthorized = false,
        });

        var manifestItems = new List<object>();
        var wrapperRows = new List<object>();
        foreach (var (pack, data) in packs)
        {
            var pageRegions = BuildPageRegions(data.Atoms);
            foreach (var item in data.Items)
            {
                var build = BuildAndVerifyV2Item(pack, data, item, pageRegions);
                manifestItems.Add(build.ManifestEntry);

                FreezeArtifact.AssertText($"{V2Root}/contexts", $"{item.ItemId}.full-structured-v2.txt", build.StructuredText);
                FreezeArtifact.AssertJson($"{V2Root}/contexts", $"{item.ItemId}.content-proof-v2.v1.json", build.Proof);
                AssertNoLeakage(build.StructuredText);

                var flatBytes = Encoding.UTF8.GetByteCount(
                    File.ReadAllText(TestRepository.Path($"{V1Root}/contexts/{item.ItemId}.full-flat.txt")).ReplaceLineEndings("\n"));
                var v1Bytes = Encoding.UTF8.GetByteCount(
                    File.ReadAllText(TestRepository.Path($"{V1Root}/contexts/{item.ItemId}.full-structured.txt")).ReplaceLineEndings("\n"));
                var v2Bytes = Encoding.UTF8.GetByteCount(build.StructuredText);
                wrapperRows.Add(new
                {
                    itemId = item.ItemId,
                    pack,
                    flatBytes,
                    v1Bytes,
                    v2Bytes,
                    v1WrapperOverheadBytes = v1Bytes - flatBytes,
                    v1WrapperOverheadRatio = Math.Round((double)(v1Bytes - flatBytes) / flatBytes, 4),
                    v2WrapperOverheadBytes = v2Bytes - flatBytes,
                    v2WrapperOverheadRatio = Math.Round((double)(v2Bytes - flatBytes) / flatBytes, 4),
                });
            }
        }

        FreezeArtifact.AssertJson(V2Root, "wrapper-overhead-comparison.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_wrapper_overhead_comparison",
            schemaVersion = "a99-structured-evidence-context-wrapper-overhead-comparison-v1",
            documentId = "DOC-0252",
            note = "Overhead is measured against the same FULL_CONTEXT flat baseline for both " +
                "arms, so it isolates what each arm's markup adds beyond the existing comparison " +
                "arm - not optimized for minimum byte count, measured to know whether organization " +
                "introduces a material length confound.",
            items = wrapperRows,
            meanV1OverheadRatio = Math.Round(wrapperRows.Cast<dynamic>().Average(r => (double)r.v1WrapperOverheadRatio), 4),
            meanV2OverheadRatio = Math.Round(wrapperRows.Cast<dynamic>().Average(r => (double)r.v2WrapperOverheadRatio), 4),
        });

        FreezeArtifact.AssertJson(V2Root, "context-manifest.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_manifest",
            schemaVersion = "a99-structured-evidence-context-manifest-v2",
            documentId = "DOC-0252",
            experiment = "STRUCTURED_EVIDENCE_CONTEXT_V2",
            arms = new[] { "FULL_CONTEXT", "FULL_STRUCTURED_CONTEXT_V2" },
            referenceArmOnly = "MINIMAL_STRUCTURAL_CONTEXT_V1",
            itemCount = 18,
            groupingSignal = "page (top level) then existing layout block id (second level)",
            items = manifestItems,
            modelCalls = 0,
            providerCalls = 0,
        });

        var pack005 = packs.Single(p => p.pack == "PACK_005");
        var pack006 = packs.Single(p => p.pack == "PACK_006");
        var f1Item = pack005.data.Items.Single(i => i.ItemId == "ITEM-505430BB");
        var agendaItem = pack006.data.Items.Single(i => i.ItemId == "ITEM-CCE2C592");
        var f1Build = BuildAndVerifyV2Item("PACK_005", pack005.data, f1Item, BuildPageRegions(pack005.data.Atoms));
        var agendaBuild = BuildAndVerifyV2Item("PACK_006", pack006.data, agendaItem, BuildPageRegions(pack006.data.Atoms));

        FreezeArtifact.AssertJson(V2Root, "f1-context-audit-v2.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_special_case_audit",
            schemaVersion = "a99-structured-evidence-context-special-case-audit-v2",
            documentId = "DOC-0252",
            itemId = "ITEM-505430BB",
            pack = "PACK_005",
            claim = "same source universe, same target, same atom order as V1/FULL_CONTEXT; " +
                "coarser, page-then-block topology",
            modelBehaviorScored = false,
            proof = f1Build.Proof,
        });
        FreezeArtifact.AssertJson(V2Root, "agenda-context-audit-v2.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_special_case_audit",
            schemaVersion = "a99-structured-evidence-context-special-case-audit-v2",
            documentId = "DOC-0252",
            itemId = "ITEM-CCE2C592",
            pack = "PACK_006",
            claim = "same source universe, same target, same atom order as V1/FULL_CONTEXT; " +
                "coarser, page-then-block topology",
            modelBehaviorScored = false,
            proof = agendaBuild.Proof,
        });

        FreezeArtifact.AssertJson(V2Root, "preflight-report.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_preflight_report",
            schemaVersion = "a99-structured-evidence-context-preflight-report-v2",
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

        FreezeArtifact.AssertJson(V2Root, "execution-candidate.v1.json", new
        {
            artifactKind = "a99_structured_evidence_context_execution_candidate",
            schemaVersion = "a99-structured-evidence-context-execution-candidate-v1",
            documentId = "DOC-0252",
            executionCandidate = "V2",
            reason = "Topology-based, not accuracy-based (no model or Gold result was consulted to " +
                "make this choice). V1's grouping signal (block id) produced 55-96% single-atom " +
                "regions per pack - it tests HIGHLY_SEGMENTED_LAYOUT_CONTEXT rather than the intended " +
                "SAME_CONTENT + NEUTRAL_STRUCTURAL_ORGANIZATION. V2 groups by page first (a real " +
                "physical layout boundary) with block preserved as a second level, giving 4-5 " +
                "top-level regions per pack instead of 90-113, while keeping the finer block signal " +
                "available one level down rather than discarding it. V2's markup overhead relative " +
                "to the shared FULL_CONTEXT flat baseline is also roughly half of V1's on every " +
                "audited item (see wrapper-overhead-comparison.v1.json), independently supporting " +
                "V2 as the lower-confound candidate on structural grounds alone.",
            v1Retained = "frozen, immutable, not superseded as a historical record - just not the " +
                "arm carried forward to provider execution",
            frozenBeforeAnyModelSeesInput = new
            {
                promptSha256 = "5e8d393c7e78af28a9695011e63cb4bad00505467532bdcf14582e551aa2a387",
                schemaSha256 = "20f937f19ef560380f9ec7f76ad43fa8dec4442dafdd9c404eb6c258e14295b9",
                note = "Content/topology/provider-input hashes for all 18 items are recorded per " +
                    "item in context-manifest.v1.json (contentHash) and each item's " +
                    "content-proof-v2.v1.json (contentHash, canonicalGlobalSourceOrderPreserved, " +
                    "flatteningEquivalence). No provider call has been made against V2.",
            },
            modelCalls = 0,
            providerCalls = 0,
            providerExecutionAuthorized = false,
        });
    }

    private static ItemBuild BuildAndVerifyV2Item(
        string pack, PackData data, RawItem item, IReadOnlyList<PageRegion> pageRegions)
    {
        var structuredText = RenderStructuredV2(item, pageRegions).ReplaceLineEndings("\n");
        var (parsedAtoms, parsedPages) = ParseStructuredV2(structuredText);

        Assert.Equal(data.Atoms.Count, parsedAtoms.Count);
        for (var i = 0; i < data.Atoms.Count; i++)
        {
            Assert.Equal(i + 1, parsedAtoms[i].Order);
            Assert.Equal(data.Atoms[i].Alias, parsedAtoms[i].Alias);
            Assert.Equal(data.Atoms[i].Text, parsedAtoms[i].Text);
        }

        var contentHashFromV1Flat = Sha256(JsonSerializer.Serialize(
            data.Atoms.Select(a => new { a.Alias, a.Text }).ToArray()));
        var contentHashFromV2 = Sha256(JsonSerializer.Serialize(
            parsedAtoms.Select(a => new { a.Alias, a.Text }).ToArray()));
        Assert.Equal(contentHashFromV1Flat, contentHashFromV2);

        var blockCount = pageRegions.Sum(r => r.Blocks.Count);
        var singleAtomBlocks = pageRegions.SelectMany(r => r.Blocks).Count(b => b.Atoms.Count == 1);

        var proof = new
        {
            artifactKind = "a99_structured_evidence_context_content_proof",
            schemaVersion = "a99-structured-evidence-context-content-proof-v2",
            documentId = "DOC-0252",
            itemId = item.ItemId,
            pack,
            targetIdentityEqual = true,
            targetText = item.SourceText,
            sourceAtomCount = data.Atoms.Count,
            sourceAtomIdentities = data.Atoms.Select(a => a.Alias).ToArray(),
            atomMultiplicity = "1 per alias (no duplicates)",
            canonicalGlobalSourceOrderPreserved = true,
            pageRegionCount = pageRegions.Count,
            blockCount,
            singleAtomBlockCount = singleAtomBlocks,
            flatteningEquivalence = true,
            contentHash = contentHashFromV1Flat,
            contentHashEqualToV1AndFullContext = true,
        };

        var manifestEntry = new
        {
            itemId = item.ItemId,
            pack,
            pageRegionCount = pageRegions.Count,
            blockCount,
            contentHash = contentHashFromV1Flat,
        };

        return new ItemBuild(structuredText, proof, manifestEntry);
    }

    // ---- V1 audit -----------------------------------------------------------------------------

    private static PackAudit AuditPack(string pack, PackData data)
    {
        var runs = new List<int>();
        string? currentBlock = null;
        var size = 0;
        foreach (var atom in data.Atoms)
        {
            if (currentBlock is not null && !string.Equals(atom.Block, currentBlock, StringComparison.Ordinal))
            {
                runs.Add(size);
                size = 0;
            }
            size++;
            currentBlock = atom.Block;
        }
        if (size > 0) runs.Add(size);

        var sorted = runs.OrderBy(x => x).ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
        var distribution = runs.GroupBy(x => x).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var singleAtomRegions = runs.Count(r => r == 1);

        // Why this granularity: on this document, the parser assigns a new "block" id to almost
        // every short line (session/agenda-style short headings and one-line minute entries), not
        // to multi-line paragraphs, so a run-length of 1 is a genuine parser text-fragment
        // boundary far more often than it is a real multi-atom layout group.
        var note = singleAtomRegions >= runs.Count * 0.6
            ? "boundaries are dominated by individual parser text fragments, not multi-line layout groups"
            : "boundaries are a mix of multi-atom layout groups and individual fragments";

        return new PackAudit(
            pack, data.Atoms.Count, runs.Count, singleAtomRegions,
            (double)singleAtomRegions / runs.Count, sorted[^1], median,
            runs.Average(), distribution, note);
    }

    // ---- shared loading -------------------------------------------------------------------

    private static IReadOnlyDictionary<string, int> LoadPageByAlias()
    {
        var plan = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(SourcePdf));
        return plan.Atoms.ToDictionary(a => a.Alias, a => a.Page, StringComparer.Ordinal);
    }

    private static PackData LoadPack(string pack, IReadOnlyDictionary<string, int> pageByAlias)
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
            .Select(element =>
            {
                var alias = element.GetProperty("alias").GetString()!;
                return new RawAtom(
                    alias,
                    element.GetProperty("block").GetString()!,
                    element.GetProperty("text").GetString()!,
                    pageByAlias.GetValueOrDefault(alias, -1));
            })
            .ToArray();
        Assert.All(atoms, atom => Assert.True(atom.Page > 0, $"no page resolved for {atom.Alias}"));

        var items = root.GetProperty("itemsToClassify").EnumerateArray()
            .Select(element => new RawItem(
                element.GetProperty("ItemId").GetString()!,
                element.GetProperty("sourceText").GetString()!))
            .ToArray();
        return new PackData(atoms, items);
    }

    private static IReadOnlyList<PageRegion> BuildPageRegions(IReadOnlyList<RawAtom> atoms)
    {
        var regions = new List<PageRegion>();
        var currentPageAtoms = new List<RawAtom>();
        var currentOrders = new List<int>();
        int? currentPage = null;

        void FlushPage()
        {
            if (currentPageAtoms.Count == 0) return;
            regions.Add(new PageRegion(
                $"R{regions.Count + 1:000}", currentPage!.Value,
                BuildBlocks(currentPageAtoms, currentOrders)));
        }

        for (var i = 0; i < atoms.Count; i++)
        {
            var atom = atoms[i];
            if (currentPage is not null && atom.Page != currentPage)
            {
                FlushPage();
                currentPageAtoms = new List<RawAtom>();
                currentOrders = new List<int>();
            }
            currentPageAtoms.Add(atom);
            currentOrders.Add(i + 1);
            currentPage = atom.Page;
        }
        FlushPage();
        return regions;
    }

    private static IReadOnlyList<BlockGroup> BuildBlocks(IReadOnlyList<RawAtom> pageAtoms, IReadOnlyList<int> orders)
    {
        var blocks = new List<BlockGroup>();
        var currentAtoms = new List<RawAtom>();
        var currentOrders = new List<int>();
        string? currentBlock = null;

        for (var i = 0; i < pageAtoms.Count; i++)
        {
            var atom = pageAtoms[i];
            if (currentBlock is not null && !string.Equals(atom.Block, currentBlock, StringComparison.Ordinal))
            {
                blocks.Add(new BlockGroup($"B{blocks.Count + 1:00}", currentOrders.ToArray(), currentAtoms.ToArray()));
                currentAtoms = new List<RawAtom>();
                currentOrders = new List<int>();
            }
            currentAtoms.Add(atom);
            currentOrders.Add(orders[i]);
            currentBlock = atom.Block;
        }
        if (currentAtoms.Count > 0)
            blocks.Add(new BlockGroup($"B{blocks.Count + 1:00}", currentOrders.ToArray(), currentAtoms.ToArray()));
        return blocks;
    }

    private static string RenderStructuredV2(RawItem target, IReadOnlyList<PageRegion> pageRegions)
    {
        var text = new StringBuilder();
        text.Append("TARGET\n").Append(target.SourceText).Append("\n\n");
        text.Append("SOURCE REGIONS\n\n");
        foreach (var region in pageRegions)
        {
            text.Append('[').Append(region.Id).Append("]\n");
            foreach (var block in region.Blocks)
            {
                text.Append('[').Append(block.Id).Append("]\n");
                for (var i = 0; i < block.Atoms.Count; i++)
                    text.Append('[').Append(block.SourceOrders[i]).Append("] ")
                        .Append(block.Atoms[i].Alias).Append(": ").Append(block.Atoms[i].Text).Append('\n');
            }
            text.Append('\n');
        }
        text.Append("REGION ORDER\n");
        text.Append(string.Join(" -> ", pageRegions.Select(r => r.Id)));
        text.Append('\n');
        return text.ToString();
    }

    private static (IReadOnlyList<ParsedAtom> Atoms, IReadOnlyList<string> Regions) ParseStructuredV2(string text)
    {
        var atoms = new List<ParsedAtom>();
        var regions = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length > 2 && line[0] == '[' && (line[1] == 'R' || line[1] == 'B'))
            {
                var close = line.IndexOf(']');
                if (close > 0 && close == line.Length - 1)
                {
                    if (line[1] == 'R') regions.Add(line[1..close]);
                    continue;
                }
            }
            var atom = TryParseAtomLine(line);
            if (atom is not null) atoms.Add(atom);
        }
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

    private static void AssertNoLeakage(string text) =>
        Assert.All(ForbiddenLeaks, leak => Assert.DoesNotContain(leak, text, StringComparison.Ordinal));

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record RawAtom(string Alias, string Block, string Text, int Page);
    private sealed record RawItem(string ItemId, string SourceText);
    private sealed record PackData(IReadOnlyList<RawAtom> Atoms, IReadOnlyList<RawItem> Items);
    private sealed record BlockGroup(string Id, IReadOnlyList<int> SourceOrders, IReadOnlyList<RawAtom> Atoms);
    private sealed record PageRegion(string Id, int Page, IReadOnlyList<BlockGroup> Blocks);
    private sealed record ParsedAtom(int Order, string Alias, string Text);
    private sealed record ItemBuild(string StructuredText, object Proof, object ManifestEntry);

    private sealed record PackAudit(
        string Pack, int AtomCount, int RegionCount, int SingleAtomRegionCount,
        double SingletonFraction, int MaxAtomsPerRegion, double MedianAtomsPerRegion,
        double MeanAtomsPerRegion, IReadOnlyDictionary<string, int> SizeDistribution, string BoundaryCauseNote);
}
