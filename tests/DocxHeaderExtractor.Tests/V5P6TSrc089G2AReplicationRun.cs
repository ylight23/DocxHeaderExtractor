using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;
using DocxHeaderExtractor.V5Qualification;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Gated single provider call: replays the exact stored SRC-089 v2 G2A request body (8828480, call 2) once, to measure whether a
/// one-shot G2A decision is stable enough to be an authority. The body is read from the committed capture and sent verbatim;
/// nothing is recomposed. It must hash to <c>18ad5242...</c>, or the run stops before the network.
/// <para>
/// Authorization (user, 2026-10-07): <c>APPROVE_PROVIDER_SRC089_V2_G2A_REPLICATION_1CALL_18AD5242</c> in
/// <c>A99_PROVIDER_SENTINEL</c> - exactly one call, no F1, no H2-C, no retry. Inert without the sentinel and an API key.
/// </para>
/// </summary>
public sealed class V5P6TSrc089G2AReplicationRun
{
    private const string Sentinel = "APPROVE_PROVIDER_SRC089_V2_G2A_REPLICATION_1CALL_18AD5242";
    private const string BodyPath = "artifacts/v5-p6t-function-membership/p6t-src089-pdf-universe-v2-requalification-20261007/call-02-G2A.request-body.json";
    private const string BodySha256 = "18ad52429cda4cc002af82e2b29a79c3b71d502ca9709db0219b909500c82e66";
    private const string OutputRoot = "artifacts/v5-p6t-function-membership/p6t-src089-pdf-universe-v2-g2a-replication-20261007";

    [Fact]
    public async Task Replay_the_stored_G2A_body_once()
    {
        if (Environment.GetEnvironmentVariable("A99_PROVIDER_SENTINEL") != Sentinel) return;
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")), "OPENROUTER_API_KEY is not set");

        var directory = Path.Combine(TestRepository.Root(), OutputRoot.Replace('/', Path.DirectorySeparatorChar));
        Assert.False(Directory.Exists(directory), "the capture directory already exists; captures are immutable");

        var body = File.ReadAllBytes(TestRepository.Path(BodyPath));
        Assert.Equal(BodySha256, Convert.ToHexStringLower(SHA256.HashData(body)));

        // The transport wants the system prompt and user message the body carries; they are read from the body, not composed.
        using var parsed = JsonDocument.Parse(body);
        var messages = parsed.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var system = messages.Single(m => m.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
        var user = messages.Single(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        var maxTokens = parsed.RootElement.GetProperty("max_tokens").GetInt32();

        var options = RemoteInferenceOptions.FromEnvironment();
        options.Model = "qwen/qwen3.7-flash";
        options.OpenRouterProviderRoute = "alibaba";
        options.OpenRouterReasoningEffort = "none";
        options.RequireJsonObjectResponse = true;
        options.TransientRequestRetries = 0;
        options.ProviderTransportTimeoutSeconds = 300;
        options.Validate();

        Directory.CreateDirectory(directory);
        using var transport = OpenRouterQualificationTransport.CreateOwned(options);
        var result = await transport.ExecuteObservedAsync(body, maxTokens, system, user).ConfigureAwait(false);

        int? Usage(string name) => result.Usage is { ValueKind: JsonValueKind.Object } usage && usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
        File.WriteAllText(Path.Combine(directory, "g2a-replication.raw-capture.v1.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "v5-p6t-src089-pdf-universe-v2-g2a-replication-v1",
            sentinel = Sentinel,
            providerCalls = 1,
            requestBodySha256 = BodySha256,
            requestBytes = body.Length,
            finishReason = result.FinishReason,
            responseSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Content))),
            promptTokens = Usage("prompt_tokens"),
            completionTokens = Usage("completion_tokens"),
            content = result.Content,
            usage = result.Usage,
            rawSse = result.RawSse,
            sseEventCount = result.SseEventCount,
            retryCount = result.RetryCount,
            goldRead = false,
        }, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }
}
