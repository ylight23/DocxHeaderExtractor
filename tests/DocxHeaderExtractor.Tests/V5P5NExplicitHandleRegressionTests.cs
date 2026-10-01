using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free regression for P5M's measured sourceOrdinal/ownedIndex ambiguity.</summary>
public sealed class V5P5NExplicitHandleRegressionTests
{
    [Fact]
    public void Serialize_only_explicit_local_handles_into_sparse_provider_view()
    {
        var packet = new V5SemanticDecisionRequestPacketV3(
            [Node("L1472:S0", 1541, "subject")], [Node("L0918:S4", 999, "context")], [], [], [], []);
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var request = V5SemanticSparseDecisionComposerV3_1.Compose(contract, packet);
        using var document = JsonDocument.Parse(request.Prompt);
        var root = document.RootElement;
        Assert.Equal(V5Protocol.ClaimSchemaVersionV3_2, root.GetProperty("protocolVersion").GetString());
        var providerPacket = root.GetProperty("packet");
        var subject = providerPacket.GetProperty("subjectEvidence")[0];
        var context = providerPacket.GetProperty("contextOnlyEvidence")[0];
        Assert.Equal(0, subject.GetProperty("ownedIndex").GetInt32());
        Assert.Equal(0, context.GetProperty("contextIndex").GetInt32());
        Assert.True(subject.GetProperty("facts").TryGetProperty("sourceType", out _));
        Assert.False(subject.GetProperty("facts").TryGetProperty("paragraphId", out _));
        Assert.False(subject.GetProperty("facts").TryGetProperty("renderBlockId", out _));
        var keys = Keys(providerPacket);
        Assert.DoesNotContain("sourceOrdinal", keys);
        Assert.DoesNotContain("sourceAlias", keys);
        Assert.DoesNotContain("sourceId", keys);
        Assert.DoesNotContain("anchor", keys);
        Assert.DoesNotContain("1541", request.Prompt);
        Assert.DoesNotContain("999", request.Prompt);
        FreezeArtifact.AssertJson("artifacts/v5-p5n-explicit-local-handles", "audit.v1.json", new
        {
            schemaVersion = "v5-p5n-explicit-local-handles-audit-v1",
            status = "PROVIDER_FREE_COMPLETE",
            providerCalls = 0,
            goldRead = false,
            protocol = V5Protocol.ClaimSchemaVersionV3_2,
            composer = V5SemanticSparseDecisionComposerV3_1.Version,
            subjectHandle = "subjectEvidence[n].ownedIndex = n",
            contextHandle = "contextOnlyEvidence[n].contextIndex = n",
            withheldHarnessAuthority = new[] { "sourceAlias", "sourceId", "sourceOrdinal", "anchor", "paragraphId", "renderBlockId" },
            factsBoundary = "Structural facts may remain, but fact keys containing id, ordinal, alias, anchor or coordinate are withheld from the provider view.",
            regression = "A source ordinal such as 1541 and source-local fact identifiers are not provider-visible and cannot be copied into ownedIndex.",
        });
    }

    private static EvidenceNode Node(string alias, int ordinal, string text) => new("E" + ordinal, "S" + ordinal, alias, ordinal,
        EvidenceModality.TEXT, text, new EvidenceAnchor("S" + ordinal, ordinal, new StructuralSpan(0, text.Length)),
        new Dictionary<string, string?> { ["sourceType"] = "document_body", ["paragraphId"] = "p-" + ordinal, ["renderBlockId"] = "r-" + ordinal });

    private static HashSet<string> Keys(JsonElement element)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { keys.Add(property.Name); Visit(property.Value); }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Visit(item);
        }
        Visit(element); return keys;
    }
}
