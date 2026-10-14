using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// P7_F1_V6_READ_TOOLS_V3 (Issue #9 D1). Three read-only tools over the parser source store, separate from the V2–V5 tools
/// (which are not changed): <c>search_document_text</c> (literal or normalised, granted pages, source order),
/// <c>get_page_text</c> (every line of one page in reading order) and <c>read_line_span</c> (the rest of one line, by character
/// offset, contiguous from what was already read). None labels, ranks or filters by heading vocabulary.
/// Every result is capped at <see cref="MaxResultBytes"/>: the cap is measured on the EXACT serialised body that is returned.
/// Overflow is never silent: fewer items are returned with an explicit nextOffset. A single line that does not fit is cut and
/// its read position is recorded, so the model can continue it with read_line_span. A line whose text was not read to the end is
/// reported as an incomplete evidence line.
/// </summary>
internal sealed class P7F1V6Tools
{
    public const string Version = "P7_F1_V6_READ_TOOLS_V3";
    public const int MaxLimit = 50, MaxQueryChars = 200, MaxResultBytes = 16384, MaxSpanChars = 20000;
    public static readonly string[] Names = ["search_document_text", "get_page_text", "read_line_span"];

    /// <summary>One transcript record per call, errors included. The binder uses it to prove evidence ids.</summary>
    public sealed record Record(string EvidenceId, string RequestId, string Tool, string SourceSha256, string UniverseSha256, string StoreSha256,
        string Status, string? ErrorCode, string ContentSha256, IReadOnlySet<string> ReturnedAliases, string Version);

    /// <summary>Content is the exact body returned to the model. Its SHA-256 is <see cref="Record.ContentSha256"/>.</summary>
    public sealed record Result(string Status, string? ErrorCode, byte[] Content, Record Record);

    /// <summary>
    /// Scopes kept apart: ReturnedToModel (what the model was shown), ReturnedBySearch (matches the model received), ScannedBySearch
    /// (every granted line a search examined), PartialViews (lines ever shown in part), IncompleteEvidence (shown lines whose text
    /// was not read to the end). Each is a snapshot.
    /// </summary>
    public sealed record Ledger(IReadOnlySet<string> ReturnedToModel, IReadOnlySet<int> ReturnedPages, IReadOnlySet<string> ReturnedBySearch,
        IReadOnlySet<string> ScannedBySearch, IReadOnlySet<string> PartialViews, IReadOnlySet<string> IncompleteEvidence);

    readonly string sourceSha256, universeSha256, storeSha256;
    readonly IReadOnlyList<F1QSourceLine> lines;     // reading order
    readonly Dictionary<string, F1QSourceLine> byAlias;
    readonly IReadOnlySet<int> grantedPages;         // the only pages this tool instance may read
    readonly HashSet<string> returnedToModel = new(StringComparer.Ordinal);
    readonly HashSet<int> returnedPages = new();
    readonly HashSet<string> returnedBySearch = new(StringComparer.Ordinal);
    readonly HashSet<string> scannedBySearch = new(StringComparer.Ordinal);
    readonly HashSet<string> partialViews = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> readUpTo = new(StringComparer.Ordinal);   // characters of each line read so far, from the start
    readonly int unplacedLines;

    public P7F1V6Tools(string sourceSha256, string universeSha256, string storeSha256, IReadOnlyList<F1QSourceLine> lines, IReadOnlySet<int>? grantedPages = null)
    {
        this.sourceSha256 = sourceSha256; this.universeSha256 = universeSha256; this.storeSha256 = storeSha256;
        this.lines = lines.OrderBy(l => l.Ordinal).ThenBy(l => l.Alias, StringComparer.Ordinal).ToArray();
        byAlias = this.lines.ToDictionary(l => l.Alias, StringComparer.Ordinal);
        var allPages = this.lines.Where(l => l.Page is not null).Select(l => l.Page!.Value).ToHashSet();
        this.grantedPages = grantedPages is null ? allPages : grantedPages.Where(allPages.Contains).ToHashSet();
        unplacedLines = this.lines.Count(l => l.Page is null);
    }

    /// <summary>Lines the tools cannot place on a page. They are counted and reported, never silently dropped.</summary>
    public int UnplacedLines => unplacedLines;

    public IReadOnlySet<int> Pages => grantedPages;

    /// <summary>Whole-document parser lines in reading order, read-only. Used for the overview observations only.</summary>
    public IReadOnlyList<F1QSourceLine> AllLines => lines;

    /// <summary>Snapshot of every scope at the moment of the call.</summary>
    public Ledger Scope
    {
        get
        {
            var incomplete = returnedToModel.Where(a => readUpTo.GetValueOrDefault(a, 0) < byAlias[a].Text.Length).ToHashSet(StringComparer.Ordinal);
            return new Ledger(new HashSet<string>(returnedToModel, StringComparer.Ordinal), new HashSet<int>(returnedPages),
                new HashSet<string>(returnedBySearch, StringComparer.Ordinal), new HashSet<string>(scannedBySearch, StringComparer.Ordinal),
                new HashSet<string>(partialViews, StringComparer.Ordinal), incomplete);
        }
    }

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
        var header = new JsonObject
        {
            ["tool"] = "search_document_text", ["version"] = Version, ["query"] = query, ["mode"] = mode,
            ["grantedPages"] = grantedPages.Count, ["unplacedLinesExcluded"] = unplacedLines,
            ["sourceStatus"] = unplacedLines == 0 ? "COMPLETE_PLACEMENT" : "PARTIAL_PLACEMENT",
        };
        return Paged(evidenceId, requestId, "search_document_text", header, "matches", matches, offset, limit, search: true);
    }

    // ---------------- get_page_text ----------------

    public Result GetPageText(string evidenceId, string requestId, int page, int offset, int limit)
    {
        if (!lines.Any(l => l.Page == page)) return Error(evidenceId, requestId, "get_page_text", "PAGE_NOT_IN_DOCUMENT");
        if (!grantedPages.Contains(page)) return Error(evidenceId, requestId, "get_page_text", "SCOPE_DENIED");
        if (offset < 0) return Error(evidenceId, requestId, "get_page_text", "OFFSET_INVALID");
        if (limit < 1 || limit > MaxLimit) return Error(evidenceId, requestId, "get_page_text", "LIMIT_INVALID");
        var header = new JsonObject { ["tool"] = "get_page_text", ["version"] = Version, ["page"] = page, ["unplacedLinesExcluded"] = unplacedLines };
        return Paged(evidenceId, requestId, "get_page_text", header, "lines", lines.Where(l => l.Page == page).ToArray(), offset, limit, search: false);
    }

    // ---------------- read_line_span: the rest of one line ----------------

    public Result ReadLineSpan(string evidenceId, string requestId, string alias, int offset, int limit)
    {
        if (!byAlias.TryGetValue(alias, out var line)) return Error(evidenceId, requestId, "read_line_span", "ALIAS_NOT_FOUND");
        if (line.Page is null || !grantedPages.Contains(line.Page.Value)) return Error(evidenceId, requestId, "read_line_span", "SCOPE_DENIED");
        if (offset < 0) return Error(evidenceId, requestId, "read_line_span", "OFFSET_INVALID");
        if (limit < 1 || limit > MaxSpanChars) return Error(evidenceId, requestId, "read_line_span", "LIMIT_INVALID");
        int upTo = readUpTo.GetValueOrDefault(alias, 0);
        if (offset > upTo) return Error(evidenceId, requestId, "read_line_span", "SPAN_GAP");     // reading must be contiguous
        var text = line.Text;
        // The longest span from offset that keeps the exact body within the cap.
        int lo = 0, hi = Math.Min(limit, text.Length - offset);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (SpanBody(line, offset, mid, offset + mid < text.Length).Length <= MaxResultBytes) lo = mid; else hi = mid - 1;
        }
        if (lo == 0 && text.Length > offset) return Error(evidenceId, requestId, "read_line_span", "CAP_TOO_SMALL");
        int end = offset + lo;
        var body = SpanBody(line, offset, lo, end < text.Length);
        readUpTo[alias] = Math.Max(upTo, end);
        returnedToModel.Add(alias);
        if (line.Page is not null) returnedPages.Add(line.Page.Value);
        if (end < text.Length) partialViews.Add(alias);
        var record = new Record(evidenceId, requestId, "read_line_span", sourceSha256, universeSha256, storeSha256, "OK", null,
            SpatialCanonical.Hash(body), new HashSet<string>([alias], StringComparer.Ordinal), Version);
        return new Result("OK", null, body, record);
    }

    byte[] SpanBody(F1QSourceLine line, int offset, int count, bool more)
    {
        var content = new JsonObject
        {
            ["tool"] = "read_line_span", ["version"] = Version, ["sourceAlias"] = line.Alias, ["page"] = line.Page,
            ["offset"] = offset, ["returnedChars"] = count, ["fullChars"] = line.Text.Length,
            ["text"] = line.Text.Substring(offset, count), ["hasMore"] = more, ["nextOffset"] = more ? offset + count : null,
        };
        return Encoding.UTF8.GetBytes(new JsonObject { ["status"] = "OK", ["errorCode"] = null, ["result"] = content }.ToJsonString());
    }

    // ---------------- pagination: the cap is measured on the exact body ----------------

    /// <summary>
    /// Takes items from <paramref name="all"/> starting at <paramref name="offset"/>, up to <paramref name="limit"/>, adding them
    /// while the exact serialised body stays within the cap. If the first item alone does not fit, its text is cut to the longest
    /// prefix whose exact body fits, and that line is reported as a partial view with its read position.
    /// </summary>
    Result Paged(string evidenceId, string requestId, string tool, JsonObject header, string itemsKey, F1QSourceLine[] all, int offset, int limit, bool search)
    {
        var candidates = all.Skip(offset).Take(limit).ToArray();
        var taken = new List<F1QSourceLine>();
        (string Alias, int Chars, int Full)? cut = null;
        foreach (var line in candidates)
        {
            var tentative = taken.Append(line).ToArray();
            if (BodyOf(tool, header, itemsKey, tentative, null, all.Length, offset, limit).Length <= MaxResultBytes) { taken.Add(line); continue; }
            if (taken.Count > 0) break;
            // The first item alone does not fit: keep the longest prefix whose exact body fits, and say so.
            int lo = 0, hi = line.Text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (BodyOf(tool, header, itemsKey, [line], (line.Alias, mid, line.Text.Length), all.Length, offset, limit).Length <= MaxResultBytes) lo = mid; else hi = mid - 1;
            }
            if (lo == 0) return Error(evidenceId, requestId, tool, "CAP_TOO_SMALL");
            cut = (line.Alias, lo, line.Text.Length);
            taken.Add(line);
            break;
        }
        var body = BodyOf(tool, header, itemsKey, taken.ToArray(), cut, all.Length, offset, limit);
        foreach (var l in taken)
        {
            returnedToModel.Add(l.Alias);
            if (l.Page is not null) returnedPages.Add(l.Page.Value);
            if (search) returnedBySearch.Add(l.Alias);
            if (cut is { } c && c.Alias == l.Alias) { partialViews.Add(l.Alias); readUpTo[l.Alias] = Math.Max(readUpTo.GetValueOrDefault(l.Alias, 0), c.Chars); }
            else readUpTo[l.Alias] = l.Text.Length;
        }
        var record = new Record(evidenceId, requestId, tool, sourceSha256, universeSha256, storeSha256, "OK", null,
            SpatialCanonical.Hash(body), taken.Select(l => l.Alias).ToHashSet(StringComparer.Ordinal), Version);
        return new Result("OK", null, body, record);
    }

    /// <summary>The exact body for these items, with the same footer the caller would receive.</summary>
    static byte[] BodyOf(string tool, JsonObject header, string itemsKey, F1QSourceLine[] items, (string Alias, int Chars, int Full)? cut,
        int total, int offset, int limit)
    {
        var arr = new JsonArray();
        foreach (var l in items)
        {
            bool isCut = cut is { } c && c.Alias == l.Alias;
            var text = isCut ? l.Text[..cut!.Value.Chars] : l.Text;
            var o = new JsonObject { ["sourceAlias"] = l.Alias, ["page"] = l.Page, ["ordinal"] = l.Ordinal, ["text"] = text, ["layout"] = Layout(l) };
            if (isCut) { o["textTruncated"] = true; o["textChars"] = l.Text.Length; o["textCharsReturned"] = cut!.Value.Chars; }
            arr.Add(o);
        }
        var content = (JsonObject)header.DeepClone();
        content[itemsKey] = arr;
        content["total"] = total; content["offset"] = offset; content["limit"] = limit; content["returned"] = items.Length;
        bool more = offset + items.Length < total;
        content["hasMore"] = more; content["nextOffset"] = more ? offset + items.Length : null;
        content["partialView"] = cut is { } cc ? new JsonObject { ["sourceAlias"] = cc.Alias, ["fullChars"] = cc.Full, ["returnedChars"] = cc.Chars } : null;
        return Encoding.UTF8.GetBytes(new JsonObject { ["status"] = "OK", ["errorCode"] = null, ["result"] = content }.ToJsonString());
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
