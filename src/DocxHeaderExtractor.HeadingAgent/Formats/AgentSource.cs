using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Sources.Snapshot;
using DocxHeaderExtractor.Sources.Pdf;
using DocxHeaderExtractor.HeadingAgent.Protocol;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// The PDF source of one agent run: the parser's lines in reading order, the identity hashes every tool result carries, the parser layout block
/// of each line, the segments (cores) the document is cut into and the layout facts (V3) of every line. Ported from the qualified V6 universe
/// (P7F1V6Universe, P7F1QEvidenceTools.FromStore, PdfSourceEvidenceStore, P7F1QLayoutPacking, layout segmentation LAYOUT_PAGE_BLOCK_48_V1); the
/// parity tests hold the two to the same bytes.
/// </summary>
internal sealed class AgentSource : IAgentSource
{
    public const int Unit = 48, Overlap = 20;

    AgentSource(string sourceSha, string universeSha, string storeSha, IReadOnlyList<AgentSourceLine> lines, IReadOnlyList<string> blocks)
    {
        SourceSha256 = sourceSha; SourceAliasUniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Lines = lines; Blocks = blocks;
        Cores = CoresOf(lines, blocks);
        LayoutFacts = AgentLayoutFacts.Compute(lines, blocks);
        // Preserve layout boundaries, splitting only oversized cores by the bytes of the actual tool facts.
        // Like DOCX/scan, leave room in the request for text, context, prompts and optional tool results.
        var factTools = Flavor.CreateTools(this);
        Cores = Cores.SelectMany(core =>
        {
            var aliases = lines.Skip(core.Start).Take(core.End - core.Start).Select(l => l.Alias).ToArray();
            var facts = AgentRequestFacts.Read(factTools, aliases);
            var sizes = facts.Select(f => Encoding.UTF8.GetByteCount(f!.ToJsonString())).ToArray();
            return DocxAgentSource.CoresOf(sizes).Select(part => (core.Start + part.Start, core.Start + part.End));
        }).ToArray();
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
    public IAgentFormat Flavor => PdfFormat.Instance;

    public static string SegmentId(int n) => $"SEG-{n:000}";

    /// <summary>The targets of one segment and the lines shown with them (20 lines of context each side), as line indices.</summary>
    public (string[] Decide, int[] Shown) Scope(int segment)
    {
        var (core, end) = Cores[segment];
        var from = Math.Max(0, core - Overlap);
        return (Enumerable.Range(core, end - core).Select(i => Lines[i].Alias).ToArray(),
            Enumerable.Range(from, Math.Min(Lines.Count, end + Overlap) - from).ToArray());
    }

    /// <summary>Parses the PDF with the production parser. Null with a failure code when the file has no text layer or the parse is not stable.</summary>
    public static AgentSource? Load(string pdfPath, out string failure)
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
        return new AgentSource(source.SourceSha256, source.SourceAliasUniverseHash, storeSha, lines, blocks);
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
