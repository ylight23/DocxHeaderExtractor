using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

public sealed record ClaimSourceEndpoint(
    [property: JsonPropertyName("sourceParts")] IReadOnlyList<SemanticSourcePart> SourceParts);

public sealed record SemanticClaimProposal(
    [property: JsonPropertyName("claimId")] string ClaimId,
    [property: JsonPropertyName("subject")] ClaimSourceEndpoint Subject,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("object")] ClaimSourceEndpoint? Object = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null);

public sealed record SemanticClaimResponse(
    [property: JsonPropertyName("claims")] IReadOnlyList<SemanticClaimProposal> Claims);

public sealed record BoundClaimEndpoint(IReadOnlyList<BoundSourcePart> Parts)
{
    public string Identity => string.Join("|", Parts.Select(part =>
        $"{part.SourceId}:{part.Start}-{part.End}"));
}

public sealed record BoundSemanticClaim(
    string ClaimId,
    BoundClaimEndpoint Subject,
    string Predicate,
    string? Value,
    BoundClaimEndpoint? Object,
    ClaimResolutionState State,
    IReadOnlyList<EvidenceNeed> EvidenceNeeds)
{
    public string Identity => $"{Subject.Identity}|{Predicate}|{Object?.Identity ?? Value ?? string.Empty}";
}

public sealed record ClaimBindingResult(
    IReadOnlyList<BoundSemanticClaim> Bound,
    IReadOnlyDictionary<string, string> Refusals)
{
    public bool IsComplete => Refusals.Count == 0;
}

/// <summary>Generic exact binder. It reuses the source-parts algorithm, never repairs or widens spans.</summary>
public static class ExactClaimBinder
{
    public static ClaimBindingResult Bind(
        IReadOnlyList<SemanticClaimProposal> proposals,
        IReadOnlyList<SemanticSourceAtom> atoms)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(atoms);
        var bound = new List<BoundSemanticClaim>();
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            if (string.IsNullOrWhiteSpace(proposal.ClaimId) || string.IsNullOrWhiteSpace(proposal.Predicate))
            {
                refusals[proposal.ClaimId ?? string.Empty] = "claim-identity-missing";
                continue;
            }
            var subject = SemanticSourcePartBinder.Bind(atoms, proposal.Subject?.SourceParts ?? []);
            if (!subject.IsBound)
            {
                refusals[proposal.ClaimId] = subject.Reason ?? subject.Status.ToString();
                continue;
            }
            BoundClaimEndpoint? target = null;
            if (proposal.Object is not null)
            {
                var objectBinding = SemanticSourcePartBinder.Bind(atoms, proposal.Object.SourceParts);
                if (!objectBinding.IsBound)
                {
                    refusals[proposal.ClaimId] = objectBinding.Reason ?? objectBinding.Status.ToString();
                    continue;
                }
                target = new BoundClaimEndpoint(objectBinding.Parts);
            }
            bound.Add(new BoundSemanticClaim(
                proposal.ClaimId,
                new BoundClaimEndpoint(subject.Parts),
                proposal.Predicate,
                proposal.Value,
                target,
                proposal.State,
                (proposal.EvidenceNeeds ?? []).Distinct().ToArray()));
        }
        return new ClaimBindingResult(bound, new ReadOnlyDictionary<string, string>(refusals));
    }
}

public static class SemanticClaimContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            claims = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "claimId", "subject", "predicate", "state" },
                    properties = new
                    {
                        claimId = new { type = "string", minLength = 1 },
                        subject = new { type = "object" },
                        predicate = new { type = "string", minLength = 1 },
                        value = new { type = "string" },
                        @object = new { type = "object" },
                        state = new { type = "string", @enum = Enum.GetNames<ClaimResolutionState>() },
                        evidenceNeeds = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
                    },
                },
            },
        },
        required = new[] { "claims" },
    };

    public static string SchemaHash() => Hashing.Sha256(JsonSerializer.Serialize(Schema(), CanonicalJson.Options));

    public static IReadOnlyList<string> Validate(SemanticClaimResponse response, DocumentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var predicates = contract.Predicates.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var relations = contract.Relations.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var issues = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in response.Claims ?? [])
        {
            if (!ids.Add(claim.ClaimId)) issues.Add($"duplicate-claim:{claim.ClaimId}");
            if (!predicates.Contains(claim.Predicate) && !relations.ContainsKey(claim.Predicate))
                issues.Add($"predicate-not-in-contract:{claim.Predicate}");
            if (claim.State == ClaimResolutionState.OPEN && (claim.EvidenceNeeds is null || claim.EvidenceNeeds.Count == 0))
                issues.Add($"open-claim-without-evidence-need:{claim.ClaimId}");
            if (relations.TryGetValue(claim.Predicate, out var relation) && claim.Object is null)
                issues.Add($"relation-missing-object:{claim.ClaimId}");
            if (claim.Value is not null && relations.ContainsKey(claim.Predicate))
                issues.Add($"relation-has-value:{claim.ClaimId}");
        }
        return issues;
    }
}

/// <summary>Strict decoder for model JSON. Unknown fields and parser-owned coordinates fail closed.</summary>
public static class SemanticClaimResponseCodec
{
    private static readonly HashSet<string> ResponseFields = ["claims"];
    private static readonly HashSet<string> ClaimFields = ["claimId", "subject", "predicate", "value", "object", "state", "evidenceNeeds"];
    private static readonly HashSet<string> EndpointFields = ["sourceParts"];
    private static readonly HashSet<string> PartFields = ["sourceAlias", "verbatimText", "occurrence", "leftExactContext", "rightExactContext"];

    public static SemanticClaimResponse Parse(JsonElement payload, DocumentTaskContract contract)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("claim-payload-not-object");
        EnsureFields(payload, ResponseFields, "response");
        if (!payload.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("claims-array-missing");
        foreach (var claim in claims.EnumerateArray())
        {
            EnsureFields(claim, ClaimFields, "claim");
            EnsureEndpoint(claim, "subject");
            if (claim.TryGetProperty("object", out var target) && target.ValueKind != JsonValueKind.Null)
                EnsureEndpoint(target, "object");
        }
        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var response = payload.Deserialize<SemanticClaimResponse>(options)
            ?? throw new InvalidOperationException("claim-payload-empty");
        var issues = SemanticClaimContract.Validate(response, contract);
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(",", issues));
        return response;
    }

    private static void EnsureEndpoint(JsonElement claim, string property)
    {
        if (!claim.TryGetProperty(property, out var endpoint) || endpoint.ValueKind != JsonValueKind.Object)
        {
            if (property == "subject") throw new InvalidOperationException("claim-subject-missing");
            return;
        }
        EnsureFields(endpoint, EndpointFields, property);
        if (!endpoint.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
            throw new InvalidOperationException($"{property}-parts-missing");
        foreach (var part in parts.EnumerateArray()) EnsureFields(part, PartFields, "source-part");
    }

    private static void EnsureFields(JsonElement element, IReadOnlySet<string> allowed, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{path}-not-object");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{path}-field-not-in-contract:{property.Name}");
    }
}

internal static class Hashing
{
    internal static string Sha256(string text) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
