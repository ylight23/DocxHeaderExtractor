using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1_DIRECT_HEADING_V1 (Issue #7, qualification only). Qwen is asked one direct question per request: which issued
/// occurrences contribute to a genuine document heading (title, subtitle or substantive heading) at their own location.
/// The answer is sparse: selected heading members plus explicitly unresolved occurrences. No E/R/O, kinds, scopes,
/// hierarchy, grouping or extents. Independent of P7F1QProtocolV2 (frozen); reuses its tools and the runner.
/// </summary>
internal static class P7F1DirectProtocol
{
    public const string Version = "P7_F1_DIRECT_HEADING_V1";
    public const int MaxJustificationChars = 300, MaxEvidenceRefs = 8, MaxMissingEvidence = 6;
    static readonly string[] RootKeys = ["protocolVersion", "requestId", "issuedSetSha256", "assessment", "headings", "unresolved"];
    static readonly string[] HeadingKeys = ["occurrence", "sourceAlias", "evidenceRefs", "justification"];
    static readonly string[] UnresolvedKeys = ["occurrence", "sourceAlias", "missingEvidence"];
    static readonly Regex ToolEvidenceId = new(@"^E\d+\.\d+$", RegexOptions.CultureInvariant);

    private const string Question = """
        You are an independent document analyst. The user wants the document's headings: the title identifying the document, a genuine subtitle, and the headings that introduce substantive regions of the document.
        Each issued occurrence is a parser-grounded piece of text with a sourceAlias. It is not necessarily a whole line, heading or sentence; one heading may span several occurrences.
        For every issued occurrence, decide whether it contributes wording to a genuine document heading operating at ITS OWN location. Select each contributing occurrence separately. Do not group occurrences, decide heading boundaries, levels or hierarchy.
        Not every visually prominent, bold, numbered or organising text is a document heading. Before selecting an occurrence, examine the competing explanations that the evidence leaves open:
        - a label, group label, column header, total or value inside a table or financial statement;
        - a table, chart or figure caption or title, or a chart panel label;
        - a list item, definition term, numbered legal provision or clause whose line continues as ordinary provision text, a theorem/definition lead-in;
        - a table-of-contents, index, list-of-tables or cross-reference entry that points to something elsewhere (navigation, not an operative heading here);
        - a form field, fill-in instruction, subject line, signature or administrative block;
        - front matter such as issuer or organisation name, letterhead, national motto, document number, date, reporting period or place;
        - a running header or footer repeated on other pages, or a page number.
        A genuine heading can still appear inside a table or form; select it when the evidence shows it introduces a region of the document rather than labelling local data. When the evidence cannot decide, report the occurrence as unresolved instead of guessing either way.
        """;
    private const string Evidence = """
        EVIDENCE. Each issued occurrence and context line carries parser layout (bbox=[left,right,bottom,top] in PDF points, bottom-left origin; font size, bold and italic ratios) and a sourceEnrichment record whose keys are defined in the request's enrichmentLegend. sourceEnrichment holds observations derived from parser geometry and text only: same-baseline row peers and how many are numeric, nearby rows with two or more numeric cells, the nearest caption-pattern line (a candidate, never asserted as owner), the previous page's last rows (continuity never asserted), how many pages carry the same normalised text, trailing page numbers and dot leaders, the same text elsewhere, vertical position and layout block. Relations carry a status: SUPPORTED_BY_LAYOUT, SUPPORTED_BY_PARSER, UNRESOLVED or NOT_AVAILABLE. These are observations, not labels; weigh them, do not obey them. pageStats give each page's text extent and median font size. No page images are supplied.
        Read-only tools over the same parser evidence are available (get_occurrence_context, compare_occurrences, get_page_geometry, get_source_span, get_repeated_occurrences); use them when the supplied evidence leaves competing explanations open. At most 3 tool-calling rounds, then you must answer. Tool results have evidenceIds such as E1.2 that you may cite.
        """;
    private const string Contract = """
        OUTPUT. Return exactly one JSON object and nothing else:
        {"protocolVersion":"P7_F1_DIRECT_HEADING_V1","requestId":"<requestId>","issuedSetSha256":"<issuedSetSha256>","assessment":"COMPLETE","headings":[{"occurrence":"O2","sourceAlias":"L0042:S0","evidenceRefs":["L0042:S0"],"justification":"..."}],"unresolved":[{"occurrence":"O9","sourceAlias":"L0051:S0","missingEvidence":["cross-page context"]}]}
        - requestId and issuedSetSha256: copy the values given in the request exactly.
        - assessment: COMPLETE when every issued occurrence has been considered. Issued occurrences you do not list in headings or unresolved are those you judge NOT to contribute to a document heading.
        - headings: one entry per selected issued occurrence (may be empty). occurrence and sourceAlias exactly as issued. evidenceRefs: 1 to 8 strings, each a sourceAlias from the request or a tool evidenceId; at least one must be this occurrence's own sourceAlias or a tool evidenceId whose result includes it. justification: at most 300 characters, a brief source-checkable reason why this occurrence contributes to a document heading here.
        - unresolved: issued occurrences whose heading membership the evidence cannot decide; missingEvidence lists 1 to 6 short kinds of evidence that would decide it.
        - An occurrence may appear at most once overall: never in both headings and unresolved. Use only issued ids; context-only lines are never subjects.
        The example values are schema examples only.
        """;

    public static string SystemPrompt() => string.Join("\n\n", new[] { Question, Evidence, Contract }
        .Select(b => b.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n')));

    public sealed record Subject(string Occurrence, string SourceAlias, int Page, string Text);

    /// <summary>Signed scope: request id, document, and every issued (occurrence, alias, page, text) in order.</summary>
    public static string IssuedSetSha256(string requestId, string documentId, IReadOnlyList<Subject> issued) =>
        SpatialCanonical.Hash(SpatialCanonical.Bytes(new { version = Version, requestId, documentId,
            issued = issued.Select(i => new { i.Occurrence, i.SourceAlias, i.Page, i.Text }) }));

    public static string UserMessage(string requestId, string issuedSetSha256, IReadOnlyList<Subject> issued,
        IReadOnlyList<(string Alias, int Page, string Text)> context, P7F1QEvidenceTools tools, P7F1SourceEnrichment enrichment)
    {
        var pages = issued.Select(i => i.Page).Concat(context.Select(c => c.Page)).Distinct().Order().ToArray();
        return JsonSerializer.Serialize(new
        {
            protocolVersion = Version, requestId, issuedSetSha256,
            layoutBasis = P7F1QEvidenceTools.LayoutBasis, enrichmentVersion = P7F1SourceEnrichment.Version,
            enrichmentLegend = P7F1SourceEnrichment.Legend.Replace("\r\n", "\n", StringComparison.Ordinal),
            pageStats = pages.Select(tools.PageStats).ToArray(),
            issuedOccurrences = issued.Select(i => new { id = i.Occurrence, sourceAlias = i.SourceAlias, page = i.Page, text = i.Text,
                layout = tools.Layout(i.SourceAlias), sourceEnrichment = enrichment.Of(i.SourceAlias) }).ToArray(),
            contextOnlyEvidence = context.OrderBy(c => tools.LineOf(c.Alias).Ordinal).Select(c => new { sourceAlias = c.Alias, page = c.Page, text = c.Text,
                layout = tools.Layout(c.Alias), sourceEnrichment = enrichment.Compact(c.Alias) }).ToArray(),
        }, P7F1QEvidenceTools.WireJson);
    }

    /// <summary>
    /// Strict runner-side contract check. Rows: one per listed heading (Assessment HEADING_MEMBER) or unresolved occurrence
    /// (Assessment UNRESOLVED); Function is always null (no E/R/O). Evidence refs that are tool evidenceIds are checked for
    /// form here and for coverage by the binder against the raw tool transcript.
    /// </summary>
    public static F1QValidation Validate(string response, string requestId, string issuedSetSha256,
        IReadOnlyList<F1QIssuedOccurrence> issued, IReadOnlySet<string> citable)
    {
        var rows = new List<F1QRowResult>(); var unresolvedRefs = new SortedSet<string>(StringComparer.Ordinal);
        F1QValidation Fail(string code) => new(false, code, rows, unresolvedRefs.ToArray());
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); } catch (JsonException) { return Fail("RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("ROOT_NOT_OBJECT");
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != RootKeys.Length || RootKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("ROOT_KEYS_INVALID");
            if (Str(root, "protocolVersion") != Version) return Fail("PROTOCOL_VERSION_INVALID");
            if (Str(root, "requestId") != requestId) return Fail("REQUEST_ID_MISMATCH");
            if (Str(root, "issuedSetSha256") != issuedSetSha256) return Fail("ISSUED_SET_SHA_MISMATCH");
            var assessment = Str(root, "assessment");
            if (assessment is not ("COMPLETE" or "INCOMPLETE")) return Fail("ASSESSMENT_INVALID");
            if (root.GetProperty("headings").ValueKind != JsonValueKind.Array || root.GetProperty("unresolved").ValueKind != JsonValueKind.Array) return Fail("LIST_NOT_ARRAY");
            var aliasById = issued.ToDictionary(i => i.Occurrence, i => i.SourceAlias, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? first = null;
            void Add(F1QRowResult r) { if (r.Valid && !seen.Add(r.Occurrence)) r = r with { Valid = false, FailureCode = "OCCURRENCE_LISTED_TWICE" }; rows.Add(r); first ??= r.Valid ? null : r.FailureCode; }
            foreach (var h in root.GetProperty("headings").EnumerateArray()) Add(Heading(h, aliasById, citable, unresolvedRefs));
            foreach (var u in root.GetProperty("unresolved").EnumerateArray()) Add(Unresolved(u, aliasById));
            if (first is not null) return Fail(first);
            if (assessment == "INCOMPLETE") return Fail("MODEL_DECLARED_INCOMPLETE");
            return new(true, null, rows, unresolvedRefs.ToArray());
        }
    }

    static F1QRowResult Heading(JsonElement h, Dictionary<string, string> aliasById, IReadOnlySet<string> citable, SortedSet<string> unresolved)
    {
        var occurrence = Str(h, "occurrence") ?? "";
        F1QRowResult Fail(string code) => new(occurrence, false, code, null);
        if (h.ValueKind != JsonValueKind.Object) return Fail("HEADING_NOT_OBJECT");
        var names = h.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != HeadingKeys.Length || HeadingKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("HEADING_KEYS_INVALID");
        if (!aliasById.TryGetValue(occurrence, out var alias)) return Fail("OCCURRENCE_NOT_ISSUED");
        if (Str(h, "sourceAlias") != alias) return Fail("SOURCE_ALIAS_MISMATCH");
        var refs = h.GetProperty("evidenceRefs");
        if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() is < 1 or > MaxEvidenceRefs || refs.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || r.GetString()!.Length == 0))
            return Fail("EVIDENCE_REFS_INVALID");
        var list = refs.EnumerateArray().Select(r => r.GetString()!).ToArray();
        foreach (var r in list)
        {
            if (ToolEvidenceId.IsMatch(r)) { unresolved.Add(r); continue; } // coverage checked by the binder against the tool transcript
            if (!citable.Contains(r)) return Fail("EVIDENCE_REF_NOT_CITABLE");
        }
        if (!list.Contains(alias) && !list.Any(r => ToolEvidenceId.IsMatch(r))) return Fail("EVIDENCE_NOT_ABOUT_SUBJECT");
        var j = Str(h, "justification");
        if (string.IsNullOrWhiteSpace(j) || j.Length > MaxJustificationChars) return Fail("JUSTIFICATION_INVALID");
        return new(occurrence, true, null, new F1QDecision(occurrence, "HEADING_MEMBER", null, "", list, j, []));
    }

    static F1QRowResult Unresolved(JsonElement u, Dictionary<string, string> aliasById)
    {
        var occurrence = Str(u, "occurrence") ?? "";
        F1QRowResult Fail(string code) => new(occurrence, false, code, null);
        if (u.ValueKind != JsonValueKind.Object) return Fail("UNRESOLVED_NOT_OBJECT");
        var names = u.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != UnresolvedKeys.Length || UnresolvedKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("UNRESOLVED_KEYS_INVALID");
        if (!aliasById.TryGetValue(occurrence, out var alias)) return Fail("OCCURRENCE_NOT_ISSUED");
        if (Str(u, "sourceAlias") != alias) return Fail("SOURCE_ALIAS_MISMATCH");
        var m = u.GetProperty("missingEvidence");
        if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() is < 1 or > MaxMissingEvidence || m.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
            return Fail("MISSING_EVIDENCE_INVALID");
        return new(occurrence, true, null, new F1QDecision(occurrence, "UNRESOLVED", null, "", [], "", m.EnumerateArray().Select(x => x.GetString()!).ToArray()));
    }

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string PromptSha256() => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(SystemPrompt()));
}
