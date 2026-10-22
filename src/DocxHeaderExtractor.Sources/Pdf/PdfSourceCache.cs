using MessagePack;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Models.Inference;
using DocxHeaderExtractor.Sources.Cache;
using DocxHeaderExtractor.Sources.Snapshot;

namespace DocxHeaderExtractor.Sources.Pdf;

/// <summary>
/// The built PDF source, kept between runs. The key is the file's bytes and path, this build of the code, and the schema; a stored build is
/// used only when every part's bytes match the hash written beside it. A build that throws is never stored.
/// </summary>
/// <remarks>
/// The build is stored as five parts, each its own file with its own hash, and a load reads them in parallel. Four parts are MessagePack
/// (the snapshot's core, its evidence rows, the layout blocks, and the per-atom contexts); the source catalog stays JSON, which the catalog's
/// own converter already reads. The rebuilt result is the same as the one that was stored.
/// </remarks>
internal static class PdfSourceCache
{
    public const string Schema = "pdf-source-cache-v3";

    /// <summary>The built source of <paramref name="pdfPath"/>, from <paramref name="cacheRoot"/> when a valid build is stored there.</summary>
    public static PdfSourceBuildResult BuildOrLoad(string pdfPath, string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        var fileSha = CacheStore.FileSha(pdfPath);
        var key = CacheStore.Key(Schema, fileSha, Path.GetFullPath(pdfPath), string.Empty);

        if (LoadParts(cacheRoot, fileSha, key) is { } cached) return cached;

        var built = PdfSourceAdapter.BuildWithDetails(pdfPath);
        StoreParts(cacheRoot, fileSha, key, built);
        return built;
    }

    static PdfSourceBuildResult? LoadParts(string cacheRoot, string fileSha, string key)
    {
        var core = Task.Run(() => CacheStore.TryLoadBinary<PdfCoreCachePart>(PartFile(cacheRoot, fileSha, key, "core"), PartSchema("core"), key));
        var catalog = Task.Run(() => CacheStore.TryLoad<PdfCatalogCachePart>(PartFile(cacheRoot, fileSha, key, "catalog"), PartSchema("catalog"), key));
        var evidence = Task.Run(() => CacheStore.TryLoadBinary<PdfEvidenceCachePart>(PartFile(cacheRoot, fileSha, key, "evidence"), PartSchema("evidence"), key));
        var blocks = Task.Run(() => CacheStore.TryLoadBinary<PdfBlocksCachePart>(PartFile(cacheRoot, fileSha, key, "blocks"), PartSchema("blocks"), key));
        var contexts = Task.Run(() => CacheStore.TryLoadBinary<PdfContextsCachePart>(PartFile(cacheRoot, fileSha, key, "contexts"), PartSchema("contexts"), key));
        Task.WaitAll(core, catalog, evidence, blocks, contexts);

        if (core.Result is not { } c || catalog.Result is not { } k || evidence.Result is not { } e || blocks.Result is not { } b || contexts.Result is not { } x)
            return null;

        // The neighbour lists are the contexts' own lists, so they are not stored twice: each row takes them from its context.
        var evidenceRows = e.Rows.Select(r =>
        {
            var context = x.Contexts[r.SourceId];
            return new CanonicalSemanticSourceEvidence(
                r.SourceAlias, r.SourceId, r.SourceOrdinal, r.ExactSourceText, r.StructuralScope, r.ContainerFacts,
                r.StyleFacts, r.NumberingFacts, r.RunFormattingFacts, r.ObservedEvidence, context.PreviousBlocks, context.NextBlocks)
            {
                LocationFacts = r.LocationFacts,
            };
        }).ToList();

        var snapshot = new DocumentSourceSnapshot(
            c.Atoms,
            c.Occurrences,
            evidenceRows,
            c.SourceAliasUniverseHash,
            c.ModelVisibleEvidenceHash,
            c.SourceSha256,
            c.OccurrenceContexts,
            k.Catalog,
            c.Aliases.Select(a => new SemanticSourceAlias(a.Alias, a.SourceId, a.SourceOrdinal, a.Text, a.SourceSpan)).ToList(),
            c.OrdinalBySourceId)
        {
            SourceKind = c.SourceKind,
            DocumentId = c.DocumentId,
        };
        return new PdfSourceBuildResult(snapshot, new PdfSourceDetails(b.Blocks, x.Contexts, c.LayoutBlockByAtom, c.ParserLineCount));
    }

    static void StoreParts(string cacheRoot, string fileSha, string key, PdfSourceBuildResult built)
    {
        var s = built.Snapshot;
        var d = built.Details;
        // The PDF route never sets an alias's anchor. A build that would lose one is not stored, rather than stored without it.
        if (s.Aliases.Any(a => a.SourceAnchor is not null)) throw new InvalidOperationException("pdf-source-cache-alias-anchor-not-cached");
        var aliases = s.Aliases.Select(a => new PdfAliasRow(a.Alias, a.SourceId, a.SourceOrdinal, a.Text, a.SourceSpan)).ToList();
        CacheStore.StoreBinary(PartFile(cacheRoot, fileSha, key, "core"), PartSchema("core"), key, new PdfCoreCachePart(
            s.Atoms, s.Occurrences, s.OccurrenceContexts, aliases, s.OrdinalBySourceId,
            s.SourceAliasUniverseHash, s.ModelVisibleEvidenceHash, s.SourceSha256, s.SourceKind, s.DocumentId,
            d.LayoutBlockByAtom, d.ParserLineCount));
        CacheStore.Store(PartFile(cacheRoot, fileSha, key, "catalog"), PartSchema("catalog"), key, new PdfCatalogCachePart(s.Catalog));
        var rows = s.Evidence.Select(e => new PdfEvidenceRow(
            e.SourceAlias, e.SourceId, e.SourceOrdinal, e.ExactSourceText, e.StructuralScope, e.ContainerFacts,
            e.StyleFacts, e.NumberingFacts, e.RunFormattingFacts, e.ObservedEvidence, e.LocationFacts)).ToList();
        CacheStore.StoreBinary(PartFile(cacheRoot, fileSha, key, "evidence"), PartSchema("evidence"), key, new PdfEvidenceCachePart(rows));
        CacheStore.StoreBinary(PartFile(cacheRoot, fileSha, key, "blocks"), PartSchema("blocks"), key, new PdfBlocksCachePart(d.Blocks));
        CacheStore.StoreBinary(PartFile(cacheRoot, fileSha, key, "contexts"), PartSchema("contexts"), key, new PdfContextsCachePart(d.Contexts));
    }

    /// <summary>One part's file: <c>&lt;root&gt;/&lt;file sha16&gt;/pdf-&lt;key&gt;/&lt;part&gt;.msgpack</c>, or <c>.json</c> for the catalog, which stays JSON.</summary>
    static string PartFile(string cacheRoot, string fileSha, string key, string part) =>
        Path.Combine(cacheRoot, fileSha[..16], "pdf-" + key, part + (part == "catalog" ? ".json" : ".msgpack"));

    static string PartSchema(string part) => Schema + "/" + part;
}

/// <summary>The snapshot's identities, atoms, occurrences and contexts, the aliases, and the parser's line-layout facts.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfCoreCachePart(
    [property: Key(0)] IReadOnlyList<SemanticSourceAtom> Atoms,
    [property: Key(1)] IReadOnlyList<DocumentOccurrence> Occurrences,
    [property: Key(2)] IReadOnlyDictionary<string, OccurrenceContext> OccurrenceContexts,
    [property: Key(3)] IReadOnlyList<PdfAliasRow> Aliases,
    [property: Key(4)] IReadOnlyDictionary<string, int> OrdinalBySourceId,
    [property: Key(5)] string SourceAliasUniverseHash,
    [property: Key(6)] string ModelVisibleEvidenceHash,
    [property: Key(7)] string SourceSha256,
    [property: Key(8)] string SourceKind,
    [property: Key(9)] string DocumentId,
    [property: Key(10)] IReadOnlyDictionary<string, string> LayoutBlockByAtom,
    [property: Key(11)] int ParserLineCount);

/// <summary>An alias without its anchor: the PDF route never sets one, and the store refuses a build that would drop it.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfAliasRow(
    [property: Key(0)] string Alias,
    [property: Key(1)] string SourceId,
    [property: Key(2)] int SourceOrdinal,
    [property: Key(3)] string Text,
    [property: Key(4)] StructuralSpan SourceSpan);

/// <summary>The source catalog, kept as JSON: its own converter validates and orders the units when it is read.</summary>
internal sealed record PdfCatalogCachePart(DocumentSourceCatalog Catalog);

[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfEvidenceCachePart([property: Key(0)] IReadOnlyList<PdfEvidenceRow> Rows);

/// <summary>One evidence row without its neighbour lists, which come from the row's context on load.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfEvidenceRow(
    [property: Key(0)] string SourceAlias,
    [property: Key(1)] string SourceId,
    [property: Key(2)] int SourceOrdinal,
    [property: Key(3)] string ExactSourceText,
    [property: Key(4)] string StructuralScope,
    [property: Key(5)] IReadOnlyList<string> ContainerFacts,
    [property: Key(6)] object StyleFacts,
    [property: Key(7)] object NumberingFacts,
    [property: Key(8)] IReadOnlyList<object> RunFormattingFacts,
    [property: Key(9)] IReadOnlyList<string> ObservedEvidence,
    [property: Key(10)] object? LocationFacts);

[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfBlocksCachePart([property: Key(0)] IReadOnlyList<PdfLayoutBlock> Blocks);

[MessagePackObject(AllowPrivate = true)]
internal sealed record PdfContextsCachePart([property: Key(0)] IReadOnlyDictionary<string, PdfSourceContext> Contexts);
