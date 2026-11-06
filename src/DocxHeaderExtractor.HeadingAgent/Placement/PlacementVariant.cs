namespace DocxHeaderExtractor.HeadingAgent.Placement;

/// <summary>
/// Qualification variants of the placement call, one change each, so their effect can be measured apart. <see cref="Production"/> sends the
/// same bytes as before: no reasoning, the source prompt unchanged, two fields per row.
/// </summary>
/// <param name="ReasoningEffort">OpenRouter reasoning effort; "none" (production) turns reasoning off.</param>
/// <param name="TypographyAsEvidence">Replace the sentence that discounts typography with one that treats consecutive headings of one
/// measured kind as evidence of siblings.</param>
/// <param name="AskReason">Each row carries a third field, "reason": one short sentence naming the evidence for the parent.</param>
public sealed record PlacementVariant(string ReasoningEffort = "none", bool TypographyAsEvidence = false, bool AskReason = false)
{
    public static PlacementVariant Production { get; } = new();

    internal const string DiscountSentence = "Judge from the source meaning and the context shown,\nnot from numbering patterns or typography alone.";
    internal const string TypographySentence = "Judge from the source meaning and the context shown. Consecutive headings with the same measured\n"
        + "style, weight, list marker kind and list level are evidence that they are siblings under one parent; keep them\n"
        + "under one parent unless the source shows a section boundary between them.";
    internal const string ReasonSentence = "\nFor this request every placement row has exactly three fields: alias, parent and reason. The reason is one\n"
        + "short sentence (at most 25 words) naming the evidence for that parent.";

    /// <summary>Reasoning tokens count against the output cap, so a reasoning call gets room for them.</summary>
    internal int ExtraOutputTokens => (ReasoningEffort == "none" ? 0 : 4096) + (AskReason ? 2048 : 0);

    internal string Prompt(string source)
    {
        var prompt = source;
        if (TypographyAsEvidence)
        {
            if (!prompt.Contains(DiscountSentence, StringComparison.Ordinal)) throw new InvalidOperationException("PLACEMENT_PROMPT_SENTENCE_MISSING");
            prompt = prompt.Replace(DiscountSentence, TypographySentence, StringComparison.Ordinal);
        }
        return AskReason ? prompt + ReasonSentence : prompt;
    }

    internal string ContractSuffix =>
        (ReasoningEffort == "none" ? "" : ":reasoning-" + ReasoningEffort) + (TypographyAsEvidence ? ":typography-evidence-v1" : "") + (AskReason ? ":reason-v1" : "");
}
