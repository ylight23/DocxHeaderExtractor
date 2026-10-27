using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Runtime;

if (args.Length != 4) throw new ArgumentException("Usage: <frozen-run-result> <source-facts> <output> <identity-json>");
var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
if (File.Exists(Path.Combine(output, "trial-start.json"))) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API_KEY_NOT_CONFIGURED");
using var runDoc = JsonDocument.Parse(File.ReadAllText(args[0])); var run = runDoc.RootElement;
using var factsDoc = JsonDocument.Parse(File.ReadAllText(args[1])); var facts = factsDoc.RootElement;
var messages = run.GetProperty("pipelineAudit").GetProperty("rawModelResponses").EnumerateArray()
    .Select(v => JsonDocument.Parse(v.GetString()!).RootElement.Clone()).ToArray();
var replay = messages.Where(v => v.TryGetProperty("stage", out var s) && s.GetString() == "HIERARCHY_PLACE").ToArray();
using var packetDoc = JsonDocument.Parse(replay[0].GetProperty("request").GetString()!); var packet = packetDoc.RootElement;
var sourceHash = run.GetProperty("input").GetProperty("sha256").GetString()!;
if (facts.GetProperty("SourceSha256").GetString() != sourceHash) throw new InvalidOperationException("SOURCE_HASH_MISMATCH");
var ids = run.GetProperty("placementExecution").GetProperty("SourceTrace").EnumerateArray()
    .ToDictionary(v => v.GetProperty("Alias").GetString()!, v => v.GetProperty("SourceId").GetString()!);
var lines = facts.GetProperty("lines").EnumerateArray().Select(v => new SemanticSourceAtom(v.GetProperty("Alias").GetString()!,
    ids[v.GetProperty("Alias").GetString()!], v.GetProperty("Ordinal").GetInt32(), v.GetProperty("Page").GetInt32(),
    v.GetProperty("Ordinal").GetInt32(), 0, v.GetProperty("Text").GetString()!)).ToArray();
var byAlias = lines.ToDictionary(l => l.Alias);
var states = messages.Single(v => v.TryGetProperty("stage", out var s) && s.GetString() == "pre-binding-alias-state")
    .GetProperty("aliases").Deserialize<Dictionary<string, HeadingAliasState>>()!;
var extents = messages.Where(v => v.TryGetProperty("unit", out _) && v.TryGetProperty("headingExtents", out var e) && e.ValueKind == JsonValueKind.Object)
    .SelectMany(v => v.GetProperty("headingExtents").EnumerateObject()).ToDictionary(v => v.Name, v => v.Value.Clone());
var bound = packet.GetProperty("headings").EnumerateArray().Select(h =>
{
    var alias = h.GetProperty("alias").GetString()!; var line = byAlias[alias];
    var parts = h.GetProperty("parts").EnumerateArray().Select(a =>
    {
        var p = byAlias[a.GetString()!];
        var start = extents.TryGetValue(p.Alias, out var e) ? e.GetProperty("start").GetInt32() : 0;
        var end = extents.TryGetValue(p.Alias, out e) ? e.GetProperty("end").GetInt32() : p.Text.Length;
        return new CanonicalSemanticBoundPart(p.Alias, p.SourceId, p.Ordinal, p.Text[start..end], start, end);
    }).ToArray();
    return new CanonicalSemanticBoundHeading(alias, line.SourceId, h.GetProperty("ordinal").GetInt32(), h.GetProperty("text").GetString()!,
        "ESTABLISHES_STRUCTURE", "heading", "document_body", [], parts.Length > 0 ? parts[0].Start : 0,
        parts.Length > 0 ? parts[0].End : line.Text.Length) { Parts = parts };
}).ToArray();
var evidence = new HeadingPlacementEvidence(packet.GetProperty("documentStyles").GetRawText(), packet.GetProperty("headings").EnumerateArray()
    .Where(h => h.GetProperty("style").ValueKind != JsonValueKind.Null).ToDictionary(h => h.GetProperty("alias").GetString()!, h => h.GetProperty("style").GetRawText()),
    packet.GetProperty("measurementsNote").ValueKind == JsonValueKind.String ? packet.GetProperty("measurementsNote").GetString() : null)
    { Source = new(lines, states, sourceHash) };
var json = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(output, "trial-start.json"), JsonSerializer.Serialize(new
{
    sourceRunId = run.GetProperty("runId").GetString(), sourceSha256 = sourceHash,
    frozenRunSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[0]))),
    buildIdentity = JsonDocument.Parse(File.ReadAllText(args[3])).RootElement.Clone(),
    replayedInitialPlacementCalls = replay.Length, liveRepairOnly = true
}, json));
var runBudget = new RunResourceBudget(0.25, 40); using var scope = runBudget.Enter();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var budget = new AgentBudget(40, 0.25);
using var handler = AgentModel.CreateHandler();
using var client = new AgentModel(handler, key, budget, cancellationToken: deadline.Token, rateLimitRetries: AgentModel.ProductionRateLimitRetries);
var access = new ModelAccess(key, () => deadline.Token.ThrowIfCancellationRequested());
var replayIndex = 0; var liveCalls = 0;
ModelTurn Model(PreparedRequest request)
{
    var body = JsonNode.Parse(request.Body)!;
    var packetText = body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>();
    var p = JsonNode.Parse(packetText)!;
    if (p["stage"]!.GetValue<string>() == "HIERARCHY_PLACE")
    {
        if (replayIndex >= replay.Length || !JsonNode.DeepEquals(p, JsonNode.Parse(replay[replayIndex].GetProperty("request").GetString()!)))
            throw new InvalidOperationException("FROZEN_PLACEMENT_REQUEST_CHANGED");
        return new(replay[replayIndex++].GetProperty("response").GetString(), [], "stop");
    }
    liveCalls++;
    File.WriteAllText(Path.Combine(output, $"repair-{liveCalls:00}.request.json"), request.Body);
    var turn = client.Turn("HIERARCHY_CHAIN_REPAIR-LIVE", liveCalls, request);
    File.WriteAllText(Path.Combine(output, $"repair-{liveCalls:00}.reply.json"), JsonSerializer.Serialize(turn, json));
    return turn;
}
var clock = Stopwatch.StartNew();
var baseline = await ParentResolver.PlaceAsync(bound, new HeadingPlacer(Model), access, evidence, deadline.Token);
if (replayIndex != replay.Length || baseline.Observation.UnresolvedCount != run.GetProperty("placementExecution").GetProperty("UnresolvedCount").GetInt32())
    throw new InvalidOperationException("FROZEN_BASELINE_NOT_REPRODUCED");
replayIndex = 0;
var candidate = await ParentResolver.PlaceAsync(bound, new HeadingPlacer(Model, repairParentChains: true), access, evidence, deadline.Token);
if (replayIndex != replay.Length) throw new InvalidOperationException("FROZEN_REPLAY_INCOMPLETE");
clock.Stop();
if (!baseline.Headings.Select(h => (h.Alias, h.Text, h.Start, h.End)).SequenceEqual(candidate.Headings.Select(h => (h.Alias, h.Text, h.Start, h.End))))
    throw new InvalidOperationException("FROZEN_HEADINGS_CHANGED");
File.WriteAllText(Path.Combine(output, "trial-result.json"), JsonSerializer.Serialize(new
{
    elapsedSeconds = clock.Elapsed.TotalSeconds, frozenSelection = true, frozenInitialPlacement = true,
    replayedCallsPerArm = replay.Length, liveRepairCalls = liveCalls, baseline = baseline.Observation, candidate = candidate.Observation,
    liveResources = ResourceAudit.Observe(budget), runResourceBudget = runBudget.Observe(),
    candidateEvidence = candidate.Observation.ModelEvidence
}, json));
Console.WriteLine(JsonSerializer.Serialize(new { baselineUnresolved = baseline.Observation.UnresolvedCount,
    candidateUnresolved = candidate.Observation.UnresolvedCount, liveCalls, seconds = clock.Elapsed.TotalSeconds, runBudget = runBudget.Observe() }));
