using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.HeadingAgent.Placement;

namespace DocxHeaderExtractor.Tests;

/// <summary>Qualification variants of the placement call: each changes one thing, and production sends what it sent before.</summary>
public sealed class PlacementVariantTests
{
    static JsonNode Body(PlacementVariant? variant) => JsonNode.Parse(HeadingPlacer.Request("{}", 512, variant: variant).Body)!;
    static string Prompt(JsonNode body) => body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public void Production_sends_the_same_request_as_before()
    {
        Assert.Equal(HeadingPlacer.Request("{}", 512).Body, HeadingPlacer.Request("{}", 512, variant: PlacementVariant.Production).Body);
        var body = Body(null);
        Assert.Equal("none", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Contains(PlacementVariant.DiscountSentence, Prompt(body), StringComparison.Ordinal);
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void The_reasoning_variant_turns_reasoning_on_and_leaves_room_for_it()
    {
        var body = Body(new PlacementVariant(ReasoningEffort: "low"));
        Assert.Equal("low", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal(512 + 4096, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(HeadingPlacer.SourcePrompt, Prompt(body));
    }

    [Fact]
    public void The_typography_variant_replaces_only_the_discount_sentence()
    {
        var prompt = Prompt(Body(new PlacementVariant(TypographyAsEvidence: true)));
        Assert.DoesNotContain(PlacementVariant.DiscountSentence, prompt, StringComparison.Ordinal);
        Assert.Contains(PlacementVariant.TypographySentence, prompt, StringComparison.Ordinal);
        Assert.Equal(HeadingPlacer.SourcePrompt.Replace(PlacementVariant.DiscountSentence, PlacementVariant.TypographySentence, StringComparison.Ordinal), prompt);
    }

    static (CanonicalSemanticBoundHeading[], HeadingPlacementEvidence) Two()
    {
        var lines = new[] { new SemanticSourceAtom("A", "id:A", 0, 1, 0, 0, "A"), new SemanticSourceAtom("B", "id:B", 1, 1, 1, 0, "B") };
        var bound = lines.Select(l => new CanonicalSemanticBoundHeading(l.Alias, l.SourceId, l.Ordinal, l.Text, "ESTABLISHES_STRUCTURE", "heading",
            "document_body", [], 0, 1)).ToArray();
        return (bound, new HeadingPlacementEvidence("{}", new Dictionary<string, string>())
            { Source = new(lines, lines.ToDictionary(l => l.Alias, _ => new HeadingAliasState("SELECT", "SELECT", "HEADING", "READY_FOR_BINDING"))) });
    }

    [Fact]
    public async Task The_reason_variant_accepts_rows_with_a_reason_and_refuses_rows_without_one()
    {
        var (bound, evidence) = Two();
        string Reply(bool withReason) => withReason
            ? $"{{\"protocolVersion\":\"{HeadingPlacer.Version}\",\"placements\":[{{\"alias\":\"A\",\"parent\":\"ROOT\",\"reason\":\"Top section.\"}},{{\"alias\":\"B\",\"parent\":\"A\",\"reason\":\"Under A.\"}}]}}"
            : $"{{\"protocolVersion\":\"{HeadingPlacer.Version}\",\"placements\":[{{\"alias\":\"A\",\"parent\":\"ROOT\"}},{{\"alias\":\"B\",\"parent\":\"A\"}}]}}";
        var withReason = await new HeadingPlacer(_ => new(Reply(true), [], "stop"), variant: new PlacementVariant(AskReason: true))
            .ProposeAsync(new PlacementRequest(bound, evidence), PlacementScript.Access, CancellationToken.None);
        Assert.Empty(withReason.Failures);
        Assert.Equal("A", withReason.Parents.Single(p => p.Key == "B").Value);
        var withoutReason = await new HeadingPlacer(_ => new(Reply(false), [], "stop"), variant: new PlacementVariant(AskReason: true))
            .ProposeAsync(new PlacementRequest(bound, evidence), PlacementScript.Access, CancellationToken.None);
        Assert.NotEmpty(withoutReason.Failures);
    }
}
