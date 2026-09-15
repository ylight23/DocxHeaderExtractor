using IdentityBenchmarkV6DR;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV6DRTests
{
    [Fact]
    public void Derivation_requires_exact_edge_for_continuation()
    {
        var assignments = new Dictionary<string, string> { ["U001"] = "N001", ["U002"] = "N001" };
        var noEdge = new HashSet<(string From, string To)>();
        var roleOnly = V6DRContracts.Derive("VALID", assignments, noEdge, "U001", "U002", out _);
        Assert.Equal(V6DRContracts.Same, roleOnly);

        var edge = new HashSet<(string From, string To)> { ("U001", "U002") };
        var continuation = V6DRContracts.Derive("VALID", assignments, edge, "U001", "U002", out var direction);
        Assert.Equal(V6DRContracts.Continuation, continuation);
        Assert.Equal(("U001", "U002"), direction);
    }

    [Fact]
    public void Derivation_is_fail_closed_and_failure_precedence_is_stable()
    {
        var assignments = new Dictionary<string, string> { ["U001"] = "N001", ["U002"] = "N002" };
        var edges = new HashSet<(string From, string To)>();
        Assert.Equal(V6DRContracts.Distinct, V6DRContracts.Derive("VALID", assignments, edges, "U001", "U002", out _));
        Assert.Equal(V6DRContracts.Invalid, V6DRContracts.Derive("INVALID_VALIDATION", assignments, edges, "U001", "U002", out _));
        Assert.Equal("INVALID_V6D_DOCUMENT_VALIDATION", V6DRContracts.FailureOwnership(V6DRContracts.Distinct, V6DRContracts.Invalid));
        Assert.Equal("WRONG_NODE_MERGE", V6DRContracts.FailureOwnership(V6DRContracts.Distinct, V6DRContracts.Same));
        Assert.Equal("WRONG_NODE_SPLIT", V6DRContracts.FailureOwnership(V6DRContracts.Continuation, V6DRContracts.Distinct));
        Assert.Equal("WRONG_CONTINUATION_EDGE", V6DRContracts.FailureOwnership(V6DRContracts.Continuation, V6DRContracts.Same));
    }
}
