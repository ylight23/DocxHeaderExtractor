using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// P7-F1 V4 (Issue #7) exact binder. Semantics-free: it re-validates a frozen raw request against its signed scope, checks
/// identity (document, sourceAlias, issued O# ↔ alias), scope hash, evidence references (tool evidenceIds against the raw
/// tool transcript), and binds every model-selected heading member to the parser-authoritative page, text and bbox —
/// including semantically wrong selections. No E/R/O mapping, no heading filter, no Gold. Status:
/// ACCEPTED_COMPLETE, INCOMPLETE (model declared it), CONTRACT_FAILED, TRANSPORT_FAILED, BUDGET_STOP, MISSING.
/// Only ACCEPTED_COMPLETE requests expose non-selected issued occurrences as evaluable negatives.
/// </summary>
internal static class P7F1DirectBinder
{
    public const string Version = "P7_F1_DIRECT_EXACT_BINDER_V1";

    public sealed record SourceFact(int Page, string Text, JsonElement? Bbox);

    public static JsonObject Bind(JsonElement planRequest, string requestDir, Func<string, SourceFact> source)
    {
        var handle = planRequest.GetProperty("handle").GetString()!;
        var document = planRequest.GetProperty("document").GetString()!;
        var requestId = planRequest.GetProperty("requestId").GetString()!;
        var scope = planRequest.GetProperty("issuedSetSha256").GetString()!;
        var result = new JsonObject { ["version"] = Version, ["handle"] = handle, ["document"] = document, ["requestId"] = requestId };
        JsonObject Status(string s, string? code = null) { result["status"] = s; result["failureCode"] = code; return result; }
        // The plan carries page and text SHA-256 per issued alias; the text itself comes from the parser-authoritative source.
        var planned = planRequest.GetProperty("issued").EnumerateArray().Select(i => (Occurrence: i.GetProperty("occurrence").GetString()!,
            Alias: i.GetProperty("sourceAlias").GetString()!, Page: i.GetProperty("page").GetInt32(), TextSha: i.GetProperty("textSha256").GetString()!)).ToArray();
        if (planned.Select(i => i.Occurrence).Distinct().Count() != planned.Length || planned.Select(i => i.Alias).Distinct().Count() != planned.Length)
            return Status("CONTRACT_FAILED", "PLAN_SCOPE_DUPLICATE_ID");
        foreach (var i in planned)
        {
            var f = source(i.Alias);
            if (f.Page != i.Page || P7F1DirectProtocol.TextSha256(f.Text) != i.TextSha) return Status("CONTRACT_FAILED", "PLAN_SOURCE_IDENTITY_MISMATCH:" + i.Alias);
        }
        var issued = planned.Select(i => new P7F1DirectProtocol.Subject(i.Occurrence, i.Alias, i.Page, source(i.Alias).Text)).ToArray();
        // Scope integrity: the plan's signed scope must be the hash of exactly these issued subjects.
        if (P7F1DirectProtocol.IssuedSetSha256(requestId, document, issued) != scope) return Status("CONTRACT_FAILED", "PLAN_SCOPE_HASH_MISMATCH");
        var attempts = Directory.Exists(requestDir) ? Directory.GetDirectories(requestDir, "attempt-*").Order(StringComparer.Ordinal).ToArray() : [];
        if (attempts.Length == 0) return Status("MISSING", "NO_RAW_ATTEMPT");
        var diagnostics = new JsonArray();
        foreach (var a in attempts)
        {
            var receiptPath = Path.Combine(a, "request-receipt.json");
            if (!File.Exists(receiptPath)) { diagnostics.Add(new JsonObject { ["attempt"] = Path.GetFileName(a), ["status"] = "NO_RECEIPT" }); continue; }
            var receipt = JsonNode.Parse(File.ReadAllBytes(receiptPath))!;
            var status = receipt["status"]!.GetValue<string>();
            diagnostics.Add(new JsonObject { ["attempt"] = Path.GetFileName(a), ["status"] = status, ["failureCode"] = receipt["failureCode"]?.GetValue<string>() });
            if (status != "ACCEPTED") continue;
            // Re-validate from the frozen raw text (normalized view when the runner applied the single outer fence rule).
            var raw = File.ReadAllText(Path.Combine(a, "response.txt"));
            var norm = Path.Combine(a, "normalization.json");
            var text = raw;
            if (File.Exists(norm) && JsonNode.Parse(File.ReadAllBytes(norm))!["applied"]!.GetValue<bool>()) text = P7F1QFenceNormalization.Normalize(raw).Normalized;
            var citable = new HashSet<string>(issued.Select(i => i.SourceAlias), StringComparer.Ordinal);
            foreach (var c in planRequest.GetProperty("contextAliases").EnumerateArray()) citable.Add(c.GetString()!);
            var tools = ToolTranscript(a);
            foreach (var t in tools.Values) foreach (var r in t) citable.Add(r);
            var v = P7F1DirectProtocol.Validate(text, requestId, scope, issued.Select(i => new F1QIssuedOccurrence(i.Occurrence, i.SourceAlias, i.Page, i.Text)).ToArray(), citable);
            if (!v.StrictAccepted) return Bound(result, "CONTRACT_FAILED", "BINDER_REVALIDATION:" + v.FailureCode, a, diagnostics);
            var bySubject = issued.ToDictionary(i => i.Occurrence);
            var selected = new JsonArray(); var unresolved = new JsonArray();
            foreach (var row in v.Rows)
            {
                var s = bySubject[row.Occurrence]; var f = source(s.SourceAlias);
                if (row.Decision!.Assessment == "HEADING_MEMBER")
                {
                    var toolRefs = row.Decision.EvidenceRefs.Where(tools.ContainsKey).ToArray();
                    var unknownTool = row.Decision.EvidenceRefs.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r, @"^E\d+\.\d+$") && !tools.ContainsKey(r)).ToArray();
                    if (unknownTool.Length > 0) return Bound(result, "CONTRACT_FAILED", "EVIDENCE_ID_NOT_IN_TRANSCRIPT:" + unknownTool[0], a, diagnostics);
                    var aboutSelf = row.Decision.EvidenceRefs.Contains(s.SourceAlias) || toolRefs.Any(r => tools[r].Contains(s.SourceAlias));
                    if (!aboutSelf) return Bound(result, "CONTRACT_FAILED", "EVIDENCE_NOT_ABOUT_SUBJECT:" + s.Occurrence, a, diagnostics);
                    selected.Add(new JsonObject
                    {
                        ["occurrence"] = s.Occurrence, ["sourceAlias"] = s.SourceAlias, ["page"] = f.Page, ["text"] = f.Text,
                        ["bbox"] = f.Bbox is null ? null : JsonNode.Parse(f.Bbox.Value.GetRawText()),
                        ["evidenceRefs"] = new JsonArray(row.Decision.EvidenceRefs.Select(r => (JsonNode?)r).ToArray()),
                        ["justification"] = row.Decision.Interpretation,
                    });
                }
                else unresolved.Add(new JsonObject { ["occurrence"] = s.Occurrence, ["sourceAlias"] = s.SourceAlias, ["page"] = f.Page,
                    ["missingEvidence"] = new JsonArray(row.Decision.MissingEvidence.Select(m => (JsonNode?)m).ToArray()) });
            }
            var listed = v.Rows.Select(r => r.Occurrence).ToHashSet(StringComparer.Ordinal);
            result["selected"] = selected; result["unresolved"] = unresolved;
            result["negativeEligible"] = new JsonArray(issued.Where(i => !listed.Contains(i.Occurrence)).Select(i => (JsonNode?)i.SourceAlias).ToArray());
            result["omissionNote"] = "negatives are inferred from omission in a strictly validated COMPLETE response; this does not prove cognitive completeness";
            return Bound(result, "ACCEPTED_COMPLETE", null, a, diagnostics);
        }
        var last = diagnostics.LastOrDefault()?["status"]?.GetValue<string>();
        var code = diagnostics.LastOrDefault()?["failureCode"]?.GetValue<string>();
        var final = code == "MODEL_DECLARED_INCOMPLETE" ? "INCOMPLETE" : last is "TRANSPORT_FAILED" or "BUDGET_STOP" ? last : "CONTRACT_FAILED";
        result["attempts"] = diagnostics;
        return Status(final, code);
    }

    static JsonObject Bound(JsonObject result, string status, string? code, string attemptDir, JsonArray diagnostics)
    {
        result["status"] = status; result["failureCode"] = code; result["boundAttempt"] = Path.GetFileName(attemptDir); result["attempts"] = diagnostics;
        return result;
    }

    /// <summary>evidenceId → aliases its successful result returned, from the raw tool files of every turn.</summary>
    static Dictionary<string, HashSet<string>> ToolTranscript(string attemptDir)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(attemptDir, "tool-*.json", SearchOption.AllDirectories))
        {
            var t = JsonNode.Parse(File.ReadAllBytes(f))!;
            if (t["status"]?.GetValue<string>() != "OK") continue;
            map[t["evidenceId"]!.GetValue<string>()] = t["returnedAliases"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        }
        return map;
    }
}
