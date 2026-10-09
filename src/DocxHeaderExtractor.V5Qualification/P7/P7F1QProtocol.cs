using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Semantics.Canonical;
using DocxHeaderExtractor.DocumentProcessing.Source.Common;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal enum F1QArm { Control, F1QNoTools, F1QTools, F1QToolsMandatory, F1QEvidenceV2 }

internal sealed record F1QIssuedOccurrence(string Occurrence, string SourceAlias, int Page, string Text);

internal sealed record F1QDecision(string Occurrence, string Assessment, string? Function, string ObservedRole,
    IReadOnlyList<string> EvidenceRefs, string Interpretation, IReadOnlyList<string> MissingEvidence);

internal sealed record F1QRowResult(string Occurrence, bool Valid, string? FailureCode, F1QDecision? Decision);

internal sealed record F1QValidation(bool StrictAccepted, string? FailureCode, IReadOnlyList<F1QRowResult> Rows,
    IReadOnlyList<string> UnresolvedEvidenceRefs);

/// <summary>
/// P7-F1Q-V1: an independent, user-intent-aware semantic question about each occurrence. F1 only: no
/// G2/H2 grouping, anchors, extents or candidates are inputs or outputs. INSUFFICIENT_EVIDENCE is an
/// epistemic assessment, never a fourth function and never a silent OTHER.
/// </summary>
internal static class P7F1QProtocol
{
    public const string Version = "P7_F1Q_V1";
    public const int MaxRole = 200, MaxInterpretation = 300, MaxRefs = 8, MaxMissing = 6, MaxMissingChars = 80;
    public static readonly string[] Functions = ["ESTABLISHES_STRUCTURE", "REPRESENTS_STRUCTURE", "OTHER"];
    private static readonly string[] DecisionKeys = ["occurrence", "assessment", "function", "observedRole", "evidenceRefs", "interpretation", "missingEvidence"];

    private const string Question = """
        You are an independent document-semantic analyst. The user's intent is to extract the document's headings, including a title identifying the document and titles introducing substantive content regions.

        A source occurrence is a parser-grounded piece of text. It is NOT necessarily a heading, paragraph, line, sentence, or complete title.

        For each issued occurrence, answer: what semantic function does this occurrence actually serve relative to the requested document headings, on the evidence from this source?
        - ESTABLISHES_STRUCTURE: the occurrence's wording contributes to a real document title or a substantive heading operating HERE, at this source location.
        - REPRESENTS_STRUCTURE: the occurrence lists, refers to or navigates to a heading operating ELSEWHERE (for example a table-of-contents or index entry).
        - OTHER: neither of the above. OTHER is a substantive judgment, never a fallback for missing evidence.

        Distinguish directly observed source facts from your interpretation. Consider the occurrence in its document context. Do not confuse organizational metadata, administrative letterheads, national mottos, reference numbers, dates, signatures, page furniture, or local table/data labels with document headings merely because they organize content. A heading inside a table may still be genuine; table membership, boldness, capitalization, alignment, numbering and prominence are not conclusive on their own. Identical words may serve different functions at different locations. A heading can span several occurrences; classify each contributing occurrence independently.

        Never invent source text, page context, evidence references, structure, relations or authority. If the evidence available to you does not distinguish the plausible functions, set assessment to INSUFFICIENT_EVIDENCE, leave function null, and list the missing evidence kinds; do not guess. Do not decide where a heading starts, which occurrences group together, where it ends, or its hierarchy. Do not report private reasoning; a short checkable interpretation is enough.
        """;

    private const string ToolClause = """

        EVIDENCE TOOLS. You may call read-only evidence tools over this document's parser evidence store: get_occurrence_context (neighbouring lines), get_page_geometry (line boxes and font summaries for a page), get_source_span (full evidence record of targets), get_repeated_occurrences (repeated text elsewhere in the document). Tools return observations only, never labels. Call a tool when the supplied text leaves the function ambiguous, for example heading vs. metadata/letterhead, table label vs. heading, title vs. subtitle, contents entry vs. body heading, multi-part headings, or repeated page furniture. Do not call tools merely for the sake of calling them; decide directly when the supplied facts are sufficient. Batch your tool calls: you have at most 3 tool-calling rounds, after which you must answer. Each tool result carries an evidenceId such as E1.2 that you may cite.
        """;

    // Plan V2 tool-chain qualification arm (Issue #5 update 2026-10-09): evidence must be requested via tools first.
    private const string MandatoryToolClause = """

        EVIDENCE TOOLS AND REQUIREMENT. Read-only evidence tools over this document's parser evidence store are available: get_occurrence_context (neighbouring lines), get_page_geometry (line boxes and font summaries for a page), get_source_span (full evidence record of targets), get_repeated_occurrences (repeated text elsewhere in the document). Tools return observations only, never labels; they can confirm or contradict your first reading. In this request you must obtain source evidence before deciding: your first response must be a batch of tool calls, not the final JSON. Target the occurrences whose function is least certain from the supplied text alone, for example possible titles or subtitles, letterhead or issuer metadata, table or column labels, contents entries versus body headings, multi-part headings and repeated lines. You have at most 3 tool-calling rounds, after which you must answer. Each tool result carries an evidenceId such as E1.2; cite the evidenceIds in evidenceRefs for the decisions they support.
        """;
    private const string NoToolClause = """

        No evidence tools are available in this request. Use only the supplied occurrences and context.
        """;

    private const string Contract = """

        OUTPUT. Return exactly one JSON object and nothing else (no markdown fences), with this shape:
        {"protocolVersion":"P7_F1Q_V1","decisions":[{"occurrence":"O1","assessment":"SUPPORTED","function":"OTHER","observedRole":"...","evidenceRefs":["L0000:S0"],"interpretation":"...","missingEvidence":[]}]}
        Emit exactly one decision for every issued occurrence id, using only issued ids. Context-only lines may be cited but are never decision subjects.
        - assessment: SUPPORTED or INSUFFICIENT_EVIDENCE.
        - function: one of ESTABLISHES_STRUCTURE, REPRESENTS_STRUCTURE, OTHER when SUPPORTED; null when INSUFFICIENT_EVIDENCE.
        - observedRole: at most 200 characters describing what the occurrence is observed to be.
        - evidenceRefs: 1 to 8 strings, each a sourceAlias that appears in the supplied evidence or in a tool result, or an evidenceId returned by a tool. Never invent references.
        - interpretation: at most 300 characters, a short checkable semantic interpretation. Do not copy whole evidence records or geometry.
        - missingEvidence: [] when SUPPORTED; 1 to 6 short evidence kinds when INSUFFICIENT_EVIDENCE.
        The example values above are schema examples only, not evidence.
        """;

    public static string SystemPrompt(F1QArm arm) => arm switch
    {
        F1QArm.F1QTools => Canonical(Join(Question, ToolClause, Contract)),
        F1QArm.F1QNoTools => Canonical(Join(Question, NoToolClause, Contract)),
        F1QArm.F1QToolsMandatory => Canonical(Join(Question, MandatoryToolClause, Contract)),
        _ => throw new InvalidOperationException("F1Q_ARM_HAS_NO_F1Q_PROMPT"),
    };

    private static string Join(params string[] blocks) => string.Join("\n\n", blocks.Select(b => b.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n')));
    private static string Canonical(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Same selectable universe, source text, correspondences and context as the frozen Control,
    /// plus read-only source aliases. No Gold, labels, candidates or prior model decisions.</summary>
    public static string UserMessage(InterpretationRequest control, IReadOnlyList<(string Alias, int Page, string Text)> context)
    {
        using var controlUser = JsonDocument.Parse(control.ControlUserMessage);
        var occurrences = controlUser.RootElement.GetProperty("occurrences").EnumerateArray().ToArray();
        if (occurrences.Length != control.Owned.Count) throw new InvalidOperationException("F1Q_CONTROL_UNIVERSE_DRIFT");
        var rows = occurrences.Select((o, i) =>
        {
            var issued = control.Owned[i];
            if (o.GetProperty("id").GetString() != issued.Id || o.GetProperty("text").GetString() != issued.Atom.Text ||
                o.GetProperty("page").GetInt32() != issued.Atom.Page)
                throw new InvalidOperationException("F1Q_CONTROL_PROJECTION_DRIFT");
            return new
            {
                id = issued.Id, sourceAlias = issued.Atom.Alias, page = issued.Atom.Page, text = issued.Atom.Text,
                correspondences = o.GetProperty("correspondences").Clone(),
            };
        }).ToArray();
        var controlContext = controlUser.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray()
            .Select(c => (c.GetProperty("page").GetInt32(), c.GetProperty("text").GetString()!)).ToArray();
        if (!controlContext.SequenceEqual(context.Select(c => (c.Page, c.Text)))) throw new InvalidOperationException("F1Q_CONTROL_CONTEXT_DRIFT");
        return JsonSerializer.Serialize(new
        {
            protocolVersion = Version,
            issuedOccurrences = rows,
            contextOnlyEvidence = context.Select(c => new { sourceAlias = c.Alias, page = c.Page, text = c.Text }).ToArray(),
        }, P7F1QEvidenceTools.WireJson);
    }

    public static IReadOnlyList<(string Alias, int Page, string Text)> Context(DocumentSourceSnapshot source, SemanticEvidencePack pack)
    {
        var owned = pack.Owned.Select(a => a.SourceAlias).ToHashSet(StringComparer.Ordinal);
        var byAlias = source.Atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        return pack.Visible.Where(a => !owned.Contains(a.SourceAlias)).Select(a => (a.SourceAlias, byAlias[a.SourceAlias].Page, byAlias[a.SourceAlias].Text)).ToArray();
    }

    /// <summary>Strict contract plus per-row diagnostics. A failed strict response is never repaired; its
    /// individually valid rows are reported as diagnostic only, never as strict success.</summary>
    public static F1QValidation Validate(string response, IReadOnlyList<F1QIssuedOccurrence> issued,
        IReadOnlySet<string> citableRefs)
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
            var ids = issued.Select(i => i.Occurrence).ToHashSet(StringComparer.Ordinal);
            var rows = new List<F1QRowResult>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in decisions.EnumerateArray())
            {
                var row = Row(d, ids, citableRefs, unresolved);
                if (row.Valid && !seen.Add(row.Occurrence)) row = row with { Valid = false, FailureCode = "OCCURRENCE_DUPLICATE" };
                rows.Add(row);
            }
            string? failure = null;
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 2 || !names.Contains("protocolVersion") || !names.Contains("decisions")) failure = "ROOT_KEYS_INVALID";
            else if (root.GetProperty("protocolVersion").ValueKind != JsonValueKind.String || root.GetProperty("protocolVersion").GetString() != Version) failure = "PROTOCOL_VERSION_INVALID";
            else if (rows.FirstOrDefault(r => !r.Valid) is { } bad) failure = bad.FailureCode;
            else if (rows.Count != issued.Count || !ids.SetEquals(seen)) failure = "DECISION_CARDINALITY_INVALID";
            return new(failure is null, failure, rows, unresolved.ToArray());
        }
    }

    private static F1QRowResult Row(JsonElement d, HashSet<string> ids, IReadOnlySet<string> citable, SortedSet<string> unresolved)
    {
        var occurrence = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("occurrence", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "";
        F1QRowResult Fail(string code) => new(occurrence, false, code, null);
        if (d.ValueKind != JsonValueKind.Object) return Fail("DECISION_NOT_OBJECT");
        var names = d.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != DecisionKeys.Length || DecisionKeys.Any(k => !names.Contains(k, StringComparer.Ordinal))) return Fail("DECISION_KEYS_INVALID");
        if (!ids.Contains(occurrence)) return Fail("OCCURRENCE_NOT_ISSUED");
        var assessment = d.GetProperty("assessment").ValueKind == JsonValueKind.String ? d.GetProperty("assessment").GetString() : null;
        var fn = d.GetProperty("function");
        string? function = fn.ValueKind switch { JsonValueKind.String => fn.GetString(), JsonValueKind.Null => null, _ => "\u0000" };
        if (function == "\u0000") return Fail("FUNCTION_TYPE_INVALID");
        if (!TryStrings(d.GetProperty("evidenceRefs"), out var refs) || !TryStrings(d.GetProperty("missingEvidence"), out var missing))
            return Fail("ARRAY_FIELD_INVALID");
        var role = d.GetProperty("observedRole"); var interpretation = d.GetProperty("interpretation");
        if (role.ValueKind != JsonValueKind.String || role.GetString()!.Trim().Length == 0 || role.GetString()!.Length > MaxRole) return Fail("OBSERVED_ROLE_INVALID");
        if (interpretation.ValueKind != JsonValueKind.String || interpretation.GetString()!.Trim().Length == 0 || interpretation.GetString()!.Length > MaxInterpretation)
            return Fail("INTERPRETATION_INVALID");
        switch (assessment)
        {
            case "SUPPORTED":
                if (function is null || !Functions.Contains(function)) return Fail("SUPPORTED_FUNCTION_INVALID");
                if (missing.Count != 0) return Fail("SUPPORTED_WITH_MISSING_EVIDENCE");
                break;
            case "INSUFFICIENT_EVIDENCE":
                if (function is not null) return Fail("ABSTENTION_WITH_FUNCTION");
                if (missing.Count is < 1 or > MaxMissing || missing.Any(m => m.Trim().Length == 0 || m.Length > MaxMissingChars)) return Fail("ABSTENTION_MISSING_EVIDENCE_INVALID");
                break;
            default: return Fail("ASSESSMENT_INVALID");
        }
        if (refs.Count is < 1 or > MaxRefs) return Fail("EVIDENCE_REFS_CARDINALITY_INVALID");
        var bad = refs.Where(r => !citable.Contains(r)).ToArray();
        foreach (var b in bad) unresolved.Add(b);
        if (bad.Length > 0) return Fail("EVIDENCE_REF_UNRESOLVED");
        return new(occurrence, true, null, new(occurrence, assessment, function, role.GetString()!, refs, interpretation.GetString()!, missing));
    }

    private static bool TryStrings(JsonElement e, out IReadOnlyList<string> values)
    {
        values = [];
        if (e.ValueKind != JsonValueKind.Array || e.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) return false;
        values = e.EnumerateArray().Select(v => v.GetString()!).ToArray();
        return true;
    }

    /// <summary>Control contract (frozen production F1): strict parse via the production protocol.</summary>
    public static F1QValidation ValidateControl(string response, InterpretationRequest control)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var parsed = OccurrenceFunctionProtocolV1.Parse(doc.RootElement, Encoding.UTF8.GetByteCount(response), int.MaxValue, control.Owned);
            return new(true, null, parsed.Decisions.Select(d => new F1QRowResult(d.OccurrenceId, true, null,
                new(d.OccurrenceId, "SUPPORTED", d.Function switch
                {
                    OccurrenceFunction.EstablishesStructure => Functions[0],
                    OccurrenceFunction.RepresentsStructure => Functions[1],
                    _ => Functions[2],
                }, "", [], "", []))).ToArray(), []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return new(false, ex.Message, [], []); }
    }
}
