using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.Tests;

/// <summary>A model provider for lane tests: its route carries the given key, and its placer is a script, so no test reaches a provider.</summary>
internal sealed class ScriptedProvider(string? key) : IModelProvider
{
    public PlacementScript Placement { get; } = new();
    public int PlacementCalls => Placement.Calls;
    public string? LastPrompt => Placement.LastPrompt;
    public string? LastPacket => Placement.LastPacket;
    public ModelRoute CreateRoute() => new("test", key);
}
