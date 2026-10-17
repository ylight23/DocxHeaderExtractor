using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.DocumentProcessing.Materialization;

namespace DocxHeaderExtractor.Tests;

/// <summary>Scripted protocol replies for existing lane fixtures, never a live accuracy measurement.</summary>
internal static class HierarchyTestProtocol
{
    internal static string? Reply(string packetText, IReadOnlyDictionary<string, string>? parents = null)
    {
        var packet = JsonNode.Parse(packetText)!;
        if (packet["protocolVersion"]?.GetValue<string>() != EvidenceHeadingPlacement.Version) return null;
        var aliases = packet["toPlace"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        if (packet["stage"]!.GetValue<string>() is "HIERARCHY_VERIFY" or "HIERARCHY_REVERIFY")
            return Accept(packet, aliases);
        return JsonSerializer.Serialize(new { protocolVersion = EvidenceHeadingPlacement.Version,
            placements = aliases.Select(a => new { alias = a, parent = parents?.GetValueOrDefault(a) ?? "ROOT" }) });
    }

    internal static string Accept(JsonNode packet, IEnumerable<string> aliases) => JsonSerializer.Serialize(new
    {
        protocolVersion = EvidenceHeadingPlacement.Version,
        verdicts = aliases.Select(a =>
        {
            var parent = packet["proposedRelations"]!.AsArray().Single(p => p!["alias"]!.GetValue<string>() == a)!["parent"]!.GetValue<string>();
            return new { alias = a, decision = parent == "UNRESOLVED" ? "UNRESOLVED" : "ACCEPT", reason = "Scripted source-grounded parent judgement.",
                evidenceRefs = parent is "ROOT" or "NONE" or "UNRESOLVED" ? new[] { a } : new[] { a, parent }, blockingAliases = Array.Empty<string>() };
        })
    });
}
