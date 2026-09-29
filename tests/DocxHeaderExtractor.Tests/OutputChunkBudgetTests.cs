using DocxHeaderExtractor.Cli;
using DocxHeaderExtractor.DocumentProcessing.Chunking;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The chunk budget sizes the output section chunks only. Model requests carry their own packing
/// budget, so neither the backend nor the local model may rewrite it, and the backend's own
/// configuration holds nothing about chunking.
/// </summary>
public sealed class OutputChunkBudgetTests
{
    [Theory]
    [InlineData()]
    [InlineData("--lmstudio")]
    [InlineData("--openrouter")]
    [InlineData("-m", "models/Qwen2.5-7B-Instruct-Q4_K_M.gguf")]
    public void Budget_is_the_same_for_every_backend(params string[] flags)
    {
        var o = CommandLineOptions.Parse(["a.docx", .. flags]);

        Assert.Equal(new ChunkingOptions().TokenBudget, o.Pipeline.Chunking.TokenBudget);
    }

    [Theory]
    [InlineData("--chunk-tokens", "3000", "--lmstudio")]
    [InlineData("--lmstudio", "--chunk-tokens", "3000")]
    public void Explicit_budget_wins_regardless_of_flag_order(params string[] flags)
    {
        var o = CommandLineOptions.Parse(["a.docx", .. flags]);

        Assert.Equal(3000, o.Pipeline.Chunking.TokenBudget);
    }

    [Fact]
    public void Ctx_belongs_to_the_local_backend_and_does_not_move_the_budget()
    {
        var o = CommandLineOptions.Parse(["a.docx", "--ctx", "16384"]);

        Assert.Equal(16384u, o.Provider.LocalModel.ContextSize);
        Assert.False(o.Provider.LocalModel.AutoContextSize);
        Assert.Equal(new ChunkingOptions().TokenBudget, o.Pipeline.Chunking.TokenBudget);
    }

    [Fact]
    public void Local_model_options_hold_no_chunking_state()
    {
        var llama = typeof(DocxHeaderExtractor.Infrastructure.AI.LocalModelOptions);
        Assert.Null(llama.GetProperty("ChunkTokenBudget"));
        Assert.Null(llama.GetProperty("MaxOutputTokens"));
        Assert.Null(llama.GetMethod("ApplyRecommendedModelProfile"));
    }
}
