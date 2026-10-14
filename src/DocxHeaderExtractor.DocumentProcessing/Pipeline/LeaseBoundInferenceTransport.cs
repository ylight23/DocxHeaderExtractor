using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The transport of a lane execution: every provider call the shared stages make (placement) starts only while the lane's lease is active and
/// publishes only if it still is, so late provider work after a timeout or a cancel is quarantined, for the PDF and the DOCX route alike.
/// </summary>
internal sealed class LeaseBoundInferenceTransport : IInferenceTransport
{
    private readonly IInferenceTransport _inner;
    private readonly LaneExecutionLease _lease;

    public LeaseBoundInferenceTransport(IInferenceTransport inner, LaneExecutionLease lease)
    {
        _inner = inner;
        _lease = lease;
    }

    public string ModelName => _inner.ModelName;
    public int ContextSize => _inner.ContextSize;
    public string RuntimeDescription => _inner.RuntimeDescription;
    public int SharedPrefixTokens => _inner.SharedPrefixTokens;
    public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
        StartAndObserve(() => _inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount));
    public void Dispose() { }

    private Task<T> StartAndObserve<T>(Func<Task<T>> start)
    {
        Task<T>? task = null;
        if (!_lease.TryStartDownstream(() => task = start())) throw new LaneLeaseLostException();
        return ObserveAsync(task!, _lease);
    }

    private static async Task<T> ObserveAsync<T>(Task<T> task, LaneExecutionLease lease)
    {
        var result = await task.ConfigureAwait(false);
        if (!lease.IsActive) throw new LaneLeaseLostException();
        return result;
    }
}
