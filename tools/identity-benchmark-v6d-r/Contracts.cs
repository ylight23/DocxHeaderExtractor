namespace IdentityBenchmarkV6DR;

public static class V6DRContracts
{
    public const string Same = "SAME_SEMANTIC_REPEAT";
    public const string Continuation = "CONTINUATION_OF";
    public const string Distinct = "DISTINCT_SEMANTIC_NODE";
    public const string Invalid = "INVALID_PREDICTION";

    public static string Derive(
        string documentStatus,
        IReadOnlyDictionary<string, string> assignments,
        IReadOnlySet<(string From, string To)> continuationEdges,
        string left,
        string right,
        out (string? From, string? To) direction)
    {
        direction = (null, null);
        if (documentStatus != "VALID" || !assignments.ContainsKey(left) || !assignments.ContainsKey(right)) return Invalid;
        if (!string.Equals(assignments[left], assignments[right], StringComparison.Ordinal)) return Distinct;
        if (continuationEdges.Contains((left, right))) { direction = (left, right); return Continuation; }
        if (continuationEdges.Contains((right, left))) { direction = (right, left); return Continuation; }
        return Same;
    }

    public static string FailureOwnership(string gold, string predicted)
    {
        if (predicted == Invalid) return "INVALID_V6D_DOCUMENT_VALIDATION";
        if (gold == Distinct && predicted is Same or Continuation) return "WRONG_NODE_MERGE";
        if (gold is Same or Continuation && predicted == Distinct) return "WRONG_NODE_SPLIT";
        if (gold == Continuation && predicted == Same) return "WRONG_CONTINUATION_EDGE";
        if (gold == Continuation && predicted == Continuation) return "CORRECT";
        return gold == predicted ? "CORRECT" : "WRONG_CONTINUATION_EDGE";
    }

    public static bool NodeConstraint(string gold, string predicted) =>
        predicted != Invalid && (gold == Distinct ? predicted == Distinct : predicted is Same or Continuation);
}
