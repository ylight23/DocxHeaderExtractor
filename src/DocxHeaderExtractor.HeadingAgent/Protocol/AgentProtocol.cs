using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.HeadingAgent.Formats;

namespace DocxHeaderExtractor.HeadingAgent.Protocol;

/// <summary>
/// The wire contract of the D4.12 method (ported from the qualified V6 agent, arm TOOLFACTS_ANALYSIS_V3): the system prompt, the user
/// instruction, the unit payload, the request envelope, the reading of a response, the strict answer parser, the single-fence unwrap and the
/// repair messages. Every string here is the qualified one; the parity tests hold prompt, payload and request bytes to the qualified SHA-256s.
/// </summary>
internal static partial class AgentProtocol
{
    public const string ChatModel = OpenRouterWire.ChatModel;
    public const string ProviderTag = OpenRouterWire.ProviderTag;
    public static readonly string[] Ignore = OpenRouterWire.Ignore;
    public const int MaxTokens = OpenRouterWire.MaxTokens;
    public const int MaxToolTurns = 3, MaxCallsPerTurn = 8, MaxPostsPerUnit = MaxToolTurns + 1;
    /// <summary>Hard cap on the content bytes of every tool message of one unit (see the qualified agent for the reserve rule).</summary>
    public const int MaxToolMessageBytesPerUnit = 65536;
    public const int VisibleResponseCapBytes = 16384;
    public const string SelectObjectsVersion = "P7_F1_V6_SELECT_OBJECTS_V1";
    /// <summary>The answer format of a flavor that lets a SELECT carry headingExtent: the exact part of the target's text that is the heading
    /// (see <see cref="DocxAgentProtocol"/> and <see cref="PdfAgentProtocol"/>). The legacy objects protocol remains available for historical comparisons.</summary>
    public const string SelectExtentVersion = "P7_F1_V6_SELECT_OBJECTS_EXTENT_V1";
    /// <summary>The answer format of a scanned source: a SELECT may also carry readingViewId, the reading of the line it takes (one of the readings
    /// the evidence holds for that line), and its headingExtent is measured on that reading.</summary>
    public const string SelectScanVersion = "P7_F1_V6_SELECT_OBJECTS_SCAN_V1";
    public const int MaxEvidenceRefs = 8, MaxInterpretations = 5, MaxFreeTextChars = 1000, MaxReasonChars = 300, MaxMissingNamed = 40;

    public static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------------- requests ----------------

    /// <summary>The user message of one unit: the instruction, the document hash, the targets and context (alias, page, text), the target range
    /// and the document style table.</summary>
    public static string Payload(string documentSha256, string unitId, IReadOnlyList<string> decide, IReadOnlyList<AgentSourceLine> shownLines, JsonObject documentStyles,
        string instruction = Instruction, IAgentFormat? flavor = null, JsonArray? targetFacts = null)
    {
        flavor ??= PdfFormat.Instance;
        var set = decide.ToHashSet(StringComparer.Ordinal);
        var shown = shownLines.Select(l => l.Alias).ToList();
        if (shown.Distinct(StringComparer.Ordinal).Count() != shown.Count) throw new ArgumentException("SHOWN_ALIAS_REPEATED", nameof(shownLines));
        if (decide.Count == 0 || set.Count != decide.Count || decide.Any(a => !shown.Contains(a, StringComparer.Ordinal))) throw new ArgumentException("DECIDE_NOT_SHOWN", nameof(decide));
        object Item(AgentSourceLine l) => flavor.PayloadItem(l);
        var targets = shownLines.Where(l => set.Contains(l.Alias)).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["instruction"] = instruction,
            ["instructionSha256"] = Sha(instruction),
            ["documentSha256"] = documentSha256,
            ["stage"] = "SEGMENT", ["unitId"] = unitId,
            ["targetCount"] = decide.Count,
            ["targetFirstAlias"] = targets[0].Alias,
            ["targetLastAlias"] = targets[^1].Alias,
            ["targets"] = targets.Select(Item).ToList(),
            ["context"] = shownLines.Where(l => !set.Contains(l.Alias)).Select(Item).ToList(),
            ["documentStyles"] = documentStyles,
        };
        if (targetFacts is not null) payload["targetFacts"] = targetFacts;
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>One POST body: model, messages (system prompt and payload as cache-marked text blocks, then the history), json_object, temperature
    /// 0, max_tokens, reasoning enabled, the eight tools, this turn's tool_choice, the locked provider block, streaming and usage.</summary>
    public static PreparedRequest Build(string payload, JsonArray history, string toolChoice, IAgentFormat? flavor = null)
    {
        flavor ??= PdfFormat.Instance;
        return OpenRouterWire.Build(flavor.SystemPrompt, payload, history, toolChoice, flavor.ToolDefinitions());
    }

    // ---------------- replies ----------------

    /// <summary>Reads choices[0].message of a chat response: content and tool_calls, as given. Never repairs anything.</summary>
    public static (ModelTurn? Turn, string? Failure) ReadResponse(string responseJson) => OpenRouterWire.ReadResponse(responseJson);

    /// <summary>A final answer wrapped in exactly one markdown fence (optional json label) around exactly one JSON object is read from inside the
    /// fence (P7_F1_V6_SINGLE_FENCE_UNWRAP_V1); anything else is left as it is.</summary>
    public static bool TryUnwrapFence(string reply, out string inner)
    {
        inner = "";
        var t = reply.Trim();
        const string fence = "```";
        if (!t.StartsWith(fence, StringComparison.Ordinal) || !t.EndsWith(fence, StringComparison.Ordinal) || t.Length < 2 * fence.Length + 2) return false;
        var firstLineEnd = t.IndexOf('\n');
        if (firstLineEnd < 0) return false;
        var label = t[fence.Length..firstLineEnd].Trim();
        if (label.Length != 0 && label != "json") return false;
        var body = t[(firstLineEnd + 1)..^fence.Length];
        if (body.Contains(fence, StringComparison.Ordinal)) return false;
        body = body.Trim();
        if (body.Length < 2 || body[0] != '{' || body[^1] != '}') return false;
        inner = body;
        return true;
    }

    // ---------------- the answer parser ----------------

    public sealed record Clarification(string Question, IReadOnlyList<string> Interpretations);

    /// <param name="Extents">The headingExtent of each SELECT that gave one, checked against the target's text; empty when the flavor has none.</param>
    /// <param name="Detail">For a refused headingExtent: what the program measured (the text at the offsets given, where the text given occurs).</param>
    public sealed record SelectReply(bool Ok, string? Failure, string? Status, IReadOnlyDictionary<string, string> Values, Clarification? Clarification,
        string? IncompleteReason, IReadOnlyList<string> Missing, IReadOnlyDictionary<string, IReadOnlyList<string>>? Refs = null, string? Analysis = null,
        IReadOnlyDictionary<string, string>? Reasons = null, IReadOnlyDictionary<string, HeadingExtent>? Extents = null, string? Detail = null)
    {
        /// <summary>The readingViewId of each SELECT that gave one (scanned sources only).</summary>
        public IReadOnlyDictionary<string, string> ReadingChoices { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>A heading's extent inside its line: [Start, End) in UTF-16 code units of the line's text, and that exact text.</summary>
    public sealed record HeadingExtent(int Start, int End, string Text);

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Has(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);

    static List<string>? ReadRefs(JsonElement arr, IReadOnlyDictionary<string, int> shownOrder, int min, out string? failure)
    {
        failure = null;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < min || arr.GetArrayLength() > MaxEvidenceRefs)
        { failure = "EVIDENCE_REFS_INVALID"; return null; }
        var refs = new List<string>();
        foreach (var r in arr.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.String || !shownOrder.ContainsKey(r.GetString()!)) { failure = "EVIDENCE_NOT_SHOWN"; return null; }
            refs.Add(r.GetString()!);
        }
        return refs;
    }

    /// <summary>The first key that appears more than once in any object of <paramref name="e"/>, with its count; null when every key is unique.</summary>
    internal static (string Name, int Count)? RepeatedKey(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var g in e.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal))
                    if (g.Count() > 1) return (g.Key, g.Count());
                foreach (var p in e.EnumerateObject())
                    if (RepeatedKey(p.Value) is { } inner) return inner;
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (RepeatedKey(item) is { } inner) return inner;
                return null;
            default:
                return null;
        }
    }

    /// <summary>The strict parser of a final answer (objects format, agent mode, analysis and reasons allowed). A missing or extra alias is
    /// reported, never filled in; a decision is never changed. shownOrder holds every alias the model saw.</summary>
    /// <param name="version">The protocolVersion the answer must carry: the flavor's answer format.</param>
    /// <param name="targetTexts">The text of every target, when the flavor lets a SELECT carry headingExtent; null refuses that key, as before.
    /// An extent is only checked, never moved: its offsets must lie inside the target's text on word boundaries, and its text must be exactly the
    /// text at those offsets.</param>
    public static SelectReply ParseSelect(string reply, IReadOnlyDictionary<string, int> shownOrder, IReadOnlyList<string> decide,
        string version = SelectObjectsVersion, IReadOnlyDictionary<string, string>? targetTexts = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? readings = null)
    {
        SelectReply Fail(string code, IReadOnlyList<string>? missing = null, string? detail = null) =>
            new(false, code, null, new Dictionary<string, string>(), null, null, missing ?? Array.Empty<string>(), Detail: detail);
        if (Encoding.UTF8.GetByteCount(reply) > VisibleResponseCapBytes) return Fail("REPLY_OVER_CAP");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(reply); } catch (JsonException) { return Fail("REPLY_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("ROOT_NOT_OBJECT");
            // A repeated key would be read only at its last value (JsonDocument), so the other decisions would look missing.
            if (RepeatedKey(root) is { } repeated)
                return Fail("KEY_REPEATED", detail: "the key \"" + repeated.Name + "\" appears " + repeated.Count + " times in one object; give it once");
            var decisionKeys = new[] { "decisions" };
            var allowed = decisionKeys.Concat(new[] { "protocolVersion", "status", "retrieve", "clarification", "incompleteReason", "analysis" }).ToHashSet();
            if (root.EnumerateObject().Any(p => !allowed.Contains(p.Name))) return Fail("ROOT_KEYS_INVALID");
            string? analysis = null;
            if (root.TryGetProperty("analysis", out var an))
            {
                if (an.ValueKind != JsonValueKind.String) return Fail("ANALYSIS_INVALID");
                analysis = an.GetString();
            }
            if (Str(root, "protocolVersion") != version) return Fail("PROTOCOL_VERSION_INVALID");
            var status = Str(root, "status");
            if (status is not ("COMPLETE" or "NEEDS_EVIDENCE" or "CLARIFICATION_REQUIRED" or "INCOMPLETE")) return Fail("STATUS_INVALID");
            if (status == "NEEDS_EVIDENCE") return Fail("NEEDS_EVIDENCE_NOT_IN_AGENT_MODE");
            if (status == "CLARIFICATION_REQUIRED")
            {
                if (decisionKeys.Any(k => Has(root, k)) || Has(root, "retrieve") || Has(root, "incompleteReason")) return Fail("CLARIFICATION_WITH_OTHER_FIELDS");
                if (!root.TryGetProperty("clarification", out var c) || c.ValueKind != JsonValueKind.Object
                    || c.EnumerateObject().Any(p => p.Name is not ("question" or "interpretations"))) return Fail("CLARIFICATION_INVALID");
                var q = Str(c, "question");
                if (string.IsNullOrWhiteSpace(q) || q.Length > MaxFreeTextChars) return Fail("CLARIFICATION_INVALID");
                if (!c.TryGetProperty("interpretations", out var ints) || ints.ValueKind != JsonValueKind.Array
                    || ints.GetArrayLength() is < 2 or > MaxInterpretations) return Fail("CLARIFICATION_INTERPRETATIONS_INVALID");
                var list = new List<string>();
                foreach (var i in ints.EnumerateArray())
                {
                    if (i.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(i.GetString()) || i.GetString()!.Length > MaxFreeTextChars)
                        return Fail("CLARIFICATION_INTERPRETATIONS_INVALID");
                    list.Add(i.GetString()!);
                }
                return new SelectReply(true, null, status, new Dictionary<string, string>(), new Clarification(q!, list), null, Array.Empty<string>(), null, analysis);
            }

            if (Has(root, "retrieve") || Has(root, "clarification")) return Fail("DECISION_REPLY_WITH_OTHER_FIELDS");
            string? reason = null;
            if (status == "INCOMPLETE")
            {
                reason = Str(root, "incompleteReason");
                if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxFreeTextChars) return Fail("INCOMPLETE_REASON_MISSING");
            }
            else if (Has(root, "incompleteReason")) return Fail("INCOMPLETE_REASON_NOT_ALLOWED");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var refsByAlias = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            var extents = new Dictionary<string, HeadingExtent>(StringComparer.Ordinal);
            var readingChoices = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("decisions", out var ds) || ds.ValueKind != JsonValueKind.Array) return Fail("DECISIONS_MISSING");
            var decideSet = decide.ToHashSet(StringComparer.Ordinal);
            foreach (var d in ds.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object || d.EnumerateObject().Any(p => p.Name is not ("alias" or "decision" or "evidenceRefs" or "reason")
                        && !(p.Name == "headingExtent" && targetTexts is not null) && !(p.Name == "readingViewId" && readings is not null)))
                    return Fail("DECISION_KEYS_INVALID");
                var alias = Str(d, "alias");
                if (alias is null) return Fail("ALIAS_MISSING");
                var value = Str(d, "decision");
                if (value is not ("SELECT" or "NOT_SELECT" or "UNRESOLVED")) return Fail("DECISION_VALUE_INVALID");
                if (value == "UNRESOLVED" && status != "INCOMPLETE") return Fail("UNRESOLVED_ONLY_IN_INCOMPLETE");
                var hasRefs = d.TryGetProperty("evidenceRefs", out var er);
                if (!hasRefs && value == "SELECT") return Fail("EVIDENCE_REFS_INVALID");
                List<string>? refs = null;
                if (hasRefs && (refs = ReadRefs(er, shownOrder, value == "SELECT" ? 1 : 0, out var rf)) is null) return Fail(rf!);
                if (!decideSet.Contains(alias)) return Fail("UNEXPECTED_ALIAS");
                if (d.TryGetProperty("reason", out var rs))
                {
                    if (rs.ValueKind != JsonValueKind.String || rs.GetString()!.Length > MaxReasonChars) return Fail("REASON_INVALID");
                    reasons[alias] = rs.GetString()!;
                }
                string? readingView = null;
                if (d.TryGetProperty("readingViewId", out var rv))
                {
                    if (value != "SELECT") return Fail("READING_NOT_ON_SELECT");
                    var available = readings!.TryGetValue(alias, out var r) ? r : new Dictionary<string, string>();
                    if (rv.ValueKind != JsonValueKind.String || !available.ContainsKey(rv.GetString()!))
                        return Fail("READING_VIEW_UNKNOWN", detail: alias + ": readingViewId must be one of " + string.Join(", ", available.Keys));
                    readingView = rv.GetString()!;
                    readingChoices[alias] = readingView;
                }
                if (d.TryGetProperty("headingExtent", out var he))
                {
                    if (value != "SELECT") return Fail("HEADING_EXTENT_NOT_ON_SELECT");
                    string? targetText = null;
                    if (readingView is not null) targetText = readings![alias][readingView];
                    else if (!targetTexts!.TryGetValue(alias, out targetText)) return Fail("UNEXPECTED_ALIAS");
                    var (extent, code, detail) = ReadExtent(he, targetText);
                    if (extent is null) return Fail(code!, detail: alias + ": " + detail);
                    extents[alias] = extent;
                }
                if (!values.TryAdd(alias, value!)) return Fail("ALIAS_LISTED_TWICE");
                refsByAlias[alias] = (IReadOnlyList<string>?)refs ?? Array.Empty<string>();
            }
            var missing = decide.Where(a => !values.ContainsKey(a)).ToArray();
            if (missing.Length > 0) return Fail("COVERAGE_INCOMPLETE", missing);
            if (status == "INCOMPLETE" && values.Values.All(v => v != "UNRESOLVED")) return Fail("INCOMPLETE_WITHOUT_UNRESOLVED");
            return new SelectReply(true, null, status, values, null, reason, Array.Empty<string>(), refsByAlias, analysis, reasons, extents) { ReadingChoices = readingChoices };
        }
    }

    /// <summary>Checks one headingExtent against its target's text. The program measures, it does not choose: a refusal says what lies at the
    /// offsets given and where the text given occurs in the target, and the model sends its own extent again.</summary>
    static (HeadingExtent? Extent, string? Code, string? Detail) ReadExtent(JsonElement e, string text)
    {
        if (e.ValueKind != JsonValueKind.Object || e.EnumerateObject().Any(p => p.Name is not ("start" or "end" or "text"))
            || !e.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt32(out var start)
            || !e.TryGetProperty("end", out var en) || en.ValueKind != JsonValueKind.Number || !en.TryGetInt32(out var end)
            || Str(e, "text") is not { } given)
            return (null, "HEADING_EXTENT_INVALID", "headingExtent must be an object with exactly start (integer), end (integer) and text (string)");
        string Where()
        {
            var at = new List<string>();
            for (var i = given.Length == 0 ? -1 : text.IndexOf(given, StringComparison.Ordinal); i >= 0 && at.Count < 3; i = text.IndexOf(given, i + 1, StringComparison.Ordinal))
                at.Add("[" + i + "," + (i + given.Length) + ")");
            return at.Count == 0 ? "the text you gave does not occur in the target's text" : "the text you gave occurs in the target's text at " + string.Join(", ", at);
        }
        if (start < 0 || end <= start || end > text.Length)
            return (null, "HEADING_EXTENT_OUT_OF_RANGE", "the target's text has " + text.Length + " characters, so 0 <= start < end <= " + text.Length + " is required; " + Where());
        var found = text[start..end];
        if (found != given)
            return (null, "HEADING_EXTENT_TEXT_MISMATCH", "the text at [" + start + "," + end + ") is " + JsonSerializer.Serialize(found, Options) + "; " + Where());
        if (given.Trim().Length != given.Length)
            return (null, "HEADING_EXTENT_EDGE_WHITESPACE", "the extent must not start or end with a space or a line break");
        var boundaries = DocxHeaderExtractor.Sources.Aliases.TextBoundaryMap.For(text);
        if (!boundaries.Contains(start) || !boundaries.Contains(end))
            return (null, "HEADING_EXTENT_NOT_ON_BOUNDARY", "start and end must not cut a word or a number");
        return (new HeadingExtent(start, end, given), null, null);
    }
}
