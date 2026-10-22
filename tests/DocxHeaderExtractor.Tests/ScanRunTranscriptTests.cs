using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Scan;
using DocxHeaderExtractor.Agents;

namespace DocxHeaderExtractor.Tests;

/// <summary>A scan run's transcript keeps every request and raw reply as sent and received, marks its folder, and says when it is incomplete.</summary>
public sealed class ScanRunTranscriptTests
{
    static PreparedRequest Request(string body) => new(body, body.Length, AgentProtocol.Sha(body));

    [Fact]
    public void Each_call_is_one_line_with_its_request_and_raw_reply_and_the_status_says_complete()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dhx-transcript-" + Guid.NewGuid().ToString("N"));
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"{\\\"precedingSelected\\\":{\\\"alias\\\":\\\"P019-L005\\\",\\\"page\\\":19}}\"}]}";
        using (var transcript = new ScanRunTranscript(folder, "test00000001"))
        {
            transcript.Record("SEG-014", 1, Request(body), () => new ModelTurn("{\"status\":\"COMPLETE\"}", [], "stop"));
        }

        Assert.True(File.Exists(Path.Combine(folder, ScanRunTranscript.Marker)));
        var line = JsonDocument.Parse(File.ReadAllLines(Path.Combine(folder, "run-test00000001.jsonl")).Single()).RootElement;
        Assert.Equal("SEG-014", line.GetProperty("unit").GetString());
        Assert.Equal(AgentProtocol.Sha(body), line.GetProperty("requestSha256").GetString());
        Assert.Equal(body, line.GetProperty("request").GetString());
        Assert.Equal("P019-L005", line.GetProperty("precedingSelected").GetString());
        Assert.Equal("{\"status\":\"COMPLETE\"}", line.GetProperty("content").GetString());
        Assert.Contains("TRANSCRIPT_COMPLETE recorded=1", File.ReadAllText(Path.Combine(folder, "run-test00000001.status.txt")));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void A_call_that_throws_is_recorded_and_the_status_says_incomplete()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dhx-transcript-" + Guid.NewGuid().ToString("N"));
        using (var transcript = new ScanRunTranscript(folder, "test00000002"))
        {
            Assert.Throws<InvalidOperationException>(() => transcript.Record("SEG-001", 1, Request("{}"),
                () => throw new InvalidOperationException("TRANSPORT")));
        }

        var status = File.ReadAllText(Path.Combine(folder, "run-test00000002.status.txt"));
        Assert.Contains("TRANSCRIPT_INCOMPLETE", status);
        Assert.Contains("threwCalls=1", status);
        Directory.Delete(folder, recursive: true);
    }
}
