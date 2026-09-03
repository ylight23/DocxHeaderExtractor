using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Vision;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>OpenAI-compatible image client for a self-hosted SGLang VLM such as Qwen2.5-VL.</summary>
public sealed class SglangVlmImageQuestion : IMultiImageVisualQuestion
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    public int MaximumImagesPerRequest { get; }

    public SglangVlmImageQuestion(HttpClient http, Uri endpoint, string model, string apiKey, int maximumImagesPerRequest = 1)
    {
        _http = http;
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        MaximumImagesPerRequest = Math.Max(1, maximumImagesPerRequest);
    }

    public static SglangVlmImageQuestion CreateOwned(Uri endpoint, string model, string apiKey, int maximumImagesPerRequest = 1) =>
        new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, endpoint, model, apiKey, maximumImagesPerRequest);

    public async Task<string> AskAsync(byte[] imageBytes, string question, int maxTokens = 300, CancellationToken ct = default)
        => await AskManyAsync([imageBytes], question, maxTokens, ct);

    public async Task<string> AskManyAsync(
        IReadOnlyList<byte[]> imageBytes,
        string question,
        int maxTokens = 300,
        CancellationToken ct = default)
    {
        if (imageBytes.Count == 0)
            throw new ArgumentException("Cần ít nhất một ảnh VLM.", nameof(imageBytes));
        if (imageBytes.Count > MaximumImagesPerRequest)
            throw new ArgumentException($"Gateway VLM chỉ cho phép {MaximumImagesPerRequest} ảnh mỗi request.", nameof(imageBytes));
        var content = new List<object> { new { type = "text", text = question } };
        content.AddRange(imageBytes.Select(bytes => (object)new
        {
            type = "image_url",
            image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(bytes) },
        }));
        var body = new
        {
            model = _model,
            temperature = 0,
            max_tokens = maxTokens,
            stream = false,
            response_format = new { type = "json_object" },
            chat_template_kwargs = new { enable_thinking = false },
            messages = new[]
            {
                new
                {
                    role = "user",
                    content,
                },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SGLang VLM returned {(int)response.StatusCode}: {raw[..Math.Min(raw.Length, 500)]}");
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim()
            ?? throw new FormatException("SGLang VLM response has empty content.");
    }

    public void Dispose() => _http.Dispose();
}
