using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1_V6_READ_TOOLS_V1 (Issue #9 D1). Two read-only tools over the parser source store, separate from the V2–V5 tools
/// (which are not changed). <c>search_document_text</c> finds a literal or normalised string over the granted pages in source
/// order. <c>get_page_text</c> returns every line of one page in reading order. Neither tool labels, ranks or filters by heading
/// vocabulary: the query is chosen by the model and the result is the source, nothing more.
/// Every call writes one transcript record (including errors) with provenance, and every result is paginated with an explicit
/// total, hasMore and nextOffset. Nothing is dropped silently.
/// </summary>
internal sealed class P7F1V6Tools
{
    public const string Version = "P7_F1_V6_READ_TOOLS_V1";
    public const int MaxLimit = 50, MaxQueryChars = 200;
    public static readonly string[] Names = ["search_document_text", "get_page_text"];

    /// <summary>One transcript record per call. The binder uses it to prove evidence ids (see P7F1V6Core.ParseDecisions).</summary>
    public sealed record Record(string EvidenceId, string RequestId, string Tool, string SourceSha256, string UniverseSha256, string StoreSha256,
        string Status, string? ErrorCode, string ContentSha256, IReadOnlySet<string> ReturnedAliases, string Version);

    public sealed record Result(string Status, string? ErrorCode, byte[] Content, Record Record);

    readonly string sourceSha256, universeSha256, storeSha256;
    readonly IReadOnlyList<F1QSourceLine> lines;     // reading order
    readonly IReadOnlySet<int> grantedPages;         // the only pages this tool instance may read
    readonly HashSet<string> observedAliases = new(StringComparer.Ordinal);
    readonly HashSet<int> observedPages = new();
    readonly HashSet<string> searchedAliases = new(StringComparer.Ordinal);
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

    /// <summary>What the model was shown (observedScope) and what it searched (searchedAliases). The evaluated scope is set by
    /// the decisions and reported separately by the caller.</summary>
    /// <summary>Snapshots, taken at the call: an audit record must not change after the fact.</summary>
    public (IReadOnlySet<string> ObservedAliases, IReadOnlySet<int> ObservedPages, IReadOnlySet<string> SearchedAliases) ScopeLedger =>
        (new HashSet<string>(observedAliases, StringComparer.Ordinal), new HashSet<int>(observedPages), new HashSet<string>(searchedAliases, StringComparer.Ordinal));

    public IReadOnlySet<int> Pages => grantedPages;

    // ---------------- search_document_text ----------------

    public Result SearchDocumentText(string evidenceId, string requestId, string query, string mode, int offset, int limit)
    {
        var (err, content, aliases) = SearchCore(query, mode, offset, limit);
        return Finish(evidenceId, requestId, "search_document_text", err, content, aliases);
    }

    (string? Error, JsonObject Content, string[] Aliases) SearchCore(string query, string mode, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(query)) return Error("QUERY_EMPTY");
        if (query.Length > MaxQueryChars) return Error("QUERY_TOO_LONG");
        if (mode is not ("literal" or "normalized")) return Error("MODE_INVALID");
        if (offset < 0) return Error("OFFSET_INVALID");
        if (limit < 1 || limit > MaxLimit) return Error("LIMIT_INVALID");
        var needle = mode == "normalized" ? Normalize(query) : query;
        var matches = new List<F1QSourceLine>();
        foreach (var l in lines)
        {
            if (l.Page is null || !grantedPages.Contains(l.Page.Value)) continue;
            var hay = mode == "normalized" ? Normalize(l.Text) : l.Text;
            if (hay.Contains(needle, StringComparison.Ordinal)) matches.Add(l);
        }
        var page = matches.Skip(offset).Take(limit).ToArray();
        var items = new JsonArray(page.Select(l => (JsonNode)new JsonObject { ["sourceAlias"] = l.Alias, ["page"] = l.Page, ["ordinal"] = l.Ordinal,
            ["text"] = l.Text, ["layout"] = Layout(l) }).ToArray());
        var content = new JsonObject
        {
            ["tool"] = "search_document_text", ["version"] = Version, ["query"] = query, ["mode"] = mode,
            ["matches"] = items, ["totalMatches"] = matches.Count, ["offset"] = offset, ["limit"] = limit, ["returned"] = page.Length,
            ["hasMore"] = offset + page.Length < matches.Count, ["nextOffset"] = offset + page.Length < matches.Count ? offset + page.Length : null,
            ["grantedPages"] = grantedPages.Count, ["unplacedLinesExcluded"] = unplacedLines,
            ["sourceStatus"] = unplacedLines == 0 ? "COMPLETE_PLACEMENT" : "PARTIAL_PLACEMENT",
        };
        return (null, content, page.Select(l => l.Alias).ToArray());
    }

    // ---------------- get_page_text ----------------

    public Result GetPageText(string evidenceId, string requestId, int page, int offset, int limit)
    {
        var (err, content, aliases) = PageCore(page, offset, limit);
        return Finish(evidenceId, requestId, "get_page_text", err, content, aliases);
    }

    (string? Error, JsonObject Content, string[] Aliases) PageCore(int page, int offset, int limit)
    {
        if (!lines.Any(l => l.Page == page)) return Error("PAGE_NOT_IN_DOCUMENT");
        if (!grantedPages.Contains(page)) return Error("SCOPE_DENIED");
        if (offset < 0) return Error("OFFSET_INVALID");
        if (limit < 1 || limit > MaxLimit) return Error("LIMIT_INVALID");
        var pageLines = lines.Where(l => l.Page == page).ToArray();
        var slice = pageLines.Skip(offset).Take(limit).ToArray();
        var items = new JsonArray(slice.Select(l => (JsonNode)new JsonObject { ["sourceAlias"] = l.Alias, ["page"] = l.Page, ["ordinal"] = l.Ordinal,
            ["text"] = l.Text, ["layout"] = Layout(l) }).ToArray());
        var content = new JsonObject
        {
            ["tool"] = "get_page_text", ["version"] = Version, ["page"] = page, ["lines"] = items, ["totalLines"] = pageLines.Length,
            ["offset"] = offset, ["limit"] = limit, ["returned"] = slice.Length,
            ["hasMore"] = offset + slice.Length < pageLines.Length, ["nextOffset"] = offset + slice.Length < pageLines.Length ? offset + slice.Length : null,
            ["unplacedLinesExcluded"] = unplacedLines,
        };
        return (null, content, slice.Select(l => l.Alias).ToArray());
    }

    // ---------------- shared ----------------

    Result Finish(string evidenceId, string requestId, string tool, string? error, JsonObject core, string[] aliases)
    {
        var content = error is null ? core : new JsonObject { ["tool"] = tool, ["version"] = Version };
        var status = error is null ? "OK" : "ERROR";
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["status"] = status, ["errorCode"] = error, ["result"] = content.DeepClone() }.ToJsonString());
        var contentSha = SpatialCanonical.Hash(body);
        if (error is null)
        {
            foreach (var a in aliases) observedAliases.Add(a);
            foreach (var l in lines.Where(l => aliases.Contains(l.Alias))) if (l.Page is not null) observedPages.Add(l.Page.Value);
            if (tool == "search_document_text") foreach (var a in aliases) searchedAliases.Add(a);
        }
        var record = new Record(evidenceId, requestId, tool, sourceSha256, universeSha256, storeSha256, status, error, contentSha,
            error is null ? aliases.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal), Version);
        return new Result(status, error, body, record);
    }

    static (string?, JsonObject, string[]) Error(string code) => (code, new JsonObject(), []);

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
