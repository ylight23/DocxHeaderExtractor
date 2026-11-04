using System.Net;
using System.Text;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using Xunit.Abstractions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Many documents extracted at once in one process, with a fake provider and every time scaled down 100×: the shared gate of 8 requests,
/// a 30 s queue wait (300 ms here) and a 60 s model call (600 ms here). Each document runs like the DOCX lane: its segments in parallel,
/// then one GROUP call, all on its own AgentModel and budget but the one account gate.
/// It shows the queue-wait failure mechanism under these fixed assumptions (3 segments a file, every call equally long); it does not
/// predict how many production files would miss headings or how long a production run takes.
/// </summary>
[Trait("Category", "Slow")]
[Collection(Timing)]
public sealed class ProviderConcurrencySimulationTests(ITestOutputHelper output)
{
    /// <summary>The outcome depends on when requests start, so these run alone, never beside the rest of the suite.</summary>
    public const string Timing = "Provider timing simulation";

    const int Gate = 8, Segments = 3;
    static readonly TimeSpan Call = TimeSpan.FromMilliseconds(600), WaitBudget = TimeSpan.FromMilliseconds(300);

    sealed class SlowProvider : HttpMessageHandler
    {
        int inFlight, peak, posts;
        public int Peak => peak;
        public int Posts => posts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref posts);
            var now = Interlocked.Increment(ref inFlight);
            for (var seen = peak; now > seen; seen = peak) Interlocked.CompareExchange(ref peak, now, seen);
            try
            {
                await Task.Delay(Call, ct);
                var sse = FakeOpenRouter.Stream(new { content = "{}" }, "stop").Replace("PROVIDER", "Alibaba");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            finally { Interlocked.Decrement(ref inFlight); }
        }
    }

    static Agents.PreparedRequest Request()
    {
        // A real selection cap, so the reserve covers the fake provider's cost and no budget halt mixes into the outcome.
        const string body = "{\"messages\":[],\"max_tokens\":32768}";
        return new(body, body.Length, AgentProtocol.Sha(body));
    }

    sealed record Run(int Files, int Calls, int Failed, IReadOnlyList<string> Codes, int Peak, TimeSpan Elapsed, int FilesIncomplete);

    /// <summary>Extracts <paramref name="files"/> documents; <paramref name="filesAtOnce"/> null starts them all (as the admin page does today).</summary>
    static async Task<Run> Extract(int files, int? filesAtOnce, TimeSpan? waitBudget = null)
    {
        // Enough pool threads for every blocked caller at once, so a slow thread-pool ramp-up does not stagger the requests.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, files * (Segments + 2)), io);
        using var provider = new SlowProvider();
        var gate = new ProviderRequestGate(Gate);
        using var queue = new SemaphoreSlim(filesAtOnce ?? files);
        var codes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var incomplete = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(1, files).Select(f => Task.Run(async () =>
        {
            await queue.WaitAsync();
            try
            {
                using var model = new AgentModel(provider, "test", new AgentBudget(20, 10), rateLimitRetries: AgentModel.ProductionRateLimitRetries,
                    retryWaitBudget: waitBudget ?? WaitBudget, requestGate: gate);
                var failed = 0;
                void Turn(string unit)
                {
                    try { model.Turn(unit, 1, Request()); }
                    catch (ReplyFailure e) { codes.Add(e.Code); Interlocked.Increment(ref failed); }
                }
                Parallel.For(0, Segments, new ParallelOptions { MaxDegreeOfParallelism = 6 }, s => Turn($"F{f}-SEG-{s + 1}"));
                Turn($"F{f}-GROUP");
                if (failed > 0) Interlocked.Increment(ref incomplete);
            }
            finally { queue.Release(); }
        })));
        ThreadPool.SetMinThreads(workers, io);
        return new(files, provider.Posts, codes.Count, codes.Distinct().ToArray(), provider.Peak, clock.Elapsed, incomplete);
    }

    void Report(string name, Run r) => output.WriteLine(
        $"{name}: {r.Files} file, {r.Calls} lượt gửi, {r.Failed} lượt bị bỏ ({string.Join(",", r.Codes)}), {r.FilesIncomplete} file thiếu, " +
        $"đỉnh {r.Peak} request cùng lúc, {r.Elapsed.TotalSeconds:F1} s giả lập");

    [Fact]
    public async Task Thirty_files_started_at_once_lose_calls_to_the_queue_wait_limit()
    {
        var run = await Extract(30, filesAtOnce: null);
        Report("30 file cùng lúc (như hiện nay)", run);
        Assert.True(run.Peak <= Gate);
        Assert.True(run.FilesIncomplete > 0);
        Assert.Equal(["TRANSPORT:PROVIDER_WAIT_LIMIT"], run.Codes);
    }

    [Fact]
    public async Task Three_files_in_one_process_already_exceed_the_gate_with_their_first_segments()
    {
        var run = await Extract(3, filesAtOnce: null);
        Report("3 file cùng lúc trong một tiến trình", run);
        Assert.True(run.Peak <= Gate);
        Assert.True(run.Failed > 0);
    }

    [Fact]
    public async Task A_file_queue_that_fits_the_gate_loses_no_call()
    {
        // Two files of three segments each: at most 6 requests, under the gate of 8, so no call ever waits in the provider queue.
        // Only because every file here has 3 segments: production runs up to 6 a file, so two large files can reach 12 and wait again.
        var run = await Extract(30, filesAtOnce: 2);
        Report("30 file, hàng đợi 2 file một lúc", run);
        Assert.Equal(0, run.Failed);
        Assert.Equal(30 * (Segments + 1), run.Calls);
    }

    [Fact]
    public async Task Thirty_files_that_may_wait_for_a_free_slot_lose_no_call_and_keep_the_gate_full()
    {
        // The queue wait no longer counted against the 30 s limit (an hour here): every call waits its turn at the gate of 8.
        var run = await Extract(30, filesAtOnce: null, waitBudget: TimeSpan.FromHours(1));
        Report("30 file cùng lúc, chờ chỗ trống không giới hạn", run);
        Assert.Equal(0, run.Failed);
        Assert.Equal(Gate, run.Peak);
    }
}

[CollectionDefinition(ProviderConcurrencySimulationTests.Timing, DisableParallelization = true)]
public sealed class ProviderTimingCollection;
