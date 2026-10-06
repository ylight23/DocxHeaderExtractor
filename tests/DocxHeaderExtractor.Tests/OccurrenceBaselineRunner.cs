using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A hard ceiling on outbound provider requests, enforced where the request is actually made.
/// <para>
/// The budget is an approval, not an estimate. Counting planned calls in a manifest does not stop
/// a retry, a placement round or a repair pass from sending a thirty-seventh request, so the count
/// lives on the wrapper every call passes through and refuses rather than exceeds. A run that hits
/// the ceiling stops at the ceiling; it does not finish the repetition first.
/// </para>
/// </summary>
internal sealed class BudgetedClassifier(IInferenceTransport inner, int ceiling) : IInferenceTransport
{
    private readonly List<CallRecord> _ledger = [];

    public IReadOnlyList<CallRecord> Ledger => _ledger;
    public int CallsMade => _ledger.Count;
    public int Remaining => ceiling - _ledger.Count;

    /// <summary>What the run is doing when a call goes out, so the ledger reads as provenance.</summary>
    public string Stage { get; set; } = "unassigned";
    public string DocumentId { get; set; } = "unassigned";
    public int Repeat { get; set; }

    public string ModelName => inner.ModelName;
    public int ContextSize => inner.ContextSize;
    public string RuntimeDescription => inner.RuntimeDescription;
    public int SharedPrefixTokens => inner.SharedPrefixTokens;

    public async Task<string> BoundaryCutAsync(
        string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
    {
        if (_ledger.Count >= ceiling)
            throw new InvalidOperationException(
                $"Provider call budget exhausted at {ceiling}. Refusing to send another request.");

        var started = DateTimeOffset.UtcNow;
        var response = await inner.BoundaryCutAsync(systemPrompt, userMessage, ct, expectedItemCount);
        _ledger.Add(new CallRecord(
            _ledger.Count + 1, DocumentId, Repeat, Stage,
            Sha256(systemPrompt), Sha256(userMessage), Sha256(response),
            userMessage.Length, response.Length,
            (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, response));
        return response;
    }

    public void Dispose() => inner.Dispose();

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal sealed record CallRecord(
        int Ordinal,
        string DocumentId,
        int Repeat,
        string Stage,
        string SystemPromptSha256,
        string RequestSha256,
        string ResponseSha256,
        int RequestChars,
        int ResponseChars,
        long ElapsedMs,
        string RawResponse);
}
