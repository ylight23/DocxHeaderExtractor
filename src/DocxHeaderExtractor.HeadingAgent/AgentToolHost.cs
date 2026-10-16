using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxHeaderExtractor.HeadingAgent;

/// <summary>
/// The machinery every set of evidence tools shares (extracted from the qualified PDF tools, whose parity tests still hold every result to the
/// qualified bytes): strict argument checks, one exact JSON body per call of at most <see cref="MaxResultBytes"/> bytes that says when it was cut,
/// a ledger of every call with the aliases its result showed, a per-conversation cache of successful calls, and the rejection of calls the loop
/// refuses before dispatch. A kind of document supplies its tools by implementing <see cref="Dispatch"/>.
/// </summary>
internal abstract class AgentToolHost : IAgentTools
{
    public const int MaxResultBytes = 16384, MaxArgumentBytes = 2048, MaxAliasChars = 64;
    public const int ResultMargin = 192;   // room, inside the result cap, for a longer evidence id and the cache note of a repeated call

    /// <summary>One call as the ledger keeps it. RawArgumentsSha256 is over the argument string exactly as Qwen sent it.</summary>
    /// <param name="CacheOf">The evidence id of the earlier call whose result this call repeated, or null (D4.11f).</param>
    public sealed record CallRecord(string EvidenceId, string Tool, string RawArgumentsSha256, string Status, string? ErrorCode,
        string ContentSha256, int ContentBytes, bool Truncated, IReadOnlyList<string> ReturnedAliases, string? CacheOf = null);

    /// <summary>Content is the exact body that goes back to Qwen as the tool message.</summary>
    public sealed record Result(string EvidenceId, string Tool, string Status, string? ErrorCode, byte[] Content, string ContentSha256,
        bool Truncated, IReadOnlyList<string> ReturnedAliases);

    protected static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Codes of a call refused before dispatch. Every one is answered under the call's own id.</summary>
    public static readonly string[] RejectionCodes = ["PER_TURN_CALL_CAP_EXCEEDED", "UNIT_TOOL_MESSAGE_BYTES_EXHAUSTED"];

    readonly IAgentSource source;
    protected readonly IReadOnlyList<AgentSourceLine> lines;
    protected readonly Dictionary<string, int> index = new(StringComparer.Ordinal);
    readonly List<CallRecord> ledger = new();
    readonly HashSet<string> observed = new(StringComparer.Ordinal);
    // The coverage ledger: the lines whose own facts a successful, dispatched facts call returned, and how many such results were cut. A
    // handler notes its lines in pending; they count only once the call has succeeded.
    readonly HashSet<string> factsFor = new(StringComparer.Ordinal);
    int factsCut;
    readonly List<string> pendingFacts = new();
    bool pendingCut;
    // Result cache (D4.11f). The tools read a fixed source, so the same call gives the same result. Only successful calls are kept, keyed by
    // the tool and its arguments in canonical form (keys sorted); a repeat returns the stored result, byte for byte but for the evidence id,
    // and says so. The cache lives as long as this registry, i.e. one unit's conversation; nothing is shared between units or arms.
    sealed record Cached(string EvidenceId, JsonNode? Arguments, JsonNode? Result, bool Truncated, string[] Returned);
    readonly Dictionary<string, Cached> cache = new(StringComparer.Ordinal);

    protected AgentToolHost(IAgentSource source)
    {
        this.source = source;
        lines = source.Lines;
        for (int i = 0; i < lines.Count; i++) index[lines[i].Alias] = i;
    }

    // ---------------- what a kind of document supplies ----------------

    /// <summary>The version the tool results carry.</summary>
    protected abstract string ToolsVersion { get; }
    /// <summary>The tools this set declares, the facts tool included.</summary>
    protected abstract bool IsRegistered(string? name);
    /// <summary>The tool the first answer must follow.</summary>
    protected abstract string FactsToolName { get; }
    /// <summary>Runs the handler the name selects. Returns the arguments as recorded, the result, and whether the result was cut.</summary>
    protected abstract (JsonNode?, JsonNode?, bool) Dispatch(string name, JsonElement args, List<string> returned);
    public abstract IReadOnlyList<string> ReservedNames { get; }
    public abstract JsonObject DocumentStyles();

    // ---------------- the shared surface ----------------

    public IReadOnlyList<CallRecord> Ledger => ledger;
    /// <summary>Every alias a successful tool result showed, in any part of the result.</summary>
    public IReadOnlySet<string> ObservedAliases => observed;
    public int CacheHits => ledger.Count(c => c.CacheOf is not null);
    public int DocumentLines => lines.Count;
    public int PositionOf(string alias) => index[alias];
    public bool FactsWereRead => requestFacts || ledger.Any(c => c.Tool == FactsToolName && c.Status == "OK");
    public IReadOnlySet<string> FactsReturnedFor => factsFor;
    public int FactsResultsCut => factsCut;
    public IReadOnlySet<string> DetailEvidenceAliases => ledger.Where(c => c.Status == "OK" && c.Tool != FactsToolName)
        .SelectMany(c => c.ReturnedAliases).ToHashSet(StringComparer.Ordinal);

    /// <summary>A facts handler names the lines whose own facts its result carries; they enter the coverage ledger once the call succeeds.</summary>
    protected void NoteFactsReturned(IEnumerable<string> aliases, bool cut)
    {
        pendingFacts.AddRange(aliases);
        pendingCut |= cut;
    }
    int IAgentTools.MaxResultBytes => MaxResultBytes;

    bool requestFacts;

    public void NoteRequestFacts(IEnumerable<string> aliases, IEnumerable<string> shown)
    {
        requestFacts = true;
        factsFor.UnionWith(aliases);
        observed.UnionWith(shown);
    }

    /// <summary>The name a result and the ledger carry: a registered name, or null. A name Qwen made up is never echoed back, so a result's
    /// size does not depend on it (D4.11b).</summary>
    string? Registered(string? name) => IsRegistered(name) ? name : null;

    static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        _ => e.GetRawText(),
    };

    // ---------------- declarations ----------------

    protected static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["additionalProperties"] = false,
            },
        },
    };

    protected static JsonObject AliasArg(string description) => new() { ["type"] = "string", ["maxLength"] = MaxAliasChars, ["description"] = description + " An alias from the request or from a tool result." };
    protected static JsonObject Int(int min, int max, string description) => new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["description"] = description };

    // ---------------- dispatch ----------------

    protected sealed class ArgumentError(string code) : Exception(code) { public string Code { get; } = code; }

    /// <summary>Validates the arguments, runs the one handler the name selects and records the call. Never throws for anything Qwen sends.</summary>
    public Result Execute(string evidenceId, string? name, string? rawArguments)
    {
        var raw = rawArguments ?? "";
        var returned = new List<string>();
        pendingFacts.Clear(); pendingCut = false;
        JsonNode? arguments = null, result = null;
        string status = "OK"; string? error = null; var truncated = false; string? cacheOf = null; string? key = null;
        try
        {
            if (!IsRegistered(name)) throw new ArgumentError("UNKNOWN_TOOL");
            using var doc = ParseArguments(raw);
            var args = doc.RootElement;
            key = name + "\n" + Canonical(args);
            if (cache.TryGetValue(key, out var hit))
            {
                (arguments, result, truncated, cacheOf) = (hit.Arguments, hit.Result, hit.Truncated, hit.EvidenceId);
                returned.AddRange(hit.Returned);
            }
            else
                (arguments, result, truncated) = Dispatch(name!, args, returned);
        }
        catch (ArgumentError e) { status = !IsRegistered(name) ? "UNKNOWN_TOOL" : "INVALID_ARGUMENT"; error = e.Code; result = null; returned.Clear(); truncated = false; cacheOf = null; key = null; }
        var content = Body(evidenceId, Registered(name), status, error, arguments, result, cacheOf);
        if (content.Length > MaxResultBytes) throw new InvalidOperationException("V6_TOOL_RESULT_CAP_EXCEEDED");   // the handlers fit before this point
        var aliases = returned.Distinct(StringComparer.Ordinal).ToArray();
        if (status == "OK") foreach (var a in aliases) observed.Add(a);
        // A repeated call returns facts already in the ledger, so only a dispatched one adds to it.
        if (status == "OK" && cacheOf is null) { factsFor.UnionWith(pendingFacts); if (pendingCut) factsCut++; }
        pendingFacts.Clear(); pendingCut = false;
        if (status == "OK" && key is not null && cacheOf is null) cache[key] = new Cached(evidenceId, arguments, result, truncated, aliases);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        ledger.Add(new CallRecord(evidenceId, Registered(name) ?? "UNREGISTERED", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), status, error,
            sha, content.Length, truncated, aliases, cacheOf));
        return new Result(evidenceId, Registered(name) ?? "UNREGISTERED", status, error, content, sha, truncated, aliases);
    }

    /// <summary>A call the loop refuses before dispatch (over the per-turn cap). It is answered and recorded like any other call.</summary>
    public Result Reject(string evidenceId, string? name, string? rawArguments, string code)
    {
        if (!RejectionCodes.Contains(code, StringComparer.Ordinal)) throw new ArgumentException("REJECTION_CODE_UNKNOWN", nameof(code));
        var content = Body(evidenceId, Registered(name), "REJECTED", code, null, null);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        ledger.Add(new CallRecord(evidenceId, Registered(name) ?? "UNREGISTERED", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawArguments ?? ""))),
            "REJECTED", code, sha, content.Length, false, Array.Empty<string>()));
        return new Result(evidenceId, Registered(name) ?? "UNREGISTERED", "REJECTED", code, content, sha, false, Array.Empty<string>());
    }

    /// <summary>The exact size of the largest rejection this call could receive. Used to reserve room before any call of a turn runs.</summary>
    public int RejectionBytes(string evidenceId, string? name) => RejectionCodes.Max(c => Body(evidenceId, Registered(name), "REJECTED", c, null, null).Length);

    protected byte[] Body(string evidenceId, string? name, string status, string? error, JsonNode? arguments, JsonNode? result, string? cacheOf = null)
    {
        var body = new JsonObject
        {
            ["evidenceId"] = evidenceId, ["tool"] = name, ["version"] = ToolsVersion, ["status"] = status, ["errorCode"] = error,
            ["arguments"] = arguments?.DeepClone(), ["result"] = result?.DeepClone(),
            ["provenance"] = new JsonObject
            {
                ["sourceSha256"] = source.SourceSha256, ["sourceAliasUniverseSha256"] = source.SourceAliasUniverseSha256,
                ["evidenceStoreSha256"] = source.EvidenceStoreSha256, ["readOnly"] = true, ["semanticLabels"] = "NONE",
            },
        };
        // Only a repeated call carries this note, so every other result keeps its bytes. The result above is the stored one.
        if (cacheOf is not null) body["cache"] = new JsonObject { ["hit"] = true, ["sameResultAs"] = cacheOf };
        return Encoding.UTF8.GetBytes(body.ToJsonString(Wire));
    }

    /// <summary>The largest prefix of the items whose exact body fits the cap. build(k) makes the result with the first k items.</summary>
    protected (JsonNode Result, int Taken) Fit(string tool, JsonNode? arguments, int available, Func<int, JsonObject> build)
    {
        for (int k = available; k >= 0; k--)
        {
            var r = build(k);
            if (Body("E0.00", tool, "OK", null, arguments, r).Length + ResultMargin <= MaxResultBytes) return (r, k);   // the margin leaves room for a longer evidence id and the cache note
        }
        throw new InvalidOperationException("V6_TOOL_EMPTY_RESULT_OVER_CAP");
    }

    protected static JsonObject Truncation(int returned, int available, int? nextOffset) => new()
    {
        ["truncated"] = returned < available, ["returned"] = returned, ["available"] = available, ["nextOffset"] = returned < available ? nextOffset : null,
    };

    protected static JsonNode? Node(object? o) => o is null ? null : JsonSerializer.SerializeToNode(o, Wire);
    protected static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    // ---------------- argument checks ----------------

    static JsonDocument ParseArguments(string raw)
    {
        // A tool without parameters may be called with an empty argument string; it means {}.
        if (string.IsNullOrWhiteSpace(raw)) return JsonDocument.Parse("{}");
        if (Encoding.UTF8.GetByteCount(raw) > MaxArgumentBytes) throw new ArgumentError("ARGUMENTS_TOO_LARGE");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { throw new ArgumentError("ARGUMENTS_NOT_JSON"); }
        if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw new ArgumentError("ARGUMENTS_NOT_OBJECT"); }
        return doc;
    }

    protected static void Keys(JsonElement args, params string[] required)
    {
        var names = args.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != names.Distinct(StringComparer.Ordinal).Count() || names.Any(n => !required.Contains(n, StringComparer.Ordinal)))
            throw new ArgumentError("ARGUMENT_KEYS_UNKNOWN_OR_DUPLICATE");
        if (required.Any(r => !names.Contains(r, StringComparer.Ordinal))) throw new ArgumentError("ARGUMENT_REQUIRED_MISSING");
    }

    protected static string Str(JsonElement args, string key) =>
        args.GetProperty(key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new ArgumentError("ARGUMENT_TYPE_INVALID");

    protected string Alias(JsonElement args, string key)
    {
        var s = Str(args, key);
        if (s.Length > MaxAliasChars || !index.ContainsKey(s)) throw new ArgumentError("ALIAS_NOT_IN_DOCUMENT");
        return s;
    }

    protected static int Range(JsonElement args, string key, int min, int max)
    {
        var v = args.GetProperty(key);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) throw new ArgumentError("ARGUMENT_TYPE_INVALID");
        if (n < min || n > max) throw new ArgumentError("ARGUMENT_OUT_OF_RANGE");
        return n;
    }
}
