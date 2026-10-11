using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1_REGION_HEADING_V5 (Issue #8, design: artifacts/web-pdf-semantic-diagnostic/p7.f1.v5.design.v1.md). Region-first
/// reasoning in one generation: the model states, in its own words, what each contiguous stretch of the chunk does at its
/// location, then selects headings inside those regions. No ontology, no E/R/O, no C# classification: the validator checks
/// shape, issued ids, reading order, non-overlap and that each selected subject lies in the region it cites. A region
/// statement never removes a heading in C#. Opaque ids, scope and evidence rules are the V4.1 ones.
/// </summary>
internal static class P7F1RegionProtocolV5
{
    public const string Version = "P7_F1_REGION_HEADING_V5";
    public const int MaxJustificationChars = 300, MaxFunctionChars = 300, MaxEvidenceRefs = 8, MaxMissingEvidence = 6;
    static readonly string[] RootRequired = ["protocolVersion", "requestId", "assessment", "regions", "headings"];
    static readonly string[] RootAllowed = ["protocolVersion", "requestId", "assessment", "regions", "headings", "unresolved"];
    static readonly string[] RegionKeys = ["from", "to", "function", "evidence"];
    static readonly string[] HeadingKeys = ["id", "region", "evidenceRefs", "justification"];
    static readonly string[] UnresolvedKeys = ["id", "missingEvidence"];
    static readonly Regex ToolEvidenceId = new(@"^E\d+\.\d+$", RegexOptions.CultureInvariant);

    private const string Question = """
        You are an independent document analyst. The user wants the document's headings: the title identifying the document, a genuine subtitle, and the headings that introduce substantive regions of the document.
        Each issued subject has an id (four letters) and a sourceAlias. It is a parser-grounded piece of text, not necessarily a whole line or heading; one heading may span several subjects.
        Work in two steps, in your own reasoning, before you write the JSON.
        STEP 1 - REGIONS. Read the subjects in reading order and divide them into contiguous regions. For each region, state in your own words what that stretch of the document does at its location: for example body prose, a table of contents or other list of entries, a table or its labels, a form's identifying lines, a title block, a running header or footer, or something else. Use the neighbouring subjects, page statistics, layout and tool evidence. Regions do not overlap, and they need not cover every subject.
        STEP 2 - HEADINGS. Select each subject that contributes wording to a genuine document heading operating AT ITS OWN LOCATION: at this position, it names or opens the content that follows it here. Judge each subject inside the region it belongs to. Your region statement should inform the decision: a subject in a region you described as navigation, a table, or a form field is selected only if it opens content of its own at this location. A line that names a heading whose content is elsewhere does not operate here. Select each subject separately. Do not group subjects, decide heading boundaries, levels or hierarchy.
        Not every visually prominent, bold, numbered or organising text is a document heading. A title may be split over several lines, including a short document-type word set apart above it; every line of the title contributes. A form's own title operates at its location.
        If the evidence cannot decide a subject, list it as unresolved. Do not invent relationships.
        """;

    private const string Contract = """
        OUTPUT. Return exactly one JSON object and nothing else:
        {"protocolVersion":"P7_F1_REGION_HEADING_V5","requestId":"<requestId>","assessment":"COMPLETE","regions":[{"from":"kqzt","to":"mbxr","function":"<your words>","evidence":["kqzt"]}],"headings":[{"id":"kqzt","region":0,"evidenceRefs":["kqzt"],"justification":"..."}],"unresolved":[{"id":"hpdv","missingEvidence":["cross-page context"]}]}
        - requestId: copy the value given in the request.
        - assessment: COMPLETE when every issued subject has been considered; INCOMPLETE only if you could not finish.
        - regions: in reading order, non-overlapping, one entry per region. from and to are issued ids; from comes at or before to. function: 1 to 300 characters, your own description. evidence: 0 to 8 refs, each an issued id, a sourceAlias from the request, or a tool evidenceId.
        - headings: one entry per selected subject (may be empty), identified by its four-letter id. region: the 0-based index of the region in your regions array that contains this subject; every heading must sit inside a region. evidenceRefs: 1 to 8 strings, each an issued id, a sourceAlias from the request, or a tool evidenceId; at least one must be this subject's own id, its own sourceAlias, or a tool evidenceId whose result includes it. justification: at most 300 characters, a brief source-checkable reason.
        - unresolved (optional, may be omitted when empty): subjects the evidence cannot decide; missingEvidence lists 1 to 6 short kinds of evidence that would decide it.
        - A subject appears at most once overall. Use only issued ids; context-only lines are never subjects.
        The example values are schema examples only.
        """;

    public static string SystemPrompt()
    {
        var v1 = P7F1DirectProtocol.SystemPrompt();
        // Evidence and tool guidance are reused verbatim from V1, as in V4.1.
        var evidence = v1[v1.IndexOf("EVIDENCE.", StringComparison.Ordinal)..v1.IndexOf("OUTPUT.", StringComparison.Ordinal)].Trim('\n');
        return string.Join("\n\n", new[] { Question.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n'), evidence,
            Contract.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n') });
    }

    public static string PromptSha256() => SpatialCanonical.Hash(Encoding.UTF8.GetBytes(SystemPrompt()));

    public static string UserMessage(string requestId, IReadOnlyList<P7F1DirectProtocol.Subject> subjects, IReadOnlyDictionary<string, string> ids,
        IReadOnlyList<(string Alias, int Page, string Text)> context, P7F1QEvidenceTools tools, P7F1SourceEnrichment enrichment)
    {
        var pages = subjects.Select(i => i.Page).Concat(context.Select(c => c.Page)).Distinct().Order().ToArray();
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            protocolVersion = Version, requestId,
            layoutBasis = P7F1QEvidenceTools.LayoutBasis, enrichmentVersion = enrichment.Ver,
            enrichmentLegend = enrichment.LegendText.Replace("\r\n", "\n", StringComparison.Ordinal),
            pageStats = pages.Select(tools.PageStats).ToArray(),
            issuedSubjects = subjects.Select(i => new { id = ids[i.SourceAlias], sourceAlias = i.SourceAlias, page = i.Page, text = i.Text,
                layout = tools.Layout(i.SourceAlias), sourceEnrichment = enrichment.Of(i.SourceAlias) }).ToArray(),
            contextOnlyEvidence = context.OrderBy(c => tools.LineOf(c.Alias).Ordinal).Select(c => new { sourceAlias = c.Alias, page = c.Page, text = c.Text,
                layout = tools.Layout(c.Alias), sourceEnrichment = enrichment.Compact(c.Alias) }).ToArray(),
        }, P7F1QEvidenceTools.WireJson);
    }

    /// <summary>Strict contract check. Subjects must be in reading order (the chunk order). Headings become rows (Function = the
    /// region's own words); the region index is re-derived by <see cref="RegionsOf"/> for the binder.</summary>
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
            var regionsEl = root.GetProperty("regions");
            var hasUnresolved = root.TryGetProperty("unresolved", out var unresolved);
            if (regionsEl.ValueKind != JsonValueKind.Array || root.GetProperty("headings").ValueKind != JsonValueKind.Array ||
                (hasUnresolved && unresolved.ValueKind != JsonValueKind.Array)) return Fail("LIST_NOT_ARRAY");
            var byId = subjects.ToDictionary(s => idByAlias[s.SourceAlias], StringComparer.Ordinal);
            var position = subjects.Select((s, i) => (Id: idByAlias[s.SourceAlias], i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
            var spans = new List<(int From, int To, string Function)>();
            foreach (var r in regionsEl.EnumerateArray())
            {
                var code = Region(r, position, byId, idByAlias, citable, unresolvedRefs, out var span);
                if (code is not null) return Fail(code);
                if (spans.Count > 0 && span.From <= spans[^1].To) return Fail("REGIONS_NOT_IN_READING_ORDER_OR_OVERLAP");
                spans.Add(span);
            }
            // Coverage (audit): a COMPLETE response must place every issued subject in exactly one contiguous region, from the
            // first subject to the last. Without it an uncovered subject would be scored negative with no region statement.
            if (spans.Count == 0 || spans[0].From != 0 || spans[^1].To != subjects.Count - 1 ||
                Enumerable.Range(1, spans.Count - 1).Any(k => spans[k].From != spans[k - 1].To + 1))
                return Fail("REGION_COVERAGE_INCOMPLETE");
            var seen = new HashSet<string>(StringComparer.Ordinal); string? first = null;
            void Add(F1QRowResult r) { if (r.Valid && !seen.Add(r.Occurrence)) r = r with { Valid = false, FailureCode = "SUBJECT_LISTED_TWICE" }; rows.Add(r); first ??= r.Valid ? null : r.FailureCode; }
            foreach (var h in root.GetProperty("headings").EnumerateArray()) Add(Heading(h, byId, position, spans, idByAlias, citable, unresolvedRefs));
            if (hasUnresolved) foreach (var u in unresolved.EnumerateArray()) Add(Unresolved(u, byId));
            if (first is not null) return Fail(first);
            if (assessment == "INCOMPLETE") return Fail("MODEL_DECLARED_INCOMPLETE");
            return new(true, null, rows, unresolvedRefs.ToArray());
        }
    }

    /// <summary>Regions of an already validated response, with aliases, for the binder; and the region index of each heading id.</summary>
    public static (IReadOnlyList<RegionRow> Regions, IReadOnlyDictionary<string, int> RegionOfId) RegionsOf(string text, IReadOnlyDictionary<string, string> aliasById)
    {
        using var doc = JsonDocument.Parse(text);
        var regions = new List<RegionRow>(); var of = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in doc.RootElement.GetProperty("regions").EnumerateArray())
        {
            var index = regions.Count;
            var evidence = r.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!)
                .Select(e => aliasById.TryGetValue(e, out var a) ? a : e).ToArray();
            regions.Add(new RegionRow(index, aliasById[Str(r, "from")!], aliasById[Str(r, "to")!], Str(r, "function")!, evidence));
        }
        foreach (var h in doc.RootElement.GetProperty("headings").EnumerateArray()) of[Str(h, "id")!] = h.GetProperty("region").GetInt32();
        return (regions, of);
    }

    public sealed record RegionRow(int Index, string FromAlias, string ToAlias, string Function, IReadOnlyList<string> Evidence);

    static string? Region(JsonElement r, IReadOnlyDictionary<string, int> position, Dictionary<string, P7F1DirectProtocol.Subject> byId,
        IReadOnlyDictionary<string, string> idByAlias, IReadOnlySet<string> citable, SortedSet<string> unresolved, out (int From, int To, string Function) span)
    {
        span = default;
        if (r.ValueKind != JsonValueKind.Object) return "REGION_NOT_OBJECT";
        var names = r.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != RegionKeys.Length || RegionKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return "REGION_KEYS_INVALID";
        var from = Str(r, "from") ?? ""; var to = Str(r, "to") ?? "";
        if (!position.TryGetValue(from, out var a) || !position.TryGetValue(to, out var b)) return "REGION_ID_NOT_ISSUED";
        if (a > b) return "REGION_FROM_AFTER_TO";
        var f = Str(r, "function");
        if (string.IsNullOrWhiteSpace(f) || f.Length > MaxFunctionChars) return "REGION_FUNCTION_INVALID";
        var ev = r.GetProperty("evidence");
        if (ev.ValueKind != JsonValueKind.Array || ev.GetArrayLength() > MaxEvidenceRefs || ev.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return "REGION_EVIDENCE_INVALID";
        foreach (var e in ev.EnumerateArray().Select(x => x.GetString()!))
        {
            if (ToolEvidenceId.IsMatch(e)) { unresolved.Add(e); continue; }
            if (!citable.Contains(e) && !byId.ContainsKey(e)) return "REGION_EVIDENCE_NOT_CITABLE";
        }
        span = (a, b, f);
        return null;
    }

    static F1QRowResult Heading(JsonElement h, Dictionary<string, P7F1DirectProtocol.Subject> byId, IReadOnlyDictionary<string, int> position,
        List<(int From, int To, string Function)> spans, IReadOnlyDictionary<string, string> idByAlias, IReadOnlySet<string> citable, SortedSet<string> unresolved)
    {
        var id = Str(h, "id") ?? "";
        F1QRowResult Fail(string code) => new(byId.TryGetValue(id, out var s0) ? s0.Occurrence : id, false, code, null);
        if (h.ValueKind != JsonValueKind.Object) return Fail("HEADING_NOT_OBJECT");
        var names = h.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != HeadingKeys.Length || HeadingKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("HEADING_KEYS_INVALID");
        if (!byId.TryGetValue(id, out var s)) return Fail("ID_NOT_ISSUED");
        var reg = h.GetProperty("region");
        if (reg.ValueKind != JsonValueKind.Number || !reg.TryGetInt32(out var ri) || ri < 0 || ri >= spans.Count) return Fail("HEADING_REGION_INVALID");
        var (from, to, function) = spans[ri];
        if (position[id] < from || position[id] > to) return Fail("HEADING_OUTSIDE_ITS_REGION");
        var refs = h.GetProperty("evidenceRefs");
        if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() is < 1 or > MaxEvidenceRefs || refs.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || x.GetString()!.Length == 0))
            return Fail("EVIDENCE_REFS_INVALID");
        var list = refs.EnumerateArray().Select(x => x.GetString()!).ToArray();
        foreach (var r in list)
        {
            if (ToolEvidenceId.IsMatch(r)) { unresolved.Add(r); continue; }
            if (!citable.Contains(r) && !byId.ContainsKey(r)) return Fail("EVIDENCE_REF_NOT_CITABLE");
        }
        if (!list.Contains(id) && !list.Contains(s.SourceAlias) && !list.Any(r => ToolEvidenceId.IsMatch(r))) return Fail("EVIDENCE_NOT_ABOUT_SUBJECT");
        var j = Str(h, "justification");
        if (string.IsNullOrWhiteSpace(j) || j.Length > MaxJustificationChars) return Fail("JUSTIFICATION_INVALID");
        var aliasById = byId.ToDictionary(p => p.Key, p => p.Value.SourceAlias, StringComparer.Ordinal);
        var normalized = list.Select(r => aliasById.TryGetValue(r, out var a) ? a : r).ToArray();
        return new(s.Occurrence, true, null, new F1QDecision(s.Occurrence, "HEADING_MEMBER", function, "", normalized, j, []));
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
