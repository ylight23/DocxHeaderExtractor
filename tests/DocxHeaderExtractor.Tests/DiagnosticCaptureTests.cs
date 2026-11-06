using System.Net;
using System.Text;
using DocxHeaderExtractor.App;

namespace DocxHeaderExtractor.Tests;

public sealed class DiagnosticCaptureTests
{
    sealed class StreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n") });
    }

    [Fact]
    public async Task Streaming_response_finish_reason_is_recorded()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-capture-sse-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CaptureRun(root, "secret-key-example");
            using var client = new HttpClient(capture.Handler("test", "SELECT", new StreamHandler()));
            using var response = await client.PostAsync("https://example.invalid", new StringContent("{}"));
            var index = Directory.GetFiles(root, "*.index.json", SearchOption.AllDirectories).Single();
            Assert.Contains("\"finishReason\":\"stop\"", File.ReadAllText(index));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    sealed class FakeHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => fail
            ? throw new HttpRequestException("test failure")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\"}]}") });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_records_wire_or_missing_response_without_headers(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-capture-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CaptureRun(root, "secret-key-example");
            using var client = new HttpClient(capture.Handler("test", "SELECT", new FakeHandler(fail)));
            client.DefaultRequestHeaders.Add("Authorization", "Bearer secret-key-example");
            using var body = new StringContent("{}", Encoding.UTF8, "application/json");
            if (fail) await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("https://example.invalid", body));
            else using (await client.PostAsync("https://example.invalid", body)) { }
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("secret-key-example"));
            Assert.Contains(files, f => f.EndsWith(".request.json"));
            Assert.Equal(!fail, files.Any(f => f.EndsWith(".response.json")));
            Assert.Contains(files, f => f.EndsWith(".index.json") && File.ReadAllText(f).Contains(fail ? "\"responseAvailable\":false" : "\"responseAvailable\":true"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Capture_rejects_credential_in_body_before_writing()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-capture-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CaptureRun(root, "secret-key-example");
            Assert.Throws<InvalidDataException>(() => capture.Record("unsafe.json", new { text = "secret-key-example" }));
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void The_capture_cost_ceiling_applies_only_when_capture_is_on_and_must_be_positive()
    {
        var outside = Path.Combine(Path.GetTempPath(), "dhx-capture-cap-" + Guid.NewGuid().ToString("N"));
        Assert.Null(new DiagnosticCaptureSettings(null, Path.GetTempPath(), "0.5").MaxRunUsd);
        Assert.Null(new DiagnosticCaptureSettings(outside, Path.GetTempPath()).MaxRunUsd);
        Assert.Equal(0.5, new DiagnosticCaptureSettings(outside, Path.GetTempPath(), "0.5").MaxRunUsd);
        Assert.Throws<InvalidOperationException>(() => new DiagnosticCaptureSettings(outside, Path.GetTempPath(), "0"));
        Assert.Throws<InvalidOperationException>(() => new DiagnosticCaptureSettings(outside, Path.GetTempPath(), "abc"));
    }
}
