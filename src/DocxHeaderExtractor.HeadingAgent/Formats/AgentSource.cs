using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// The PDF source of one agent run: the parser's lines in reading order, the identity hashes every tool result carries, the parser layout block
/// of each line, the segments (cores) the document is cut into and the layout facts (V3) of every line. Ported from the qualified V6 universe
/// (P7F1V6Universe, P7F1QEvidenceTools.FromStore, PdfSourceEvidenceStore). Production packing is ordered-page-budget-v1;
/// legacy 48-line packing remains only for historical parity fixtures.
/// </summary>
internal sealed class AgentSource : IAgentSource
{
    public const int Unit = 48, Overlap = 20;

    AgentSource(string sourceSha, string universeSha, string storeSha, IReadOnlyList<AgentSourceLine> lines, IReadOnlyList<string> blocks, RequestPolicy policy, string instruction, bool legacyPacking, bool inlineGrouping, bool sourceBound, bool requirePageLayoutEvidence, bool preloadPageLayoutEvidence, bool includeLayoutBlockEvidence, bool unitSelection,
        IReadOnlyDictionary<int, System.Text.Json.Nodes.JsonObject>? rasterGeometry = null,
        IReadOnlyDictionary<int, byte[]>? visualImages = null, bool compactGeometry = false, bool dynamicCompletionBudget = false,
        IReadOnlyDictionary<int, PdfPageImageCrop>? visualCrops = null)
    {
        VisualCrops = visualCrops ?? new Dictionary<int, PdfPageImageCrop>();
        CompactGeometry = compactGeometry;
        DynamicCompletionBudget = dynamicCompletionBudget;
        ResourcePolicy = policy; policy.Validate();
        if (inlineGrouping && sourceBound) throw new ArgumentException("INDEPENDENT_PDF_CANDIDATES_REQUIRED");
        RasterGeometryEnabled = rasterGeometry is not null;
        RasterGeometry = rasterGeometry ?? new Dictionary<int, System.Text.Json.Nodes.JsonObject>();
        VisualImages = visualImages ?? new Dictionary<int, byte[]>();
        Flavor = requirePageLayoutEvidence || preloadPageLayoutEvidence || includeLayoutBlockEvidence || unitSelection || RasterGeometryEnabled || AdaptiveVisualSelection
            ? new PdfFormat(inlineGrouping, sourceBound, requirePageLayoutEvidence: requirePageLayoutEvidence || preloadPageLayoutEvidence || includeLayoutBlockEvidence || unitSelection,
                preloadPageLayoutEvidence: preloadPageLayoutEvidence, includeLayoutBlockEvidence: includeLayoutBlockEvidence, unitSelection: unitSelection, rasterGeometry: RasterGeometryEnabled,
                adaptiveVisualEvidence: AdaptiveVisualSelection)
            : sourceBound ? PdfFormat.WithSourceBinding : inlineGrouping ? PdfFormat.WithInlineGrouping : PdfFormat.Instance;
        SourceSha256 = sourceSha; SourceAliasUniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Lines = lines; Blocks = blocks;
        Cores = [];
        LayoutFacts = AgentLayoutFacts.Compute(lines, blocks);
        if (legacyPacking)
        {
            var factTools = Flavor.CreateTools(this);
            Cores = CoresOf(lines, blocks).SelectMany(core =>
            {
                var aliases = lines.Skip(core.Start).Take(core.End - core.Start).Select(l => l.Alias).ToArray();
                var facts = AgentRequestFacts.Read(factTools, aliases);
                var sizes = facts.Select(f => Encoding.UTF8.GetByteCount(f!.ToJsonString())).ToArray();
                return DocxAgentSource.CoresOf(sizes).Select(part => (core.Start + part.Start, core.Start + part.End));
            }).ToArray();
            return;
        }
        var measured = new Dictionary<(int, int, int), (int Bytes, string Sha256, RequestResources Resources)>();
        bool Fits(int start, int end, int segment)
        {
            if (policy.SelectionOutputDemand(end - start) > policy.MaxCompletionTokens) return false;
            var key = (start, end, segment);
            if (!measured.TryGetValue(key, out var measurement))
            {
                var request = unitSelection
                    ? PdfUnitSelectionTask.PrepareFirstRequest(this, segment, instruction, (start, end))
                    : AgentUnit.PrepareFirstRequest(this, segment, instruction, (start, end));
                measurement = (request.Bytes, request.Sha256, RequestResources.Measure(request, policy));
                measured.Add(key, measurement);
            }
            return policy.Refusal(measurement.Resources, reserveContinuation: true) is null;
        }
        var planned = OrderedScopePlanner.Plan(NaturalPages(), lines.Count, Fits,
            maxPackedSiblings: unitSelection && AdaptiveVisualSelection ? 2 : int.MaxValue);
        Cores = planned.Select(s => (s.Start, s.End)).ToArray();
        Plan = planned.Select((scope, segment) => new ScopeObservation(segment, scope.Start, scope.End, scope.Kind,
            measured[(scope.Start, scope.End, segment)].Bytes, measured[(scope.Start, scope.End, segment)].Sha256)).ToArray();
        PlanId = AgentProtocol.Sha(string.Join("|", SourceSha256, SourceAliasUniverseSha256, EvidenceStoreSha256,
            System.Text.Json.JsonSerializer.Serialize(policy), instruction, string.Join(";", Plan.Select(s => s.RequestSha256))));
    }

    internal sealed record ScopeObservation(int Segment, int Start, int End, string Kind, int FirstRequestBytes, string RequestSha256);
    internal IReadOnlyList<ScopeObservation> Plan { get; } = [];
    internal string? PlanId { get; }
    public RequestPolicy ResourcePolicy { get; }
    internal bool RasterGeometryEnabled { get; }
    internal bool CompactGeometry { get; }
    internal bool DynamicCompletionBudget { get; }
    internal IReadOnlyDictionary<int, System.Text.Json.Nodes.JsonObject> RasterGeometry { get; }
    internal IReadOnlyDictionary<int, byte[]> VisualImages { get; }
    /// <summary>The band each page image shows, when page images are cropped; empty otherwise.</summary>
    internal IReadOnlyDictionary<int, PdfPageImageCrop> VisualCrops { get; }
    internal bool AdaptiveVisualSelection => VisualImages.Count != 0;

    internal IReadOnlyList<System.Text.Json.Nodes.JsonObject> SelectionImages((int Start, int End) scope)
    {
        if (!AdaptiveVisualSelection) return [];
        var pages = Lines.Skip(scope.Start).Take(scope.End - scope.Start).Select(l => l.Page)
            .Where(p => p is not null).Select(p => p!.Value).Distinct().Order().ToArray();
        if (pages.Length == 0 || pages.Any(page => !VisualImages.ContainsKey(page)))
            throw new InvalidOperationException("PDF_VISUAL_SCOPE_PAGE_INVALID");
        return pages.Select(page => new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new System.Text.Json.Nodes.JsonObject
            {
                ["url"] = DataUrl(VisualImages[page]),
                ["detail"] = "high"
            }
        }).ToArray();
    }

    /// <summary>The page image as a data URL, labelled by its real format: PNG (the 1-bit trial) or JPEG.</summary>
    static string DataUrl(byte[] bytes)
    {
        var png = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        return (png ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(bytes);
    }

    IReadOnlyList<NaturalScope> NaturalPages()
    {
        NaturalScope Node(int start, int end, string kind, IReadOnlyList<NaturalScope> children) => new(start, end, kind, children);
        var structuralUnits = Flavor is PdfFormat { UnitSelection: true } ? PdfSelectionUnits.Build(this) : [];
        var pages = new List<NaturalScope>();
        for (var start = 0; start < Lines.Count;)
        {
            var end = start + 1;
            while (end < Lines.Count && Lines[end].Page == Lines[start].Page) end++;
            if (structuralUnits.Count > 0)
            {
                var regions = structuralUnits.Where(u => u.Members[0] >= start && u.Members[^1] < end).Select(u =>
                    Node(u.Members[0], u.Members[^1] + 1, u.Kind,
                        [Node(u.Members[0], u.Members[^1] + 1, "region-members", [])])).ToArray();
                if (regions.SelectMany(r => Enumerable.Range(r.Start, r.End - r.Start)).Count() != end - start)
                    throw new InvalidOperationException("PDF_STRUCTURAL_UNIT_PAGE_PARTITION_INVALID");
                pages.Add(Node(start, end, "page", regions));
                start = end;
                continue;
            }
            var blocks = new List<NaturalScope>();
            for (var first = start; first < end;)
            {
                var last = first + 1;
                while (last < end && Blocks[last] == Blocks[first]) last++;
                var rows = new List<NaturalScope>();
                for (var rowStart = first; rowStart < last;)
                {
                    // Parser aliases Lnnnn:Sx identify segments of the same visual row. Keep the whole row before trying atoms.
                    var rowId = Lines[rowStart].Alias.Split(':')[0];
                    var rowEnd = rowStart + 1;
                    while (rowEnd < last && Lines[rowEnd].Alias.Split(':')[0] == rowId) rowEnd++;
                    rows.Add(Node(rowStart, rowEnd, "visual-row", Enumerable.Range(rowStart, rowEnd - rowStart)
                        .Select(i => Node(i, i + 1, "atom", [])).ToArray()));
                    rowStart = rowEnd;
                }
                blocks.Add(Node(first, last, "layout-block", rows));
                first = last;
            }
            pages.Add(Node(start, end, "page", blocks));
            start = end;
        }
        // The V14 adaptive-visual scheduler may combine complete neighboring pages after measuring
        // the serialized packet. Each page remains a child so units and ownership never cross page
        // boundaries; the planner limits lookahead to two page siblings per candidate request.
        return Flavor is PdfFormat { UnitSelection: true } && AdaptiveVisualSelection
            ? [Node(0, Lines.Count, "document", pages)]
            : pages;
    }

    public string SourceSha256 { get; }
    public string SourceAliasUniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    public IReadOnlyList<AgentSourceLine> Lines { get; }
    /// <summary>The parser layout block of each line, parallel to <see cref="Lines"/>.</summary>
    public IReadOnlyList<string> Blocks { get; }
    /// <summary>The decided range of each segment, [Start, End) into <see cref="Lines"/>, covering every line once.</summary>
    public IReadOnlyList<(int Start, int End)> Cores { get; }
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> LayoutFacts { get; }
    public IAgentFormat Flavor { get; }

    public static string SegmentId(int n) => $"SEG-{n:000}";

    /// <summary>The targets of one segment and the lines shown with them (20 lines of context each side), as line indices.</summary>
    public (string[] Decide, int[] Shown) Scope(int segment)
    {
        return AgentUnit.Scope(this, Cores[segment]);
    }

    /// <summary>Parses the PDF with the production parser. Null with a failure code when the file has no text layer or the parse is not stable.</summary>
    public static AgentSource? Load(string pdfPath, out string failure, RequestPolicy? policy = null, string? instruction = null, bool legacyPacking = false, bool inlineGrouping = false, bool sourceBound = false, bool requirePageLayoutEvidence = false, bool preloadPageLayoutEvidence = false, bool includeLayoutBlockEvidence = false, bool unitSelection = false,
        bool rasterGeometry = false, bool adaptiveVisualSelection = false, CancellationToken cancellationToken = default,
        bool compactGeometry = false, int visualMaxWidth = 1200, int visualQuality = 58, bool dynamicCompletionBudget = false,
        bool visualGrayscale = false, bool visualCropMargins = false, string? visualCacheRoot = null, bool visualBilevel = false)
    {
        var parsed = PdfSourceAdapter.BuildWithDetails(pdfPath);
        var source = parsed.Snapshot;
        if (source.Atoms.Count == 0) { failure = "EMPTY_UNIVERSE"; return null; }
        var (storeSha, rows) = EvidenceStore.Build(source, parsed.Details);
        if (!string.Equals(source.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdfPath))), StringComparison.Ordinal))
        { failure = "SOURCE_CHANGED_DURING_PARSE"; return null; }
        var lines = rows.OrderBy(l => l.Ordinal).ThenBy(l => l.Alias, StringComparer.Ordinal).ToArray();
        if (lines.Select(l => l.Alias).Distinct(StringComparer.Ordinal).Count() != lines.Length) { failure = "DUPLICATE_ALIAS"; return null; }
        var blockByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in source.Evidence) blockByAlias.TryAdd(e.SourceAlias, parsed.Details.LayoutBlockByAtom.GetValueOrDefault(e.SourceId) ?? "NO_BLOCK:" + e.SourceId);
        var blocks = lines.Select(l => blockByAlias.GetValueOrDefault(l.Alias) ?? "NO_BLOCK:" + l.Alias).ToArray();
        failure = "OK";
        var fingerprint = PdfVisualCache.Fingerprint(lines.Select(l => (l.Alias, l.Page, l.Bbox?.GetRawText())));
        var visualOptions = $"{visualMaxWidth}|{visualQuality}|{visualGrayscale}|{visualCropMargins}|{visualBilevel}";
        IReadOnlyDictionary<int, System.Text.Json.Nodes.JsonObject>? geometry = null;
        if (rasterGeometry)
        {
            var occurrences = lines.Where(l => l.Page.HasValue && l.Bbox is not null).Select(l =>
            {
                var box = l.Bbox!.Value;
                return new PdfGeometryOccurrence(l.Alias, l.Page!.Value, box.GetProperty("left").GetDouble(), box.GetProperty("right").GetDouble(),
                    box.GetProperty("bottom").GetDouble(), box.GetProperty("top").GetDouble());
            }).ToArray();
            geometry = PdfVisualCache.Geometry(pdfPath, visualCacheRoot, fingerprint,
                () => PdfGeometryMeasurements.MeasureDocument(pdfPath, occurrences, cancellationToken));
            if (!string.Equals(source.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdfPath))), StringComparison.Ordinal))
            { failure = "SOURCE_CHANGED_DURING_GEOMETRY"; return null; }
        }
        IReadOnlyDictionary<int, byte[]>? visualImages = null;
        IReadOnlyDictionary<int, PdfPageImageCrop>? visualCrops = null;
        if (adaptiveVisualSelection)
        {
            try
            {
                var pages = lines.Where(l => l.Page is not null).Select(l => l.Page!.Value).Distinct().ToArray();
                if (visualCropMargins)
                {
                    var boxes = lines.Where(l => l.Page is not null && l.Bbox is not null).GroupBy(l => l.Page!.Value)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<(double TopPt, double BottomPt)>)g.Select(l =>
                            (l.Bbox!.Value.GetProperty("top").GetDouble(), l.Bbox!.Value.GetProperty("bottom").GetDouble())).ToArray());
                    (visualImages, visualCrops) = PdfVisualCache.Visual(pdfPath, visualCacheRoot, visualOptions, fingerprint,
                        () => PdfVisualSelectionImages.RenderCroppedJpegOverviews(pdfPath, pages, boxes, cancellationToken,
                            visualMaxWidth, visualQuality, visualGrayscale, visualBilevel));
                }
                else
                    (visualImages, visualCrops) = PdfVisualCache.Visual(pdfPath, visualCacheRoot, visualOptions, fingerprint,
                        () => (PdfVisualSelectionImages.RenderJpegOverviews(pdfPath, pages, cancellationToken, visualMaxWidth, visualQuality, visualGrayscale, visualBilevel),
                            new Dictionary<int, PdfPageImageCrop>()));
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            { failure = "VISUAL_EVIDENCE_RENDER_FAILED:" + e.GetType().Name; return null; }
        }
        try
        {
            return new AgentSource(source.SourceSha256, source.SourceAliasUniverseHash, storeSha, lines, blocks,
                policy ?? new RequestPolicy(), instruction ?? PdfAgentProtocol.Instruction, legacyPacking, inlineGrouping, sourceBound, requirePageLayoutEvidence, preloadPageLayoutEvidence, includeLayoutBlockEvidence, unitSelection, geometry, visualImages, compactGeometry, dynamicCompletionBudget, visualCrops);
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("UNSPLITTABLE_REQUIRED_SCOPE:", StringComparison.Ordinal))
        { failure = e.Message; return null; }
    }

    /// <summary>LAYOUT_PAGE_BLOCK_48_V1: whole pages when they fit in 48 lines, else parser layout blocks of the page, else 48-line slices;
    /// consecutive units are packed while the core stays within 48 lines. Only page numbers and parser block ids decide.</summary>
    static IReadOnlyList<(int Start, int End)> CoresOf(IReadOnlyList<AgentSourceLine> lines, IReadOnlyList<string> blocks)
    {
        int PageOf(int i) => lines[i].Page ?? 0;
        var units = new List<(int Start, int End)>();
        for (var s = 0; s < lines.Count;)
        {
            var e = s; while (e < lines.Count && PageOf(e) == PageOf(s)) e++;
            if (e - s <= Unit) { units.Add((s, e)); s = e; continue; }
            for (var b = s; b < e;)
            {
                var be = b; while (be < e && blocks[be] == blocks[b]) be++;
                for (var k = b; k < be; k += Unit) units.Add((k, Math.Min(be, k + Unit)));
                b = be;
            }
            s = e;
        }
        var cores = new List<(int, int)>();
        for (var u = 0; u < units.Count;)
        {
            var start = units[u].Start; var end = units[u].End; u++;
            while (u < units.Count && units[u].End - start <= Unit) { end = units[u].End; u++; }
            cores.Add((start, end));
        }
        var next = 0;
        foreach (var (a, b) in cores)
        {
            if (a != next || b <= a || b - a > Unit) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
            next = b;
        }
        if (next != lines.Count) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
        return cores;
    }
}
