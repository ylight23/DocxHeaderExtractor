using DocxHeaderExtractor.Core.Application.Routing;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests.Architecture;

public sealed class AuthorityRoutePolicyWiringTests
{
    public static TheoryData<SourceCapabilities, AuthorityRoute> CurrentRuntimeMatrix => new()
    {
        { new SourceCapabilities(true, true, true), AuthorityRoute.PdfAuthority },
        { new SourceCapabilities(true, true, false), AuthorityRoute.DocxAuthority },
        { new SourceCapabilities(true, false, true), AuthorityRoute.DocxAuthority },
        { new SourceCapabilities(true, false, false), AuthorityRoute.DocxAuthority },
    };

    [Theory]
    [MemberData(nameof(CurrentRuntimeMatrix))]
    public void Default_policy_matches_current_runtime_matrix(
        SourceCapabilities capabilities,
        AuthorityRoute expected)
    {
        Assert.Equal(expected, new DefaultAuthorityRoutePolicy().Decide(capabilities));
    }

    [Fact]
    public async Task Authority_pipeline_uses_injected_policy_for_normal_route()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhx-route-policy-{Guid.NewGuid():N}.docx");
        var policy = new RecordingRoutePolicy(AuthorityRoute.DocxAuthority);
        try
        {
            SampleDocumentFactory.Create(path);
            using var pipeline = new AuthorityExtractionPipeline(
                new PipelineOptions { DisableLlm = true }, policy);

            var outline = await pipeline.RunAsync(path);

            Assert.Equal("docx-authority-v1", outline.DeterministicRoute);
            Assert.Equal(new SourceCapabilities(true, false, false), policy.LastCapabilities);
        }
        finally
        {
            LegacyDocConverter.TryDelete(path);
        }
    }

    [Fact]
    public void Unsupported_contract_state_is_explicit()
    {
        Assert.Equal(
            AuthorityRoute.Unsupported,
            new DefaultAuthorityRoutePolicy().Decide(new SourceCapabilities(false, true, true)));
    }

    private sealed class RecordingRoutePolicy(AuthorityRoute route) : IAuthorityRoutePolicy
    {
        public SourceCapabilities? LastCapabilities { get; private set; }

        public AuthorityRoute Decide(SourceCapabilities capabilities)
        {
            LastCapabilities = capabilities;
            return route;
        }
    }
}
