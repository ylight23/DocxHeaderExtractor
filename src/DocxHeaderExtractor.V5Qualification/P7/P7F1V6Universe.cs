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
    private P7F1V6Universe(string rel, string sourceSha, string universeSha, string storeSha, IReadOnlyList<P7F1V6Direct.Line> lines, P7F1QEvidenceTools tools)
    {
        Tools = tools;
        DocumentRelativePath = rel; SourceSha256 = sourceSha; UniverseSha256 = universeSha; EvidenceStoreSha256 = storeSha; Lines = lines;
        Aliases = lines.Select(l => l.Alias).ToArray();
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
        return new P7F1V6Universe("owner-supplied:" + Path.GetFileName(fullPath), store.SourceSha256, store.SourceAliasUniverseSha256, store.StoreSha256, lines, tools);
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
        return new P7F1V6Universe(relativePath, store.SourceSha256, store.SourceAliasUniverseSha256, store.StoreSha256, lines, tools);
    }
}
