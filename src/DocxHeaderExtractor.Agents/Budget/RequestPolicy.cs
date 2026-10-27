using System.Text;
using System.Text.Json;
using System.Collections.Immutable;

namespace DocxHeaderExtractor.Agents.Budget;

/// <summary>A versioned operator envelope. Sizes are bytes, token allowances are estimates, and money is USD.
/// These defaults describe the locked OpenRouter route; callers may supply a stricter profile. They are not provider discovery.</summary>
public sealed record RequestPolicy
{
    public string ProfileId { get; init; } = "openrouter-alibaba-qwen37-20261024-v1";
    public int MaxRequestBytes { get; init; } = 262144;
    public int ContextTokens { get; init; } = 1000000;
    public int MaxPromptTokens { get; init; } = 983616;
    public int MaxCompletionTokens { get; init; } = 65536;
    public int ContinuationReserveBytes { get; init; } = 65536;
    public int ContinuationReserveTokens { get; init; } = 65536;
    public double MaxContinuationFraction { get; init; } = 0.25;
    public int ImageTokenAllowance { get; init; } = 16384;
    public int SelectionReasoningAllowance { get; init; } = 8192;
    public int SelectionTokensPerTarget { get; init; } = 128;
    public int SelectionMinimumCompletion { get; init; } = OpenRouterWire.MaxTokens;
    /// <summary>USD per token, selected by estimated input tokens. Snapshot pricing, not a live quote.</summary>
    public ImmutableArray<PriceTier> Pricing { get; init; } =
        [new(0, 0.00000003, 0.00000013), new(32000, 0.0000001, 0.0000004), new(256000, 0.0000002, 0.0000008)];
    public sealed record PriceTier(int MinInputTokens, double InputUsdPerToken, double OutputUsdPerToken);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileId) || MaxRequestBytes <= 0 || ContextTokens <= 0 || MaxPromptTokens <= 0 ||
            MaxCompletionTokens <= 0 || ContinuationReserveBytes < 0 ||
            ContinuationReserveTokens < 0 || ImageTokenAllowance <= 0 || SelectionReasoningAllowance < 0 ||
            SelectionTokensPerTarget <= 0 || SelectionMinimumCompletion <= 0 || !double.IsFinite(MaxContinuationFraction) ||
            MaxContinuationFraction < 0 || MaxContinuationFraction >= 1)
            throw new ArgumentException("REQUEST_POLICY_INVALID");
        if (Pricing.IsDefaultOrEmpty || Pricing[0].MinInputTokens != 0 ||
            Pricing.Any(p => p.MinInputTokens < 0 || !double.IsFinite(p.InputUsdPerToken) || !double.IsFinite(p.OutputUsdPerToken) ||
                p.InputUsdPerToken < 0 || p.OutputUsdPerToken < 0) ||
            Pricing.Zip(Pricing.Skip(1)).Any(p => p.First.MinInputTokens >= p.Second.MinInputTokens))
            throw new ArgumentException("REQUEST_PRICING_INVALID");
    }

    internal int SelectionOutputDemand(int targets) => checked(Math.Max(Math.Min(SelectionMinimumCompletion, MaxCompletionTokens),
        512 + targets * SelectionTokensPerTarget + SelectionReasoningAllowance));

    internal string? Refusal(RequestResources resources, bool reserveContinuation = false)
    {
        var byteReserve = reserveContinuation ? Math.Min(ContinuationReserveBytes, (int)(MaxRequestBytes * MaxContinuationFraction)) : 0;
        var tokenReserve = reserveContinuation ? Math.Min(ContinuationReserveTokens,
            (int)(Math.Min(ContextTokens, MaxPromptTokens) * MaxContinuationFraction)) : 0;
        if ((long)resources.WireBytes + byteReserve > MaxRequestBytes) return "REQUEST_OVER_BYTE_CAP";
        if (resources.CompletionTokens <= 0 || resources.CompletionTokens > MaxCompletionTokens) return "COMPLETION_OVER_PROFILE_CAP";
        if ((long)resources.InputTokenEstimate + tokenReserve > MaxPromptTokens) return "REQUEST_OVER_PROMPT_ESTIMATE";
        if ((long)resources.InputTokenEstimate + tokenReserve + resources.CompletionTokens > ContextTokens) return "REQUEST_OVER_CONTEXT_ESTIMATE";
        return null;
    }
}

/// <summary>Exact wire size and enforced completion; input tokens are explicitly an estimate, not tokenizer output.
/// Text uses its UTF-8 length plus message framing as a conservative proxy; image bytes are never called tokens.</summary>
internal sealed record RequestResources(int WireBytes, int InputTokenEstimate, int CompletionTokens, int Images,
    string Estimator = "utf8-text-and-configured-image-allowance-v1")
{
    public static RequestResources Measure(PreparedRequest request, RequestPolicy policy)
    {
        using var doc = JsonDocument.Parse(request.Body);
        var root = doc.RootElement;
        var completion = root.TryGetProperty("max_tokens", out var cap) ? cap.GetInt32() : OpenRouterWire.MaxTokens;
        long text = 256;
        var images = 0;
        void Count(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() is "image_url" or "input_image") { images++; return; }
                foreach (var property in element.EnumerateObject()) { text += property.Name.Length + 4; Count(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Count(item);
            else if (element.ValueKind == JsonValueKind.String) text += Encoding.UTF8.GetByteCount(element.GetString()!) + 4;
            else text += element.GetRawText().Length;
        }
        // Only provider-visible prompt material: request serialization/escaping and generation settings are not prompt tokens.
        if (root.TryGetProperty("messages", out var messages)) Count(messages);
        if (root.TryGetProperty("tools", out var tools)) Count(tools);
        var input = checked((int)Math.Min(int.MaxValue, text + (long)images * policy.ImageTokenAllowance));
        return new(Encoding.UTF8.GetByteCount(request.Body), input, completion, images);
    }
}
