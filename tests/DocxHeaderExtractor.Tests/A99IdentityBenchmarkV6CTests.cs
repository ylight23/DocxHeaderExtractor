using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class A99IdentityBenchmarkV6CTests
{
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void V6C_preflight_is_document_global_and_source_only()
    {
        var path = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "owner-induction", "preflight-v1", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("READY_FOR_PROVIDER_EXECUTION", json.GetProperty("status").GetString());
        Assert.Equal(3, json.GetProperty("requestCount").GetInt32());
        Assert.Equal(226, json.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal(22, json.GetProperty("parserScopeGroupCount").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("v5cReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("v6aReadCount").GetInt32());
        Assert.Equal(0, json.GetProperty("semanticOwnerAssignments").GetInt32());
    }

    [Fact]
    public void V6C_v2_addressable_preflight_is_a_new_no_call_boundary()
    {
        var path = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "owner-induction", "preflight-v2-addressable", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var json = doc.RootElement;
        Assert.Equal("READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", json.GetProperty("status").GetString());
        Assert.Equal("preflight-v1", json.GetProperty("supersedes").GetString());
        Assert.Equal(3, json.GetProperty("requestCount").GetInt32());
        Assert.Equal(226, json.GetProperty("occurrenceCount").GetInt32());
        Assert.False(json.GetProperty("v1PredictionMutation").GetBoolean());
        Assert.Equal(0, json.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());
    }

    [Fact]
    public void V6C_v3_opaque_handles_are_source_only_and_membership_is_single_authority()
    {
        var root = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "owner-induction", "preflight-v3-opaque-handles");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var json = manifest.RootElement;
        Assert.Equal("READY_FOR_SEPARATE_PROVIDER_AUTHORIZATION", json.GetProperty("status").GetString());
        Assert.Equal("preflight-v2-addressable", json.GetProperty("supersedes").GetString());
        Assert.Equal(3, json.GetProperty("requestCount").GetInt32());
        Assert.Equal(226, json.GetProperty("occurrenceCount").GetInt32());
        Assert.Equal("100%", json.GetProperty("occurrenceHandleCoverage").GetString());
        Assert.Equal("100%", json.GetProperty("evidenceHandleCoverage").GetString());
        Assert.False(json.GetProperty("duplicatedMembershipRepresentation").GetBoolean());
        Assert.True(json.GetProperty("semanticContractUnchanged").GetBoolean());
        Assert.Equal(0, json.GetProperty("providerCalls").GetInt32());
        Assert.Equal(0, json.GetProperty("goldReadCount").GetInt32());

        using var requests = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "requests.json")));
        foreach (var record in requests.RootElement.GetProperty("records").EnumerateArray())
        {
            var request = record.GetProperty("request");
            Assert.Equal("ASSIGNMENTS_ONLY_DERIVE_MEMBERS", request.GetProperty("membershipRepresentation").GetString());
            Assert.False(request.TryGetProperty("memberOccurrenceIds", out _));
            Assert.False(request.GetProperty("goldDerivedInput").GetBoolean());
            Assert.All(request.GetProperty("allowedOccurrenceRefs").EnumerateArray(), handle => Assert.Matches("^U[0-9]{3}$", handle.GetString()!));
            Assert.All(request.GetProperty("allowedEvidenceRefs").EnumerateArray(), handle => Assert.Matches("^E[0-9]{3}$", handle.GetString()!));
        }
    }

    [Fact]
    public void V6C_v3_distinguishes_target_occurrences_from_context_handle_universe()
    {
        var path = Path.Combine(Root(), "artifacts", "identity-benchmark", "v6", "owner-induction", "preflight-v3-opaque-handles", "requests.json");
        using var requests = JsonDocument.Parse(File.ReadAllText(path));
        var targetCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["DOC-0123"] = 116,
            ["DOC-0133"] = 57,
            ["DOC-0252"] = 53,
        };
        foreach (var record in requests.RootElement.GetProperty("records").EnumerateArray())
        {
            var request = record.GetProperty("request");
            var documentId = request.GetProperty("documentId").GetString()!;
            Assert.Equal(targetCounts[documentId], request.GetProperty("occurrences").GetArrayLength());
            Assert.True(request.GetProperty("allowedOccurrenceRefs").GetArrayLength() >= request.GetProperty("occurrences").GetArrayLength());
        }
    }
}
