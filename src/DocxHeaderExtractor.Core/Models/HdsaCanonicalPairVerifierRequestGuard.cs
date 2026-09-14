namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Fail-closed integrity check for a frozen request entry before any transport is allowed.
/// </summary>
public static class HdsaCanonicalPairVerifierRequestGuard
{
    public static HdsaFrozenRequestVerification Verify(
        HdsaIdentityPairVerificationRequest request,
        string expectedSha256,
        int expectedBytes)
        => Verify(HdsaCanonicalPairVerifierRequestBuilder.Build(request), expectedSha256, expectedBytes);

    public static HdsaFrozenRequestVerification Verify(
        HdsaCanonicalPairVerifierRequest built,
        string expectedSha256,
        int expectedBytes)
    {
        if (!string.Equals(built.Sha256, expectedSha256, StringComparison.Ordinal))
            return new(false, "REQUEST_SHA256_MISMATCH", built.Sha256, built.Utf8Bytes.Length);
        if (built.Utf8Bytes.Length != expectedBytes)
            return new(false, "REQUEST_BYTE_LENGTH_MISMATCH", built.Sha256, built.Utf8Bytes.Length);
        return new(true, null, built.Sha256, built.Utf8Bytes.Length);
    }
}

public sealed record HdsaFrozenRequestVerification(
    bool Accepted,
    string? RejectionReason,
    string ReconstructedSha256,
    int ReconstructedBytes);
