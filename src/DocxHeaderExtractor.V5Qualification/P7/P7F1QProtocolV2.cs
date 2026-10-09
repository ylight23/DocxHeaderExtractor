using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7_F1Q_V2 (plan V3, after the 2026-10-09 audit of V1/V2): same three functions and the same F1-only
/// question, plus (a) parser layout projected into the initial payload, (b) an evidence-grounded judgment
/// procedure, (c) per-decision source identity and subject-relevant evidence references. Still no Gold,
/// labels, candidates, grouping, extents or hierarchy.
/// </summary>
internal static class P7F1QProtocolV2
{
    public const string Version = "P7_F1Q_V2";
    public const int ChunkSize = 48;
    private static readonly string[] DecisionKeys = ["occurrence", "sourceAlias", "assessment", "function", "observedRole", "evidenceRefs", "interpretation", "missingEvidence"];

    private const string Question = """
        You are an independent document-semantic analyst. The user's intent is to extract the document's headings, including a title identifying the document and titles introducing substantive content regions.

        A source occurrence is a parser-grounded piece of text. It is NOT necessarily a heading, paragraph, line, sentence, or complete title.

        For each issued occurrence, answer: what semantic function does this occurrence actually serve relative to the requested document headings, on the evidence from this source?
        - ESTABLISHES_STRUCTURE: the occurrence's wording contributes to a real document title, subtitle or substantive heading operating HERE, at this source location.
        - REPRESENTS_STRUCTURE: the occurrence lists, refers to or navigates to a heading operating ELSEWHERE (for example a table-of-contents or index entry).
        - OTHER: neither of the above. OTHER is a substantive judgment, never a fallback for missing evidence.

        Do not confuse organizational metadata, administrative letterheads, national mottos, reference numbers, dates, signatures, page furniture, or local table/data labels with document headings merely because they organize content. A heading inside a table may still be genuine. Identical words may serve different functions at different locations. A heading can span several occurrences; classify each contributing occurrence independently. Do not decide where a heading starts, which occurrences group together, where it ends, or its hierarchy.
        """;

    private const string Judgment = """
        EVIDENCE-GROUNDED SEMANTIC JUDGMENT. Do not classify an occurrence solely by its wording, numbering, prominence, or proximity to another heading. Each issued occurrence and context line carries its parser layout (bbox=[left,right,bottom,top] in PDF points with a bottom-left origin, font size, bold and italic ratios); pageStats give each page's text extent and median font size. These are observations, not labels.
        Before returning SUPPORTED for an occurrence:
        1. Identify the semantic claim you are making about THIS occurrence (its own id and sourceAlias), not a neighbouring occurrence.
        2. Identify the source observations that support that claim.
        3. Consider whether another plausible function remains consistent with the observations.
        4. If the distinction depends on layout, use the supplied geometry and typography, or fetch more.
        5. If the distinction depends on document context, use or fetch the surrounding source occurrences.
        6. If the distinction depends on repetition, navigation or a table of contents, compare the actual source locations and their contexts.
        7. For text immediately below a document title, evaluate whether it functions as part of a subtitle or as ordinary body prose; do not assume either from proximity alone.
        8. If the evidence supports competing interpretations, request additional evidence where available.
        9. If the available evidence still cannot resolve the function, return INSUFFICIENT_EVIDENCE, not OTHER.
        Never describe a position, typography, relationship or document role that is absent from or contradicted by the source evidence. A valid source identifier is not by itself proof that an interpretation is supported.
        """;

    private const string Tools = """
        EVIDENCE TOOLS. Read-only tools over this document's parser evidence store: get_occurrence_context (neighbouring lines with layout and spatial relations to the target, and lines on the same baseline), compare_occurrences (two locations side by side with layout deltas), get_page_geometry (lines of one page), get_source_span (full evidence records), get_repeated_occurrences (repeated text elsewhere in the document). Tools return observations only, never labels. Use them when the supplied evidence leaves competing interpretations open; you have at most 3 tool-calling rounds, after which you must answer. Each tool result has an evidenceId such as E1.2 that you may cite.
        """;

    private const string Contract = """
        OUTPUT. Return exactly one JSON object and nothing else, with this shape:
        {"protocolVersion":"P7_F1Q_V2","decisions":[{"occurrence":"O1","sourceAlias":"L0000:S0","assessment":"SUPPORTED","function":"OTHER","observedRole":"...","evidenceRefs":["L0000:S0"],"interpretation":"...","missingEvidence":[]}]}
        Emit exactly one decision for every issued occurrence id, using only issued ids. Context-only lines may be cited but are never decision subjects.
        - sourceAlias: the sourceAlias issued with that occurrence id, copied exactly.
        - assessment: SUPPORTED or INSUFFICIENT_EVIDENCE.
        - function: ESTABLISHES_STRUCTURE, REPRESENTS_STRUCTURE or OTHER when SUPPORTED (never null); null when INSUFFICIENT_EVIDENCE.
        - observedRole: at most 200 characters, only what the evidence shows about this occurrence.
        - evidenceRefs: 1 to 8 strings; each is a sourceAlias from the supplied evidence or a tool result, or an evidenceId returned by a tool. At least one reference must be about this occurrence itself: its own sourceAlias, or an evidenceId whose result includes it.
        - interpretation: at most 300 characters, a short checkable semantic interpretation. Do not copy whole evidence records or geometry.
        - missingEvidence: [] when SUPPORTED; 1 to 6 short evidence kinds when INSUFFICIENT_EVIDENCE.
        The example values above are schema examples only, not evidence.
        """;

    public static string SystemPrompt() => string.Join("\n\n", new[] { Question, Judgment, Tools, Contract }
        .Select(b => b.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n')));

    /// <summary>Contiguous chunks of the pack's issued occurrences, keeping the original O# ids.</summary>
    public static IReadOnlyList<IReadOnlyList<V5Issued>> Chunks(IReadOnlyList<V5Issued> issued)
    {
        var count = (issued.Count + ChunkSize - 1) / ChunkSize;
        var size = (issued.Count + count - 1) / count; // balanced
        return Enumerable.Range(0, count).Select(i => (IReadOnlyList<V5Issued>)issued.Skip(i * size).Take(size).ToArray()).Where(c => c.Count > 0).ToArray();
    }

    internal sealed record V5Issued(string Occurrence, string SourceAlias, int Page, string Text, JsonElement Correspondences);

    /// <summary>Decision subjects = one chunk; every other pack occurrence and the pack's context-only lines are
    /// context (alias, page, text, layout; no id). Same source text and correspondences as the frozen Control.</summary>
    public static string UserMessage(IReadOnlyList<V5Issued> chunk, IReadOnlyList<V5Issued> pack,
        IReadOnlyList<(string Alias, int Page, string Text)> packContext, P7F1QEvidenceTools tools)
    {
        var subjects = chunk.Select(c => c.SourceAlias).ToHashSet(StringComparer.Ordinal);
        var context = pack.Where(p => !subjects.Contains(p.SourceAlias)).Select(p => (Alias: p.SourceAlias, p.Page, p.Text))
            .Concat(packContext).OrderBy(c => tools.LineOf(c.Alias).Ordinal).ToArray();
        var pages = chunk.Select(c => c.Page).Concat(context.Select(c => c.Page)).Distinct().Order().ToArray();
        return JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            layoutBasis = P7F1QEvidenceTools.LayoutBasis,
            pageStats = pages.Select(tools.PageStats).ToArray(),
            issuedOccurrences = chunk.Select(c => new
            {
                id = c.Occurrence, sourceAlias = c.SourceAlias, page = c.Page, text = c.Text,
                layout = tools.Layout(c.SourceAlias), correspondences = c.Correspondences,
            }).ToArray(),
            contextOnlyEvidence = context.Select(c => new { sourceAlias = c.Alias, page = c.Page, text = c.Text, layout = tools.Layout(c.Alias) }).ToArray(),
        }, P7F1QEvidenceTools.WireJson);
    }

    /// <summary>Strict P7_F1Q_V2 contract. Adds per-decision source identity and subject-relevant evidence on top of
    /// V1 checks. Never repairs; individually valid rows of a rejected response are diagnostic only.</summary>
    public static F1QValidation Validate(string response, IReadOnlyList<F1QIssuedOccurrence> issued, IReadOnlySet<string> citable,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> evidenceCoverage)
    {
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(response); }
        catch (JsonException) { return new(false, "RESPONSE_NOT_JSON", [], []); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(false, "ROOT_NOT_OBJECT", [], []);
            if (!root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array)
                return new(false, "DECISIONS_MISSING", [], []);
            var aliasById = issued.ToDictionary(i => i.Occurrence, i => i.SourceAlias, StringComparer.Ordinal);
            var rows = new List<F1QRowResult>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in decisions.EnumerateArray())
            {
                var row = Row(d, aliasById, citable, evidenceCoverage, unresolved);
                if (row.Valid && !seen.Add(row.Occurrence)) row = row with { Valid = false, FailureCode = "OCCURRENCE_DUPLICATE" };
                rows.Add(row);
            }
            string? failure = null;
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 2 || !names.Contains("protocolVersion") || !names.Contains("decisions")) failure = "ROOT_KEYS_INVALID";
            else if (root.GetProperty("protocolVersion").ValueKind != JsonValueKind.String || root.GetProperty("protocolVersion").GetString() != Version) failure = "PROTOCOL_VERSION_INVALID";
            else if (rows.FirstOrDefault(r => !r.Valid) is { } bad) failure = bad.FailureCode;
            else if (rows.Count != issued.Count || !aliasById.Keys.ToHashSet().SetEquals(seen)) failure = "DECISION_CARDINALITY_INVALID";
            return new(failure is null, failure, rows, unresolved.ToArray());
        }
    }

    private static F1QRowResult Row(JsonElement d, Dictionary<string, string> aliasById, IReadOnlySet<string> citable,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> coverage, SortedSet<string> unresolved)
    {
        var occurrence = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("occurrence", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "";
        F1QRowResult Fail(string code) => new(occurrence, false, code, null);
        if (d.ValueKind != JsonValueKind.Object) return Fail("DECISION_NOT_OBJECT");
        var names = d.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != DecisionKeys.Length || DecisionKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("DECISION_KEYS_INVALID");
        if (!aliasById.TryGetValue(occurrence, out var subjectAlias)) return Fail("OCCURRENCE_NOT_ISSUED");
        if (d.GetProperty("sourceAlias") is not { ValueKind: JsonValueKind.String } sa || sa.GetString() != subjectAlias) return Fail("SOURCE_ALIAS_MISMATCH");
        var assessment = d.GetProperty("assessment").ValueKind == JsonValueKind.String ? d.GetProperty("assessment").GetString() : null;
        var fn = d.GetProperty("function");
        string? function = fn.ValueKind switch { JsonValueKind.String => fn.GetString(), JsonValueKind.Null => null, _ => "\u0000" };
        if (function == "\u0000") return Fail("FUNCTION_TYPE_INVALID");
        if (!Strings(d.GetProperty("evidenceRefs"), out var refs) || !Strings(d.GetProperty("missingEvidence"), out var missing)) return Fail("ARRAY_FIELD_INVALID");
        var role = d.GetProperty("observedRole"); var interpretation = d.GetProperty("interpretation");
        if (role.ValueKind != JsonValueKind.String || role.GetString()!.Trim().Length == 0 || role.GetString()!.Length > P7F1QProtocol.MaxRole) return Fail("OBSERVED_ROLE_INVALID");
        if (interpretation.ValueKind != JsonValueKind.String || interpretation.GetString()!.Trim().Length == 0 || interpretation.GetString()!.Length > P7F1QProtocol.MaxInterpretation)
            return Fail("INTERPRETATION_INVALID");
        switch (assessment)
        {
            case "SUPPORTED":
                if (function is null || !P7F1QProtocol.Functions.Contains(function)) return Fail("SUPPORTED_FUNCTION_INVALID");
                if (missing.Count != 0) return Fail("SUPPORTED_WITH_MISSING_EVIDENCE");
                break;
            case "INSUFFICIENT_EVIDENCE":
                if (function is not null) return Fail("ABSTENTION_WITH_FUNCTION");
                if (missing.Count is < 1 or > P7F1QProtocol.MaxMissing || missing.Any(m => m.Trim().Length == 0 || m.Length > P7F1QProtocol.MaxMissingChars))
                    return Fail("ABSTENTION_MISSING_EVIDENCE_INVALID");
                break;
            default: return Fail("ASSESSMENT_INVALID");
        }
        if (refs.Count is < 1 or > P7F1QProtocol.MaxRefs) return Fail("EVIDENCE_REFS_CARDINALITY_INVALID");
        var bad = refs.Where(r => !citable.Contains(r)).ToArray();
        foreach (var b in bad) unresolved.Add(b);
        if (bad.Length > 0) return Fail("EVIDENCE_REF_UNRESOLVED");
        if (!refs.Any(r => r == subjectAlias || (coverage.TryGetValue(r, out var covered) && covered.Contains(subjectAlias))))
            return Fail("EVIDENCE_NOT_ABOUT_SUBJECT");
        return new(occurrence, true, null, new(occurrence, assessment, function, role.GetString()!, refs, interpretation.GetString()!, missing));
    }

    private static bool Strings(JsonElement e, out IReadOnlyList<string> values)
    {
        values = [];
        if (e.ValueKind != JsonValueKind.Array || e.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) return false;
        values = e.EnumerateArray().Select(v => v.GetString()!).ToArray();
        return true;
    }
}
