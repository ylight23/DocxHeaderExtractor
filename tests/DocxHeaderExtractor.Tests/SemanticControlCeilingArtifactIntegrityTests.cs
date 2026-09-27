using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class SemanticControlCeilingArtifactIntegrityTests
{
    private const string Root = "eval/a99-closed-loop/semantic-control-ceiling-study";

    [Fact]
    public void Provider_free_study_artifacts_are_pinned_and_stop_at_authorization_gate()
    {
        using var reasoning = Read("reasoning-transport-audit.v1.json");
        using var residual = Read("residual-causal-audit.v1.json");
        using var ontology = Read("semantic-function-expressiveness-audit.v1.json");
        using var preflight = Read("r1-reasoning-preflight.v1.json");

        Assert.Equal("a99_semantic_control_ceiling_reasoning_transport_audit", reasoning.RootElement.GetProperty("artifactKind").GetString());
        Assert.Equal("none", reasoning.RootElement.GetProperty("actualProviderValue").GetString());
        Assert.False(reasoning.RootElement.GetProperty("sourceEvidence").GetProperty("reasoningIsUnconditional").GetBoolean());
        Assert.Equal(0, reasoning.RootElement.GetProperty("providerObservedReasoning").GetProperty("responsesWithReasoningTokens").GetInt32());
        Assert.Equal(34, residual.RootElement.GetProperty("summary").GetProperty("MODEL_SEMANTIC_MISCLASSIFICATION").GetInt32());
        Assert.Equal(18, residual.RootElement.GetProperty("summary").GetProperty("CLAIM_EXTENT_ERROR").GetInt32());
        Assert.Equal(0, residual.RootElement.GetProperty("summary").GetProperty("ONTOLOGY_GAP").GetInt32());
        Assert.Equal(9, ontology.RootElement.GetProperty("functions").GetArrayLength());
        Assert.Equal(6, ontology.RootElement.GetProperty("boundaryMatrix").GetArrayLength());
        Assert.Equal("V4R1_SEMANTIC_FUNCTION_EXPLICIT_REASONING", preflight.RootElement.GetProperty("requestVersion").GetString());
        Assert.Equal(25, preflight.RootElement.GetProperty("calls").GetProperty("total").GetInt32());
        Assert.True(preflight.RootElement.GetProperty("gates").GetProperty("providerAuthorizationRequired").GetBoolean());
        Assert.Equal(0, preflight.RootElement.GetProperty("gates").GetProperty("providerCalls").GetInt32());
    }

    private static JsonDocument Read(string file) =>
        JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{file}")));
}
