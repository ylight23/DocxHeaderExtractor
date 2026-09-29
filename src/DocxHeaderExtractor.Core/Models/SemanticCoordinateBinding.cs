using System.Collections.ObjectModel;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>Everything a coordinate contract's binder needs to resolve one segment's proposals.</summary>
public sealed record SemanticCoordinateBindingRequest(
    IReadOnlyList<CanonicalSemanticProposal> Proposals,
    IReadOnlyList<SemanticSourceAlias> Aliases,
    IReadOnlySet<string>? OwnedAliases = null,
    IReadOnlyList<SemanticSourceAtom>? Atoms = null);

/// <summary>What a binder resolved, and what it refused and why.</summary>
public sealed record SemanticCoordinateBindingOutcome(
    IReadOnlyList<CanonicalSemanticBoundHeading> Bound,
    IReadOnlyList<CanonicalSemanticBindingObservation> Observations);

/// <summary>
/// The coordinate half of a contract: how a proposal in this contract's shape is checked against
/// the source, and how it is resolved into harness-owned coordinates.
/// <para>
/// Validation and binding travel together because they are the same knowledge asked twice. A
/// contract whose replies address a claim as an ordered tuple of atoms cannot be validated by rules
/// written for one alias and one span - the first such pairing produced a run that decoded every
/// heading correctly and then discarded all of them at a validator asking for verbatim text the
/// structured shape never carries.
/// </para>
/// </summary>
public sealed record SemanticCoordinateBinding(
    string BindingId,
    Func<CanonicalSemanticProposal, IReadOnlyDictionary<string, SemanticSourceAlias>, IReadOnlySet<string>?,
        IReadOnlyList<SemanticContractIssue>> ValidateProposal,
    Func<SemanticCoordinateBindingRequest, SemanticCoordinateBindingOutcome> Bind)
{
    /// <summary>
    /// One alias, one exact selection inside it - the DOCX paragraph.
    /// Unchanged: it is the existing validator and the existing exact binder, reached through this
    /// seam rather than called directly, so that adding a second coordinate system could not change
    /// what the first one does.
    /// </summary>
    public static readonly SemanticCoordinateBinding AliasSpan = new(
        "ALIAS_SPAN",
        CanonicalSemanticContractValidator.Validate,
        request =>
        {
            var bound = CanonicalSemanticExactBinder.Bind(
                request.Proposals, request.Aliases, request.OwnedAliases, out var observations);
            return new SemanticCoordinateBindingOutcome(bound, observations);
        });

    /// <summary>
    /// An ordered tuple of exact selections over coordinate atoms. Resolution is
    /// <see cref="SemanticSourcePartBinder"/>'s and is not reimplemented here: locality, ordering,
    /// overlap, duplication, ambiguity and page transitions are its rules, and a second
    /// implementation of them would be a second answer to where a heading is.
    /// </summary>
    public static readonly SemanticCoordinateBinding SourceParts = new(
        "STRUCTURED_SOURCE_PARTS",
        ValidateStructuredProposal,
        BindStructured);

    /// <summary>
    /// Validates a batch under this binding, splitting what may be bound from what may not - the
    /// contract-aware counterpart of <see cref="CanonicalSemanticContractValidator.ValidateProposals"/>.
    /// </summary>
    public SemanticProposalValidationSummary ValidateProposals(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlySet<string>? ownedAliases = null)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(aliases);

        var valid = new List<CanonicalSemanticProposal>();
        var issues = new List<SemanticContractIssue>();
        foreach (var proposal in proposals)
        {
            var proposalIssues = ValidateProposal(proposal, aliases, ownedAliases);
            if (proposalIssues.Count == 0) valid.Add(proposal);
            else issues.AddRange(proposalIssues);
        }
        return new(valid, issues);
    }

    /// <summary>
    /// What the atom binder deliberately does not check: whether an alias exists in this document's
    /// catalog at all, and whether this segment is allowed to claim it. Everything else - does the
    /// quoted text occur, is the tuple in source order, are the parts adjacent - belongs to the
    /// binder and is left there.
    /// </summary>
    private static IReadOnlyList<SemanticContractIssue> ValidateStructuredProposal(
        CanonicalSemanticProposal proposal,
        IReadOnlyDictionary<string, SemanticSourceAlias> aliases,
        IReadOnlySet<string>? ownedAliases)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(aliases);

        var issues = new List<SemanticContractIssue>();
        if (proposal.SourceParts is not { Count: > 0 })
        {
            issues.Add(new("MISSING_SOURCE_PARTS", proposal.SourceAlias,
                "This contract addresses a claim as an ordered list of source parts."));
            return issues;
        }

        foreach (var part in proposal.SourceParts)
        {
            if (!aliases.ContainsKey(part.SourceAlias))
            {
                issues.Add(new("UNKNOWN_ALIAS", part.SourceAlias,
                    "The alias does not exist in this source catalog."));
                continue;
            }
            if (ownedAliases is not null && !ownedAliases.Contains(part.SourceAlias))
                issues.Add(new("OUT_OF_OWNED_SEGMENT", part.SourceAlias,
                    "The alias may be visible as context but is outside this segment's ownership."));
            if (part.SelectionMode is not (CanonicalSemanticSelectionMode.WholeAlias
                or CanonicalSemanticSelectionMode.VerbatimText))
                issues.Add(new("INVALID_SELECTION_MODE", part.SourceAlias,
                    "A source part uses an unsupported selection mode."));
        }

        return issues;
    }

    private static SemanticCoordinateBindingOutcome BindStructured(SemanticCoordinateBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Atoms is null)
            throw new InvalidOperationException("STRUCTURED_BINDING_REQUIRES_SOURCE_ATOMS");

        var bound = new List<CanonicalSemanticBoundHeading>();
        var observations = new List<CanonicalSemanticBindingObservation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < request.Proposals.Count; index++)
        {
            var proposal = request.Proposals[index];
            if (!proposal.IsHeading)
            {
                observations.Add(new(index, proposal,
                    CanonicalSemanticBindingStatus.NonHeadingIgnored, null, null, null, null));
                continue;
            }
            if (proposal.SourceParts is not { Count: > 0 })
            {
                observations.Add(new(index, proposal,
                    CanonicalSemanticBindingStatus.MissingVerbatimText, null, null, null, "MISSING_SOURCE_PARTS"));
                continue;
            }

            var binding = SemanticSourcePartBinder.Bind(
                request.Atoms, new SemanticSourcePartsProposal(proposal.SourceParts));
            if (!binding.IsBound)
            {
                // The binder's own refusal, carried through under its own name rather than
                // flattened into one generic failure: which rule refused a claim is the whole
                // diagnostic value of refusing it.
                observations.Add(new(index, proposal, StatusOf(binding.Status), null, null, null,
                    binding.Status.ToString()));
                continue;
            }

            var parts = binding.Parts
                .Select(part => new CanonicalSemanticBoundPart(
                    part.Alias, part.SourceId, part.Ordinal, part.Text, part.Start, part.End))
                .ToArray();

            // Identity is the whole tuple. Two claims that select different parts of the same atom,
            // or that wrap across a different pair of atoms, are different claims.
            if (!seen.Add(binding.Identity))
            {
                observations.Add(new(index, proposal, CanonicalSemanticBindingStatus.DuplicateBinding,
                    parts[0].SourceId, parts[0].Start, parts[0].End, "DUPLICATE_BINDING"));
                continue;
            }

            // Start and End are the first part's own resolved offsets inside its own atom - an
            // anchor for ordering, not a span over the tuple. Nothing here invents a range covering
            // several atoms, because no such range exists: the atoms are separate pieces of text.
            var anchor = parts[0];
            observations.Add(new(index, proposal, CanonicalSemanticBindingStatus.Bound,
                anchor.SourceId, anchor.Start, anchor.End, null));
            bound.Add(new CanonicalSemanticBoundHeading(
                anchor.Alias,
                anchor.SourceId,
                anchor.SourceOrdinal,
                SemanticSourceProjection.Render(binding.Parts),
                proposal.SemanticRole ?? "OTHER_STRUCTURAL_LABEL",
                proposal.StructuralType ?? "Heading",
                proposal.Scope ?? "document_body",
                proposal.RelationHints ?? [],
                anchor.Start,
                anchor.End)
            {
                Parts = parts,
            });
        }

        return new SemanticCoordinateBindingOutcome(
            new ReadOnlyCollection<CanonicalSemanticBoundHeading>(bound),
            new ReadOnlyCollection<CanonicalSemanticBindingObservation>(observations));
    }

    private static CanonicalSemanticBindingStatus StatusOf(SemanticSourcePartsStatus status) => status switch
    {
        SemanticSourcePartsStatus.UnknownAlias => CanonicalSemanticBindingStatus.UnknownAlias,
        SemanticSourcePartsStatus.TextNotInAtom => CanonicalSemanticBindingStatus.NonVerbatimText,
        SemanticSourcePartsStatus.AmbiguousSelection => CanonicalSemanticBindingStatus.AmbiguousBinding,
        SemanticSourcePartsStatus.DuplicatePart => CanonicalSemanticBindingStatus.DuplicateBinding,
        _ => CanonicalSemanticBindingStatus.NonVerbatimText,
    };
}
