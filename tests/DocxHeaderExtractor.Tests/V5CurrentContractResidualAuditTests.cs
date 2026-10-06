using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// P5B: audits the current V5 request/response contract without executing a provider or changing
/// production behavior.  It intentionally distinguishes a model instruction or policy from a
/// provider-facing structural restriction.
/// </summary>
public sealed class V5CurrentContractResidualAuditTests
{
    private const string AuditRoot = "artifacts/v5-current-contract-residual-audit";
    private const string InspectedHead = "78ff71001ac1325ed3a17e78746ed99325a216a3";

    [Fact]
    public void Freeze_current_contract_residual_audit()
    {
        var contract = Contract();
        var packet = Packet();
        var canonical = V5SemanticRequestComposerV2_1.BuildCanonical(contract, packet);
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(SemanticClaimContractV2_1.Schema(), CanonicalJson.Options));
        var sourcePartSchema = schema.RootElement.GetProperty("properties").GetProperty("claims").GetProperty("items")
            .GetProperty("properties").GetProperty("subject").GetProperty("properties").GetProperty("sourceParts")
            .GetProperty("items");
        var sourceAliasSchema = sourcePartSchema.GetProperty("properties").GetProperty("sourceAlias");
        var claimSchema = schema.RootElement.GetProperty("properties").GetProperty("claims");

        // 1. The same generic endpoint schema is used for subjects and objects. It has string/minLength,
        // not an enum or a packet-local index, so it cannot express the owned-subject set.
        Assert.Equal("string", sourceAliasSchema.GetProperty("type").GetString());
        Assert.Equal(1, sourceAliasSchema.GetProperty("minLength").GetInt32());
        Assert.False(sourceAliasSchema.TryGetProperty("enum", out _));
        var objectSourceAliasSchema = schema.RootElement.GetProperty("properties").GetProperty("claims").GetProperty("items")
            .GetProperty("properties").GetProperty("object").GetProperty("properties").GetProperty("sourceParts")
            .GetProperty("items").GetProperty("properties").GetProperty("sourceAlias");
        Assert.Equal(sourceAliasSchema.GetRawText(), objectSourceAliasSchema.GetRawText());

        // 2. The current source-selection policy explicitly makes whole-atom selection alias-only,
        // while retaining verbatimText only for a strict substring.
        using var policy = JsonDocument.Parse(JsonSerializer.Serialize(canonical.SourceSelectionPolicy, CanonicalJson.Options));
        Assert.Equal(V5SourceSelectionPolicy.WholeAtomMode, policy.RootElement.GetProperty("default").GetString());
        var wholeAtom = policy.RootElement.GetProperty("wholeAtom");
        Assert.Equal("MUST_BE_OMITTED", wholeAtom.GetProperty("verbatimText").GetString());
        var wholeShape = wholeAtom.GetProperty("shape");
        Assert.Equal(["sourceAlias"], wholeShape.EnumerateObject().Select(property => property.Name));
        Assert.Contains("Default to sourceAlias only.", canonical.Instructions, StringComparison.Ordinal);
        Assert.Contains("Do NOT include verbatimText when the intended selection is the whole atom.", canonical.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("selectionMode", JsonSerializer.Serialize(new ProviderSourcePartV2_1("OWNED")), StringComparison.Ordinal);

        // 3. The claims array permits an empty response and has no per-owned-subject decision field,
        // cardinality, or coverage list. Sparse claim selection therefore remains legal.
        Assert.False(claimSchema.TryGetProperty("minItems", out _));
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("properties")
            .TryGetProperty("decisions", out _));
        using var emptyResponse = JsonDocument.Parse("""{"claims":[]}""");
        var emptyQuarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(emptyResponse.RootElement, contract);
        Assert.Equal(0, emptyQuarantine.RawClaimCount);
        Assert.Empty(emptyQuarantine.Eligible);

        // 4. A halo alias is still syntactically accepted by the codec because schema/codec know no
        // packet-local owned alias set. The exact binder is the later fail-closed guardrail.
        using var haloResponse = JsonDocument.Parse("""
            {"claims":[{"subject":{"sourceParts":[{"sourceAlias":"HALO"}]},"predicate":"DESCRIBES","value":"fact","state":"RESOLVED","evidenceNeeds":[]}]}
            """);
        var haloQuarantine = SemanticClaimResponseCodecV2_1.ParseWithClaimQuarantine(haloResponse.RootElement, contract);
        Assert.Single(haloQuarantine.Eligible);
        var atoms = new[]
        {
            new SemanticSourceAtom("OWNED", "source-owned", 0, 1, 1, 0, "Owned heading"),
            new SemanticSourceAtom("HALO", "source-halo", 1, 1, 1, 1, "Halo heading"),
        };
        var binding = ExactClaimBinderV2_1.Bind("p5b-current-contract", haloQuarantine.Eligible, atoms,
            ClaimBindingScope.Create(["OWNED"], ["OWNED", "HALO"]));
        Assert.Empty(binding.Bound);
        Assert.Equal("subject-alias-not-owned:HALO", binding.Refusals.Values.Single());

        Write("evidence.v1.json", new
        {
            schemaVersion = "v5-current-contract-residual-evidence-v1", providerCalls = 0, goldRead = false,
            inspectedHead = InspectedHead, composerVersion = canonical.ComposerVersion, protocolVersion = canonical.ProtocolVersion,
            sources = new[]
            {
                "src/DocxHeaderExtractor.V5Qualification/LegacyCore/V5ClaimProtocolV2_1.cs",
                "src/DocxHeaderExtractor.V5Qualification/LegacyCore/V5SemanticRequestComposerV2_1.cs",
                "src/DocxHeaderExtractor.V5Qualification/LegacyCore/V5SourceSelectionPolicy.cs"
            },
            providerFacingSourceAlias = new { type = sourceAliasSchema.GetProperty("type").GetString(), minLength = sourceAliasSchema.GetProperty("minLength").GetInt32(), enumPresent = false, subjectAndObjectSchemasIdentical = true },
            wholeAtomPolicy = new { mode = V5SourceSelectionPolicy.WholeAtomMode, shapeFields = wholeShape.EnumerateObject().Select(property => property.Name).ToArray(), verbatimText = "MUST_BE_OMITTED", selectionModeOnProviderWire = false },
            sparseResponse = new { emptyClaimsAccepted = true, claimsMinItemsPresent = false, perOwnedDecisionFieldPresent = false },
            haloProbe = new { codecAccepted = haloQuarantine.Eligible.Count == 1, binderRefused = binding.Refusals.Values.Single(), note = "The probe is an in-memory current-code exercise; it is not a provider call." }
        });
        Write("summary.v1.json", new
        {
            schemaVersion = "v5-current-contract-residual-summary-v1", status = "P5B_COMPLETE_PROVIDER_FREE",
            providerCalls = 0, goldRead = false, inspectedHead = InspectedHead,
            currentContract = new
            {
                OWNER_IDENTITY_HARD_CLOSED = "NO",
                WHOLE_ATOM_RETYPE_REMOVED = "YES_POLICY_AND_WIRE_SHAPE",
                ALL_OWNED_DECISION_REQUIRED = "NO",
                HALO_AS_SUBJECT_STRUCTURALLY_IMPOSSIBLE = "NO"
            },
            interpretation = new
            {
                ownerIdentity = "Ownership is fail-closed in ExactClaimBinderV2_1, not expressible as a provider-facing schema restriction: sourceAlias remains generic string/minLength.",
                wholeAtom = "The current policy and instructions require whole atoms to be sourceAlias-only and remove selectionMode. This is a meaningful prevention of retyping, but verbatimText remains syntactically available for legitimate strict substrings.",
                sparse = "The response is an unconstrained claims array; an empty list is codec-valid and no every-owned-subject decision ledger exists.",
                halo = "A context-only alias passes the current schema/codec as a proposed subject and is refused only at binding. Halo leakage therefore remains a guardrail event, not a structurally impossible wire state."
            },
            designImplication = "P5C may move occurrence identity and ownership entirely to the harness and give the model an owned subject index/decision interface; this audit does not change production behavior."
        });
    }

    private static V5EvidencePacketV2_1 Packet()
    {
        var graph = EvidenceGraphBuilder.Build([
            new SourceObservation("owned-evidence", "source-owned", "OWNED", 0, EvidenceModality.TEXT, "Owned heading", new StructuralSpan(0, 13)),
            new SourceObservation("halo-evidence", "source-halo", "HALO", 1, EvidenceModality.TEXT, "Halo heading", new StructuralSpan(0, 12)),
        ]);
        return new V5EvidencePacketV2_1([graph.Nodes[0]], [graph.Nodes[1]], [], [], [], []);
    }

    private static DocumentTaskContract Contract() => new(
        V5Protocol.TaskContractVersion, "p5b-current-contract-audit", "Provider-free contract audit.",
        [new SemanticPredicateDefinition("DESCRIBES", "A unary fact.")],
        [new SemanticRelationDefinition("RELATES_TO", "A relation.")],
        [new ProjectionRequest("claims", "Claims.")],
        new EvidencePolicy([EvidenceModality.TEXT], [EvidenceNeed.MORE_CONTEXT, EvidenceNeed.GLOBAL_TARGET, EvidenceNeed.IDENTITY_DISAMBIGUATION]),
        "retain-open", new ExecutionBudget(MaxSemanticModelCalls: 1));

    private static void Write(string name, object value)
    {
        var path = TestRepository.Path($"{AuditRoot}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, new UTF8Encoding(false));
    }
}
