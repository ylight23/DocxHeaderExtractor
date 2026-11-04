using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Diagnostic only: compare the actual initial DOCX requests, without changing the cache or calling a provider.</summary>
static class DocxPacketDiff
{
    sealed record Packet(int Segment, string RequestSha, JsonObject Request, JsonObject Payload);

    public static int Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("docx-packet-diff <original.docx> <changed.docx> <output.json>");
        var asm = typeof(DocxHeaderExtractor.HeadingAgent.Selectors.DocxSelector).Assembly;
        var sourceType = asm.GetType("DocxHeaderExtractor.HeadingAgent.Formats.DocxAgentSource", true)!;
        var protocol = asm.GetType("DocxHeaderExtractor.HeadingAgent.Protocol.DocxAgentProtocol", true)!;
        var instruction = (string)protocol.GetField("Instruction")!.GetRawConstantValue()!;
        var unit = asm.GetType("DocxHeaderExtractor.HeadingAgent.Runtime.AgentUnit", true)!;
        var prepare = unit.GetMethod("PrepareFirstRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        List<Packet> Read(string path)
        {
            object?[] loadArgs = [Path.GetFullPath(path), null, null, 0];
            var source = sourceType.GetMethod("Load")!.Invoke(null, loadArgs) ?? throw new InvalidDataException("DOCX_LOAD_FAILED:" + loadArgs[2]);
            var cores = (System.Collections.ICollection)sourceType.GetProperty("Cores")!.GetValue(source)!;
            var result = new List<Packet>();
            for (var i = 0; i < cores.Count; i++)
            {
                var request = prepare.Invoke(null, [source, i, instruction, null, null])!;
                var body = (string)request.GetType().GetProperty("Body")!.GetValue(request)!;
                var sha = (string)request.GetType().GetProperty("Sha256")!.GetValue(request)!;
                var wire = JsonNode.Parse(body)!.AsObject();
                var content = wire["messages"]![1]!["content"]!;
                var userText = content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>();
                result.Add(new(i, sha, wire, JsonNode.Parse(userText)!.AsObject()));
            }
            return result;
        }
        static string FileSha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        static string Dump(JsonNode? node) => node?.ToJsonString() ?? "null";
        static void DiffPaths(JsonNode? a, JsonNode? b, string path, List<string> changes)
        {
            if (changes.Count >= 100 || Dump(a) == Dump(b)) return;
            if (a is JsonObject ao && b is JsonObject bo)
            {
                foreach (var k in ao.Select(x => x.Key).Concat(bo.Select(x => x.Key)).Distinct().Order())
                    DiffPaths(ao[k], bo[k], path + "." + k, changes);
            }
            else if (a is JsonArray aa && b is JsonArray ba)
            {
                for (var i = 0; i < Math.Max(aa.Count, ba.Count) && changes.Count < 100; i++)
                    DiffPaths(i < aa.Count ? aa[i] : null, i < ba.Count ? ba[i] : null, path + "[" + i + "]", changes);
            }
            else changes.Add(path);
        }
        var before = Read(args[1]);
        var after = Read(args[2]);
        var rows = new List<object>();
        for (var i = 0; i < Math.Max(before.Count, after.Count); i++)
        {
            if (i >= before.Count || i >= after.Count) { rows.Add(new { segment = i, status = "ADDED_OR_REMOVED" }); continue; }
            var a = before[i]; var b = after[i];
            var fields = a.Payload.Select(x => x.Key).Concat(b.Payload.Select(x => x.Key)).Distinct().Order().ToArray();
            var changedFields = fields.Where(k => Dump(a.Payload[k]) != Dump(b.Payload[k])).ToArray();
            var changedPaths = new List<string>();
            DiffPaths(a.Payload, b.Payload, "$", changedPaths);
            var pa = a.Payload.DeepClone().AsObject(); var pb = b.Payload.DeepClone().AsObject();
            pa.Remove("documentSha256"); pb.Remove("documentSha256");
            rows.Add(new { segment = i, exactRequestMatch = a.RequestSha == b.RequestSha,
                oldRequest = a.RequestSha, newRequest = b.RequestSha, changedFields, changedPaths,
                payloadMatchesWithoutDocumentHash = Dump(pa) == Dump(pb),
                before = new { first = a.Payload["targetFirstAlias"]?.GetValue<string>(), last = a.Payload["targetLastAlias"]?.GetValue<string>(), count = a.Payload["targetCount"]?.GetValue<int>() },
                after = new { first = b.Payload["targetFirstAlias"]?.GetValue<string>(), last = b.Payload["targetLastAlias"]?.GetValue<string>(), count = b.Payload["targetCount"]?.GetValue<int>() } });
        }
        var output = new { before = new { path = Path.GetFullPath(args[1]), sha256 = FileSha(args[1]) },
            after = new { path = Path.GetFullPath(args[2]), sha256 = FileSha(args[2]) }, note = "Diagnostic payload comparison only; no cache hit or semantic equivalence is inferred.", segments = rows };
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(args[3], JsonSerializer.Serialize(output, options));
        Console.WriteLine($"segments: {before.Count} -> {after.Count}; comparison: {args[3]}");
        return 0;
    }
}
