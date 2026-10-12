namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// The C1 runner (D3.5a). A send happens only through <see cref="RunState.Dispatch"/>, which accepts only a <see cref="P7F1V6Preflight.Result"/>
/// that was built from evidence, and only through a port that matches the result's mode: a mock result goes only to a mock port, a live
/// result only to a live port, and a live result only when every gate is met. Before each attempt the call limit and the cost reserve are
/// checked. Retry is at most one, and only for a request that produced no response; it sends the identical prepared body.
/// Every HTTP 200 is audited (usage present, completion not cut, usage consistent) before its text is returned, and the provider-reported
/// cost, when present, is checked against the reserve. HTTP 200 is not taken to mean that inference succeeded.
/// The reserve is a worst case under stated assumptions, not the billed amount.
/// </summary>
internal static class P7F1V6Runner
{
    public const int MaxCalls = 6;
    public const int MaxRetries = 1;
    public const decimal CeilingUsd = 0.30m;
    public const int WorstCompletionTokens = 65536;

    /// <summary>Gate names and reasons for reporting. The send path does not use this list; it uses the evidence-built result.</summary>
    public sealed record Gate(string Name, bool Passed, string Reason);

    /// <summary>Port for one send. A mock port answers without any network and is the only port a mock result may use.</summary>
    public interface ISendPort
    {
        bool IsMock { get; }
        P7F1V6OpenRouterTransport.SendResult Send(P7F1V6Protocol.PreparedRequest request);
    }

    /// <summary>Worst-case cost of one attempt: the envelope bytes bound the prompt tokens (tokens are at most bytes); the completion is the
    /// largest cap; the tier is the one the byte count reaches.</summary>
    public static double WorstAttemptUsd(int envelopeBytes)
    {
        var tier = P7F1V6Usage.AlibabaTiers.Last(t => envelopeBytes >= t.MinPromptTokens);
        return envelopeBytes * tier.Prompt + WorstCompletionTokens * tier.Completion;
    }

    public sealed class RunState
    {
        readonly P7F1V6Preflight.Result preflight;

        /// <summary>Only an evidence-built preflight result can start a run. There is no constructor that takes a list of gates.</summary>
        public RunState(P7F1V6Preflight.Result preflight) { this.preflight = preflight; }

        public int Attempts { get; private set; }
        public int RetriesUsed { get; private set; }
        public double ReservedUsd { get; private set; }
        public double ActualProviderUsd { get; private set; }
        public string? StopReason { get; private set; }
        public string? Content { get; private set; }
        public List<string> AttemptLog { get; } = new();

        /// <summary>Sends one request, after every check that applies before an attempt. Returns the assistant text, or null with StopReason set.</summary>
        public string? Dispatch(P7F1V6Protocol.PreparedRequest request, ISendPort port)
        {
            if (StopReason is not null) return null;
            if (!preflight.Allowed) return Stop("PREFLIGHT_NOT_ALLOWED:" + string.Join(",", preflight.Gates.Where(g => !g.Passed).Select(g => g.Name)));
            if (preflight.Mode == P7F1V6Preflight.RunMode.Mock && !port.IsMock) return Stop("MOCK_PREFLIGHT_WITH_LIVE_PORT");
            if (preflight.Mode == P7F1V6Preflight.RunMode.Live && port.IsMock) return Stop("LIVE_PREFLIGHT_WITH_MOCK_PORT");
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
                if (sent.Outcome is "BOUNDARY_SHA_MISMATCH" or "PREPARED_SHA_MISMATCH") return Stop(sent.Outcome);
                if (sent.Outcome == "HTTP_OK") return Audit(request, sent.Body ?? "", worst);

                // Retry only when no response arrived: transport failure or timeout. Never on any HTTP status.
                var noResponse = sent.Outcome is "TRANSPORT_ERROR" or "TIMEOUT_OR_CANCELLED";
                if (!noResponse) return Stop("HTTP_NOT_RETRIED:" + sent.Outcome);
                if (RetriesUsed >= MaxRetries) return Stop("TRANSPORT_RETRY_EXHAUSTED");
                if (Attempts >= MaxCalls) return Stop("CALL_LIMIT_EXCEEDED");
                if (ReservedUsd + worst > (double)CeilingUsd) return Stop("COST_CAP_EXCEEDED");
                RetriesUsed++;
            }
        }

        /// <summary>The response audit that every HTTP 200 must pass before its text reaches the parser.</summary>
        string? Audit(P7F1V6Protocol.PreparedRequest request, string body, double worst)
        {
            var audit = P7F1V6Usage.Check(body, request.Bytes, P7F1V6Protocol.MaxTokens);
            if (audit.Codes.Count != 1 || audit.Codes[0] != "OK") return Stop("RESPONSE_AUDIT:" + string.Join("+", audit.Codes));
            if (audit.ProviderCostUsd is { } paid)
            {
                ActualProviderUsd += paid;
                if (paid > worst) return Stop("PROVIDER_COST_OVER_RESERVE");
            }
            var (content, failure) = P7F1V6ResponseAdapter.ExtractContent(body);
            if (failure is not null) return Stop("RESPONSE_" + failure);
            Content = content;
            return content;
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

/// <summary>Early deletion and retention rule (D3.5). A run is deleted at 30 days at most, and seven days after its own review.</summary>
internal static class P7F1V6RetentionPolicy
{
    public const int MaxRetentionDays = 30;
    public const int DaysAfterReview = 7;

    public static bool ShouldDelete(DateTime createdUtc, DateTime? reviewCompletedUtc, DateTime nowUtc) =>
        (nowUtc - createdUtc).TotalDays > MaxRetentionDays
        || (reviewCompletedUtc is not null && (nowUtc - reviewCompletedUtc.Value).TotalDays > DaysAfterReview);
}
