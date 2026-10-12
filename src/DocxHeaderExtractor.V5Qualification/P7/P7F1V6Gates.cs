using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>The source snapshot used for any send: the bytes that were hashed are the bytes that are returned, read once under a share lock.</summary>
internal sealed record P7F1V6SourceSnapshot(bool Allowed, string Code, byte[]? Bytes, string? Sha256);

/// <summary>Source gate (D3.5a). A document is allowed only if: the relative path is under the corpus prefix; every directory from the
/// repository root to the file exists and is not a reparse point (symbolic link or junction); the file is a regular file, not a reparse
/// point; its bytes hash to the pinned SHA-256. The file is opened with a share lock that denies writers, read once, and the returned
/// bytes are the only bytes a caller may use. Public status is not established by code: it is a fact the corpus owner records.</summary>
internal static class P7F1V6SourceGate
{
    public static P7F1V6SourceSnapshot Snapshot(string repoRoot, string relativePath, string pinnedSha256)
    {
        if (!P7F1V6DataPolicy.IsAllowedSource(relativePath)) return new(false, "SOURCE_NOT_IN_ALLOWED_PREFIX", null, null);
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return new(false, "SOURCE_OUTSIDE_ROOT", null, null);

        // Every segment from the root down to the file must be a real directory or the file itself, never a reparse point.
        var cursor = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, segment);
            if (!File.Exists(cursor) && !Directory.Exists(cursor)) return new(false, "SOURCE_MISSING", null, null);
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return new(false, "SOURCE_PATH_HAS_REPARSE_POINT", null, null);
        }
        if (!File.Exists(full)) return new(false, "SOURCE_MISSING", null, null);

        byte[] bytes;
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return sha == pinnedSha256 ? new(true, "OK", bytes, sha) : new(false, "SOURCE_HASH_NOT_PINNED", null, sha);
    }

    /// <summary>Compatibility form used by earlier tests: the decision only.</summary>
    public static (bool Allowed, string Code) Check(string repoRoot, string relativePath, string pinnedSha256)
    {
        var s = Snapshot(repoRoot, relativePath, pinnedSha256);
        return (s.Allowed, s.Code);
    }
}

/// <summary>Retention for raw runs (D3.5a). Each run carries its own review record, run.review.json, written by the review step. The
/// seven-day clock starts from that run's review date, never from another run's. Deletion accepts only a run directory that is a direct
/// child of the fixed raw root, named run-&lt;32 hex&gt;, and holds only the store's files: 64-hex .bin items, .tmp files, and the review record.</summary>
internal static class P7F1V6Retention
{
    public const string ReviewFile = "run.review.json";
    /// <summary>The D4.5 run summary (D4.5a). Owned only when it is a JSON object whose artifact is the D4.5 summary and it is not oversized.</summary>
    public const string SummaryFile = "run-summary.json";
    public const string SummaryArtifact = "p7.f1.v6.d4.5-run-summary.v1";
    const long MaxSummaryBytes = 16L * 1024 * 1024;
    static readonly Regex RunName = new("^run-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    static readonly Regex ItemName = new("^[0-9a-f]{64}\\.bin$", RegexOptions.CultureInvariant);
    static readonly Regex TempName = new("^[0-9a-f]{64}\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);

    /// <summary>Writes the review date for one run. The review step calls this once the review is complete.</summary>
    public static void RecordReview(string runDirectory, DateTime reviewCompletedUtc)
    {
        if (!Directory.Exists(runDirectory) || !RunName.IsMatch(Path.GetFileName(runDirectory))) throw new InvalidOperationException("NOT_A_RUN_DIRECTORY");
        File.WriteAllText(Path.Combine(runDirectory, ReviewFile), JsonSerializer.Serialize(new { reviewCompletedUtc = reviewCompletedUtc.ToString("O") }) + "\n");
    }

    public static DateTime? ReviewCompletedUtc(string runDirectory)
    {
        var path = Path.Combine(runDirectory, ReviewFile);
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return DateTime.Parse(doc.RootElement.GetProperty("reviewCompletedUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    /// <summary>Owned run directories that the rule marks for deletion now, each judged by its own age and its own review date.</summary>
    public static IReadOnlyList<string> Due(string root, DateTime nowUtc)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(full)
            .Where(d => RunName.IsMatch(Path.GetFileName(d)) && IsOwnedRun(d))
            .Where(d => P7F1V6RetentionPolicy.ShouldDelete(Directory.GetCreationTimeUtc(d), ReviewCompletedUtc(d), nowUtc))
            .OrderBy(d => d, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Deletes the runs that are due. Returns the directories removed.</summary>
    public static IReadOnlyList<string> Sweep(string root, DateTime nowUtc)
    {
        var removed = new List<string>();
        foreach (var d in Due(root, nowUtc)) { DeleteRun(root, d); removed.Add(d); }
        return removed;
    }

    /// <summary>Deletes one run directory under the given root. Refuses a path that is not a direct child of that root, and any content that is not the store's own.</summary>
    public static int DeleteRun(string root, string runDirectory)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var runFull = Path.GetFullPath(runDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(runFull), rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("RUN_NOT_UNDER_RAW_ROOT");
        if (!RunName.IsMatch(Path.GetFileName(runFull))) throw new InvalidOperationException("NOT_A_RUN_DIRECTORY");
        if (!IsOwnedRun(runFull)) throw new InvalidOperationException("RUN_DIRECTORY_CONTAINS_FOREIGN_CONTENT");
        var files = Directory.EnumerateFiles(runFull).ToArray();
        foreach (var f in files) File.Delete(f);
        Directory.Delete(runFull);
        return files.Length;
    }

    static bool IsOwnedRun(string dir)
    {
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
        if (Directory.EnumerateDirectories(dir).Any()) return false;
        return Directory.EnumerateFiles(dir).All(f =>
        {
            var name = Path.GetFileName(f);
            if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) return false;
            if (name == SummaryFile) return IsRunSummary(f);
            return name == ReviewFile || ItemName.IsMatch(name) || TempName.IsMatch(name);
        });
    }

    static bool IsRunSummary(string path)
    {
        if (new FileInfo(path).Length > MaxSummaryBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("artifact", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == SummaryArtifact;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>Route data policy (D3.5a). Each fact carries its status, source URL and the date it was read. Sources read on 2026-10-12:
/// Alibaba Model Studio data retention policy (last updated 2026-10-11), https://www.alibabacloud.com/help/en/model-studio/data-retention-policy;
/// OpenRouter data collection, https://www.openrouter.ai/docs/guides/privacy/data-collection; OpenRouter provider selection,
/// https://openrouter.ai/docs/guides/routing/provider-selection. A fact not on those pages stays UNKNOWN.</summary>
internal static class P7F1V6RoutePolicy
{
    const string AlibabaRetention = "https://www.alibabacloud.com/help/en/model-studio/data-retention-policy";

    public sealed record Fact(string Route, string Subject, string Status, string? Source, string? ReadOn, string Note);

    public static readonly Fact[] Facts =
    [
        new("alibaba", "training on request data", "VERIFIED", AlibabaRetention, "2026-10-12",
            "Section 5.2: without separate consent, Alibaba Cloud will not use business data (inputs, outputs, files) to train any foundation model."),
        new("alibaba", "retention period of request data", "VERIFIED", AlibabaRetention, "2026-10-12",
            "Section 1: prompts and responses are retained for no longer than 30 days by default; longer where law requires or for service security."),
        new("alibaba", "zero data retention for qwen3.7-flash", "NOT_SUPPORTED", AlibabaRetention, "2026-10-12",
            "Section 4.1 lists ZDR-supported models; qwen3.7-flash is not among them (the flash entry begins at qwen3.8-flash)."),
        new("alibaba", "ZDR decision by the user", "WAIVER_NOT_VERIFIED", null, null,
            "The user has not recorded a decision to accept sending data to an endpoint without ZDR. A request-level approval does not count. Stays open until a written decision names the risk."),
        new("alibaba", "logging of request content through the OpenRouter endpoint", "UNKNOWN", null, null,
            "The OpenRouter provider page read on 2026-10-12 does not state Alibaba's storage through OpenRouter (NOT_ON_PAGE)."),
        new("openrouter", "own storage of prompts and responses", "VERIFIED", "https://www.openrouter.ai/docs/guides/privacy/data-collection", "2026-10-12",
            "Prompts and responses are not stored unless the account opts in to logging; request metadata is stored."),
        new("openrouter", "data_collection request preference", "VERIFIED", "https://openrouter.ai/docs/guides/routing/provider-selection", "2026-10-12",
            "Exists with values allow (default) and deny. The envelope does not set it; setting deny is a decision with its own consequence (not decided)."),
    ];

    /// <summary>True only when every fact for the route is VERIFIED. Any other status closes the gate and is named.</summary>
    public static (bool Allowed, IReadOnlyList<string> Blocking) RouteAllowed(string route)
    {
        var blocking = Facts.Where(f => f.Route == route && f.Status != "VERIFIED").Select(f => f.Route + ":" + f.Subject + ":" + f.Status).ToArray();
        return (blocking.Length == 0 && Facts.Any(f => f.Route == route), blocking);
    }
}

/// <summary>Response adapter (D3.4). Takes the assistant text from an OpenRouter chat response and nothing else. The text is passed to the
/// V6 parser unchanged; the adapter never repairs, trims or reinterprets it.</summary>
internal static class P7F1V6ResponseAdapter
{
    public static (string? Content, string? Failure) ExtractContent(string responseJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(responseJson); }
        catch (JsonException) { return (null, "RESPONSE_NOT_JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return (null, "NO_CHOICES");
            var c0 = choices[0];
            if (!c0.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return (null, "NO_MESSAGE");
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return (null, "CONTENT_NOT_TEXT");
            return (content.GetString(), null);
        }
    }
}

/// <summary>The provider-run guard (D3.4), kept as a reporting function. The live path uses P7F1V6Preflight, which reads evidence.</summary>
internal static class P7F1V6ProviderGuard
{
    public sealed record AuthorisedRun(string CandidateSha256, string Route, string DocumentRelativePath, string SourceSha256, int MaxCalls,
        decimal CeilingUsd, int Retries, string ApprovedBy);

    public static IReadOnlyList<string> Refusals(AuthorisedRun? auth, string pinnedCandidateSha256, bool safeStoreInUse,
        P7F1V6Hier.RunSummary? c0Summary, bool sourceGateOk, string sourceGateCode)
    {
        var reasons = new List<string>();
        if (auth is null) reasons.Add("NO_AUTHORISATION_RECORD");
        if (!safeStoreInUse) reasons.Add("SAFE_STORE_NOT_IN_USE");
        if (!sourceGateOk) reasons.Add("SOURCE_GATE:" + sourceGateCode);
        var routeOk = P7F1V6RoutePolicy.RouteAllowed("alibaba");
        if (!routeOk.Allowed) reasons.AddRange(routeOk.Blocking.Select(b => "DATA_POLICY_NOT_VERIFIED:" + b));
        if (auth is not null)
        {
            if (auth.CandidateSha256 != pinnedCandidateSha256) reasons.Add("CANDIDATE_SHA_NOT_PINNED");
            if (auth.Route != "alibaba") reasons.Add("ROUTE_NOT_LOCKED_TO_ALIBABA");
            if (auth.Retries is < 0 or > P7F1V6Runner.MaxRetries) reasons.Add("RETRY_OUT_OF_RANGE");
            if (auth.MaxCalls < 1 || auth.MaxCalls > P7F1V6Runner.MaxCalls) reasons.Add("CALL_LIMIT_OUT_OF_RANGE");
            if (auth.CeilingUsd <= 0 || auth.CeilingUsd > P7F1V6Runner.CeilingUsd) reasons.Add("CEILING_OUT_OF_RANGE");
        }
        if (c0Summary is null) reasons.Add("C0_REPLAY_NOT_RUN");
        else if (!c0Summary.ProtocolResolved) reasons.Add("C0_REPLAY_NOT_PROTOCOL_RESOLVED");
        return reasons;
    }
}
