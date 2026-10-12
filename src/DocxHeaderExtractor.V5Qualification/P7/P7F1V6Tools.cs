using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1_V6_READ_TOOLS_V2 (Issue #9 D1). Two read-only tools over the parser source store, separate from the V2–V5 tools
/// (which are not changed). <c>search_document_text</c> finds a literal or normalised string over the granted pages in source
/// order. <c>get_page_text</c> returns every line of one page in reading order. Neither tool labels, ranks or filters by heading
/// vocabulary. Every result is capped at <see cref="MaxResultBytes"/> UTF-8 bytes. Overflow is never silent: fewer items are
/// returned with an explicit nextOffset, and a single line that alone exceeds the cap is cut and flagged as a partial view.
/// </summary>
internal sealed class P7F1V6Tools
{
    public const string Version = "P7_F1_V6_READ_TOOLS_V2";
    public const int MaxLimit = 50, MaxQueryChars = 200, MaxResultBytes = 16384;
    public static readonly string[] Names = ["search_document_text", "get_page_text"];

    /// <summary>One transcript record per call, errors included. The binder uses it to prove evidence ids.</summary>
    public sealed record Record(string EvidenceId, string RequestId, string Tool, string SourceSha256, string UniverseSha256, string StoreSha256,
        string Status, string? ErrorCode, string ContentSha256, IReadOnlySet<string> ReturnedAliases, string Version);

    public sealed record Result(string Status, string? ErrorCode, byte[] Content, Record Record);

    /// <summary>
    /// Four scopes, kept apart: ReturnedToModel (what the model was shown), ReturnedBySearch (matches the model received),
    /// ScannedBySearch (every granted line a search examined, whether or not it was returned), PartialViews (lines shown only in
    /// part because of the byte cap). Each is a snapshot.
    /// </summary>
    public sealed record Ledger(IReadOnlySet<string> ReturnedToModel, IReadOnlySet<int> ReturnedPages, IReadOnlySet<string> ReturnedBySearch,
        IReadOnlySet<string> ScannedBySearch, IReadOnlySet<string> PartialViews);

    readonly string sourceSha256, universeSha256, storeSha256;
    readonly IReadOnlyList<F1QSourceLine> lines;     // reading order
    readonly IReadOnlySet<int> grantedPages;         // the only pages this tool instance may read
    readonly HashSet<string> returnedToModel = new(StringComparer.Ordinal);
    readonly HashSet<int> returnedPages = new();
    readonly HashSet<string> returnedBySearch = new(StringComparer.Ordinal);
    readonly HashSet<string> scannedBySearch = new(StringComparer.Ordinal);
    readonly HashSet<string> partialViews = new(StringComparer.Ordinal);
    readonly int unplacedLines;

    public P7F1V6Tools(string sourceSha256, string universeSha256, string storeSha256, IReadOnlyList<F1QSourceLine> lines, IReadOnlySet<int>? grantedPages = null)
    {
        this.sourceSha256 = sourceSha256; this.universeSha256 = universeSha256; this.storeSha256 = storeSha256;
        this.lines = lines.OrderBy(l => l.Ordinal).ThenBy(l => l.Alias, StringComparer.Ordinal).ToArray();
        var allPages = this.lines.Where(l => l.Page is not null).Select(l => l.Page!.Value).ToHashSet();
        this.grantedPages = grantedPages is null ? allPages : grantedPages.Where(allPages.Contains).ToHashSet();
        unplacedLines = this.lines.Count(l => l.Page is null);
    }

    /// <summary>Lines the tools cannot place on a page. They are counted and reported, never silently dropped.</summary>
    public int UnplacedLines => unplacedLines;

    public IReadOnlySet<int> Pages => grantedPages;

    /// <summary>Whole-document parser lines in reading order, read-only. Used for the overview observations only.</summary>
    public IReadOnlyList<F1QSourceLine> AllLines => lines;

    /// <summary>Snapshot of all four scopes at the moment of the call.</summary>
    public Ledger Scope => new(new HashSet<string>(returnedToModel, StringComparer.Ordinal), new HashSet<int>(returnedPages),
        new HashSet<string>(returnedBySearch, StringComparer.Ordinal), new HashSet<string>(scannedBySearch, StringComparer.Ordinal),
        new HashSet<string>(partialViews, StringComparer.Ordinal));

    // ---------------- search_document_text ----------------

    public Result SearchDocumentText(string evidenceId, string requestId, string query, string mode, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return Error(evidenceId, requestId, "search_document_text", "QUERY_EMPTY");
        if (query.Length > MaxQueryChars) return Error(evidenceId, requestId, "search_document_text", "QUERY_TOO_LONG");
        if (mode is not ("literal" or "normalized")) return Error(evidenceId, requestId, "search_document_text", "MODE_INVALID");
        if (offset < 0) return Error(evidenceId, requestId, "search_document_text", "OFFSET_INVALID");
        if (limit < 1 || limit > MaxLimit) return Error(evidenceId, requestId, "search_document_text", "LIMIT_INVALID");
        var needle = mode == "normalized" ? Normalize(query) : query;
        var granted = lines.Where(l => l.Page is not null && grantedPages.Contains(l.Page.Value)).ToArray();
        var matches = granted.Where(l => (mode == "normalized" ? Normalize(l.Text) : l.Text).Contains(needle, StringComparison.Ordinal)).ToArray();
        foreach (var l in granted) scannedBySearch.Add(l.Alias);
        var page = Paginate(matches, offset, limit, out var partial);
        var header = new JsonObject
        {
            ["tool"] = "search_document_text", ["version"] = Version, ["query"] = query, ["mode"] = mode,
            ["totalMatches"] = matches.Length, ["grantedPages"] = grantedPages.Count, ["unplacedLinesExcluded"] = unplacedLines,
            ["sourceStatus"] = unplacedLines == 0 ? "COMPLETE_PLACEMENT" : "PARTIAL_PLACEMENT",
        };
        return Finish(evidenceId, requestId, "search_document_text", header, "matches", page, matches.Length, offset, limit, partial, search: true);
    }

    // ---------------- get_page_text ----------------

    public Result GetPageText(string evidenceId, string requestId, int page, int offset, int limit)
    {
        if (!lines.Any(l => l.Page == page)) return Error(evidenceId, requestId, "get_page_text", "PAGE_NOT_IN_DOCUMENT");
        if (!grantedPages.Contains(page)) return Error(evidenceId, requestId, "get_page_text", "SCOPE_DENIED");
        if (offset < 0) return Error(evidenceId, requestId, "get_page_text", "OFFSET_INVALID");
        if (limit < 1 || limit > MaxLimit) return Error(evidenceId, requestId, "get_page_text", "LIMIT_INVALID");
        var pageLines = lines.Where(l => l.Page == page).ToArray();
        var slice = Paginate(pageLines, offset, limit, out var partial);
        var header = new JsonObject
        {
            ["tool"] = "get_page_text", ["version"] = Version, ["page"] = page,
            ["unplacedLinesExcluded"] = unplacedLines,
        };
        return Finish(evidenceId, requestId, "get_page_text", header, "lines", slice, pageLines.Length, offset, limit, partial, search: false);
    }

    // ---------------- pagination under the byte cap ----------------

    /// <summary>
    /// Takes lines from <paramref name="all"/> starting at <paramref name="offset"/>, up to <paramref name="limit"/>, adding them
    /// while the serialised result stays within the byte cap. If the first line alone does not fit, its text is cut to the longest
    /// prefix that fits and it is reported as partial. Deterministic for the same input.
    /// </summary>
    static F1QSourceLine[] Paginate(F1QSourceLine[] all, int offset, int limit, out (string? Alias, int FullBytes, int KeptBytes) partial)
    {
        partial = (null, 0, 0);
        var taken = new List<F1QSourceLine>();
        foreach (var line in all.Skip(offset).Take(limit))
        {
            taken.Add(line);
            if (Bytes(taken.ToArray(), null) <= MaxResultBytes) continue;
            taken.RemoveAt(taken.Count - 1);
            if (taken.Count > 0) break;
            // The first line alone exceeds the cap: keep the longest prefix that fits, and say so.
            int lo = 0, hi = line.Text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Bytes([line], (line.Alias, mid)) <= MaxResultBytes) lo = mid; else hi = mid - 1;
            }
            partial = (line.Alias, Encoding.UTF8.GetByteCount(line.Text), Encoding.UTF8.GetByteCount(line.Text[..lo]));
            taken.Add(line);
            break;
        }
        return taken.ToArray();
    }

    static int Bytes(F1QSourceLine[] items, (string Alias, int Chars)? cut) =>
        Encoding.UTF8.GetByteCount(ItemsJson(items, cut).ToJsonString()) + 512;   // headroom for the header and footer

    static JsonArray ItemsJson(F1QSourceLine[] items, (string Alias, int Chars)? cut)
    {
        var arr = new JsonArray();
        foreach (var l in items)
        {
            var text = cut is { } c && c.Alias == l.Alias ? l.Text[..c.Chars] : l.Text;
            var o = new JsonObject { ["sourceAlias"] = l.Alias, ["page"] = l.Page, ["ordinal"] = l.Ordinal, ["text"] = text, ["layout"] = Layout(l) };
            if (cut is { } cc && cc.Alias == l.Alias) { o["textTruncated"] = true; o["textChars"] = l.Text.Length; }
            arr.Add(o);
        }
        return arr;
    }

    // ---------------- result, provenance and ledger ----------------

    Result Finish(string evidenceId, string requestId, string tool, JsonObject header, string itemsKey, F1QSourceLine[] items,
        int total, int offset, int limit, (string? Alias, int FullBytes, int KeptBytes) partial, bool search)
    {
        var content = (JsonObject)header.DeepClone();
        content[itemsKey] = ItemsJson(items, partial.Alias is null ? null : (partial.Alias, partial.KeptBytes > 0 ? CharsFor(items, partial) : 0));
        content["total"] = total; content["offset"] = offset; content["limit"] = limit; content["returned"] = items.Length;
        bool more = offset + items.Length < total;
        content["hasMore"] = more; content["nextOffset"] = more ? offset + items.Length : null;
        content["partialView"] = partial.Alias is null ? null : new JsonObject { ["sourceAlias"] = partial.Alias, ["fullBytes"] = partial.FullBytes, ["returnedBytes"] = partial.KeptBytes };
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["status"] = "OK", ["errorCode"] = null, ["result"] = content.DeepClone() }.ToJsonString());
        var aliases = items.Select(l => l.Alias).ToArray();
        foreach (var l in items)
        {
            returnedToModel.Add(l.Alias);
            if (l.Page is not null) returnedPages.Add(l.Page.Value);
            if (search) returnedBySearch.Add(l.Alias);
        }
        if (partial.Alias is not null) partialViews.Add(partial.Alias);
        var record = new Record(evidenceId, requestId, tool, sourceSha256, universeSha256, storeSha256, "OK", null,
            SpatialCanonical.Hash(body), aliases.ToHashSet(StringComparer.Ordinal), Version);
        return new Result("OK", null, body, record);
    }

    static int CharsFor(F1QSourceLine[] items, (string? Alias, int FullBytes, int KeptBytes) partial)
    {
        var line = items.First(l => l.Alias == partial.Alias);
        int lo = 0, hi = line.Text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Encoding.UTF8.GetByteCount(line.Text[..mid]) <= partial.KeptBytes) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    Result Error(string evidenceId, string requestId, string tool, string code)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["status"] = "ERROR", ["errorCode"] = code, ["tool"] = tool, ["version"] = Version }.ToJsonString());
        var record = new Record(evidenceId, requestId, tool, sourceSha256, universeSha256, storeSha256, "ERROR", code,
            SpatialCanonical.Hash(body), new HashSet<string>(StringComparer.Ordinal), Version);
        return new Result("ERROR", code, body, record);
    }

    static JsonObject? Layout(F1QSourceLine l)
    {
        if (l.Bbox is not { ValueKind: JsonValueKind.Object } b) return null;
        var o = new JsonObject { ["bbox"] = JsonNode.Parse(b.GetRawText()) };
        if (l.Typography is { ValueKind: JsonValueKind.Object } t) o["font"] = JsonNode.Parse(t.GetRawText());
        return o;
    }

    /// <summary>Normalised comparison: Vietnamese diacritics removed, đ folded, case folded, ALL whitespace removed. Parser
    /// output can join or split words, so spacing is not part of the match.</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
        }
        return new string(sb.ToString().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
    }
}
