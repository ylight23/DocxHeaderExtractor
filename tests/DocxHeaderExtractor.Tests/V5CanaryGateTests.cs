using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The canary gate is the only thing standing between a bug in pack selection and accidentally
/// running all 31 provider requests. It must fail closed on both dimensions: wrong count, and no
/// explicit authorization - and it never performs a network call itself.
/// </summary>
public sealed class V5CanaryGateTests
{
    [Fact]
    public void Canary_request_count_is_hard_pinned_to_three()
    {
        Assert.Equal(3, V5CanaryGate.CanaryRequestCount);
    }

    [Fact]
    public void A_full_cohort_count_is_rejected_even_if_authorized()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => V5CanaryGate.Authorize(31, providerExecutionAuthorized: true));
        Assert.Contains("canary-request-count-must-be-exactly-3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unauthorized_canary_count_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => V5CanaryGate.Authorize(3, providerExecutionAuthorized: false));
        Assert.Equal("canary-provider-execution-not-authorized", ex.Message);
    }

    [Fact]
    public void An_authorized_exact_canary_count_passes()
    {
        var ex = Record.Exception(() => V5CanaryGate.Authorize(3, providerExecutionAuthorized: true));
        Assert.Null(ex);
    }
}
