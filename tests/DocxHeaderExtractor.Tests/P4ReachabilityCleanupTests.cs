using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class P4ReachabilityCleanupTests
{
    [Fact]
    public void Retired_pdf_analyst_prompt_fingerprint_remains_artifact_owned()
    {
        var path = Path.Combine(
            TestRepository.Root(),
            "artifacts/identity-benchmark/v8h0/heading-extraction-preflight-v1/manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var fingerprint = document.RootElement
            .GetProperty("detector")
            .GetProperty("promptProfileSha256")
            .GetString();

        Assert.Equal(
            "028ed77b71687bebadd8f5e702b7ad0890e11dca845597cf0a48652e8aec7aab",
            fingerprint);
    }

}
