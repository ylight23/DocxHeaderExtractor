using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A stand-in model that claims the first few owned occurrences of each segment as headings.
/// <para>
/// The canonical lanes have no heuristic that proposes a heading, by design - with no model, a PDF
/// run legitimately produces none. That makes anything downstream of the semantic stage untestable
/// without either a provider call or a script, and a provider call would measure the model rather
/// than the wiring.
/// </para>
/// <para>
/// It reads the aliases out of the packet it is given rather than being told them, so it answers
/// whatever document it is pointed at and never needs updating when a fixture changes. It claims,
/// it does not judge: what it returns is not a statement about the document.
/// </para>
/// </summary>
internal sealed class ScriptedSemanticClassifier(int perSegment = 3) : IInferenceTransport
{
    public int Calls { get; private set; }

    public string ModelName => "scripted-owned-prefix";
    public int ContextSize => 1 << 20;
    public string RuntimeDescription => "test script, no provider";
    public int SharedPrefixTokens => 0;

    public Task<string> BoundaryCutAsync(
        string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
    {
        Calls++;
        var body = userMessage;
        var schemaAt = body.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
        if (schemaAt >= 0) body = body[..schemaAt];

        using var document = JsonDocument.Parse(body);
        // The placement pass sends a different packet and gets a benign answer: nothing to place
        // leaves the headings exactly as unresolved as they already were.
        if (!document.RootElement.TryGetProperty("sourceEvidence", out var evidence))
            return Task.FromResult("{\"placements\":[]}");

        var owned = evidence.EnumerateArray()
            .Where(item => item.TryGetProperty("owned", out var isOwned) && isOwned.GetBoolean())
            .Where(item => (item.GetProperty("text").GetString() ?? string.Empty).Trim().Length >= 8)
            .Take(perSegment)
            .Select(item => item.GetProperty("alias").GetString()!)
            .ToArray();

        // Answer in the shape of the contract that issued the request.
        var protocol = document.RootElement.TryGetProperty("protocol", out var p) ? p.GetString() : null;
        object[] claimed = protocol == SemanticFunctionMembershipContractV1.ProtocolVersion
            ? owned.Select(alias => (object)new
            {
                sourceParts = new[] { new { sourceAlias = alias } },
                semanticFunction = "REGION_STRUCTURE",
            }).ToArray()
            : owned.Select(alias => (object)new
            {
                sourceAlias = alias,
                isHeading = true,
                semanticRole = "SECTION",
                selectionMode = CanonicalSemanticSelectionMode.WholeAlias,
            }).ToArray();

        return Task.FromResult(JsonSerializer.Serialize(new { headings = claimed }));
    }

    public void Dispose() { }
}
