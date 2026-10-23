using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A placer whose model is a script, so no test reaches a provider: every round's system prompt, packet and request are recorded, and the
/// reply comes from the script (by default the test protocol's parents for the rows asked for).
/// </summary>
internal sealed class PlacementScript
{
    readonly Func<JsonNode, string?> reply;
    readonly object gate = new();

    public PlacementScript(Func<JsonNode, string?>? reply = null)
    {
        this.reply = reply ?? (packet => HierarchyTestProtocol.Reply(packet.ToJsonString()));
        Placer = new HeadingPlacer(Answer);
    }

    /// <summary>Access for a run that never ends. The key is a placeholder: the script sends nothing anywhere.</summary>
    public static ModelAccess Access { get; } = new("sk-test", () => { });

    public HeadingPlacer Placer { get; }
    public int Calls { get; private set; }
    public string? LastPrompt { get; private set; }
    public string? LastPacket { get; private set; }
    /// <summary>Every request, as the provider would have received it.</summary>
    public List<PreparedRequest> Requests { get; } = [];

    ModelTurn Answer(PreparedRequest request)
    {
        var body = JsonNode.Parse(request.Body)!;
        var prompt = body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        var packet = body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>();
        lock (gate)
        {
            Calls++;
            LastPrompt = prompt;
            LastPacket = packet;
            Requests.Add(request);
        }
        return new ModelTurn(reply(JsonNode.Parse(packet)!) ?? "{}", [], "stop");
    }
}
