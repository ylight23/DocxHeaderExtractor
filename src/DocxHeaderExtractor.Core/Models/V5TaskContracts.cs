using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.V5;

/// <summary>Versioned, serializable task vocabulary for the generic V5 agent.</summary>
public static class V5Protocol
{
    public const string TaskContractVersion = "v5-task-contract-1";
    public const string ClaimSchemaVersion = "v5-source-backed-claim-1";
    public const string ClaimSchemaVersionV2 = "v5-source-backed-claim-2";
}

public enum ClaimResolutionState
{
    OPEN,
    RESOLVED,
    CONFLICTED,
    EXHAUSTED,
}

public enum EvidenceNeed
{
    GLOBAL_TARGET,
    MORE_CONTEXT,
    IDENTITY_DISAMBIGUATION,
    STRUCTURAL_CONTEXT,
    LAYOUT_EVIDENCE,
    VISUAL_EVIDENCE,
}

public enum EvidenceModality
{
    TEXT,
    LAYOUT,
    VISUAL,
    HUMAN,
}

public sealed record ExecutionBudget(
    int MaxSemanticModelCalls = 1,
    int MaxRetrievalRounds = 2,
    int MaxRetrievedEvidenceNodes = 64,
    int MaxLayoutCalls = 1,
    int MaxVisualCalls = 1,
    int MaxTokens = 0,
    int MaxWallClockSeconds = 300,
    int MaxUnresolvedRefinements = 8)
{
    public void Validate()
    {
        if (MaxSemanticModelCalls < 0 || MaxRetrievalRounds < 0 || MaxRetrievedEvidenceNodes < 0 ||
            MaxLayoutCalls < 0 || MaxVisualCalls < 0 || MaxTokens < 0 || MaxWallClockSeconds <= 0 || MaxUnresolvedRefinements < 0)
            throw new InvalidOperationException("execution-budget-invalid");
    }
}

public sealed record TaskGoal(string TaskId, string Description, IReadOnlyList<string> RequestedProjections);

public interface ITaskCompiler
{
    DocumentTaskContract Compile(TaskGoal goal, DocumentTaskContract vocabulary);
}

/// <summary>
/// Provider/model-independent compiler. It only selects an already declared contract vocabulary;
/// it never invents predicates or a document-domain ontology from prose.
/// </summary>
public sealed class DeterministicTaskCompiler : ITaskCompiler
{
    public DocumentTaskContract Compile(TaskGoal goal, DocumentTaskContract vocabulary)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(vocabulary);
        vocabulary.Validate();
        if (string.IsNullOrWhiteSpace(goal.TaskId) || string.IsNullOrWhiteSpace(goal.Description))
            throw new InvalidOperationException("task-goal-identity-missing");
        var requested = goal.RequestedProjections.ToHashSet(StringComparer.Ordinal);
        if (requested.Any(name => vocabulary.Projections.All(item => item.Name != name)))
            throw new InvalidOperationException("task-goal-projection-not-declared");
        return vocabulary with
        {
            TaskId = goal.TaskId,
            Description = goal.Description,
            Projections = vocabulary.Projections.Where(item => requested.Count == 0 || requested.Contains(item.Name)).ToArray(),
        };
    }
}

public sealed record SemanticPredicateDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("allowsValue")] bool AllowsValue = true,
    [property: JsonPropertyName("allowsObject")] bool AllowsObject = false);

public sealed record SemanticRelationDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("structuralParent")] bool StructuralParent = false,
    [property: JsonPropertyName("identityMerge")] bool IdentityMerge = false);

public sealed record ProjectionRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required")] bool Required = false);

public sealed record EvidencePolicy(
    [property: JsonPropertyName("modalities")] IReadOnlyList<EvidenceModality> Modalities,
    [property: JsonPropertyName("needs")] IReadOnlyList<EvidenceNeed> Needs,
    [property: JsonPropertyName("allowHumanReview")] bool AllowHumanReview = false);

/// <summary>
/// Generic task description. It owns vocabulary and projections; the semantic engine owns neither
/// a document domain nor a heading ontology.
/// </summary>
public sealed record DocumentTaskContract(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("predicates")] IReadOnlyList<SemanticPredicateDefinition> Predicates,
    [property: JsonPropertyName("relations")] IReadOnlyList<SemanticRelationDefinition> Relations,
    [property: JsonPropertyName("projections")] IReadOnlyList<ProjectionRequest> Projections,
    [property: JsonPropertyName("evidencePolicy")] EvidencePolicy EvidencePolicy,
    [property: JsonPropertyName("unresolvedPolicy")] string UnresolvedPolicy,
    [property: JsonPropertyName("executionBudget")] ExecutionBudget ExecutionBudget)
{
    public void Validate()
    {
        if (!string.Equals(SchemaVersion, V5Protocol.TaskContractVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("unknown-task-contract-version");
        if (string.IsNullOrWhiteSpace(TaskId) || string.IsNullOrWhiteSpace(Description))
            throw new InvalidOperationException("task-contract-identity-missing");
        if (Predicates is null || Relations is null || Projections is null || EvidencePolicy is null || ExecutionBudget is null)
            throw new InvalidOperationException("task-contract-fields-missing");
        EnsureUnique(Predicates.Select(item => item.Name), "predicate");
        EnsureUnique(Relations.Select(item => item.Name), "relation");
        EnsureUnique(Projections.Select(item => item.Name), "projection");
        if (string.IsNullOrWhiteSpace(UnresolvedPolicy)) throw new InvalidOperationException("unresolved-policy-missing");
        ExecutionBudget.Validate();
    }

    public string Hash()
    {
        Validate();
        var canonical = new
        {
            SchemaVersion,
            TaskId,
            Description,
            Predicates = Predicates.OrderBy(item => item.Name, StringComparer.Ordinal),
            Relations = Relations.OrderBy(item => item.Name, StringComparer.Ordinal),
            Projections = Projections.OrderBy(item => item.Name, StringComparer.Ordinal),
            EvidencePolicy = new EvidencePolicy(
                EvidencePolicy.Modalities.OrderBy(item => item).ToArray(),
                EvidencePolicy.Needs.OrderBy(item => item).ToArray(),
                EvidencePolicy.AllowHumanReview),
            UnresolvedPolicy,
            ExecutionBudget,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        var items = values.ToArray();
        if (items.Any(string.IsNullOrWhiteSpace) || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidOperationException($"duplicate-or-empty-{kind}");
    }
}

internal static class CanonicalJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
