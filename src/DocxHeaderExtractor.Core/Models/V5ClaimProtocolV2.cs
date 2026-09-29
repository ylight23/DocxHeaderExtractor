using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Source-backed V5 claim protocol v2. The model proposes meaning and exact source parts; the
/// harness assigns the durable claim identity after binding. This type intentionally has no
/// model-owned <c>claimId</c> field.
/// </summary>
public sealed record SemanticClaimProposalV2(
    [property: JsonPropertyName("subject")] ClaimSourceEndpoint Subject,
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("object")] ClaimSourceEndpoint? Object = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null,
    [property: JsonPropertyName("existingClaimId")] string? ExistingClaimId = null);

public sealed record SemanticClaimResponseV2(
    [property: JsonPropertyName("claims")] IReadOnlyList<SemanticClaimProposalV2> Claims);

public sealed record BoundSemanticClaimV2(
    BoundSemanticClaim Claim,
    string? ExistingClaimId);

public sealed record ClaimBindingResultV2(
    IReadOnlyList<BoundSemanticClaimV2> Bound,
    IReadOnlyDictionary<string, string> Refusals)
{
    public bool IsComplete => Refusals.Count == 0;
}

/// <summary>Deterministic harness identity derived only after exact source binding.</summary>
public static class HarnessClaimIdentityV2
{
    public const string Prefix = "v5claim2-";

    public static string Create(
        string requestId,
        BoundClaimEndpoint subject,
        string predicate,
        string? value,
        BoundClaimEndpoint? target,
        ClaimResolutionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        var canonical = string.Join("\n", [
            "v5-source-backed-claim-2",
            requestId,
            subject.Identity,
            predicate,
            target?.Identity ?? string.Empty,
            value ?? string.Empty,
            state.ToString(),
        ]);
        return Prefix + Hashing.Sha256(canonical)[..32];
    }
}

/// <summary>Exact binder for v2. It never trusts a model claim id and never widens a source span.</summary>
public static class ExactClaimBinderV2
{
    public static ClaimBindingResultV2 Bind(
        string requestId,
        IReadOnlyList<SemanticClaimProposalV2> proposals,
        IReadOnlyList<SemanticSourceAtom> atoms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(atoms);
        var bound = new List<BoundSemanticClaimV2>();
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            var key = proposal.ExistingClaimId ?? $"proposal-{index + 1}";
            if (string.IsNullOrWhiteSpace(proposal.Predicate))
            {
                refusals[key] = "predicate-missing";
                continue;
            }
            var subject = SemanticSourcePartBinder.Bind(atoms, proposal.Subject?.SourceParts ?? []);
            if (!subject.IsBound)
            {
                refusals[key] = subject.Reason ?? subject.Status.ToString();
                continue;
            }
            BoundClaimEndpoint? target = null;
            if (proposal.Object is not null)
            {
                var objectBinding = SemanticSourcePartBinder.Bind(atoms, proposal.Object.SourceParts);
                if (!objectBinding.IsBound)
                {
                    refusals[key] = objectBinding.Reason ?? objectBinding.Status.ToString();
                    continue;
                }
                target = new BoundClaimEndpoint(objectBinding.Parts);
            }
            var subjectEndpoint = new BoundClaimEndpoint(subject.Parts);
            var claimId = HarnessClaimIdentityV2.Create(
                requestId, subjectEndpoint, proposal.Predicate, proposal.Value, target, proposal.State);
            if (!ids.Add(claimId))
            {
                refusals[key] = "duplicate-harness-claim-id";
                continue;
            }
            var claim = new BoundSemanticClaim(
                claimId,
                subjectEndpoint,
                proposal.Predicate,
                proposal.Value,
                target,
                proposal.State,
                (proposal.EvidenceNeeds ?? []).Distinct().ToArray());
            bound.Add(new BoundSemanticClaimV2(claim, proposal.ExistingClaimId));
        }
        return new ClaimBindingResultV2(bound, new ReadOnlyDictionary<string, string>(refusals));
    }
}

/// <summary>
/// Recursive source-backed schema and semantic validation for v2. The codec below uses the same
/// field sets and schema construction, so schema and decoder cannot silently drift apart.
/// </summary>
public static class SemanticClaimContractV2
{
    public const string SchemaVersion = "v5-source-backed-claim-2";

    internal static IReadOnlySet<string> ResponseFields { get; } = new HashSet<string>(["claims"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> ClaimFields { get; } = new HashSet<string>(
        ["subject", "predicate", "value", "object", "state", "evidenceNeeds", "existingClaimId"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> EndpointFields { get; } = new HashSet<string>(["sourceParts"], StringComparer.Ordinal);
    internal static IReadOnlySet<string> PartFields { get; } = new HashSet<string>(
        ["sourceAlias", "selectionMode", "verbatimText", "occurrence", "leftExactContext", "rightExactContext"], StringComparer.Ordinal);

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            claims = new
            {
                type = "array",
                items = ClaimSchema(),
            },
        },
        required = new[] { "claims" },
    };

    private static object ClaimSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            subject = EndpointSchema(),
            predicate = new { type = "string", minLength = 1 },
            value = new { type = "string" },
            @object = EndpointSchema(),
            state = new { type = "string", @enum = Enum.GetNames<ClaimResolutionState>() },
            evidenceNeeds = new { type = "array", items = new { type = "string", @enum = Enum.GetNames<EvidenceNeed>() } },
            existingClaimId = new { type = "string", minLength = 1 },
        },
        required = new[] { "subject", "predicate", "state" },
    };

    private static object EndpointSchema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            sourceParts = new
            {
                type = "array",
                minItems = 1,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        sourceAlias = new { type = "string", minLength = 1 },
                        selectionMode = new { type = "string", minLength = 1 },
                        verbatimText = new { type = "string" },
                        occurrence = new { type = "integer", minimum = 1 },
                        leftExactContext = new { type = "string" },
                        rightExactContext = new { type = "string" },
                    },
                    required = new[] { "sourceAlias", "selectionMode" },
                },
            },
        },
        required = new[] { "sourceParts" },
    };

    public static string SchemaHash() => Hashing.Sha256(JsonSerializer.Serialize(Schema(), CanonicalJson.Options));

    public static IReadOnlyList<string> Validate(SemanticClaimResponseV2 response, DocumentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        var predicates = contract.Predicates.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var relations = contract.Relations.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var issues = new List<string>();
        foreach (var claim in response.Claims ?? [])
        {
            if (string.IsNullOrWhiteSpace(claim.Predicate))
            {
                issues.Add("claim-predicate-missing");
                continue;
            }
            if (!predicates.Contains(claim.Predicate) && !relations.ContainsKey(claim.Predicate))
                issues.Add($"predicate-not-in-contract:{claim.Predicate}");
            if (claim.Subject is null || claim.Subject.SourceParts is null || claim.Subject.SourceParts.Count == 0)
                issues.Add("claim-subject-missing");
            if (claim.State == ClaimResolutionState.OPEN && (claim.EvidenceNeeds is null || claim.EvidenceNeeds.Count == 0))
                issues.Add("open-claim-without-evidence-need");
            if (relations.TryGetValue(claim.Predicate, out _) && claim.Object is null)
                issues.Add("relation-missing-object");
            if (relations.ContainsKey(claim.Predicate) && claim.Value is not null)
                issues.Add("relation-has-value");
            if (!relations.ContainsKey(claim.Predicate) && claim.Object is not null)
                issues.Add("unary-claim-has-object");
        }
        return issues;
    }
}

/// <summary>Strict JSON decoder for v2. Unknown fields, model claim ids, empty parts and bad enums fail closed.</summary>
public static class SemanticClaimResponseCodecV2
{
    public static SemanticClaimResponseV2 Parse(JsonElement payload, DocumentTaskContract contract)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("claim-payload-not-object");
        EnsureFields(payload, SemanticClaimContractV2.ResponseFields, "response");
        if (!payload.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("claims-array-missing");
        foreach (var claim in claims.EnumerateArray())
        {
            EnsureFields(claim, SemanticClaimContractV2.ClaimFields, "claim");
            EnsureRequiredString(claim, "predicate");
            EnsureRequiredString(claim, "state");
            EnsureEndpoint(claim, "subject", required: true);
            if (claim.TryGetProperty("object", out var target) && target.ValueKind != JsonValueKind.Null)
                EnsureEndpoint(target, "object", required: false);
        }
        var options = new JsonSerializerOptions(CanonicalJson.Options)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var response = payload.Deserialize<SemanticClaimResponseV2>(options)
            ?? throw new InvalidOperationException("claim-payload-empty");
        var issues = SemanticClaimContractV2.Validate(response, contract);
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(",", issues));
        return response;
    }

    private static void EnsureEndpoint(JsonElement claim, string property, bool required)
    {
        if (!claim.TryGetProperty(property, out var endpoint) || endpoint.ValueKind != JsonValueKind.Object)
        {
            if (required) throw new InvalidOperationException("claim-subject-missing");
            return;
        }
        EnsureFields(endpoint, SemanticClaimContractV2.EndpointFields, property);
        if (!endpoint.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
            throw new InvalidOperationException($"{property}-parts-missing");
        foreach (var part in parts.EnumerateArray())
        {
            EnsureFields(part, SemanticClaimContractV2.PartFields, "source-part");
            if (!part.TryGetProperty("sourceAlias", out var alias) || alias.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(alias.GetString()))
                throw new InvalidOperationException("source-part-alias-missing");
            if (!part.TryGetProperty("selectionMode", out var mode) || mode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(mode.GetString()))
                throw new InvalidOperationException("source-part-selection-mode-missing");
        }
    }

    private static void EnsureFields(JsonElement element, IReadOnlySet<string> allowed, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{path}-not-object");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{path}-field-not-in-contract:{property.Name}");
    }

    private static void EnsureRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"claim-{property}-missing");
    }
}

public enum StructuredOutputMode
{
    JsonSchemaStrict,
    JsonObject,
}

public sealed record ProviderStructuredOutputCapabilities(
    string Provider,
    string Model,
    bool JsonSchemaStrictSupported,
    bool JsonObjectSupported,
    string EvidenceSource)
{
    public StructuredOutputMode Select(bool requireStrict)
    {
        if (requireStrict)
        {
            if (!JsonSchemaStrictSupported)
                throw new InvalidOperationException("provider-strict-json-schema-capability-unverified");
            return StructuredOutputMode.JsonSchemaStrict;
        }
        if (JsonObjectSupported) return StructuredOutputMode.JsonObject;
        throw new InvalidOperationException("provider-has-no-supported-structured-output-mode");
    }
}

/// <summary>Provider-free capability record. It never probes a provider or runs inference.</summary>
public static class ProviderStructuredOutputRegistry
{
    public static ProviderStructuredOutputCapabilities QwenFlashAlibaba { get; } =
        new("Alibaba", "qwen/qwen3.7-flash", JsonSchemaStrictSupported: false, JsonObjectSupported: true,
            EvidenceSource: "explicit-preflight-registration; strict support unverified");
}
