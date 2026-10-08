using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.DocumentProcessing.Materialization;

namespace DocxHeaderExtractor.Tests;

public sealed class HeadingPlacementObservabilityTests
{
    [Fact]
    public async Task Accepted_NONE_is_a_positive_placement_judgment_not_a_failed_or_unknown_call()
    {
        var bound = new[] { Heading("O1", 1), Heading("O2", 2), Heading("O3", 3), Heading("O4", 4) };
        using var transport = new Transport("""
            {"placements":[{"alias":"O1","parent":"NONE"},{"alias":"O2","parent":"ROOT"},{"alias":"O3","parent":"O2"}]}
            """);
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, transport, CancellationToken.None);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("placement-accepted", result.Observation.Status);
        Assert.Equal(4, result.Observation.RequestedCount);
        Assert.Equal(3, result.Observation.ResponseEntryCount);
        Assert.Equal(2, result.Observation.PlacedCount);
        Assert.Equal(1, result.Observation.OutOfHierarchyCount);
        Assert.Equal(1, result.Observation.UnresolvedCount);
        Assert.Null(result.Observation.FailureClass);
        Assert.Equal("model-out-of-hierarchy", result.Observation.Decisions.Single(d => d.Alias == "O1").Resolution);
        Assert.Equal("unresolved", result.Observation.Decisions.Single(d => d.Alias == "O4").Resolution);
        Assert.Equal(bound.Select(h => (h.SourceId, h.Text, h.Start, h.End)),
            result.Headings.Select(h => (h.SourceId, h.Text, h.Start, h.End)));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"placements\":null}")]
    [InlineData("{\"placements\":{}}")]
    [InlineData("{\"placements\":[{\"alias\":1,\"parent\":\"ROOT\"}]}")]
    public async Task Invalid_responses_are_observed_without_inventing_hierarchy_or_reexecuting(string response)
    {
        var bound = new[] { Heading("O1", 1) };
        using var transport = new Transport(response);
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, transport, CancellationToken.None);
        Assert.Same(bound, result.Headings);
        Assert.Equal("placement-invalid-response", result.Observation.Status);
        Assert.Equal(1, result.Observation.UnresolvedCount);
        Assert.NotNull(result.Observation.FailureClass);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(result.Headings[0].RelationHints);
    }

    [Fact]
    public async Task Transport_failure_is_observed_but_exception_message_and_response_are_not_retained()
    {
        var bound = new[] { Heading("O1", 1) };
        using var transport = new Transport("PRIVATE completion", new HttpRequestException("PRIVATE credential"));
        var result = await HeadingParentResolver.PlaceWithObservationAsync(bound, transport, CancellationToken.None);
        Assert.Same(bound, result.Headings);
        Assert.Equal("placement-transport-failed", result.Observation.Status);
        Assert.Equal(nameof(HttpRequestException), result.Observation.FailureClass);
        Assert.Equal(1, result.Observation.UnresolvedCount);
        Assert.Equal(1, transport.Calls);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result.Observation));
    }

    [Fact]
    public async Task Cancellation_is_not_downgraded_to_an_unresolved_placement()
    {
        using var transport = new Transport("unused", new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => HeadingParentResolver.PlaceWithObservationAsync(
            [Heading("O1", 1)], transport, CancellationToken.None));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Not_required_is_distinct_from_requested_but_unresolved()
    {
        using var transport = new Transport("{\"placements\":[]}");
        var root = Heading("O1", 1) with { RelationHints = ["parent-node:ROOT"] };
        var skipped = await HeadingParentResolver.PlaceWithObservationAsync([root], transport, CancellationToken.None);
        Assert.Equal("placement-not-required", skipped.Observation.Status);
        Assert.Equal(0, transport.Calls);
        var unresolved = await HeadingParentResolver.PlaceWithObservationAsync([Heading("O2", 2)], transport, CancellationToken.None);
        Assert.Equal("placement-unresolved", unresolved.Observation.Status);
        Assert.Equal(1, unresolved.Observation.RequestedCount);
        Assert.Equal(0, unresolved.Observation.PlacedCount);
        Assert.Equal(1, unresolved.Observation.UnresolvedCount);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Rejected_parent_does_not_become_accepted_just_because_the_JSON_parsed()
    {
        using var transport = new Transport("{\"placements\":[{\"alias\":\"O1\",\"parent\":\"O1\"}]}");
        var result = await HeadingParentResolver.PlaceWithObservationAsync([Heading("O1", 1)], transport, CancellationToken.None);
        Assert.Equal("placement-unresolved", result.Observation.Status);
        Assert.Equal(1, result.Observation.ResponseEntryCount);
        Assert.Equal(0, result.Observation.PlacedCount);
        Assert.Equal("unresolved", Assert.Single(result.Observation.Decisions).Resolution);
    }

    [Fact]
    public void Runtime_observation_does_not_rebaseline_compatibility_audit_JSON()
    {
        var audit = new PipelineExecutionAudit("test", 0, 0, 0, 0, [], [], [], []);
        var observed = audit with { PlacementExecution = new("placement-transport-failed", 1, 0, 0, 0, 1,
            nameof(HttpRequestException), [new("source-1", "O1", "unresolved")]) };
        Assert.Equal(JsonSerializer.Serialize(audit), JsonSerializer.Serialize(observed));
    }

    [Fact]
    public async Task Observed_entrypoint_retains_legacy_request_bytes_expected_count_and_heading_output()
    {
        var bound = new[] { Heading("O1", 1), Heading("O2", 2) };
        const string response = "{\"placements\":[{\"alias\":\"O1\",\"parent\":\"ROOT\"},{\"alias\":\"O2\",\"parent\":\"O1\"}]}";
        using var legacy = new Transport(response);
        using var observed = new Transport(response);
        var first = await HeadingParentResolver.PlaceUnresolvedHeadingsAsync(bound, legacy, CancellationToken.None);
        var second = await HeadingParentResolver.PlaceWithObservationAsync(bound, observed, CancellationToken.None);
        Assert.Equal(legacy.Request, observed.Request);
        Assert.Equal(HeadingParentResolver.PlacementPrompt, observed.Request!.Value.Prompt);
        Assert.Equal(2, observed.Request.Value.Count);
        Assert.Equal("{\"headings\":[{\"alias\":\"O1\",\"text\":\"Heading\"},{\"alias\":\"O2\",\"text\":\"Heading\"}],\"toPlace\":[\"O1\",\"O2\"]}", observed.Request.Value.User);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second.Headings));
        Assert.Equal("placement-accepted", second.Observation.Status);
    }

    [Fact]
    public void Unresolved_headings_without_a_transport_do_not_claim_a_requested_call()
    {
        var result = HeadingParentResolver.Observe([Heading("O1", 1)],
            new HashSet<string> { "source-1" }, "placement-not-requested");
        Assert.Equal(0, result.Observation.RequestedCount);
        Assert.Equal(1, result.Observation.UnresolvedCount);
    }

    [Fact]
    public async Task Malformed_filtered_row_is_reported_without_changing_existing_valid_sibling_admission()
    {
        using var transport = new Transport("{\"placements\":[{\"alias\":\"O1\",\"parent\":\"ROOT\"},{\"alias\":\"O2\"}]}");
        var result = await HeadingParentResolver.PlaceWithObservationAsync(
            [Heading("O1", 1), Heading("O2", 2)], transport, CancellationToken.None);
        Assert.Equal("placement-invalid-response", result.Observation.Status);
        Assert.Equal("placement-entry-schema-invalid", result.Observation.FailureClass);
        Assert.Equal(2, result.Observation.ResponseEntryCount);
        Assert.Equal(1, result.Observation.PlacedCount);
        Assert.Equal(1, result.Observation.UnresolvedCount);
        Assert.Equal(["parent-node:ROOT"], result.Headings[0].RelationHints);
        Assert.Empty(result.Headings[1].RelationHints);
        Assert.Equal(1, transport.Calls);
    }

    private static CanonicalSemanticBoundHeading Heading(string alias, int ordinal) =>
        new(alias, $"source-{ordinal}", ordinal, "Heading", "SECTION", "Heading", "document_body", [], 0, 7);

    private sealed class Transport(string response, Exception? failure = null) : IInferenceTransport
    {
        public int Calls { get; private set; }
        public (string Prompt, string User, int Count)? Request { get; private set; }
        public string ModelName => "placement-test";
        public int ContextSize => 65536;
        public string RuntimeDescription => "test";
        public int SharedPrefixTokens => 0;
        public Task<string> BoundaryCutAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0)
        {
            Calls++;
            Request = (systemPrompt, userMessage, expectedItemCount);
            return failure is null ? Task.FromResult(response) : Task.FromException<string>(failure);
        }
        public void Dispose() { }
    }
}
