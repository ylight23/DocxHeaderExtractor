using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>Thread-safe per-run admission and attempt ledger. Unknown billing retains its reserve.</summary>
internal sealed class AgentBudget
{
    public const int MaxRequestBytes = 131072, PromptTokenBudget = 900000, MaxConsecutiveContractFailures = 3;
    readonly object gate = new();
    readonly Dictionary<string, Queue<Attempt>> pending = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, Attempt> attempts = [];
    readonly RunResourceBudget? run = RunResourceBudget.Current;
    double reservedUsd, actualUsd;
    int calls;

    internal sealed class Attempt(Guid id, PreparedRequest request, RequestResources resources, double reserve)
    {
        public Guid Id { get; } = id;
        public string RequestSha256 { get; } = request.Sha256;
        public RequestResources Resources { get; } = resources;
        public double ReserveUsd { get; } = reserve;
        public double? CostUsd { get; set; }
        public Usage.Audit? Usage { get; set; }
        public string Status { get; set; } = "RESERVED";
        public TransportAttempt? Transport { get; set; }
    }

    public AgentBudget(int maxCalls, double ceilingUsd, RequestPolicy? policy = null, DateTimeOffset? deadline = null)
    {
        if (maxCalls < 0 || !double.IsFinite(ceilingUsd) || ceilingUsd < 0) throw new ArgumentOutOfRangeException(nameof(ceilingUsd));
        Policy = policy ?? run?.Policy ?? new RequestPolicy { MaxRequestBytes = MaxRequestBytes, ContinuationReserveBytes = 0 };
        Policy.Validate();
        MaxCalls = maxCalls; CeilingUsd = ceilingUsd; Deadline = deadline; ByteCap = Policy.MaxRequestBytes;
    }

    public RequestPolicy Policy { get; }
    public DateTimeOffset? Deadline { get; }
    public int MaxCalls { get; }
    public double CeilingUsd { get; }
    public int Calls { get { lock (gate) return calls; } }
    public double ReservedUsd { get { lock (gate) return reservedUsd; } }
    public double ActualUsd { get { lock (gate) return actualUsd; } }
    public int ByteCap { get; set; }
    public string? HaltReason { get; private set; }
    public int ConsecutiveContractFailures { get; private set; }
    public IReadOnlyList<Attempt> Attempts { get { lock (gate) return attempts.Values.ToArray(); } }

    // Compatibility for conservative planning of unknown future bodies, explicitly an estimate, never actual tokens.
    public static double AttemptUsd(int requestBytes) => AttemptUsd(new RequestResources(requestBytes, requestBytes, OpenRouterWire.MaxTokens, 0, "unbuilt-body-allowance"));
    public static double AttemptUsd(RequestResources resources, RequestPolicy? policy = null)
    {
        var tier = (policy ?? new RequestPolicy()).Pricing.Last(t => resources.InputTokenEstimate >= t.MinInputTokens);
        return resources.InputTokenEstimate * tier.InputUsdPerToken + resources.CompletionTokens * tier.OutputUsdPerToken;
    }
    public double EstimateUsd(PreparedRequest request) => AttemptUsd(RequestResources.Measure(request, Policy), Policy);

    public void Halt(string reason) { lock (gate) HaltReason ??= reason; }
    public void NoteContract(bool passed)
    {
        lock (gate)
        {
            if (passed) { ConsecutiveContractFailures = 0; return; }
            if (++ConsecutiveContractFailures >= MaxConsecutiveContractFailures) HaltReason ??= "CONSECUTIVE_CONTRACT_FAILURES";
        }
    }

    public string? Admit(PreparedRequest request) => Reserve(request, true, out _);

    public void ObserveTransport(Guid id, TransportAttempt observation)
    {
        lock (gate) attempts[id].Transport = observation;
    }

    string? Reserve(PreparedRequest request, bool enqueue, out Attempt? admitted)
    {
        lock (gate)
        {
            admitted = null;
            RequestResources resources;
            try { resources = RequestResources.Measure(request, Policy); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException)
            { return "REQUEST_RESOURCES_INVALID"; }
            var reserve = AttemptUsd(resources, Policy);
            if (HaltReason is not null) return "HALTED:" + HaltReason;
            if (Deadline is { } end && DateTimeOffset.UtcNow >= end) return "DEADLINE_EXHAUSTED";
            if (calls >= MaxCalls) return "CALL_LIMIT";
            if (resources.WireBytes != request.Bytes) return "PREPARED_BYTES_MISMATCH";
            if (resources.WireBytes > ByteCap) return "REQUEST_OVER_BYTE_CAP";
            if ((Policy with { MaxRequestBytes = ByteCap }).Refusal(resources) is { } refusal) return refusal;
            if (actualUsd + reservedUsd + reserve > CeilingUsd + 1e-12) return "RESERVE_OVER_CEILING";
            var attempt = new Attempt(Guid.NewGuid(), request, resources, reserve);
            if (run?.Reserve(attempt.Id, reserve) is { } runRefusal) return runRefusal;
            attempts.Add(attempt.Id, attempt);
            if (enqueue)
            {
                if (!pending.TryGetValue(request.Sha256, out var queue)) pending[request.Sha256] = queue = new();
                queue.Enqueue(attempt);
            }
            admitted = attempt;
            calls++; reservedUsd += reserve;
            return null;
        }
    }

    /// <summary>Transport consumes one caller admission. A direct call or an internal HTTP retry needs its own admission.</summary>
    public string? Claim(PreparedRequest request, out Attempt? attempt, bool fresh = false)
    {
        lock (gate)
        {
            attempt = null;
            if (fresh || !pending.TryGetValue(request.Sha256, out var queue) || queue.Count == 0)
                return Reserve(request, false, out attempt);
            attempt = queue.Dequeue();
            return null;
        }
    }

    /// <summary>Idempotent settlement. Null usage/cost (timeout, cancellation, broken stream) stays provisionally reserved.
    /// A later confirmed bill may settle a provisional entry; it never authorizes a late model result.</summary>
    public void Settle(Guid id, double? cost, Usage.Audit? usage = null)
    {
        lock (gate)
        {
            if (!attempts.TryGetValue(id, out var attempt)) throw new InvalidOperationException("UNKNOWN_ATTEMPT");
            if (attempt.CostUsd is not null) return;
            attempt.Usage ??= usage;
            if (cost is null) { attempt.Status = "BILLING_UNKNOWN"; run?.Settle(id, null); return; }
            if (!double.IsFinite(cost.Value) || cost < 0) { HaltReason ??= "PROVIDER_COST_INVALID"; attempt.Status = "BILLING_UNKNOWN"; return; }
            reservedUsd = Math.Max(0, reservedUsd - attempt.ReserveUsd);
            actualUsd += cost.Value;
            run?.Settle(id, cost);
            attempt.CostUsd = cost;
            attempt.Status = "CONFIRMED";
            if (cost > attempt.ReserveUsd + 1e-12) HaltReason ??= "PROVIDER_COST_OVER_RESERVE";
        }
    }
}
