using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Problems;

namespace DocxHeaderExtractor.Entities;

/// <summary>
/// Keeps the proposed entities the source supports: the unit exists, the type is one of the defaults, the span lies inside the unit's text, and
/// the same entity is not kept twice. Everything else is refused with its reason, not dropped. Calls no model.
/// </summary>
public sealed class EntityChecker : IChecker<EntityProposal, EntityOutput>
{
    public EntityOutput Check(ProblemSource source, EntityProposal proposal)
    {
        var units = source.Catalog.Units.ToDictionary(u => u.SourceId, StringComparer.Ordinal);
        var types = new HashSet<string>(EntityTypes.All, StringComparer.Ordinal);
        var kept = new List<EntityFound>();
        var refused = new List<EntityRefusal>();
        var seen = new HashSet<(string, int, int, string)>();
        foreach (var e in proposal.Entities)
        {
            if (!units.TryGetValue(e.SourceId, out var unit)) refused.Add(new(e, "UNKNOWN_SOURCE"));
            else if (!types.Contains(e.Type)) refused.Add(new(e, "TYPE_UNKNOWN"));
            else if (!new StructuralSpan(e.Start, e.End).IsValidFor(unit.Text)) refused.Add(new(e, "SPAN_OUTSIDE_TEXT"));
            else if (!seen.Add((e.SourceId, e.Start, e.End, e.Type))) refused.Add(new(e, "DUPLICATE"));
            else kept.Add(new(e.SourceId, e.Start, e.End, e.Type, unit.Text[e.Start..e.End]));
        }
        return new(kept, refused, proposal.BatchFailures);
    }
}
