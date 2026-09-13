using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Core.Models;

public static class SemanticAdjudicationDecision
{
    public const string Select = "SELECT";
    // Kept as a source-compatible name for Phase-A callers; the wire value is SELECT.
    public const string Resolved = Select;
    public const string Unresolved = "UNRESOLVED";
}

/// <summary>
/// Model-facing representation of one existing semantic alternative. Text and coordinates are
/// deliberately absent; the selected alternative is always mapped back to its frozen proposal.
/// </summary>
public sealed record SemanticAdjudicationAlternative(
    [property: JsonPropertyName("alternativeId")] string AlternativeId,
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("sourceAliases")] IReadOnlyList<string> SourceAliases,
    [property: JsonPropertyName("selectionMode")] string SelectionMode,
    [property: JsonPropertyName("isHeading")] bool IsHeading,
    [property: JsonPropertyName("semanticRole")] string? SemanticRole,
    [property: JsonPropertyName("structuralType")] string? StructuralType,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonIgnore] CanonicalSemanticProposal OriginalProposal);

/// <summary>Parser-owned source evidence supplied to a future adjudication request.</summary>
public sealed record SemanticAdjudicationSourceEvidence(
    [property: JsonPropertyName("sourceAlias")] string SourceAlias,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("sourceOrdinal")] int SourceOrdinal,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// Frozen input for one future semantic adjudication. It contains no Gold or evaluation data.
/// </summary>
public sealed record SemanticAdjudicationCase(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("sourceOccurrence")] string PhysicalSourceIdentity,
    [property: JsonPropertyName("alternatives")] IReadOnlyList<SemanticAdjudicationAlternative> Alternatives,
    [property: JsonPropertyName("sourceEvidence")] IReadOnlyList<SemanticAdjudicationSourceEvidence> SourceEvidence,
    [property: JsonPropertyName("localContext")] IReadOnlyList<string> LocalContext,
    [property: JsonPropertyName("structuralEvidence")] IReadOnlyList<string> StructuralEvidence,
    [property: JsonPropertyName("task")] string Task = "Resolve the semantic disagreement or return unresolved.");

/// <summary>
/// Minimal model response. The model selects one frozen alternative or explicitly remains
/// unresolved; it cannot return text, offsets, source identities, hierarchy levels, or a new
/// semantic proposal.
/// </summary>
public sealed record SemanticAdjudicationResponse(
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("selectedAlternativeId")] string? SelectedAlternativeId = null);

public sealed record SemanticAdjudicationValidationResult(
    bool IsValid,
    string Status,
    CanonicalSemanticProposal? AcceptedProposal,
    IReadOnlyList<string> Errors);

/// <summary>Separate contract for a future adjudication call; the production heading schema is untouched.</summary>
public static class SemanticAdjudicationContract
{
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            caseId = new { type = "string", minLength = 1 },
            decision = new { type = "string", @enum = new[] { SemanticAdjudicationDecision.Resolved, SemanticAdjudicationDecision.Unresolved } },
            selectedAlternativeId = new { type = "string", minLength = 1 },
        },
        required = new[] { "caseId", "decision" },
    };
}

/// <summary>
/// Phase-A adjudication architecture only. This class creates deterministic cases and validates
/// a future response; it never calls a provider and never chooses a semantic winner itself.
/// </summary>
public static class SemanticConflictAdjudicator
{
    public static SemanticAdjudicationCase CreateCase(
        SemanticAttributeConflict conflict,
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlyList<string>? localContext = null,
        IReadOnlyList<string>? structuralEvidence = null,
        int contextRadius = 2) =>
        CreateCase(new SemanticProposalConflict(
            conflict.PhysicalSourceIdentity,
            conflict.Alternatives,
            conflict.Classification), aliases, localContext, structuralEvidence, contextRadius);

    public static SemanticAdjudicationCase CreateCase(
        SemanticProposalConflict conflict,
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlyList<string>? localContext = null,
        IReadOnlyList<string>? structuralEvidence = null,
        int contextRadius = 2)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(aliases);
        if (conflict.Alternatives.Count == 0)
            throw new ArgumentException("An adjudication case must contain alternatives.", nameof(conflict));
        if (contextRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(contextRadius));

        var byAlias = aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal);
        var alternatives = conflict.Alternatives
            .Select(proposal => new
            {
                Proposal = proposal,
                Fingerprint = SemanticFingerprint(proposal),
            })
            .OrderBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ThenBy(item => JsonSerializer.Serialize(item.Proposal), StringComparer.Ordinal)
            .Select((item, index) => ToAlternative(item.Proposal, $"A{index + 1:0000}"))
            .ToArray();

        var targetAliases = alternatives
            .SelectMany(item => item.SourceAliases)
            .Distinct(StringComparer.Ordinal)
            .Select(name => byAlias.TryGetValue(name, out var alias)
                ? new ResolvedAlias(name, alias)
                : new ResolvedAlias(name, null))
            .OrderBy(item => item.Alias?.SourceOrdinal ?? int.MaxValue)
            .ThenBy(item => item.Alias?.SourceId ?? item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();

        var sourceEvidence = targetAliases
            .Where(item => item.Alias is not null)
            .Select(item => new SemanticAdjudicationSourceEvidence(
                item.Name,
                item.Alias!.SourceId,
                item.Alias.SourceOrdinal,
                item.Alias.Text))
            .ToArray();

        var orderedAliases = aliases
            .OrderBy(alias => alias.SourceOrdinal)
            .ThenBy(alias => alias.SourceId, StringComparer.Ordinal)
            .ToArray();
        var targetIndexes = targetAliases
            .Where(item => item.Alias is not null)
            .Select(item => Array.FindIndex(orderedAliases, alias =>
                string.Equals(alias.Alias, item.Name, StringComparison.Ordinal)))
            .Where(index => index >= 0)
            .ToArray();
        var neighborContext = targetIndexes.Length == 0
            ? []
            : Enumerable.Range(
                    Math.Max(0, targetIndexes.Min() - contextRadius),
                    Math.Min(orderedAliases.Length - 1, targetIndexes.Max() + contextRadius) -
                    Math.Max(0, targetIndexes.Min() - contextRadius) + 1)
                .Select(index => $"[{orderedAliases[index].Alias}] {orderedAliases[index].Text}")
                .ToArray();
        var mergedLocalContext = (localContext ?? [])
            .Concat(neighborContext)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var derivedStructuralEvidence = targetAliases
            .Where(item => item.Alias?.SourceAnchor is not null)
            .SelectMany(item => StructuralFacts(item.Name, item.Alias!.SourceAnchor!))
            .Concat(structuralEvidence ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        var caseFingerprint = string.Join("\n", conflict.PhysicalSourceIdentity,
            string.Join("\n", alternatives.Select(item => JsonSerializer.Serialize(item))),
            string.Join("\n", sourceEvidence.Select(item => JsonSerializer.Serialize(item))));
        var caseId = "SAC-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(caseFingerprint))).ToLowerInvariant()[..16];

        return new(
            caseId,
            conflict.PhysicalSourceIdentity,
            alternatives,
            sourceEvidence,
            mergedLocalContext,
            derivedStructuralEvidence);
    }

    public static SemanticAdjudicationValidationResult ValidateResponse(
        SemanticAdjudicationCase adjudicationCase,
        SemanticAdjudicationResponse response)
    {
        ArgumentNullException.ThrowIfNull(adjudicationCase);
        ArgumentNullException.ThrowIfNull(response);
        var errors = new List<string>();
        if (!string.Equals(response.CaseId, adjudicationCase.CaseId, StringComparison.Ordinal))
            errors.Add("CASE_ID_MISMATCH");

        if (string.Equals(response.Decision, SemanticAdjudicationDecision.Unresolved, StringComparison.Ordinal))
        {
            if (response.SelectedAlternativeId is not null)
                errors.Add("UNRESOLVED_HAS_SELECTION");
            return new(errors.Count == 0, SemanticAdjudicationDecision.Unresolved, null, errors);
        }

        if (!string.Equals(response.Decision, SemanticAdjudicationDecision.Resolved, StringComparison.Ordinal))
            errors.Add("INVALID_DECISION");
        if (string.IsNullOrWhiteSpace(response.SelectedAlternativeId))
            errors.Add("MISSING_SELECTED_ALTERNATIVE");

        var selected = adjudicationCase.Alternatives.FirstOrDefault(item =>
            string.Equals(item.AlternativeId, response.SelectedAlternativeId, StringComparison.Ordinal));
        if (selected is null)
            errors.Add("UNKNOWN_SELECTED_ALTERNATIVE");

        return new(
            errors.Count == 0,
            errors.Count == 0 ? SemanticAdjudicationDecision.Resolved : "INVALID",
            errors.Count == 0 ? selected!.OriginalProposal : null,
            errors);
    }

    private static SemanticAdjudicationAlternative ToAlternative(
        CanonicalSemanticProposal proposal,
        string alternativeId) =>
        new(
            alternativeId,
            proposal.SourceAlias,
            ResolveAliases(proposal),
            proposal.SelectionMode ?? CanonicalSemanticSelectionMode.VerbatimText,
            proposal.IsHeading,
            proposal.SemanticRole,
            proposal.StructuralType,
            proposal.Scope,
            proposal);

    private static IEnumerable<string> StructuralFacts(string alias, SourceAnchor anchor)
    {
        yield return $"{alias}.sourceType={anchor.SourceType}";
        if (!string.IsNullOrWhiteSpace(anchor.ParagraphId))
            yield return $"{alias}.paragraphId={anchor.ParagraphId}";
        if (!string.IsNullOrWhiteSpace(anchor.RenderBlockId))
            yield return $"{alias}.renderBlockId={anchor.RenderBlockId}";
        foreach (var lineId in anchor.RenderLineIds.Where(item => !string.IsNullOrWhiteSpace(item)))
            yield return $"{alias}.renderLineId={lineId}";
    }

    private static string SemanticFingerprint(CanonicalSemanticProposal proposal) =>
        JsonSerializer.Serialize(new
        {
            sourceAliases = ResolveAliases(proposal),
            selectionMode = proposal.SelectionMode ?? CanonicalSemanticSelectionMode.VerbatimText,
            isHeading = proposal.IsHeading,
            semanticRole = proposal.SemanticRole,
            structuralType = proposal.StructuralType,
            scope = proposal.Scope,
            occurrence = proposal.Occurrence,
            // Relationship and hierarchy hints are intentionally adjudicated elsewhere.
            relationHints = Array.Empty<string>(),
        });

    private static IReadOnlyList<string> ResolveAliases(CanonicalSemanticProposal proposal) =>
        proposal.SourceAliases is { Count: > 0 } ? proposal.SourceAliases : [proposal.SourceAlias];

    private sealed record ResolvedAlias(string Name, SemanticSourceAlias? Alias);
}
