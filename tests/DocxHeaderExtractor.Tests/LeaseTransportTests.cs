using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.Tests;

public sealed class LeaseTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Structured_calls_cannot_start_or_publish_after_cancellation_or_timeout(bool lateReply, bool timeout)
    {
        var completion = new TaskCompletionSource<StructuredInferenceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inner = new StructuredTransport(completion.Task);
        var lease = new LaneLease();
        using var bound = LeaseTransport.Bind(inner, lease);
        var structured = Assert.IsAssignableFrom<IStructuredInferenceTransport>(bound);
        var terminal = timeout ? LaneState.TimedOut : LaneState.Cancelled;
        if (!lateReply) Assert.True(lease.TryTransition(terminal));
        await Assert.ThrowsAsync<LaneLeaseLostException>(async () =>
        {
            var pending = structured.CompleteStructuredAsync("JSON", "{}", 16896);
            Assert.True(lease.TryTransition(terminal));
            completion.SetResult(new("{}", "stop", 16896));
            await pending;
        });
        Assert.Equal(lateReply ? 1 : 0, inner.Calls);
    }

    [Fact]
    public async Task Legacy_transport_keeps_its_capability_and_item_count()
    {
        using var inner = new LegacyTransport();
        using var bound = LeaseTransport.Bind(inner, new LaneLease());
        Assert.False(bound is IStructuredInferenceTransport);
        Assert.Equal("{}", await bound.BoundaryCutAsync("JSON", "{}", expectedItemCount: 16));
        Assert.Equal(16, inner.ExpectedItems);
    }

    sealed class StructuredTransport(Task<StructuredInferenceReply> reply) : IStructuredInferenceTransport
    {
        public int Calls { get; private set; }
        public string ModelName => "test";
        public int ContextSize => 32768;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;
        public Task<StructuredInferenceReply> CompleteStructuredAsync(string systemPrompt, string userMessage,
            int maxOutputTokens, CancellationToken ct = default)
        {
            Assert.Equal(16896, maxOutputTokens);
            Calls++;
            return reply;
        }
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default,
            int expectedItemCount = 0) => throw new InvalidOperationException("Structured capability was lost.");
        public void Dispose() { }
    }

    sealed class LegacyTransport : IInferenceTransport
    {
        public int ExpectedItems { get; private set; }
        public string ModelName => "test";
        public int ContextSize => 32768;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default,
            int expectedItemCount = 0)
        {
            ExpectedItems = expectedItemCount;
            return Task.FromResult("{}");
        }
        public void Dispose() { }
    }
}
