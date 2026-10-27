using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;

/// <summary>Build real production first requests for cumulative ablations. No provider, replay or Gold.</summary>
internal static class PdfRequestOptimizationTrial
{
    internal static void Audit(string directory)
    {
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "baseline", "manifest.json")))!["requests"]!.AsArray();
        AgentSource? baselineSource = null;
        var checkedPackets = 0;
        var changedScopes = 0;
        foreach (var variant in new[] { "geometry", "geometry-images", "geometry-images-budget", "geometry-images-budget-gray" })
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, variant, "manifest.json")))!["requests"]!.AsArray();
            for (var i = 0; i < manifest.Count; i++)
            {
                JsonNode Wire(string folder, JsonNode record) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, folder, record["Sha256"]!.GetValue<string>() + ".request.json")))!;
                var b = Wire(variant, manifest[i]!);
                JsonNode a;
                if (i < baseline.Count && JsonNode.DeepEquals(baseline[i]!["start"], manifest[i]!["start"])
                    && JsonNode.DeepEquals(baseline[i]!["end"], manifest[i]!["end"])) a = Wire("baseline", baseline[i]!);
                else
                {
                    // Smaller packets can change adaptive packing. Compare representation on the
                    // exact same owned scope, and report the scheduling change separately.
                    changedScopes++;
                    var pdf = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")))!["pdf"]!.GetValue<string>();
                    baselineSource ??= AgentSource.Load(pdf, out _, new RequestPolicy { MaxRequestBytes = 786432 },
                        PdfMembershipInstructionV12.Instruction, sourceBound: true, unitSelection: true,
                        rasterGeometry: true, adaptiveVisualSelection: true)!;
                    var matched = PdfUnitSelectionTask.PrepareFirstRequest(baselineSource, i, PdfMembershipInstructionV12.Instruction,
                        (manifest[i]!["start"]!.GetValue<int>(), manifest[i]!["end"]!.GetValue<int>()));
                    a = JsonNode.Parse(matched.Body)!;
                    File.WriteAllText(Path.Combine(directory, variant, $"matched-baseline-{i:000}.json"), matched.Body);
                }
                JsonObject Payload(JsonNode wire) => JsonNode.Parse(wire["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
                var pa = Payload(a); var pb = Payload(b);
                var geometry = pb["pageGeometryEvidence"]!.AsObject();
                foreach (var page in geometry["pages"]!.AsArray().OfType<JsonObject>())
                {
                    foreach (var field in geometry["sharedPageFacts"]!.AsObject()) page[field.Key] = field.Value?.DeepClone();
                    if (page["occurrenceColumns"] is not JsonArray columns) continue;
                    page["occurrences"] = new JsonArray(page["occurrences"]!.AsArray().Select(row =>
                    {
                        var obj = new JsonObject();
                        for (var k = 0; k < columns.Count; k++) obj[columns[k]!.GetValue<string>()] = row![k]?.DeepClone();
                        return (JsonNode)obj;
                    }).ToArray());
                    page.Remove("occurrenceColumns");
                }
                geometry.Remove("encoding"); geometry.Remove("encodingNote"); geometry.Remove("sharedPageFacts");
                if (!JsonNode.DeepEquals(pa, pb)) throw new InvalidOperationException("EVIDENCE_ROUNDTRIP_FAILED:" + variant + ":" + i);
                if (!JsonNode.DeepEquals(a["messages"]![0], b["messages"]![0]) || !JsonNode.DeepEquals(a["tools"], b["tools"]))
                    throw new InvalidOperationException("SYSTEM_OR_TOOLS_CHANGED");
                if (b["max_tokens"]!.GetValue<int>() > a["max_tokens"]!.GetValue<int>()) throw new InvalidOperationException("INITIAL_BUDGET_GREW");
                var ai = a["messages"]![1]!["content"]!.AsArray().Skip(1).ToArray();
                var bi = b["messages"]![1]!["content"]!.AsArray().Skip(1).ToArray();
                if (ai.Length != bi.Length) throw new InvalidOperationException("IMAGE_COVERAGE_CHANGED");
                if (variant == "geometry" && !ai.Zip(bi).All(pair => JsonNode.DeepEquals(pair.First, pair.Second)))
                    throw new InvalidOperationException("GEOMETRY_ONLY_IMAGES_CHANGED");
                checkedPackets++;
            }
        }
        File.WriteAllText(Path.Combine(directory, "evidence-roundtrip-audit.json"), JsonSerializer.Serialize(new {
            checkedPackets, changedScopes, allTextFactsContextUnitsAndGeometryEqualAfterDecodingOnMatchedScopes = true,
            systemAndToolsUnchanged = true, pageImageCountUnchanged = true, providerCalls = 0, goldRead = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"EVIDENCE_ROUNDTRIP_PASS packets={checkedPackets}");
    }

    internal static void Run(string pdf, string output)
    {
        if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var summaries = new List<object>();
        (string Alias, string Text)[]? originalLines = null;
        string[]? originalUnits = null;
        foreach (var variant in new[] { "baseline", "geometry", "geometry-images", "geometry-images-budget", "geometry-images-budget-gray" })
        {
            var timer = Stopwatch.StartNew();
            var compact = variant != "baseline";
            var images = variant is "geometry-images" or "geometry-images-budget" or "geometry-images-budget-gray";
            var dynamic = variant is "geometry-images-budget" or "geometry-images-budget-gray";
            var grayscale = variant == "geometry-images-budget-gray";
            var source = AgentSource.Load(pdf, out var failure, new RequestPolicy { MaxRequestBytes = 786432 },
                PdfMembershipInstructionV12.Instruction, sourceBound: true, unitSelection: true,
                rasterGeometry: true, adaptiveVisualSelection: true, compactGeometry: compact,
                visualMaxWidth: images ? 960 : 1200, visualQuality: images ? 50 : 58,
                dynamicCompletionBudget: dynamic, visualGrayscale: grayscale) ?? throw new InvalidOperationException(failure);
            var lines = source.Lines.Select(l => (l.Alias, l.Text)).ToArray();
            var units = PdfSelectionUnits.Build(source).Select(u => u.UnitId + ":" + string.Join(",", u.Members)).ToArray();
            originalLines ??= lines; originalUnits ??= units;
            if (!originalLines.SequenceEqual(lines) || !originalUnits.SequenceEqual(units)) throw new InvalidOperationException("SOURCE_OR_UNIT_CHANGED");
            if (!source.Cores.SelectMany(c => Enumerable.Range(c.Start, c.End - c.Start)).SequenceEqual(Enumerable.Range(0, lines.Length)))
                throw new InvalidOperationException("COVERAGE_CHANGED");
            var directory = Path.Combine(output, variant); Directory.CreateDirectory(directory);
            var requests = new List<object>();
            long total = 0, imageBytes = 0, geometryBytes = 0, textBytes = 0;
            var caps = new List<int>(); var pageImages = 0;
            foreach (var i in Enumerable.Range(0, source.Cores.Count))
            {
                var request = PdfUnitSelectionTask.PrepareFirstRequest(source, i, PdfMembershipInstructionV12.Instruction);
                if (request.Sha256 != source.Plan[i].RequestSha256) throw new InvalidOperationException("PLANNED_ACTUAL_SHA_MISMATCH");
                if (source.ResourcePolicy.Refusal(RequestResources.Measure(request, source.ResourcePolicy), true) is { } refusal)
                    throw new InvalidOperationException(refusal);
                File.WriteAllText(Path.Combine(directory, request.Sha256 + ".request.json"), request.Body);
                var wire = JsonNode.Parse(request.Body)!;
                var content = wire["messages"]![1]!["content"]!.AsArray();
                var text = content[0]!["text"]!.GetValue<string>();
                var packet = JsonNode.Parse(text)!;
                if (packet["instruction"]!.GetValue<string>() != PdfMembershipInstructionV12.Instruction) throw new InvalidOperationException("PROMPT_CHANGED");
                total += request.Bytes; textBytes += Encoding.UTF8.GetByteCount(text);
                geometryBytes += Encoding.UTF8.GetByteCount(packet["pageGeometryEvidence"]!.ToJsonString(AgentUnit.ReviewJson));
                var cap = wire["max_tokens"]!.GetValue<int>(); caps.Add(cap);
                foreach (var image in content.Where(c => c!["type"]!.GetValue<string>() == "image_url"))
                { imageBytes += image!["image_url"]!["url"]!.GetValue<string>().Length; pageImages++; }
                var core = source.Cores[i];
                requests.Add(new { segment = i, request.Sha256, request.Bytes, maxTokens = cap,
                    start = core.Start, end = core.End, units = packet["targetUnits"]!.AsArray().Count,
                    pages = source.Lines.Skip(core.Start).Take(core.End - core.Start).Select(l => l.Page).Distinct().ToArray() });
            }
            summaries.Add(new { variant, requests = requests.Count, totalBytes = total, imageBytes, geometryBytes, textBytes, pageImages,
                minCompletion = caps.Min(), maxCompletion = caps.Max(), meanCompletion = caps.Average(),
                sourceCount = lines.Length, units = units.Length, buildSeconds = timer.Elapsed.TotalSeconds });
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { source.SourceSha256,
                source.PlanId, instructionSha256 = AgentProtocol.Sha(PdfMembershipInstructionV12.Instruction), requests }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PACKETS {variant} count={requests.Count} bytes={total} geometry={geometryBytes} images={imageBytes} cap={caps.Min()}..{caps.Max()}");
        }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { pdf, providerCalls = 0, goldRead = false, variants = summaries }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
