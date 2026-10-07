using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Inference;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The provider request composer moved out of document processing behind <see cref="IFrozenInferenceRequestComposer"/>.
/// These tests prove it still produces the exact bytes the provider was sent for the SRC-089 V2 requalification: the stored
/// bodies are taken apart into the prompt, message and token limit they carry and composed again. No provider call.
/// </summary>
public sealed class OpenRouterQwen37InferenceRequestComposerParityTests
{
    private const string Root = "artifacts/v5-p6t-function-membership/p6t-src089-pdf-universe-v2-requalification-20261007";

    [Theory]
    [InlineData("call-01-F1.request-body.json", "85aca595db7e174b3f89ebea803a9d1e4a317d524378d3a5c91c22d9b42f989f")]
    [InlineData("call-02-G2A.request-body.json", "18ad52429cda4cc002af82e2b29a79c3b71d502ca9709db0219b909500c82e66")]
    public void The_composer_reproduces_the_stored_SRC089_V2_provider_body_byte_for_byte(string file, string expectedSha256)
    {
        var stored = File.ReadAllBytes(TestRepository.Path($"{Root}/{file}"));
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(stored)));

        using var document = JsonDocument.Parse(stored);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var system = messages.Single(item => item.GetProperty("role").GetString() == "system").GetProperty("content").GetString()!;
        var user = messages.Single(item => item.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        var maxTokens = document.RootElement.GetProperty("max_tokens").GetInt32();

        IFrozenInferenceRequestComposer composer = new OpenRouterQwen37InferenceRequestComposer();
        var composed = composer.Build(system, user, maxTokens);

        Assert.Equal(stored, composed);
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(composed)));
    }

    [Fact]
    public void Document_processing_names_no_provider_model_or_wire_envelope()
    {
        var banned = new[] { "qwen/qwen3.7-flash", "OpenRouterQwen37", "V5ProviderEnvelope", "QualifiedInferenceRequestFactory", "alibaba" };
        foreach (var file in Directory.EnumerateFiles(TestRepository.Path("src/DocxHeaderExtractor.DocumentProcessing"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            foreach (var token in banned)
                Assert.False(source.Contains(token, StringComparison.OrdinalIgnoreCase), $"{token} in {Path.GetFileName(file)}");
        }
    }
}
