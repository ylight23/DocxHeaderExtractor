using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Models;

namespace DocxHeaderExtractor.HeadingAgent.Placement;

/// <summary>Find unusable proposed ancestor chains without choosing any replacement relation.</summary>
internal static class ParentChainRepair
{
    internal const int MaxPasses = 2;
    internal const string Phase = "HIERARCHY_CHAIN_REPAIR";
    internal const string Prompt = """
        This is a focused parent-chain repair. Existing relations are proposals, not instructions.
        Each reported chain ends outside the tree, unresolved, missing, or in an invalid edge, so it
        cannot supply a level to its descendants. Reconsider only toPlace, including the blocking
        ancestor when it is in toPlace. Use the source meaning to decide which relation should change;
        do not mechanically turn NONE or UNRESOLVED into ROOT. A heading with parent NONE cannot
        contain outline children. An explicit UNRESOLVED is valid when the evidence is insufficient.
        Relations outside repairScope are fixed for this call. Send one row per toPlace alias, using
        the same response protocol. Do not change heading membership, text, or grouping.
        """;

    internal static ParentChainConflict[] Find(IReadOnlyList<CanonicalSemanticBoundHeading> headings,
        IReadOnlyDictionary<string, string> parents)
    {
        var byAlias = headings.ToDictionary(h => h.Alias, StringComparer.Ordinal);
        var conflicts = new List<ParentChainConflict>();
        foreach (var h in headings)
        {
            // A terminal NONE/UNRESOLVED is itself a valid judgement; only a claimed child chain conflicts.
            if (!parents.TryGetValue(h.Alias, out var first) || first is "ROOT" or "NONE" or "UNRESOLVED") continue;
            var path = new List<string> { h.Alias };
            var current = h.Alias;
            while (true)
            {
                if (!parents.TryGetValue(current, out var parent))
                { Add("ANCESTOR_MISSING", current); break; }
                if (parent == "ROOT") break;
                if (parent is "NONE" or "UNRESOLVED")
                { Add(parent == "NONE" ? "ANCESTOR_OUTSIDE_TREE" : "ANCESTOR_UNRESOLVED", current); break; }
                if (!byAlias.TryGetValue(parent, out var node))
                { Add("PARENT_UNAVAILABLE", parent); break; }
                if (path.Contains(parent, StringComparer.Ordinal) || node.SourceOrdinal >= byAlias[current].SourceOrdinal)
                { Add("PARENT_NOT_EARLIER", current); break; }
                path.Add(parent);
                current = parent;
            }
            void Add(string code, string blocker) => conflicts.Add(new(h.Alias, code, blocker, path.ToArray()));
        }
        return conflicts.ToArray();
    }
}
