using System.Text.Json;
using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.Tests;

public sealed class UsageLedgerTests
{
    [Fact]
    public void Cache_creation_and_cache_read_tokens_are_parsed_separately()
    {
        var usage = Usage.Check(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = "{}" } } },
            usage = new
            {
                prompt_tokens = 5000, completion_tokens = 300,
                prompt_tokens_details = new { cached_tokens = 1200, cache_creation_input_tokens = 800 },
            },
        }), 100, 4096);

        Assert.Equal(1200, usage.CachedTokens);
        Assert.Equal(800, usage.CacheCreationTokens);
    }

    [Fact]
    public void Missing_cache_fields_leave_the_new_counter_empty()
    {
        var usage = Usage.Check(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = "{}" } } },
            usage = new { prompt_tokens = 100, completion_tokens = 10 },
        }), 100, 4096);

        Assert.Null(usage.CacheCreationTokens);
    }
}
