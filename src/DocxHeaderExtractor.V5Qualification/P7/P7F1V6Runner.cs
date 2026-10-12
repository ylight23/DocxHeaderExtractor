namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// The C1 runner (D3.5). Every condition is evaluated and reported; none is skipped. A send happens only through <see cref="Dispatch"/>,
/// which re-checks the call limit and the cost reserve before every attempt. Retry is at most one, and only for a request that produced
/// no response (transport failure or timeout); the retry sends the identical prepared body and counts against the call limit and the
/// cost reserve. The reserve is the worst case per attempt: tokens are bounded by the UTF-8 bytes of the body, and completion by the
/// largest completion cap the route allows. It is a ceiling under those assumptions, not a guarantee of the billed amount.
/// </summary>
internal static class P7F1V6Runner
{
    public const int MaxCalls = 6;
    public const int MaxRetries = 1;
    public const decimal CeilingUsd = 0.30m;
    public const int WorstCompletionTokens = 65536;

    public sealed record PinSet(string SourceSha256, string UniverseSha256, string CandidatesSha256, string ManifestSha256);

    public sealed record Inputs(PinSet Pins, PinSet Observed, string SourceGateCode, bool PublicConfirmed,
        IReadOnlyList<P7F1V6Protocol.EndpointRecord> Inventory, bool InventoryFresh, bool SafeStore, bool C0Passed, bool LiveAuthorised);

    public sealed record Gate(string Name, bool Passed, string Reason);

    /// <summary>All gates, each with its own result. Nothing short-circuits, so the report shows every refusal at once.</summary>
    public static IReadOnlyList<Gate> Gates(Inputs i)
    {
        var gates = new List<Gate>();

        var pinFailures = new List<string>();
        if (i.Observed.CandidatesSha256 != i.Pins.CandidatesSha256) pinFailures.Add("CANDIDATES_NOT_PINNED");
        if (i.Observed.SourceSha256 != i.Pins.SourceSha256) pinFailures.Add("SOURCE_NOT_PINNED");
        if (i.Observed.UniverseSha256 != i.Pins.UniverseSha256) pinFailures.Add("UNIVERSE_NOT_PINNED");
        if (i.Observed.ManifestSha256 != i.Pins.ManifestSha256) pinFailures.Add("MANIFEST_NOT_PINNED");
        gates.Add(new Gate("CANDIDATE_PIN", pinFailures.Count == 0, pinFailures.Count == 0 ? "OK" : string.Join(",", pinFailures)));

        var sourceOk = i.SourceGateCode == "OK" && i.PublicConfirmed;
        gates.Add(new Gate("SOURCE_AUTHORIZED", sourceOk,
            sourceOk ? "OK" : i.SourceGateCode != "OK" ? "SOURCE_GATE:" + i.SourceGateCode : "SOURCE_NOT_AUTHORIZED:public status not confirmed by the corpus owner"));

        var route = P7F1V6Protocol.EvaluateRoute(i.Inventory);
        var routeOk = route.Eligible && route.Endpoint == P7F1V6Protocol.ProviderTag;
        gates.Add(new Gate("ROUTE_POLICY", routeOk, routeOk ? "OK" : "ROUTE_POLICY_UNKNOWN:" + string.Join(",", route.Reasons)));

        gates.Add(new Gate("INVENTORY_FRESH", i.InventoryFresh, i.InventoryFresh ? "OK" : "INVENTORY_NOT_FRESH:no approved fresh metadata read"));

        var data = P7F1V6RoutePolicy.RouteAllowed("alibaba");
        gates.Add(new Gate("DATA_POLICY", data.Allowed, data.Allowed ? "OK" : "DATA_POLICY_UNVERIFIED:" + string.Join(",", data.Blocking)));

        gates.Add(new Gate("RAW_STORE", i.SafeStore, i.SafeStore ? "OK" : "SAFE_STORE_REQUIRED"));
        gates.Add(new Gate("C0_REPLAY", i.C0Passed, i.C0Passed ? "OK" : "C0_NOT_PASSED"));
        gates.Add(new Gate("LIVE_AUTHORISATION", i.LiveAuthorised, i.LiveAuthorised ? "OK" : "LIVE_NOT_AUTHORISED"));
        gates.Add(new Gate("CALL_LIMIT", true, $"OK: at most {MaxCalls} attempts, at most {MaxRetries} retry"));
        return gates;
    }

    public static bool AllPassed(IEnumerable<Gate> gates) => gates.All(g => g.Passed);

    /// <summary>Worst-case cost of one attempt: the envelope bytes bound the prompt tokens (tokens are at most bytes), the completion is the
    /// largest cap. The tier is the one the byte count reaches, as in the cost bound.</summary>
    public static double WorstAttemptUsd(int envelopeBytes)
    {
        var tier = P7F1V6Usage.AlibabaTiers.Last(t => envelopeBytes >= t.MinPromptTokens);
        return envelopeBytes * tier.Prompt + WorstCompletionTokens * tier.Completion;
    }

    /// <summary>Port for one send. The real port wraps the OpenRouter transport; tests supply a counting fake.</summary>
    public interface ISendPort
    {
        P7F1V6OpenRouterTransport.SendResult Send(P7F1V6Protocol.PreparedRequest request);
    }

    public sealed class RunState
    {
        readonly IReadOnlyList<Gate> gates;

        /// <summary>The gates are fixed for the run. Dispatch refuses every send while any of them is not met, and names the ones that are not.</summary>
        public RunState(IReadOnlyList<Gate> gates) { this.gates = gates; }

        public int Attempts { get; private set; }
        public int RetriesUsed { get; private set; }
        public double ReservedUsd { get; private set; }
        public string? StopReason { get; private set; }
        public string? Content { get; private set; }
        public List<string> AttemptLog { get; } = new();

        /// <summary>Sends one request, with the checks that apply before each attempt. Returns the assistant text, or null with StopReason set.</summary>
        public string? Dispatch(P7F1V6Protocol.PreparedRequest request, ISendPort port)
        {
            if (StopReason is not null) return null;
            if (!AllPassed(gates)) return Stop("GATES_NOT_PASSED:" + string.Join(",", gates.Where(g => !g.Passed).Select(g => g.Name)));
            if (Attempts >= MaxCalls) return Stop("CALL_LIMIT_EXCEEDED");
            var worst = WorstAttemptUsd(request.Bytes);
            if (ReservedUsd + worst > (double)CeilingUsd) return Stop("COST_CAP_EXCEEDED");

            while (true)
            {
                Attempts++;
                ReservedUsd += worst;
                var sent = port.Send(request);
                AttemptLog.Add($"{sent.Outcome}|{request.Sha256}|{request.Bytes}");
                // The bytes that crossed the boundary must be the prepared bytes; a mismatch is never retried.
                if (sent.Outcome == "BOUNDARY_SHA_MISMATCH" || sent.Outcome == "PREPARED_SHA_MISMATCH") return Stop(sent.Outcome);
                if (sent.Outcome == "HTTP_OK")
                {
                    var (content, failure) = P7F1V6ResponseAdapter.ExtractContent(sent.Body ?? "");
                    if (failure is not null) return Stop("RESPONSE_" + failure);
                    Content = content;
                    return content;
                }
                // Retry only when no response arrived: transport failure or timeout. Never on any HTTP status.
                var noResponse = sent.Outcome is "TRANSPORT_ERROR" or "TIMEOUT_OR_CANCELLED";
                if (!noResponse) return Stop("HTTP_NOT_RETRIED:" + sent.Outcome);
                if (RetriesUsed >= MaxRetries) return Stop("TRANSPORT_RETRY_EXHAUSTED");
                if (Attempts >= MaxCalls) return Stop("CALL_LIMIT_EXCEEDED");
                if (ReservedUsd + worst > (double)CeilingUsd) return Stop("COST_CAP_EXCEEDED");
                RetriesUsed++;
            }
        }

        string? Stop(string reason) { StopReason ??= reason; return null; }
    }

    /// <summary>Dry-run record for one planned request: bytes, hash, worst cost and whether the running reserve stays under the ceiling.</summary>
    public sealed record DryRunLine(int Index, int EnvelopeBytes, string Sha256, double WorstUsd, double ReserveAfterUsd, bool WithinCeiling);

    public static IReadOnlyList<DryRunLine> DryRun(IReadOnlyList<P7F1V6Protocol.PreparedRequest> planned)
    {
        var lines = new List<DryRunLine>();
        double reserve = 0;
        for (int k = 0; k < planned.Count; k++)
        {
            var w = WorstAttemptUsd(planned[k].Bytes);
            reserve += w;
            lines.Add(new DryRunLine(k, planned[k].Bytes, planned[k].Sha256, w, reserve, reserve <= (double)CeilingUsd && k < MaxCalls));
        }
        return lines;
    }
}

/// <summary>Early deletion and retention decision (D3.5). A run is deleted at 30 days at most, and 7 days after its review is complete.
/// Deletion touches only the run directory it is given, never the source corpus.</summary>
internal static class P7F1V6RetentionPolicy
{
    public const int MaxRetentionDays = 30;
    public const int DaysAfterReview = 7;

    public static bool ShouldDelete(DateTime createdUtc, DateTime? reviewCompletedUtc, DateTime nowUtc) =>
        (nowUtc - createdUtc).TotalDays > MaxRetentionDays
        || (reviewCompletedUtc is not null && (nowUtc - reviewCompletedUtc.Value).TotalDays > DaysAfterReview);
}
