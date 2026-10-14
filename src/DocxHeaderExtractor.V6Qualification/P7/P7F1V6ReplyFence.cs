namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// The one fence the V6 reader may take off a final answer (D4.12). Qwen sometimes wraps the whole JSON answer in a markdown fence even with
/// response_format json_object. A final answer is accepted in that shape only when it is exactly: optional whitespace, an opening fence line
/// ("```" or "```json"), one JSON object, a closing fence line ("```"), optional whitespace. Prose before or after, a second fence, another
/// language label, or anything between the fences that is not one object is not unwrapped and stays REPLY_NOT_JSON. The object inside goes
/// through the same strict parser as any answer; a run that used this says so (FenceUnwrapped) and is counted apart from a bare answer.
/// </summary>
internal static class P7F1V6ReplyFence
{
    public const string Version = "P7_F1_V6_SINGLE_FENCE_UNWRAP_V1";

    public static bool TryUnwrap(string reply, out string inner)
    {
        inner = "";
        var t = reply.Trim();
        const string fence = "```";
        if (!t.StartsWith(fence, StringComparison.Ordinal) || !t.EndsWith(fence, StringComparison.Ordinal) || t.Length < 2 * fence.Length + 2) return false;
        var firstLineEnd = t.IndexOf('\n');
        if (firstLineEnd < 0) return false;
        var label = t[fence.Length..firstLineEnd].Trim();
        if (label.Length != 0 && label != "json") return false;
        var body = t[(firstLineEnd + 1)..^fence.Length];
        if (body.Contains(fence, StringComparison.Ordinal)) return false;
        body = body.Trim();
        if (body.Length < 2 || body[0] != '{' || body[^1] != '}') return false;
        inner = body;
        return true;
    }
}
