using System.Security.Cryptography;
using DocxHeaderExtractor.DocumentProcessing.Source.Pdf;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// The source universe of one PDF, rebuilt here from the pinned source with the parser and evidence store the run uses (D4.4). The only way
/// to obtain one is <see cref="Rebuild"/>; a caller cannot supply an alias list or a universe hash of its own. The run's lines and the
/// scorer's universe both come from this object, so the aliases, their order, the universe SHA and the source SHA are the parser's, not a
/// caller's declaration. PDF only: a DOCX universe needs its own parser and is refused.
/// </summary>
internal sealed class P7F1V6Universe
{
    public const string FixedSegmentation = "FIXED_48_LINES_V1";
    public const string LayoutSegmentation = "LAYOUT_PAGE_BLOCK_48_V1";
    public const string LayoutSegmentation96 = "LAYOUT_PAGE_BLOCK_96_V1";

    private P7F1V6Universe(string rel, string sourceSha, string universeSha, string storeSha, IReadOnlyList<P7F1V6Direct.Line> lines, P7F1QEvidenceTools tools,
        IReadOnlyList<string> layoutBlocks, string segmentation = FixedSegmentation)
    {
        Tools = tools;
        DocumentRelativePath = rel; SourceSha256 = sourceSha; UniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Lines = lines;
        Aliases = lines.Select(l => l.Alias).ToArray();
        LayoutBlocks = layoutBlocks;
        Segmentation = segmentation;
        Cores = CoresOf(lines, layoutBlocks, segmentation);
        LayoutFacts = P7F1V6LayoutFacts.Compute(tools.AllLines, layoutBlocks);
    }

    /// <summary>Geometry measurements of every line (D4.12), by alias. Used only by the arms that state them.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> LayoutFacts { get; }

    /// <summary>The parser's layout block of each line, parallel to <see cref="Lines"/>.</summary>
    public IReadOnlyList<string> LayoutBlocks { get; }
    /// <summary>How the document is cut into segments: FIXED_48_LINES_V1 (consecutive 48-line cores, the default) or LAYOUT_PAGE_BLOCK_48_V1
    /// (the F1Q arm-B rule: whole pages when they fit, else parser layout blocks, cores of at most 48 lines). Only page numbers and parser block ids decide.</summary>
    public string Segmentation { get; }
    /// <summary>The decided range of each segment, in reading order: [Start, End) indices into <see cref="Lines"/>, covering every line exactly once.</summary>
    public IReadOnlyList<(int Start, int End)> Cores { get; }

    public P7F1V6Universe WithSegmentation(string policy) =>
        new(DocumentRelativePath, SourceSha256, UniverseSha256, EvidenceStoreSha256, Lines, Tools, LayoutBlocks, policy);

    /// <summary>The most lines one segment decides: 48, or 96 for LAYOUT_PAGE_BLOCK_96_V1 (the F1Q pack bound: whole consecutive pages up to 96 lines).</summary>
    public static int MaxCore(string policy) => policy == LayoutSegmentation96 ? 96 : P7F1V6DirectCanary410.Unit;

    static IReadOnlyList<(int Start, int End)> CoresOf(IReadOnlyList<P7F1V6Direct.Line> lines, IReadOnlyList<string> blocks, string policy)
    {
        const int unit = P7F1V6DirectCanary410.Unit;
        List<(int, int)> cores;
        if (policy == FixedSegmentation)
            cores = Enumerable.Range(0, (lines.Count + unit - 1) / unit).Select(n => (n * unit, Math.Min(lines.Count, (n + 1) * unit))).ToList();
        else if (policy is LayoutSegmentation or LayoutSegmentation96)
            cores = P7F1QLayoutPacking.Chunks<int>(Enumerable.Range(0, lines.Count).ToArray(), i => lines[i].Page, i => blocks[i], MaxCore(policy))
                .Select(c => (c[0], c[^1] + 1)).ToList();
        else throw new ArgumentException("SEGMENTATION_UNKNOWN", nameof(policy));
        var next = 0;
        foreach (var (a, b) in cores)
        {
            if (a != next || b <= a || b - a > MaxCore(policy)) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
            next = b;
        }
        if (next != lines.Count) throw new InvalidOperationException("SEGMENTATION_NOT_A_PARTITION");
        return cores;
    }

    static string[] BlocksOf(DocxHeaderExtractor.DocumentProcessing.Source.Pdf.PdfSourceBuildResult parsed, IReadOnlyList<P7F1V6Direct.Line> lines)
    {
        var byAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in parsed.Snapshot.Evidence) byAlias.TryAdd(e.SourceAlias, P7F1QLayoutPacking.Block(parsed.Details, e));
        return lines.Select(l => byAlias.GetValueOrDefault(l.Alias) ?? "NO_BLOCK:" + l.Alias).ToArray();
    }

    public string DocumentRelativePath { get; }
    public string SourceSha256 { get; }
    public string UniverseSha256 { get; }
    public string EvidenceStoreSha256 { get; }
    public IReadOnlyList<P7F1V6Direct.Line> Lines { get; }
    public IReadOnlyList<string> Aliases { get; }
    /// <summary>The parser evidence (text, page, bbox, typography) the universe was built from; read by the V6 evidence tools.</summary>
    public P7F1QEvidenceTools Tools { get; }

    /// <summary>
    /// A document the owner supplies from outside the public corpus (D4.11j). The source gate (public corpus prefix) is deliberately not used: this
    /// path exists only for a plan that declares the document owner-supplied, pins its file hash, and is authorized by the owner, who also decides on
    /// redaction. The file must hash to the pinned value before and after parsing. redact, if given, is applied to every line text before the lines
    /// reach a request, the evidence tools or the prefetch, so a tool result cannot show what a request does not. The source hash stays the original's.
    /// </summary>
    public static P7F1V6Universe? RebuildOwnerSupplied(string fullPath, string pinnedSourceSha256, Func<string, string>? redact, out string failure)
    {
        if (!fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { failure = "UNIVERSE_REBUILD_PDF_ONLY"; return null; }
        if (!File.Exists(fullPath)) { failure = "OWNER_DOCUMENT_MISSING"; return null; }
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath))) != pinnedSourceSha256) { failure = "OWNER_DOCUMENT_SHA_DIFFERS"; return null; }
        var parsed = PdfSourceAdapter.BuildWithDetails(fullPath);
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        if (store.SourceSha256 != pinnedSourceSha256 || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath))) != pinnedSourceSha256)
        {
            failure = "SOURCE_CHANGED_DURING_PARSE";
            return null;
        }
        if (parsed.Snapshot.Atoms.Count == 0) { failure = "EMPTY_UNIVERSE"; return null; }
        var issued = new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias };
        var tools = P7F1QEvidenceTools.FromStore(store, issued, toolSet: 2);
        if (redact is not null)
            tools = new P7F1QEvidenceTools(store.SourceSha256, store.SourceAliasUniverseSha256, store.StoreSha256,
                tools.AllLines.Select(l => l with { Text = redact(l.Text) }).ToArray(), issued, 2);
        var lines = tools.AllLines
            .Select(l => new P7F1V6Direct.Line(l.Alias, l.Page ?? 0, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToArray();
        if (lines.Select(l => l.Alias).Distinct(StringComparer.Ordinal).Count() != lines.Length) { failure = "DUPLICATE_ALIAS"; return null; }
        failure = "OK";
        return new P7F1V6Universe("owner-supplied:" + Path.GetFileName(fullPath), store.SourceSha256, store.SourceAliasUniverseSha256, store.StoreSha256, lines, tools, BlocksOf(parsed, lines));
    }

    /// <summary>Reads the source through the source gate (prefix, path segments, pinned SHA), parses it, builds the evidence store, and
    /// checks that the store describes the same bytes. Returns null with a failure code on any mismatch.</summary>
    public static P7F1V6Universe? Rebuild(string repoRoot, string relativePath, string pinnedSourceSha256, out string failure)
    {
        if (!relativePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { failure = "UNIVERSE_REBUILD_PDF_ONLY"; return null; }
        var snapshot = P7F1V6SourceGate.Snapshot(repoRoot, relativePath, pinnedSourceSha256);
        if (!snapshot.Allowed) { failure = "SOURCE_GATE:" + snapshot.Code; return null; }

        var path = Path.Combine(repoRoot, relativePath);
        var parsed = PdfSourceAdapter.BuildWithDetails(path);
        var store = PdfSourceEvidenceStore.Build(parsed.Snapshot, parsed.Details);
        // The parser read the file again; it must have read the same bytes the gate pinned.
        if (store.SourceSha256 != pinnedSourceSha256 || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != pinnedSourceSha256)
        {
            failure = "SOURCE_CHANGED_DURING_PARSE";
            return null;
        }
        if (parsed.Snapshot.Atoms.Count == 0) { failure = "EMPTY_UNIVERSE"; return null; }
        var tools = P7F1QEvidenceTools.FromStore(store, new Dictionary<string, string> { ["O1"] = parsed.Snapshot.Atoms[0].Alias }, toolSet: 2);
        var lines = tools.AllLines
            .Select(l => new P7F1V6Direct.Line(l.Alias, l.Page ?? 0, l.Text, P7F1QEvidenceTools.CompactLayout(l.Bbox, l.Typography))).ToArray();
        if (lines.Select(l => l.Alias).Distinct(StringComparer.Ordinal).Count() != lines.Length) { failure = "DUPLICATE_ALIAS"; return null; }
        failure = "OK";
        return new P7F1V6Universe(relativePath, store.SourceSha256, store.SourceAliasUniverseSha256, store.StoreSha256, lines, tools, BlocksOf(parsed, lines));
    }
}
