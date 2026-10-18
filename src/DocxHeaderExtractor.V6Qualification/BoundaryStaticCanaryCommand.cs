using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Source.Scan;
using DocxHeaderExtractor.HeadingAgent;
using DocxHeaderExtractor.HeadingAgent.Scan;

namespace DocxHeaderExtractor.V6Qualification;

internal static class BoundaryStaticCanaryCommand
{
    internal sealed record Scope(string Id, string[] Aliases);
    internal sealed record Manifest(string SourceDocument, string OcrEvidence, string EvidenceEntry, string StoreRoot, Scope[] Cases, int Pairs = 2);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    sealed class CacheOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("QUALIFICATION_TRANSCRIPTION_CACHE_MISS");
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 4 || args[3] != "--live")
        {
            Console.Error.WriteLine("boundary-static-ab <manifest.json> <new-output-directory> --live; requires OPENROUTER_API_KEY. Qualification only.");
            return 2;
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(args[1]), Json)!;
        if (manifest.Pairs is < 1 or > 4 || manifest.Cases.Length is < 1 or > 8
            || manifest.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Cases.Length
            || manifest.Cases.Any(c => c.Aliases.Length is < 2 or > 24 || c.Aliases.Distinct(StringComparer.Ordinal).Count() != c.Aliases.Length)
            || manifest.Cases.Any(c => c.Id.Length == 0 || c.Id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-')))
            throw new InvalidOperationException("INVALID_CANARY_MANIFEST");
        var output = Path.GetFullPath(args[2]);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("CANARY_OUTPUT_EXISTS");
        Directory.CreateDirectory(Path.Combine(output, "private"));
        void Private(string name, string text) => File.WriteAllText(Path.Combine(output, "private", name + ".json"), text);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var ct = cancellation.Token;
        var evidence = JsonSerializer.Deserialize<ScanOcrEvidence>(File.ReadAllText(manifest.OcrEvidence), Json)!;
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest.SourceDocument))) != evidence.SourceSha256)
            throw new InvalidOperationException("CANARY_SOURCE_HASH_MISMATCH");
        Console.WriteLine("qualification: reading cached transcription and static source snapshot (new preflight HTTP forbidden)");
        var transcriptions = new ScanTranscriptionPreflight(manifest.StoreRoot, () => new CacheOnlyHandler())
            .Run(evidence, manifest.EvidenceEntry, "qualification-cache-only", ct: ct);
        if (transcriptions.ProviderCalls != 0 || transcriptions.Failures.Count != 0)
            throw new InvalidOperationException("CANARY_REQUIRES_COMPLETE_CACHED_TRANSCRIPTION");
        var typography = ScanTypography.MeasureAll(evidence, manifest.EvidenceEntry, TesseractOcr.FindImageMagick(), ct);
        var source = new ScanAgentSource(evidence, transcriptions: transcriptions, typography: typography, evidenceEntry: manifest.EvidenceEntry);
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API_KEY_MISSING");
        var budget = new AgentBudget(180, 10);
        using var handler = AgentModel.CreateHandler();
        using var provider = new AgentModel(handler, apiKey, budget, cancellationToken: ct);
        AgentUnit.Model model = (unit, turn, request) =>
        {
            Console.WriteLine(unit + " POST " + turn);
            return provider.Turn(unit, turn, request);
        };
        var positions = source.Lines.Select((l, i) => (l.Alias, i)).ToDictionary(x => x.Alias, x => x.i);
        if (manifest.Cases.Any(c => !c.Aliases.Select(a => positions[a]).SequenceEqual(Enumerable.Range(positions[c.Aliases[0]], c.Aliases.Length))))
            throw new InvalidOperationException("CANARY_SCOPE_MUST_BE_CONTIGUOUS_AND_ORDERED");
        var coreIds = manifest.Cases.SelectMany(c => c.Aliases).Distinct().Select(a => positions[a])
            .Select(i => Enumerable.Range(0, source.Cores.Count).Single(n => i >= source.Cores[n].Start && i < source.Cores[n].End)).Distinct().Order().ToArray();
        // Only source cores covering the explicit target scopes; no full extraction, hierarchy, or writeback.
        var selections = new List<AgentUnitOutcome>();
        foreach (var core in coreIds)
        {
            var result = AgentUnit.Run(source, core, model, budget, 0, ScanAgentProtocol.Instruction);
            selections.Add(result);
            Private("selection-" + core, JsonSerializer.Serialize(result, Json));
        }
        var selected = selections.Where(s => s.Status == "COMPLETE").SelectMany(s => s.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key)).ToHashSet(StringComparer.Ordinal);
        var extents = selections.Where(s => s.Status == "COMPLETE" && s.Extents is not null)
            .SelectMany(s => s.Extents!).Where(k => selected.Contains(k.Key))
            .ToDictionary(k => k.Key, k => new TextOffsetSpan(k.Value.Start, k.Value.End));
        var readings = selections.Where(s => s.Status == "COMPLETE" && s.ReadingChoices is not null)
            .SelectMany(s => s.ReadingChoices!).Where(k => selected.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value);
        var cases = new List<object>();
        void Save() => File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            qualificationOnly = true, productionAuthorization = false, manifest.Pairs,
            source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            selectionCoreCount = coreIds.Length, providerPosts = budget.Calls, costUsd = budget.ActualUsd,
            cachePreflightProviderCalls = transcriptions.ProviderCalls, cases
        }, Json));
        Save();
        foreach (var scope in manifest.Cases)
        {
            if (scope.Aliases.Any(a => !selected.Contains(a)))
                cases.Add(new { scope.Id, status = "SELECTION_NOT_ALL_HEADING", aliases = scope.Aliases });
            else
            {
                var proposal = AgentGrouping.Group(source, scope.Aliases.ToHashSet(StringComparer.Ordinal), model, budget, 1, ct,
                    0, ScanAgentProtocol.Instruction, scope.Id + "-INITIAL");
                Private(scope.Id + "-initial", JsonSerializer.Serialize(proposal, Json));
                if (proposal.Failures.Count > 0 || proposal.Disputes.Count > 0 || !proposal.Groups.SelectMany(g => g).SequenceEqual(scope.Aliases))
                    cases.Add(new { scope.Id, status = "NO_VALID_INITIAL_PARTITION", aliases = scope.Aliases });
                else
                    cases.Add(BoundaryStaticCanary.Run(scope.Id, source, proposal.Groups.Select(g => g.ToArray()).ToArray(),
                        extents, readings, model, budget, manifest.Pairs, ct, Private));
            }
            Save();
        }
        Console.WriteLine("qualification complete; safe report.json and private forensic snapshots saved locally");
        return 0;
    }
}
