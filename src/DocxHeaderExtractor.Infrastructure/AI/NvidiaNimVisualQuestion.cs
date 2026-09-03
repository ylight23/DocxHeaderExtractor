using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using DocxHeaderExtractor.DocumentProcessing.Vision;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Hosted NVIDIA NIM image adapter. It sends only the already-selected rendered crop and the
/// evidence-only question; source text, role resolution, and output remain local contracts.
/// </summary>
public sealed class NvidiaNimVisualQuestion : IPdfVisualQuestion, IPdfVisualAttemptAuditable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _requestTimeoutSeconds;
    private readonly int _transientRetries;
    public IReadOnlyList<PdfVisualAttemptOutcome> LastAttemptOutcomes { get; private set; } = [];

    public NvidiaNimVisualQuestion(
        Uri endpoint,
        string apiKey,
        string model,
        int requestTimeoutSeconds = 90,
        int transientRetries = 2,
        HttpClient? http = null)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _model = model;
        _requestTimeoutSeconds = Math.Clamp(requestTimeoutSeconds, 10, 600);
        _transientRetries = Math.Clamp(transientRetries, 0, 4);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string> AskAsync(byte[] imageBytes, string question, int maxTokens = 300, CancellationToken ct = default)
    {
        var outcomes = new List<PdfVisualAttemptOutcome>();
        var image = $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}";
        var body = new
        {
            model = _model,
            temperature = 0,
            max_tokens = maxTokens,
            stream = false,
            messages = new object[]
            {
                new { role = "user", content = new object[]
                {
                    new { type = "image_url", image_url = new { url = image } },
                    new { type = "text", text = question },
                } },
            },
            response_format = new { type = "json_object" },
        };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = JsonContent.Create(body),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                var started = Environment.TickCount64;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var raw = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"NVIDIA NIM VLM returned {(int)response.StatusCode}: {Safe(raw, 500)}", null, response.StatusCode);

                using var document = JsonDocument.Parse(raw);
                var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content)) throw new FormatException("NVIDIA NIM VLM returned empty content.");
                outcomes.Add(new PdfVisualAttemptOutcome(attempt + 1, "success", (int)response.StatusCode, Environment.TickCount64 - started, null));
                LastAttemptOutcomes = outcomes;
                return content;
            }
            catch (Exception error)
            {
                outcomes.Add(new PdfVisualAttemptOutcome(attempt + 1, "failed", (error as HttpRequestException)?.StatusCode is { } code ? (int)code : null, 0, error.GetType().Name));
                LastAttemptOutcomes = outcomes;
                if (attempt >= _transientRetries || !IsTransient(error, ct)) throw;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), ct);
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static bool IsTransient(Exception error, CancellationToken callerToken) => error switch
    {
        OperationCanceledException when !callerToken.IsCancellationRequested => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout } => true,
        _ => false,
    };

    private static string Safe(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
}
