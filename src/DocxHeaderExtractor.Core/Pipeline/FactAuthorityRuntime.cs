using System.Security.Cryptography;
using System.Text;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>Validates source coordinates, schema shape, and semantic authority before materializing facts.</summary>
public sealed class FactProposalValidator
{
    public FactProposalValidationOutcome Validate(
        FactProposal proposal,
        DocumentExtractionResult extraction,
        IFactSchemaRegistry schemas,
        IFactSemanticAuthority? semanticAuthority)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(schemas);

        if (string.IsNullOrWhiteSpace(proposal.ProposalId))
            return Reject(proposal, "proposal-id-missing");

        var chunk = extraction.Chunks.FirstOrDefault(item =>
            string.Equals(item.Id, proposal.ContextChunkId, StringComparison.Ordinal));
        if (chunk is null)
            return Reject(proposal, "context-not-grounded");

        if (!schemas.TryGet(proposal.SchemaKey, out var schema))
            return Reject(proposal, "schema-not-supported");

        var schemaFields = schema.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var proposedFields = proposal.Fields ?? [];
        var unknownField = proposedFields.FirstOrDefault(field => !schemaFields.ContainsKey(field.FieldName));
        if (unknownField is not null)
            return Reject(proposal, "field-not-supported");

        var duplicateSingleField = proposedFields
            .GroupBy(field => field.FieldName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1 && !schemaFields[group.Key].AllowMultiple);
        if (duplicateSingleField is not null)
            return Reject(proposal, "duplicate-fact-field");

        var missingRequired = schema.Fields.FirstOrDefault(field =>
            field.Required && !proposedFields.Any(proposed =>
                string.Equals(proposed.FieldName, field.Name, StringComparison.Ordinal)));
        if (missingRequired is not null)
            return Reject(proposal, "required-field-missing");

        var catalog = extraction.SourceCatalog.Units.ToDictionary(unit => unit.SourceId, StringComparer.Ordinal);
        var chunkSourceIds = chunk.SourceIds.ToHashSet(StringComparer.Ordinal);
        var groundedFields = new List<ValidatedFactField>(proposedFields.Count);
        foreach (var field in proposedFields)
        {
            if (!catalog.TryGetValue(field.SourceId, out var source))
                return Reject(proposal, "fact-source-not-grounded");
            if (!chunkSourceIds.Contains(field.SourceId))
                return Reject(proposal, "fact-source-outside-context");
            if (!field.Span.IsValidFor(source.Text))
                return Reject(proposal, "fact-span-invalid");

            groundedFields.Add(new ValidatedFactField(
                field.FieldName,
                source.Text.Substring(field.Span.Start, field.Span.End - field.Span.Start),
                new SourceReference(source.SourceId, source.SourceOrdinal, field.Span)));
        }

        var structureIds = extraction.Structure.Elements
            .Select(element => element.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (chunk.StructuralElementIds.Any(elementId => !structureIds.Contains(elementId)))
            return Reject(proposal, "fact-structural-context-not-grounded");

        if (semanticAuthority is null)
            return Reject(proposal, "fact-semantic-authority-missing");

        var semanticDecision = semanticAuthority.Validate(new FactSemanticContext(proposal, schema, groundedFields));
        if (!semanticDecision.Accepted)
            return Reject(proposal, semanticDecision.RejectionReason ?? "fact-semantic-rejected");

        var fact = new ValidatedFact(
            CreateFactId(extraction.DocumentIdentity.DocumentId, proposal.SchemaKey, groundedFields),
            proposal.SchemaKey,
            extraction.DocumentIdentity.DocumentId,
            chunk.Id,
            chunk.SectionId,
            groundedFields,
            chunk.StructuralElementIds,
            new FactValidation(true, true, true, true),
            new FactAuthority(semanticDecision.Basis, proposal.Confidence));
        return new FactProposalValidationOutcome(fact, null);
    }

    private static FactProposalValidationOutcome Reject(FactProposal proposal, string reason) =>
        new(null, new RejectedFactProposal(proposal.ProposalId, proposal.SchemaKey, reason));

    private static string CreateFactId(
        string documentId,
        string schemaKey,
        IReadOnlyList<ValidatedFactField> fields)
    {
        var identity = string.Join(
            "|",
            documentId,
            schemaKey,
            fields.OrderBy(field => field.Name, StringComparer.Ordinal)
                .ThenBy(field => field.Source.SourceId, StringComparer.Ordinal)
                .ThenBy(field => field.Source.Span.Start)
                .ThenBy(field => field.Source.Span.End)
                .Select(field => string.Join(
                    ":",
                    field.Name,
                    field.Source.SourceId,
                    field.Source.Span.Start,
                    field.Source.Span.End)));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return "fact:" + digest;
    }
}

/// <summary>Runs deterministic fact authority over proposals without invoking an AI provider.</summary>
public sealed class FactAuthorityRuntime
{
    private readonly IFactSchemaRegistry _schemas;
    private readonly IFactSemanticAuthority _semanticAuthority;
    private readonly FactProposalValidator _validator;

    public FactAuthorityRuntime(
        IFactSchemaRegistry schemas,
        IFactSemanticAuthority semanticAuthority,
        FactProposalValidator? validator = null)
    {
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _semanticAuthority = semanticAuthority ?? throw new ArgumentNullException(nameof(semanticAuthority));
        _validator = validator ?? new FactProposalValidator();
    }

    public FactAuthorityResult Evaluate(
        DocumentExtractionResult extraction,
        IEnumerable<FactProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(proposals);

        var facts = new List<ValidatedFact>();
        var rejections = new List<RejectedFactProposal>();
        foreach (var proposal in proposals)
        {
            var outcome = _validator.Validate(proposal, extraction, _schemas, _semanticAuthority);
            if (outcome.Fact is not null)
                facts.Add(outcome.Fact);
            if (outcome.Rejection is not null)
                rejections.Add(outcome.Rejection);
        }

        return new FactAuthorityResult(facts, rejections);
    }
}
