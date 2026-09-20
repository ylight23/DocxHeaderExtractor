using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class P4ReachabilityCleanupTests
{
    [Fact]
    public void Retired_pdf_analyst_prompt_fingerprint_remains_artifact_owned()
    {
        var path = Path.Combine(
            RepositoryRoot(),
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocxHeaderExtractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot find repository root.");
    }

}
