using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>Source gate (Gate S2, D3.4). A document may be sent only if it is under the allowed prefix, is a regular file (not a reparse
/// point), and its SHA-256 equals the hash pinned for it. The prefix alone does not prove the file is public; the pin does not prove
/// public status either. Public status is a fact the corpus owner must confirm, and it is recorded, not inferred here.</summary>
internal static class P7F1V6SourceGate
{
    public static (bool Allowed, string Code) Check(string repoRoot, string relativePath, string pinnedSha256)
    {
        if (!P7F1V6DataPolicy.IsAllowedSource(relativePath)) return (false, "SOURCE_NOT_IN_ALLOWED_PREFIX");
        var full = Path.GetFullPath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(repoRoot);
        if (!full.StartsWith(Path.Combine(rootFull, "todo10_8") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (false, "SOURCE_OUTSIDE_CORPUS");
        if (!File.Exists(full)) return (false, "SOURCE_MISSING");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return (false, "SOURCE_IS_REPARSE_POINT");
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
        return sha == pinnedSha256 ? (true, "OK") : (false, "SOURCE_HASH_NOT_PINNED");
    }
}

/// <summary>Retention and deletion for raw runs (Gate S2). Only directories named run-&lt;32 hex&gt; that contain nothing but .bin and .tmp
/// files are candidates. Reparse points are refused. Nothing outside a run directory is touched.</summary>
internal static class P7F1V6Retention
{
    static readonly Regex RunName = new("^run-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ExpiredRuns(string root, DateTime nowUtc, int retentionDays)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var cutoff = nowUtc.AddDays(-retentionDays);
        return Directory.EnumerateDirectories(root)
            .Where(d => RunName.IsMatch(Path.GetFileName(d)) && Directory.GetLastWriteTimeUtc(d) < cutoff && IsOwnedRun(d))
            .OrderBy(d => d, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Deletes one run directory. Refuses anything that is not an owned run directory. Returns the number of files removed.</summary>
    public static int DeleteRun(string runDirectory)
    {
        if (!RunName.IsMatch(Path.GetFileName(runDirectory))) throw new InvalidOperationException("NOT_A_RUN_DIRECTORY");
        if (!IsOwnedRun(runDirectory)) throw new InvalidOperationException("RUN_DIRECTORY_CONTAINS_FOREIGN_CONTENT");
        var files = Directory.EnumerateFiles(runDirectory).ToArray();
        foreach (var f in files) File.Delete(f);
        Directory.Delete(runDirectory);
        return files.Length;
    }

    static bool IsOwnedRun(string dir)
    {
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
        if (Directory.EnumerateDirectories(dir).Any()) return false;
        return Directory.EnumerateFiles(dir).All(f =>
            (Path.GetExtension(f) is ".bin" or ".tmp") && (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0);
    }
}

/// <summary>Route data policy (Gate S3, D3.4). Each fact is recorded with its source URL and the date it was read. A fact the sources did
/// not establish stays UNKNOWN. The gate is closed unless every fact the provider run depends on is VERIFIED for that route.</summary>
internal static class P7F1V6RoutePolicy
{
    public sealed record Fact(string Route, string Subject, string Status, string? Source, string? ReadOn, string Note);

    public static readonly Fact[] Facts =
    [
        new("openrouter", "own storage of prompts and responses", "VERIFIED", "https://www.openrouter.ai/docs/guides/privacy/data-collection", "2026-10-12",
            "Prompts and responses are not stored unless the account opts in to logging; request metadata (token counts, latency) is stored."),
        new("openrouter", "per-request data-policy filter parameter", "UNKNOWN", null, null,
            "The data-collection page does not name the request parameter."),
        new("alibaba", "training on request data", "UNKNOWN", null, null,
            "Not on the OpenRouter page. Alibaba Model Studio documentation has not been read."),
        new("alibaba", "retention period of request data", "UNKNOWN", null, null,
            "Not on the OpenRouter page. Alibaba Model Studio documentation has not been read."),
        new("alibaba", "logging of request content", "UNKNOWN", null, null,
            "Not established."),
    ];

    /// <summary>True only when every fact for the route is VERIFIED. Any UNKNOWN fact closes the gate.</summary>
    public static (bool Allowed, IReadOnlyList<string> Blocking) RouteAllowed(string route)
    {
        var blocking = Facts.Where(f => f.Route == route && f.Status != "VERIFIED").Select(f => f.Route + ":" + f.Subject).ToArray();
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

/// <summary>The provider-run guard (D3.4). Every condition is checked before a request is built for sending. A run that fails any
/// condition reports every reason and sends nothing. The guard is not a substitute for the checks themselves; it only refuses to proceed.</summary>
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
            if (auth.Retries != 0) reasons.Add("RETRY_MUST_BE_ZERO");
            if (auth.MaxCalls < 1 || auth.MaxCalls > 6) reasons.Add("CALL_LIMIT_OUT_OF_RANGE");
            if (auth.CeilingUsd <= 0 || auth.CeilingUsd > 0.30m) reasons.Add("CEILING_OUT_OF_RANGE");
        }
        if (c0Summary is null) reasons.Add("C0_REPLAY_NOT_RUN");
        else if (!c0Summary.ProtocolResolved) reasons.Add("C0_REPLAY_NOT_PROTOCOL_RESOLVED");
        return reasons;
    }
}
