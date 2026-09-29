using System.Net;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The production streaming transport against real OpenRouter streams. The production re-baseline
/// recorded every leaf's raw network chunks byte for byte, at their original chunk boundaries.
/// Replaying them through <see cref="OpenRouterHeaderExtractor"/> must reproduce exactly the content
/// the qualified runner accepted - no provider call is made.
/// </summary>
public sealed class OpenRouterStreamingReplayTests
{
    private const string RunDir = "eval/a99-closed-loop/production-rebaseline-v1/run-20260929T030127Z";

    [Fact]
    public async Task Production_transport_reassembles_every_recorded_rebaseline_stream_exactly()
    {
        var root = TestRepository.Path(RunDir);
        var leaves = Directory.GetFiles(root, "leaf-*.network-chunks.jsonl").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(31, leaves.Length);

        foreach (var chunksPath in leaves)
        {
            var stem = Path.GetFileName(chunksPath)[..^".network-chunks.jsonl".Length];
            var chunks = File.ReadAllLines(chunksPath)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("base64").GetString()!)
                .Select(Convert.FromBase64String)
                .ToArray();
            var expected = File.ReadAllText(Path.Combine(root, $"{stem}.content.json")).Trim();

            using var http = new HttpClient(new RecordedStream(chunks));
            using var model = new OpenRouterHeaderExtractor(http, new RemoteInferenceOptions
            {
                ApiKey = "replay-only",
                Model = "qwen/qwen3.7-flash",
                OpenRouterProviderRoute = "Alibaba",
                TransientRequestRetries = 0,
            });

            var content = await model.BoundaryCutAsync("Return JSON.", "{\"sourceParts\":[]}");

            Assert.True(expected == content, $"{stem}: reassembled content differs from the accepted content");
        }
    }

    /// <summary>Serves the recorded chunks with their original boundaries.</summary>
    private sealed class RecordedStream(byte[][] chunks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkStream(chunks))
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                },
            });
    }

    private sealed class ChunkStream(byte[][] chunks) : Stream
    {
        private int _chunk;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_chunk >= chunks.Length) return 0;
            var current = chunks[_chunk];
            var n = Math.Min(buffer.Length, current.Length - _offset);
            current.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            if (_offset == current.Length)
            {
                _chunk++;
                _offset = 0;
            }
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
