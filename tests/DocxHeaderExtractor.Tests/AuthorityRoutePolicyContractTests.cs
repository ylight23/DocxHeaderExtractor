using Xunit;

namespace DocxHeaderExtractor.Tests.Architecture;

public sealed record SourceCapabilities(bool HasDocx, bool HasPdf, bool AnalystAvailable);

public enum AuthorityRoute
{
    DocxAuthority,
    PdfAuthority,
    Unsupported,
}

public interface IAuthorityRoutePolicy
{
    AuthorityRoute Decide(SourceCapabilities capabilities);
}

internal sealed class CharacterizedAuthorityRoutePolicy : IAuthorityRoutePolicy
{
    public AuthorityRoute Decide(SourceCapabilities capabilities)
    {
        if (!capabilities.HasDocx) return AuthorityRoute.Unsupported;
        return capabilities.HasPdf && capabilities.AnalystAvailable
            ? AuthorityRoute.PdfAuthority
            : AuthorityRoute.DocxAuthority;
    }
}

public sealed class AuthorityRoutePolicyContractTests
{
    public static TheoryData<SourceCapabilities, AuthorityRoute> CurrentAuthorityMatrix => new()
    {
        { new SourceCapabilities(true, true, true), AuthorityRoute.PdfAuthority },
        { new SourceCapabilities(true, true, false), AuthorityRoute.DocxAuthority },
        { new SourceCapabilities(true, false, true), AuthorityRoute.DocxAuthority },
        { new SourceCapabilities(true, false, false), AuthorityRoute.DocxAuthority },
    };

    [Theory]
    [MemberData(nameof(CurrentAuthorityMatrix))]
    public void Contract_matches_current_authority_truth_table(
        SourceCapabilities capabilities,
        AuthorityRoute expected)
    {
        var policy = new CharacterizedAuthorityRoutePolicy();

        Assert.Equal(expected, policy.Decide(capabilities));
    }

    [Fact]
    public void Missing_docx_is_explicitly_unsupported_and_not_invented()
    {
        var policy = new CharacterizedAuthorityRoutePolicy();

        Assert.Equal(
            AuthorityRoute.Unsupported,
            policy.Decide(new SourceCapabilities(false, true, true)));
    }

    [Fact]
    public void Contract_has_no_legacy_fallback_or_pipeline_dependency()
    {
        var method = typeof(IAuthorityRoutePolicy).GetMethod(nameof(IAuthorityRoutePolicy.Decide));

        Assert.NotNull(method);
        Assert.Equal(typeof(AuthorityRoute), method!.ReturnType);
        Assert.Equal([typeof(SourceCapabilities)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(method.GetParameters(), parameter =>
            parameter.ParameterType.Namespace?.Contains("Llm", StringComparison.Ordinal) == true);
    }
}
