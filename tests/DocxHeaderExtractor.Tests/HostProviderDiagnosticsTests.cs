using System.Net;
using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace DocxHeaderExtractor.Tests;

public sealed class HostProviderDiagnosticsTests
{
    [Theory]
    [InlineData("--openrouter")]
    [InlineData("--lmstudio")]
    [InlineData("--sglang")]
    public void Cli_raw_diagnostics_are_explicit_host_state_even_with_quiet(string backend)
    {
        var options = CommandLineOptions.Parse(["input.docx", backend, "--show-raw", "--quiet"]);
        var log = new List<string>();
        options.ConfigureProviderDiagnostics(log.Add);
        Assert.True(options.ShowRawOutput);
        Assert.True(options.Quiet);
        Assert.Null(typeof(PipelineOptions).GetProperty("ShowRawOutput"));
        options.Provider.Remote.DebugLog!("payload");
        Assert.Equal(new[] { "payload" }, log);

        var disabled = CommandLineOptions.Parse(["input.docx", backend]);
        disabled.ConfigureProviderDiagnostics(log.Add);
        Assert.Null(disabled.Provider.Remote.DebugLog);
    }

    [Fact]
    public void Local_provider_does_not_acquire_remote_diagnostics()
    {
        var options = CommandLineOptions.Parse(["input.docx", "--show-raw"]);
        options.ConfigureProviderDiagnostics(_ => throw new InvalidOperationException());
        Assert.Null(options.Provider.Remote.DebugLog);
    }

    [Theory]
    [InlineData(InferenceBackend.LmStudio, true)]
    [InlineData(InferenceBackend.OpenRouter, true)]
    [InlineData(InferenceBackend.OpenRouter, false)]
    [InlineData(InferenceBackend.Local, true)]
    public void Web_raw_flag_is_host_owned_and_opt_in(InferenceBackend backend, bool enabled)
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { ["showRaw"] = enabled.ToString().ToLowerInvariant() });
        var provider = new InferenceProviderSelection { Backend = backend };
        var log = new List<string>();
        RequestOptions.ConfigureProviderDiagnostics(form, provider, log.Add);
        Assert.Equal(enabled && backend != InferenceBackend.Local, provider.Remote.DebugLog is not null);
        provider.Remote.DebugLog?.Invoke("payload");
        Assert.Equal(enabled && backend != InferenceBackend.Local ? 1 : 0, log.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_callback_logs_actual_payload_without_headers_or_wire_changes(bool sglang)
    {
        const string response = """{"choices":[{"message":{"content":"{\"ok\":true}"}}]}""";
        var log = new List<string>();
        var first = new CaptureHandler(response);
        using var http = new HttpClient(first);
        var options = new RemoteInferenceOptions
        {
            Endpoint = new Uri("http://127.0.0.1:1234/v1/chat/completions"),
            Model = "test-model",
            ApiKey = "secret-header-token",
            TransientRequestRetries = 0,
            DebugLog = log.Add,
        };
        using IInferenceTransport transport = sglang
            ? new SglangInferenceTransport(http, options)
            : new LmStudioInferenceTransport(http, options);
        Assert.Equal("{\"ok\":true}", await transport.BoundaryCutAsync("SYSTEM", "document <&> text"));
        Assert.Equal(2, log.Count);
        Assert.Contains(first.Body, log[0]);
        Assert.Contains(response, log[1]);
        Assert.All(log, line => Assert.DoesNotContain(options.ApiKey, line));

        var second = new CaptureHandler(response);
        using var silentHttp = new HttpClient(second);
        options.DebugLog = null;
        using IInferenceTransport silent = sglang
            ? new SglangInferenceTransport(silentHttp, options)
            : new LmStudioInferenceTransport(silentHttp, options);
        await silent.BoundaryCutAsync("SYSTEM", "document <&> text");
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Hosts_apply_diagnostics_at_composition_not_processing()
    {
        Assert.Contains("options.ConfigureProviderDiagnostics", File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Cli/Program.cs")));
        Assert.Contains("RequestOptions.ConfigureProviderDiagnostics", File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.Web/Program.cs")));
        Assert.DoesNotContain("ShowRawOutput", File.ReadAllText(TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing/Pipeline/PipelineOptions.cs")));
    }

    private sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
