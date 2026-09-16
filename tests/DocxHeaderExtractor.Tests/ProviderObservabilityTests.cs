using System.Text.Json;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

public sealed class ProviderObservabilityTests
{
    [Fact]
    public void Writes_ordered_milestones_and_one_terminal_for_normal_call()
    {
        var root = Path.Combine(Path.GetTempPath(), "dhx-provider-observability-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var logical = ProviderCallTelemetry.Start(new ProviderObservabilityOptions
            {
                RootDirectory = root,
                CampaignId = "test",
                DocumentId = "DOC-TEST",
                HeartbeatSeconds = 60,
            }, new ProviderLogicalCallMetadata
            {
                Stage = "TEST",
                LogicalCallId = "logical-1",
                RequestHash = "request-hash",
                RequestBytes = 128,
                EstimatedInputTokens = 32,
                MaxOutputTokens = 64,
            });
            using var attempt = logical!.StartAttempt("attempt-1", "attempt-hash", 128, 32, 64);
            attempt.Event("RESPONSE_HEADERS_RECEIVED");
            attempt.Event("PARSE_STARTED");
            attempt.Event("PARSE_COMPLETED");
            attempt.Event("BIND_STARTED");
            attempt.Event("BIND_COMPLETED");
            attempt.Complete();
            logical.Complete();

            var events = File.ReadAllLines(Path.Combine(root, "telemetry", "events.jsonl"))
                .Select(line => JsonDocument.Parse(line))
                .Select(d => d.RootElement.GetProperty("eventType").GetString())
                .ToArray();
            Assert.Equal(1, events.Count(e => e == "ATTEMPT_STARTED"));
            Assert.Equal(1, events.Count(e => e == "ATTEMPT_COMPLETED"));
            Assert.Equal(1, events.Count(e => e == "LOGICAL_CALL_COMPLETED"));
            Assert.True(Array.IndexOf(events, "ATTEMPT_STARTED") < Array.IndexOf(events, "PARSE_STARTED"));
            Assert.True(Array.IndexOf(events, "BIND_COMPLETED") < Array.IndexOf(events, "LOGICAL_CALL_COMPLETED"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "telemetry"), "attempt.started.*.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
