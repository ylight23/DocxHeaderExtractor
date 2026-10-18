using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.V6Qualification;

internal static class BoundarySuggestedCanaryCommand
{
    sealed class CacheOnly : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("QUALIFICATION_TRANSCRIPTION_CACHE_MISS");
    }
    internal static int Run(string[] args)
    {
        if (args.Length != 6 || args[5] != "--live" || args[0] is not ("boundary-suggested-c" or "boundary-grounded-d"))
        {
            Console.Error.WriteLine("boundary-suggested-c|boundary-grounded-d <ab-manifest.json> <seed.json> <frozen.json> <new-output-directory> --live");
            return 2;
        }
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var manifest = JsonSerializer.Deserialize<BoundaryStaticCanaryCommand.Manifest>(File.ReadAllText(args[1]), json)!;
        var seed = JsonNode.Parse(File.ReadAllText(args[2]))!.AsObject();
        var frozen = JsonNode.Parse(File.ReadAllText(args[3]))!.AsObject();
        var output = Path.GetFullPath(args[4]);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("CANARY_OUTPUT_EXISTS");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var ct = cancellation.Token;
        var source = LoadSource(manifest, ct);
        if (frozen["SourceSha256"]?.GetValue<string>() != source.SourceSha256 ||
            frozen["SourceAliasUniverseSha256"]?.GetValue<string>() != source.SourceAliasUniverseSha256 ||
            frozen["EvidenceStoreSha256"]?.GetValue<string>() != source.EvidenceStoreSha256 ||
            seed["packet"]!["documentSha256"]?.GetValue<string>() != source.SourceSha256)
            throw new InvalidOperationException("FROZEN_SOURCE_MISMATCH");
        var target = seed["packet"]!["candidates"]![0]!;
        var oldStatic = frozen["staticEvidence"]!;
        if (!JsonNode.DeepEquals(seed["Verdicts"]![0], frozen["feedback"]!["verifier"]) ||
            !JsonNode.DeepEquals(target["proposedGroups"], frozen["feedback"]!["proposedGroups"]) ||
            !JsonNode.DeepEquals(target["targetFacts"], oldStatic["targetFacts"]) ||
            !JsonNode.DeepEquals(target["lines"], oldStatic["lines"]) ||
            !JsonNode.DeepEquals(seed["packet"]!["context"], oldStatic["context"]) ||
            !JsonNode.DeepEquals(seed["packet"]!["documentStyles"], oldStatic["documentStyles"]))
            throw new InvalidOperationException("FROZEN_PACKET_MISMATCH");
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_MISSING");
        Directory.CreateDirectory(Path.Combine(output, "private"));
        File.Copy(args[2], Path.Combine(output, "private", "seed.json"));
        File.Copy(args[3], Path.Combine(output, "private", "frozen.json"));
        var budget = new AgentBudget(12, 1);
        using var handler = AgentModel.CreateHandler();
        using var provider = new AgentModel(handler, key, budget, cancellationToken: ct);
        AgentUnit.Model model = (unit, turn, request) =>
        {
            Console.WriteLine(unit + " POST " + turn);
            return provider.Turn(unit, turn, request);
        };
        void Retain(string name, string text) => File.WriteAllText(Path.Combine(output, "private", name + ".json"), text);
        var grounded = args[0] == "boundary-grounded-d";
        object result = grounded ? BoundaryGroundedCanary.Run(source, seed, model, budget, ct, Retain)
            : BoundarySuggestedCanary.Run(source, seed, model, budget, ct, Retain);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            qualificationOnly = true, productionAuthorization = false,
            experiment = grounded ? "D: grounded REGROUP and bounded consistency repair with frozen C0 complaint"
                : "C: fresh proposer with frozen C0 complaint",
            source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            providerPosts = budget.Calls, costUsd = budget.ActualUsd, result
        }, json));
        Console.WriteLine("qualification complete; safe report.json saved; no production headings emitted");
        return 0;
    }

    internal static ScanAgentSource LoadSource(BoundaryStaticCanaryCommand.Manifest manifest, CancellationToken ct)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var evidence = JsonSerializer.Deserialize<ScanOcrEvidence>(File.ReadAllText(manifest.OcrEvidence), json)!;
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest.SourceDocument))) != evidence.SourceSha256)
            throw new InvalidOperationException("CANARY_SOURCE_HASH_MISMATCH");
        var transcription = new ScanTranscriptionPreflight(manifest.StoreRoot, () => new CacheOnly())
            .Run(evidence, manifest.EvidenceEntry, "qualification-cache-only", ct: ct);
        if (transcription.ProviderCalls != 0 || transcription.Failures.Count != 0)
            throw new InvalidOperationException("CANARY_REQUIRES_COMPLETE_CACHED_TRANSCRIPTION");
        var typography = ScanTypography.MeasureAll(evidence, manifest.EvidenceEntry, TesseractOcr.FindImageMagick(), ct);
        return new(evidence, transcriptions: transcription, typography: typography, evidenceEntry: manifest.EvidenceEntry);
    }
}
