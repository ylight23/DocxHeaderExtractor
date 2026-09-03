using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Validates untrusted schema selection against the registered pack boundary.</summary>
public static class SchemaSelectionValidator
{
    public static SchemaSelectionValidationResult Validate(
        SchemaSelectionProposal proposal,
        IFactSchemaPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(registry);

        if (string.IsNullOrWhiteSpace(proposal.ProposalId))
            return Result(proposal, [], "REJECTED", "schema-selection-proposal-malformed", []);

        var rawKeys = proposal.SchemaKeys ?? [];
        if (rawKeys.Any(key => string.IsNullOrWhiteSpace(key)))
            return Result(proposal, [], "REJECTED", "schema-selection-key-malformed", []);

        var keys = rawKeys
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var unknown = keys.Where(key => !registry.TryGet(key, out _)).ToArray();
        if (keys.Length == 0)
            return Result(proposal, [], "NO_SCHEMA_MATCH", "no-schema-match", []);
        if (unknown.Length > 0)
            return Result(proposal, [], "REJECTED", "schema-pack-missing", unknown);

        return Result(proposal, keys, "matched", "registered-schema-selection", []);
    }

    public static SchemaSelectionValidationResult ValidateExplicit(
        IEnumerable<string> schemaKeys,
        IFactSchemaPackRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(schemaKeys);
        ArgumentNullException.ThrowIfNull(registry);
        var proposal = new SchemaSelectionProposal(
            "explicit-schema-selection",
            schemaKeys.ToArray(),
            ["caller-explicit-selection"],
            null);
        return Validate(proposal, registry);
    }

    private static SchemaSelectionValidationResult Result(
        SchemaSelectionProposal proposal,
        IReadOnlyList<string> keys,
        string status,
        string reason,
        IReadOnlyList<string> unknown) =>
        new(new ValidatedSchemaSelection(
            proposal.ProposalId,
            keys,
            status,
            reason), unknown);
}
