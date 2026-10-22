namespace DocxHeaderExtractor.Cli.Commands;

/// <summary><c>--show-raw</c>: what the model streamed and wrote for one reply. Reasoning and content are the model's own text.</summary>
internal static class ModelReplyPrinter
{
    public static void Print(DocxHeaderExtractor.HeadingAgent.Selectors.ModelReply r)
    {
        var request = r.RequestSha256.Length >= 12 ? r.RequestSha256[..12] : "-";
        Console.Error.WriteLine($"--- [{r.Unit}] lượt {r.Turn} ({r.FinishReason ?? "—"}) req={request} preceding={r.PrecedingSelected ?? "-"} ---");
        if (!string.IsNullOrEmpty(r.Reasoning)) Console.Error.WriteLine($"[suy luận]\n{r.Reasoning}");
        foreach (var c in r.ToolCalls) Console.Error.WriteLine($"[gọi tool] {c.Name} {c.Arguments}");
        foreach (var x in r.ToolResults) Console.Error.WriteLine($"[kết quả tool trước lượt này] {x.Content}");
        if (!string.IsNullOrEmpty(r.Content)) Console.Error.WriteLine($"[nội dung]\n{r.Content}");
    }
}
