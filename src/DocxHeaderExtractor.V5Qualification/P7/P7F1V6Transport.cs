using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.V5Qualification.P7;

/// <summary>
/// The OpenRouter chat transport (Gate S/T, D3.3). It sends the PreparedRequest it is given, byte for byte, and nothing else. The bytes
/// that pass into the HTTP content stream are captured and hashed, and compared with the prepared hash at the HTTP boundary. It never
/// follows a redirect, never retries, and applies one timeout. The API key is taken as a parameter, put only into the Authorization
/// header, and not returned in any result. A mock HttpMessageHandler can stand in for the network; no connection is opened by the tests.
/// </summary>
internal sealed class P7F1V6OpenRouterTransport
{
    public const string Url = "https://openrouter.ai/api/v1/chat/completions";

    readonly HttpClient client;

    public P7F1V6OpenRouterTransport(HttpMessageHandler handler, TimeSpan timeout)
    {
        client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
    }

    /// <summary>The handler for real use: no redirects and no cookies.</summary>
    public static HttpClientHandler CreateProductionHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>Outcome of one send. Outcome is always set; Body is the response text when a response arrived.</summary>
    /// <summary>BodyBytes (D4.11d) are the response body exactly as received from HTTP, before any decoding; Body is their strict UTF-8
    /// reading. A body that is not valid UTF-8 gives Body null and the outcome RESPONSE_NOT_UTF8 on a 2xx status: nothing is repaired or
    /// replaced, and the bytes are still available to be kept.</summary>
    public sealed record SendResult(string Outcome, int? Status, string? Body, string? BoundarySha256, long? BoundaryBytes, int Attempts, byte[]? BodyBytes = null);

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public SendResult Send(P7F1V6Protocol.PreparedRequest request, string apiKey, CancellationToken ct = default)
    {
        if (!P7F1V6Protocol.VerifyTransportBody(request.Body, request.Sha256))
            return new SendResult("PREPARED_SHA_MISMATCH", null, null, null, null, 0);

        var content = new BoundaryContent(Encoding.UTF8.GetBytes(request.Body));
        using var message = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        // SendAsync works with any handler; the synchronous Send is not implemented by all of them.
        try { response = client.SendAsync(message, ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return new SendResult("TIMEOUT_OR_CANCELLED", null, null, content.Sha, content.Length, 1); }
        catch (HttpRequestException) { return new SendResult("TRANSPORT_ERROR", null, null, content.Sha, content.Length, 1); }

        using (response)
        {
            var status = (int)response.StatusCode;
            var bodyBytes = response.Content.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
            string? body;
            try { body = StrictUtf8.GetString(bodyBytes); }
            catch (DecoderFallbackException) { body = null; }
            // The check that matters: the bytes that reached the stream are the prepared bytes.
            var boundaryOk = content.Written && content.Sha == request.Sha256 && content.Length == request.Bytes;
            var outcome = !boundaryOk ? "BOUNDARY_SHA_MISMATCH" : Classify(status);
            if (outcome == "HTTP_OK" && body is null) outcome = "RESPONSE_NOT_UTF8";
            return new SendResult(outcome, status, body, content.Sha, content.Length, 1, bodyBytes);
        }
    }

    static string Classify(int status) => status switch
    {
        >= 200 and < 300 => "HTTP_OK",
        >= 300 and < 400 => "REDIRECT_REFUSED",
        429 => "RATE_LIMITED_NO_RETRY",
        >= 500 => "SERVER_ERROR_NO_RETRY",
        >= 400 => "CLIENT_ERROR",
        _ => "UNEXPECTED_STATUS",
    };

    /// <summary>Writes the prepared bytes and records exactly what passed into the stream.</summary>
    sealed class BoundaryContent : HttpContent
    {
        readonly byte[] bytes;
        public bool Written { get; private set; }
        public string? Sha { get; private set; }
        public long? Length { get; private set; }

        public BoundaryContent(byte[] bytes) { this.bytes = bytes; Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }; }

        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            WriteCapturedAsync(stream, CancellationToken.None);

        async Task WriteCapturedAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var tee = new CapturingStream(stream);
            await tee.WriteAsync(bytes.AsMemory(), cancellationToken);
            await tee.FlushAsync(cancellationToken);
            var captured = tee.Captured;
            Sha = Convert.ToHexStringLower(SHA256.HashData(captured));
            Length = captured.Length;
            Written = true;
        }
    }

    /// <summary>A pass-through stream that keeps a copy of every byte it forwards.</summary>
    sealed class CapturingStream : Stream
    {
        readonly Stream inner;
        readonly MemoryStream copy = new();
        public CapturingStream(Stream inner) { this.inner = inner; }
        public byte[] Captured => copy.ToArray();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await copy.WriteAsync(buffer, cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            copy.Write(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) copy.Dispose(); base.Dispose(disposing); }
    }
}
