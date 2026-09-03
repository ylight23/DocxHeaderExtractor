using System.Net.Http.Headers;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.Web;

public sealed record LmStudioModelEntry(string Id);

/// <summary>Server-side proxy chỉ đọc danh sách model từ endpoint loopback đã cấu hình.</summary>
public sealed class LmStudioModelDiscovery(IHttpClientFactory httpClientFactory)
{
    public async Task<IReadOnlyList<LmStudioModelEntry>> ListAsync(CancellationToken ct = default)
    {
        var options = RemoteInferenceOptions.FromEnvironment("lmstudio");
        options.Validate(requireModel: false);
        if (!RemoteInferenceOptions.IsLoopback(options.Endpoint))
            throw new InvalidOperationException("LMSTUDIO_ENDPOINT phải là địa chỉ loopback.");
        using var request = new HttpRequestMessage(HttpMethod.Get, options.ModelsEndpoint);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var client = httpClientFactory.CreateClient("LmStudio");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LM Studio trả {(int)response.StatusCode} {response.ReasonPhrase} khi đọc /v1/models.",
                null,
                response.StatusCode);

        using var json = JsonDocument.Parse(text);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("LM Studio /v1/models không có mảng data.");

        return [.. data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new LmStudioModelEntry(id!))
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)];
    }
}
