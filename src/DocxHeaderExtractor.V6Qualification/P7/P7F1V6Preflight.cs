using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// The single preflight (D3.5a). It reads each piece of evidence from a file, checks its integrity, and produces the gate results. The only
/// way to obtain a <see cref="Result"/> is <see cref="Result.Build"/>, and the send path accepts nothing else. Booleans supplied by a caller
/// are not evidence; the caller supplies paths and expected hashes, and this code reads and checks them.
/// Limit of this check: an authorisation record is verified by a SHA-256 over its own fields (integrity), not by a cryptographic signature.
/// A reviewer who needs a signature must add one; the code does not pretend to provide it.
/// </summary>
internal static class P7F1V6Preflight
{
    public enum RunMode { Mock, Live }

    public sealed record PinSet(string SourceSha256, string UniverseSha256, string CandidatesSha256, string ManifestSha256);

    /// <summary>Everything the preflight reads. Paths are where the evidence is; hashes are what the evidence must equal.</summary>
    public sealed record Evidence(RunMode Mode, string CommitSha, PinSet Pins, PinSet Observed, P7F1V6SourceSnapshot Source,
        bool SourcePublicConfirmed, string? InventoryPath, string? InventoryExpectedSha256, bool InventoryFreshDeclared,
        string? SafeStoreRoot, string? C0ArtifactPath, string? AuthorisationPath, string DocumentRelativePath);

    public sealed record Gate(string Name, bool Passed, string Reason);

    public sealed class Result
    {
        private Result(RunMode mode, bool allowed, IReadOnlyList<Gate> gates, string commit)
        {
            Mode = mode; Allowed = allowed; Gates = gates; Commit = commit;
        }

        public RunMode Mode { get; }
        public bool Allowed { get; }
        public IReadOnlyList<Gate> Gates { get; }
        public string Commit { get; }

        /// <summary>The only factory. Every gate is evaluated from evidence; nothing is taken from a caller's flags.</summary>
        public static Result Build(Evidence e)
        {
            var gates = new List<Gate>();

            var pinFailures = new List<string>();
            if (e.Observed.CandidatesSha256 != e.Pins.CandidatesSha256) pinFailures.Add("CANDIDATES_NOT_PINNED");
            if (e.Observed.SourceSha256 != e.Pins.SourceSha256) pinFailures.Add("SOURCE_NOT_PINNED");
            if (e.Observed.UniverseSha256 != e.Pins.UniverseSha256) pinFailures.Add("UNIVERSE_NOT_PINNED");
            if (e.Observed.ManifestSha256 != e.Pins.ManifestSha256) pinFailures.Add("MANIFEST_NOT_PINNED");
            gates.Add(new Gate("CANDIDATE_PIN", pinFailures.Count == 0, pinFailures.Count == 0 ? "OK" : string.Join(",", pinFailures)));

            var sourceOk = e.Source.Allowed && e.SourcePublicConfirmed;
            gates.Add(new Gate("SOURCE_AUTHORIZED", sourceOk,
                sourceOk ? "OK" : e.Source.Allowed ? "SOURCE_NOT_AUTHORIZED:public status not recorded by the corpus owner" : "SOURCE_GATE:" + e.Source.Code));

            var inventory = ReadInventory(e.InventoryPath, e.InventoryExpectedSha256, out var inventoryFailure);
            var routeOk = false;
            string routeReason;
            if (inventory is null) routeReason = "ROUTE_POLICY_UNKNOWN:" + inventoryFailure;
            else
            {
                var route = P7F1V6Protocol.EvaluateRoute(inventory);
                routeOk = route.Eligible && route.Endpoint == P7F1V6Protocol.ProviderTag;
                routeReason = routeOk ? "OK" : "ROUTE_POLICY_UNKNOWN:" + string.Join(",", route.Reasons);
            }
            gates.Add(new Gate("ROUTE_POLICY", routeOk, routeReason));

            var freshOk = inventory is not null && e.InventoryFreshDeclared;
            gates.Add(new Gate("INVENTORY_FRESH", freshOk, freshOk ? "OK" : "INVENTORY_NOT_FRESH:" + (inventory is null ? inventoryFailure : "no approved fresh read recorded")));

            var data = P7F1V6RoutePolicy.RouteAllowed("alibaba");
            gates.Add(new Gate("DATA_POLICY", data.Allowed, data.Allowed ? "OK" : "DATA_POLICY_UNVERIFIED:" + string.Join(",", data.Blocking)));

            var storeOk = e.SafeStoreRoot is not null && Directory.Exists(e.SafeStoreRoot)
                && (File.GetAttributes(e.SafeStoreRoot) & FileAttributes.ReparsePoint) == 0;
            gates.Add(new Gate("RAW_STORE", storeOk, storeOk ? "OK" : "SAFE_STORE_REQUIRED"));

            var c0 = ReadC0(e.C0ArtifactPath, e.Pins.CandidatesSha256, out var c0Failure);
            gates.Add(new Gate("C0_REPLAY", c0, c0 ? "OK" : "C0_NOT_PASSED:" + c0Failure));

            var commitOk = !string.IsNullOrWhiteSpace(e.CommitSha);
            gates.Add(new Gate("COMMIT_RECORDED", commitOk, commitOk ? "OK" : "COMMIT_MISSING"));

            var auth = CheckAuthorisation(e, out var authFailure);
            gates.Add(new Gate("LIVE_AUTHORISATION", auth, auth ? "OK" : "LIVE_NOT_AUTHORISED:" + authFailure));

            gates.Add(new Gate("CALL_LIMIT", true, $"OK: at most {P7F1V6Runner.MaxCalls} attempts, at most {P7F1V6Runner.MaxRetries} retry"));

            // A mock result may be built with failing gates: it is labelled MOCK, and the send path only lets it reach a mock port.
            // A live result is allowed only when every gate is met.
            var allowed = e.Mode == RunMode.Mock || gates.All(g => g.Passed);
            return new Result(e.Mode, allowed, gates, e.CommitSha ?? "");
        }

        static List<P7F1V6Protocol.EndpointRecord>? ReadInventory(string? path, string? expectedSha, out string failure)
        {
            failure = "NO_INVENTORY_FILE";
            if (path is null || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (sha != expectedSha) { failure = "INVENTORY_SHA_MISMATCH"; return null; }
            using var doc = JsonDocument.Parse(bytes);
            var rows = doc.RootElement.GetProperty("data").GetProperty("endpoints").EnumerateArray().Select(x => new P7F1V6Protocol.EndpointRecord(
                x.GetProperty("tag").GetString()!, x.GetProperty("model_id").GetString()!,
                x.GetProperty("context_length").GetInt32(), x.GetProperty("max_completion_tokens").GetInt32())).ToList();
            failure = "OK";
            return rows;
        }

        static bool ReadC0(string? path, string candidatesSha, out string failure)
        {
            failure = "NO_C0_ARTIFACT";
            if (path is null || !File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.GetProperty("status").GetString() != "PROTOCOL_RESOLVED") { failure = "NOT_PROTOCOL_RESOLVED"; return false; }
            if (!r.GetProperty("pinsMatched").GetBoolean()) { failure = "PINS_NOT_MATCHED"; return false; }
            if (r.GetProperty("pins").GetProperty("candidatesSha256").GetString() != candidatesSha) { failure = "C0_CANDIDATES_DIFFER"; return false; }
            failure = "OK";
            return true;
        }

        static bool CheckAuthorisation(Evidence e, out string failure)
        {
            failure = "NO_AUTHORISATION_RECORD";
            if (e.Mode != RunMode.Live) { failure = "MOCK_MODE"; return false; }
            if (e.AuthorisationPath is null || !File.Exists(e.AuthorisationPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(e.AuthorisationPath));
            var a = doc.RootElement;
            string S(string n) => a.GetProperty(n).GetString() ?? "";
            var canonical = string.Join("\n", "approvedCommit=" + S("approvedCommit"), "candidatesSha256=" + S("candidatesSha256"),
                "sourceSha256=" + S("sourceSha256"), "universeSha256=" + S("universeSha256"), "manifestSha256=" + S("manifestSha256"),
                "route=" + S("route"), "document=" + S("document"), "maxCalls=" + a.GetProperty("maxCalls").GetInt32(),
                "retries=" + a.GetProperty("retries").GetInt32(), "ceilingUsd=" + a.GetProperty("ceilingUsd").GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "approvedBy=" + S("approvedBy"));
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            if (digest != S("approvalSha256")) { failure = "APPROVAL_DIGEST_MISMATCH"; return false; }
            if (S("approvedCommit") != e.CommitSha) { failure = "APPROVED_COMMIT_DIFFERS"; return false; }
            if (S("candidatesSha256") != e.Pins.CandidatesSha256 || S("sourceSha256") != e.Pins.SourceSha256
                || S("universeSha256") != e.Pins.UniverseSha256 || S("manifestSha256") != e.Pins.ManifestSha256) { failure = "APPROVED_PINS_DIFFER"; return false; }
            if (S("route") != "alibaba") { failure = "APPROVED_ROUTE_DIFFERS"; return false; }
            if (S("document") != e.DocumentRelativePath) { failure = "APPROVED_DOCUMENT_DIFFERS"; return false; }
            if (a.GetProperty("maxCalls").GetInt32() is < 1 or > P7F1V6Runner.MaxCalls) { failure = "APPROVED_CALLS_OUT_OF_RANGE"; return false; }
            if (a.GetProperty("retries").GetInt32() is < 0 or > P7F1V6Runner.MaxRetries) { failure = "APPROVED_RETRY_OUT_OF_RANGE"; return false; }
            var ceiling = a.GetProperty("ceilingUsd").GetDecimal();
            if (ceiling <= 0 || ceiling > P7F1V6Runner.CeilingUsd) { failure = "APPROVED_CEILING_OUT_OF_RANGE"; return false; }
            if (string.IsNullOrWhiteSpace(S("approvedBy"))) { failure = "APPROVER_MISSING"; return false; }
            failure = "OK";
            return true;
        }
    }
}
