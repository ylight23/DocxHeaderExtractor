using static DocxHeaderExtractor.Agents.OpenRouterWire;

namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>
/// The admission budget of one document: a call limit, a reserve ceiling in USD (each POST reserves its worst case), a request byte cap and an
/// early stop after three consecutive contract failures. Thread-safe; ported from the qualified V6 budget.
/// </summary>
internal sealed class AgentBudget(int maxCalls, double ceilingUsd)
{
    public const int MaxRequestBytes = 131072, PromptTokenBudget = 900000, MaxConsecutiveContractFailures = 3;
    static readonly (int MinPromptTokens, double Prompt, double Completion)[] Tiers = [(0, 0.00000003, 0.00000013), (32000, 0.0000001, 0.0000004), (256000, 0.0000002, 0.0000008)];

    /// <summary>Worst-case cost of one attempt: the bytes bound the prompt tokens; the completion is max_tokens.</summary>
    public static double AttemptUsd(int requestBytes)
    {
        var tier = Tiers.Last(t => requestBytes >= t.MinPromptTokens);
        return requestBytes * tier.Prompt + MaxTokens * tier.Completion;
    }

    readonly object gate = new();
    public int MaxCalls { get; } = maxCalls;
    public double CeilingUsd { get; } = ceilingUsd;
    public int Calls { get; private set; }
    public double ReservedUsd { get; private set; }
    public double ActualUsd { get; private set; }
    /// <summary>The largest request admitted, in bytes: 128 KB by default; a scan run raises it (its request carries the context lines' facts).</summary>
    public int ByteCap { get; set; } = MaxRequestBytes;
    public string? HaltReason { get; private set; }
    public int ConsecutiveContractFailures { get; private set; }

    public void Halt(string reason) { lock (gate) HaltReason ??= reason; }

    public void NoteContract(bool passed)
    {
        lock (gate)
        {
            if (passed) { ConsecutiveContractFailures = 0; return; }
            if (++ConsecutiveContractFailures >= MaxConsecutiveContractFailures) HaltReason ??= "CONSECUTIVE_CONTRACT_FAILURES";
        }
    }

    public void RecordActual(double? providerCostUsd, double attemptReserveUsd)
    {
        lock (gate)
        {
            if (providerCostUsd is null) return;
            ActualUsd += providerCostUsd.Value;
            if (providerCostUsd.Value > attemptReserveUsd) HaltReason ??= "PROVIDER_COST_OVER_RESERVE";
        }
    }

    /// <summary>Null when the request may be sent (and reserves it), else the refusal code.</summary>
    public string? Admit(PreparedRequest request)
    {
        lock (gate)
        {
            var worst = AttemptUsd(request.Bytes);
            if (HaltReason is not null) return "HALTED:" + HaltReason;
            if (Calls >= MaxCalls) return "CALL_LIMIT";
            if (request.Bytes > ByteCap) return "REQUEST_OVER_BYTE_CAP";
            if (request.Bytes > PromptTokenBudget) return "REQUEST_OVER_TOKEN_BUDGET";
            if (ReservedUsd + worst > CeilingUsd) return "RESERVE_OVER_CEILING";
            Calls++;
            ReservedUsd += worst;
            return null;
        }
    }
}
