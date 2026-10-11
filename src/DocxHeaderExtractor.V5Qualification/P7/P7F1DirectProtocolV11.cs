using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1_DIRECT_HEADING_V1_1 (Issue #7, after the V4 pilot audit). Same direct heading-membership task as V1 (no E/R/O,
/// kinds, hierarchy). Contract repairs only: opaque 4-letter subject ids that cannot be confused with line numbers
/// (the C# side resolves the sourceAlias); no model-echoed scope hash (the scope is pinned in the plan and checked by the
/// binder); an absent "unresolved" reads as empty. Prompt adds the at-location condition (navigation lines name headings
/// that operate elsewhere) and two general title/form notes. V1 stays frozen for the pilot.
/// </summary>
internal static class P7F1DirectProtocolV11
{
    public const string Version = "P7_F1_DIRECT_HEADING_V1_1";
    public const int MaxJustificationChars = 300, MaxEvidenceRefs = 8, MaxMissingEvidence = 6;
    static readonly string[] RootRequired = ["protocolVersion", "requestId", "assessment", "headings"];
    static readonly string[] RootAllowed = ["protocolVersion", "requestId", "assessment", "headings", "unresolved"];
    static readonly string[] HeadingKeys = ["id", "evidenceRefs", "justification"];
    static readonly string[] UnresolvedKeys = ["id", "missingEvidence"];
    static readonly Regex ToolEvidenceId = new(@"^E\d+\.\d+$", RegexOptions.CultureInvariant);
    const string Letters = "bcdfghjkmnpqrstvwxz"; // consonants only: no words, no digits, nothing that reads like L0007 or O7

    private const string Question = """
        You are an independent document analyst. The user wants the document's headings: the title identifying the document, a genuine subtitle, and the headings that introduce substantive regions of the document.
        Each issued subject has an id (four letters) and a sourceAlias. It is a parser-grounded piece of text, not necessarily a whole line, heading or sentence; one heading may span several subjects.
        For every issued subject decide whether it contributes wording to a genuine document heading operating AT ITS OWN LOCATION: at this position in the document, it names or opens the content that follows it here. Select each contributing subject separately. Do not group subjects, decide heading boundaries, levels or hierarchy.
        AT-LOCATION CONDITION. A line that names a heading whose content is somewhere else does not operate here. Table-of-contents, list-of-tables/figures, index and cross-reference lines are navigation: they are not selected, even when numbered, bold, or worded exactly like a real heading, and even when they sit on the same page as a real heading. The heading that opens the navigation region itself (for example "Contents" or "List of Tables") does operate here.
        Not every visually prominent, bold, numbered or organising text is a document heading. Before selecting a subject, examine the competing explanations that the evidence leaves open:
        - a label, group label, column header, total or value inside a table or financial statement;
        - a table, chart or figure caption or title, or a chart panel label;
        - a list item, definition term, numbered legal provision or clause whose line continues as ordinary provision text, a theorem/definition lead-in;
        - a navigation line (see the at-location condition);
        - a form field, fill-in instruction, subject line, signature or administrative block;
        - front matter such as issuer or organisation name, letterhead, national motto, document number, date, reporting period or place;
        - a running header or footer repeated on other pages, or a page number.
        A title may be split over several lines, including a short document-type word set apart above it (such as LAW, DECREE, ORDER, LUẬT, NGHỊ ĐỊNH); every line of the title contributes. A form's own title (for example "Form X" naming the form that follows) operates at its location. A genuine heading can also sit inside a table or form; select it when the evidence shows it introduces a region of the document rather than labelling local data. When the evidence cannot decide, report the subject as unresolved instead of guessing either way.
        """;
    private const string Contract = """
        OUTPUT. Return exactly one JSON object and nothing else:
        {"protocolVersion":"P7_F1_DIRECT_HEADING_V1_1","requestId":"<requestId>","assessment":"COMPLETE","headings":[{"id":"kqzt","evidenceRefs":["kqzt"],"justification":"..."}],"unresolved":[{"id":"mbxr","missingEvidence":["cross-page context"]}]}
        - requestId: copy the value given in the request.
        - assessment: COMPLETE when every issued subject has been considered. Subjects you do not list are those you judge NOT to contribute to a document heading.
        - headings: one entry per selected subject (may be empty), identified by its four-letter id only. evidenceRefs: 1 to 8 strings, each an issued id, a sourceAlias from the request, or a tool evidenceId; at least one must be this subject's own id, its own sourceAlias, or a tool evidenceId whose result includes it. justification: at most 300 characters, a brief source-checkable reason why this subject contributes to a document heading at its own location.
        - unresolved (optional, may be omitted when empty): subjects whose heading membership the evidence cannot decide; missingEvidence lists 1 to 6 short kinds of evidence that would decide it.
        - A subject appears at most once overall. Use only issued ids; context-only lines are never subjects.
        The example values are schema examples only.
        """;

    // Evidence and tool guidance are unchanged from V1 (reused verbatim through P7F1DirectProtocol's prompt section).
    public static string SystemPrompt()
    {
        var v1 = P7F1DirectProtocol.SystemPrompt();
        var evidence = v1[v1.IndexOf("EVIDENCE.", StringComparison.Ordinal)..v1.IndexOf("OUTPUT.", StringComparison.Ordinal)].Trim('\n');
        return string.Join("\n\n", new[] { Question.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n'), evidence,
            Contract.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n') });
    }

    public static string PromptSha256() => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(SystemPrompt()));

    /// <summary>Deterministic opaque ids: four consonants from sha256(requestId|alias|salt), unique within the request.</summary>
    public static IReadOnlyDictionary<string, string> OpaqueIds(string requestId, IReadOnlyList<P7F1DirectProtocol.Subject> subjects)
    {
        var used = new HashSet<string>(StringComparer.Ordinal); var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in subjects)
            for (var salt = 0; ; salt++)
            {
                var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|{requestId}|{s.SourceAlias}|{salt}"));
                var id = new string(Enumerable.Range(0, 4).Select(i => Letters[h[i] % Letters.Length]).ToArray());
                if (used.Add(id)) { map[s.SourceAlias] = id; break; }
            }
        return map;
    }

    /// <summary>Plan-side scope (never echoed by the model): request, document, and every issued (id, alias, page, text).</summary>
    public static string ScopeSha256(string requestId, string documentId, IReadOnlyList<P7F1DirectProtocol.Subject> subjects, IReadOnlyDictionary<string, string> ids) =>
        SpatialCanonical.Hash(SpatialCanonical.Bytes(new { version = Version, requestId, documentId,
            issued = subjects.Select(s => new { id = ids[s.SourceAlias], s.Occurrence, s.SourceAlias, s.Page, s.Text }) }));

    public static string UserMessage(string requestId, IReadOnlyList<P7F1DirectProtocol.Subject> subjects, IReadOnlyDictionary<string, string> ids,
        IReadOnlyList<(string Alias, int Page, string Text)> context, P7F1QEvidenceTools tools, P7F1SourceEnrichment enrichment)
    {
        var pages = subjects.Select(i => i.Page).Concat(context.Select(c => c.Page)).Distinct().Order().ToArray();
        return JsonSerializer.Serialize(new
        {
            protocolVersion = Version, requestId,
            layoutBasis = P7F1QEvidenceTools.LayoutBasis, enrichmentVersion = P7F1SourceEnrichment.Version,
            enrichmentLegend = P7F1SourceEnrichment.Legend.Replace("\r\n", "\n", StringComparison.Ordinal),
            pageStats = pages.Select(tools.PageStats).ToArray(),
            issuedSubjects = subjects.Select(i => new { id = ids[i.SourceAlias], sourceAlias = i.SourceAlias, page = i.Page, text = i.Text,
                layout = tools.Layout(i.SourceAlias), sourceEnrichment = enrichment.Of(i.SourceAlias) }).ToArray(),
            contextOnlyEvidence = context.OrderBy(c => tools.LineOf(c.Alias).Ordinal).Select(c => new { sourceAlias = c.Alias, page = c.Page, text = c.Text,
                layout = tools.Layout(c.Alias), sourceEnrichment = enrichment.Compact(c.Alias) }).ToArray(),
        }, P7F1QEvidenceTools.WireJson);
    }

    /// <summary>
    /// Strict contract check. Rows carry the issued O# as Occurrence (resolved from the id), Assessment HEADING_MEMBER or
    /// UNRESOLVED, Function null. Tool evidenceIds are checked for form here and for coverage by the binder.
    /// </summary>
    public static F1QValidation Validate(string response, string requestId, IReadOnlyList<P7F1DirectProtocol.Subject> subjects,
        IReadOnlyDictionary<string, string> idByAlias, IReadOnlySet<string> citable)
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
            if (RootRequired.Any(k => !names.Contains(k, StringComparer.Ordinal)) || names.Any(n => !RootAllowed.Contains(n, StringComparer.Ordinal))) return Fail("ROOT_KEYS_INVALID");
            if (Str(root, "protocolVersion") != Version) return Fail("PROTOCOL_VERSION_INVALID");
            if (Str(root, "requestId") != requestId) return Fail("REQUEST_ID_MISMATCH");
            var assessment = Str(root, "assessment");
            if (assessment is not ("COMPLETE" or "INCOMPLETE")) return Fail("ASSESSMENT_INVALID");
            var hasUnresolved = root.TryGetProperty("unresolved", out var unresolved);
            if (root.GetProperty("headings").ValueKind != JsonValueKind.Array || (hasUnresolved && unresolved.ValueKind != JsonValueKind.Array)) return Fail("LIST_NOT_ARRAY");
            var byId = subjects.ToDictionary(s => idByAlias[s.SourceAlias], StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal); string? first = null;
            void Add(F1QRowResult r) { if (r.Valid && !seen.Add(r.Occurrence)) r = r with { Valid = false, FailureCode = "SUBJECT_LISTED_TWICE" }; rows.Add(r); first ??= r.Valid ? null : r.FailureCode; }
            foreach (var h in root.GetProperty("headings").EnumerateArray()) Add(Heading(h, byId, idByAlias, citable, unresolvedRefs));
            if (hasUnresolved) foreach (var u in unresolved.EnumerateArray()) Add(Unresolved(u, byId));
            if (first is not null) return Fail(first);
            if (assessment == "INCOMPLETE") return Fail("MODEL_DECLARED_INCOMPLETE");
            return new(true, null, rows, unresolvedRefs.ToArray());
        }
    }

    static F1QRowResult Heading(JsonElement h, Dictionary<string, P7F1DirectProtocol.Subject> byId, IReadOnlyDictionary<string, string> idByAlias,
        IReadOnlySet<string> citable, SortedSet<string> unresolved)
    {
        var id = Str(h, "id") ?? "";
        F1QRowResult Fail(string code) => new(byId.TryGetValue(id, out var s0) ? s0.Occurrence : id, false, code, null);
        if (h.ValueKind != JsonValueKind.Object) return Fail("HEADING_NOT_OBJECT");
        var names = h.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != HeadingKeys.Length || HeadingKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("HEADING_KEYS_INVALID");
        if (!byId.TryGetValue(id, out var s)) return Fail("ID_NOT_ISSUED");
        var refs = h.GetProperty("evidenceRefs");
        if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() is < 1 or > MaxEvidenceRefs || refs.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String || r.GetString()!.Length == 0))
            return Fail("EVIDENCE_REFS_INVALID");
        var list = refs.EnumerateArray().Select(r => r.GetString()!).ToArray();
        foreach (var r in list)
        {
            if (ToolEvidenceId.IsMatch(r)) { unresolved.Add(r); continue; }
            if (!citable.Contains(r) && !byId.ContainsKey(r)) return Fail("EVIDENCE_REF_NOT_CITABLE");
        }
        if (!list.Contains(id) && !list.Contains(s.SourceAlias) && !list.Any(r => ToolEvidenceId.IsMatch(r))) return Fail("EVIDENCE_NOT_ABOUT_SUBJECT");
        var j = Str(h, "justification");
        if (string.IsNullOrWhiteSpace(j) || j.Length > MaxJustificationChars) return Fail("JUSTIFICATION_INVALID");
        // Evidence refs are normalised to source aliases for the binder (an issued id becomes its sourceAlias).
        var aliasById = byId.ToDictionary(p => p.Key, p => p.Value.SourceAlias, StringComparer.Ordinal);
        var normalized = list.Select(r => aliasById.TryGetValue(r, out var a) ? a : r).ToArray();
        return new(s.Occurrence, true, null, new F1QDecision(s.Occurrence, "HEADING_MEMBER", null, "", normalized, j, []));
    }

    static F1QRowResult Unresolved(JsonElement u, Dictionary<string, P7F1DirectProtocol.Subject> byId)
    {
        var id = Str(u, "id") ?? "";
        F1QRowResult Fail(string code) => new(byId.TryGetValue(id, out var s0) ? s0.Occurrence : id, false, code, null);
        if (u.ValueKind != JsonValueKind.Object) return Fail("UNRESOLVED_NOT_OBJECT");
        var names = u.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != UnresolvedKeys.Length || UnresolvedKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("UNRESOLVED_KEYS_INVALID");
        if (!byId.TryGetValue(id, out var s)) return Fail("ID_NOT_ISSUED");
        var m = u.GetProperty("missingEvidence");
        if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() is < 1 or > MaxMissingEvidence || m.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
            return Fail("MISSING_EVIDENCE_INVALID");
        return new(s.Occurrence, true, null, new F1QDecision(s.Occurrence, "UNRESOLVED", null, "", [], "", m.EnumerateArray().Select(x => x.GetString()!).ToArray()));
    }

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
