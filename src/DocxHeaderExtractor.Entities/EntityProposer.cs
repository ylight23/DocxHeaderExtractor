using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Agents.Transport;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Problems;
using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.Entities;

/// <summary>
/// The AI half of entity extraction: the units of the document are sent in batches, and the model answers each batch with the entities it
/// finds, as spans of the unit's text. One model turn per batch, no tools. A batch whose reply fails is recorded and the others still run.
/// </summary>
public sealed class EntityProposer(Func<HttpMessageHandler>? handlerFactory = null) : IProposer<EntityProposal>
{
    public const string Version = "entities-v1";
    public const int MaxBatchChars = 6000, MaxBatchUnits = 40, MaxAttempts = 2;
    /// <summary>The most one run may spend on the model (the budget reserves each POST's worst case).</summary>
    public const double CeilingUsd = 1.0;

    const string SystemPrompt =
        "Find the named entities in the document units you are given. Each unit has a sourceId and a text. " +
        "Answer only with a JSON object {\"entities\":[{\"sourceId\":\"...\",\"start\":0,\"end\":0,\"type\":\"...\"}]}. " +
        "start and end are character offsets into that unit's text: the entity is text[start:end], end exclusive. " +
        "Use only the types listed in types. Use only the text you were given: do not invent entities or offsets. " +
        "A unit with no entities contributes none.";

    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Contract => Version;

    public Task<EntityProposal> ProposeAsync(ProblemSource source, ModelAccess access, CancellationToken ct) =>
        Task.Run(() => Propose(source, access, ct), ct);

    EntityProposal Propose(ProblemSource source, ModelAccess access, CancellationToken ct)
    {
        var batches = Batches(source.Catalog.Units);
        var makeHandler = handlerFactory ?? (() => AgentModel.CreateHandler());
        using var handler = makeHandler();
        using var model = new AgentModel(handler, access.ApiKey, new AgentBudget(batches.Count * 3, CeilingUsd), cancellationToken: ct,
            rateLimitRetries: AgentModel.ProductionRateLimitRetries);
        var found = new List<ProposedEntity>();
        var failures = new List<string>();
        for (var k = 0; k < batches.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            access.EnsureActive();
            var id = $"batch-{k + 1:000}";
            // A batch gets MaxAttempts tries with the same bytes: a failed reply is often a single bad sample, not a bad batch.
            string? failure = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var request = OpenRouterWire.Build(SystemPrompt, PayloadOf(batches[k]), new JsonArray(), null, null);
                    found.AddRange(ParseReply(model.Turn(id, attempt, request).Content));
                    failure = null;
                    break;
                }
                catch (ReplyFailure ex)
                {
                    failure = ex.Code;
                }
                catch (Exception ex) when (ex is FormatException or JsonException)
                {
                    failure = "REPLY_NOT_JSON";
                }
                access.EnsureActive();
            }
            if (failure is not null) failures.Add($"{id}:{failure}");
            access.EnsureActive();
        }
        return new EntityProposal(found, failures);
    }

    /// <summary>Consecutive units with text, packed until a batch reaches the character or unit limit. A unit longer than the limit is a batch of its own.</summary>
    internal static List<List<DocumentSourceUnit>> Batches(IEnumerable<DocumentSourceUnit> units)
    {
        var batches = new List<List<DocumentSourceUnit>>();
        var current = new List<DocumentSourceUnit>();
        var chars = 0;
        foreach (var u in units.Where(u => !string.IsNullOrWhiteSpace(u.Text)))
        {
            if (current.Count > 0 && (chars + u.Text.Length > MaxBatchChars || current.Count >= MaxBatchUnits))
            {
                batches.Add(current);
                current = [];
                chars = 0;
            }
            current.Add(u);
            chars += u.Text.Length;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    static string PayloadOf(IReadOnlyList<DocumentSourceUnit> batch)
    {
        var payload = new JsonObject
        {
            ["types"] = new JsonArray(EntityTypes.All.Select(t => (JsonNode)t).ToArray()),
            ["units"] = new JsonArray(batch.Select(u => (JsonNode)new JsonObject { ["sourceId"] = u.SourceId, ["text"] = u.Text }).ToArray()),
        };
        return payload.ToJsonString(Json);
    }

    /// <summary>The entities of one reply. A field that is missing or of the wrong kind becomes a value the checker refuses.</summary>
    internal static IReadOnlyList<ProposedEntity> ParseReply(string? content)
    {
        if (content is null) throw new FormatException("NO_CONTENT");
        var root = JsonNode.Parse(content) as JsonObject ?? throw new FormatException("NOT_AN_OBJECT");
        var list = root["entities"] as JsonArray ?? new JsonArray();
        var entities = new List<ProposedEntity>();
        foreach (var item in list)
        {
            var o = item as JsonObject;
            entities.Add(new ProposedEntity(
                o?["sourceId"] is JsonValue s && s.TryGetValue<string>(out var sid) ? sid : "",
                o?["start"] is JsonValue st && st.TryGetValue<int>(out var start) ? start : -1,
                o?["end"] is JsonValue en && en.TryGetValue<int>(out var end) ? end : -1,
                o?["type"] is JsonValue t && t.TryGetValue<string>(out var type) ? type : ""));
        }
        return entities;
    }
}
