using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Resolves the part of an outline tree that the document declares through marker facts. Proposed
/// parent/level are telemetry only; the stack below is the authority for typed numbering.
/// </summary>
public static class MarkerHierarchyResolver
{
    public static IReadOnlyList<ValidatedHeading> Resolve(
        IEnumerable<ProposalValidationResult> acceptedProposals,
        HeadingPolicy? policy = null)
    {
        policy ??= new HeadingPolicy();
        var stack = new List<ValidatedHeading>();
        var result = new List<ValidatedHeading>();
        var seenRoman = false;
        foreach (var candidate in acceptedProposals.Where(x => x.Accepted && x.Source is not null))
        {
            var source = candidate.Source!;
            var proposal = candidate.Proposal;
            if (!policy.Includes(proposal.Role) || proposal.HeadingSpan is null) continue;

            // Typed markers are an explicit structural declaration. Without one this resolver
            // must not manufacture a tree from a model hint or a visual-style rank.
            if (source.Marker is null)
            {
                result.Add(new ValidatedHeading
                {
                    Id = source.SourceId,
                    SourceId = source.SourceId,
                    Role = proposal.Role,
                    HeadingSpan = proposal.HeadingSpan,
                    Level = Math.Clamp(proposal.ProposedLevel ?? 1, 1, 9),
                    ParentId = null,
                    Validation = candidate.Validation with
                    {
                        MarkerSequenceValid = false,
                        HierarchyValid = false,
                        ParentValid = false,
                        ParentResolution = "unresolved",
                    },
                });
                continue;
            }

            var level = ResolveLevel(source.Marker, stack, seenRoman, proposal.ProposedLevel);
            if (source.Marker?.Kind == MarkerKind.RomanUpper) seenRoman = true;
            while (stack.Count >= level) stack.RemoveAt(stack.Count - 1);
            var parentId = level > 1 && stack.Count >= level - 1 ? stack[level - 2].Id : null;
            var proposedParentMatched = string.IsNullOrEmpty(proposal.ProposedParentId) ||
                                       string.Equals(proposal.ProposedParentId, parentId, StringComparison.Ordinal);
            var validation = candidate.Validation with
            {
                MarkerSequenceValid = source.Marker is not null,
                HierarchyValid = source.Marker is not null,
                ParentValid = proposedParentMatched,
                ParentResolution = proposedParentMatched ? "accepted-or-not-proposed" : "overridden",
            };
            var heading = new ValidatedHeading
            {
                Id = source.SourceId,
                SourceId = source.SourceId,
                Role = proposal.Role,
                HeadingSpan = proposal.HeadingSpan,
                Level = level,
                ParentId = parentId,
                Validation = validation,
            };
            result.Add(heading);
            stack.Add(heading);
        }
        return result;
    }

    private static int ResolveLevel(MarkerFacts? marker, IReadOnlyList<ValidatedHeading> stack, bool seenRoman, int? proposed)
    {
        if (marker is null) return Math.Clamp(proposed ?? Math.Max(1, stack.Count), 1, 9);
        return marker.Kind switch
        {
            MarkerKind.RomanUpper or MarkerKind.RomanLower => 1,
            MarkerKind.DocxNumbering => Math.Clamp((marker.Depth ?? 1), 1, 9),
            MarkerKind.DecimalDotted => Math.Clamp((marker.Depth ?? 1) + (seenRoman ? 1 : 0), 1, 9),
            MarkerKind.Decimal => seenRoman ? 2 : 1,
            MarkerKind.AlphaUpper or MarkerKind.AlphaLower => Math.Clamp(Math.Max(2, stack.Count + 1), 1, 9),
            _ => Math.Clamp(proposed ?? 1, 1, 9),
        };
    }
}
