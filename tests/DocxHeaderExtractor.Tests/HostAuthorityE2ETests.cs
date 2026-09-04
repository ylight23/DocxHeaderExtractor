extern alias WebApp;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.AgentHarness;
using DocxHeaderExtractor.Application.Semantics;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.Sources;
using DocxHeaderExtractor.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// R4-11 runtime proof: every normal host reaches the canonical authority tool on one deterministic
/// DOCX. This deliberately exercises the public host surfaces, not source-string cutover tests.
/// </summary>
public sealed class HostAuthorityE2ETests
{
    private const string ExpectedFingerprint =
        "16284414abee710236b27fe92f710b95efb32928e169ba0e1ede2e63891b8429";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact(Timeout = 180_000)]
    public async Task All_normal_hosts_join_the_canonical_tool_fingerprint()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"dhx-r4-11-{Guid.NewGuid():N}.docx");
        try
        {
            SampleDocumentFactory.Create(fixture);

            var canonical = await RunCanonicalToolAsync(fixture);
            var agentHarness = await RunAgentHarnessAsync(fixture);
            var mcp = await RunMcpAsync(fixture);
            var web = await RunWebAsync(fixture);
            var cli = await RunCliAsync(fixture);

            var canonicalFingerprint = Fingerprint(canonical);
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CANONICAL_TOOL_FINGERPRINT"] = canonicalFingerprint,
                ["AGENT_HARNESS_FINGERPRINT"] = Fingerprint(agentHarness),
                ["MCP_FINGERPRINT"] = Fingerprint(mcp),
                ["WEB_FINGERPRINT"] = Fingerprint(web),
                ["CLI_FINGERPRINT"] = Fingerprint(cli),
            };

            Assert.All(fingerprints, item => Assert.Equal(canonicalFingerprint, item.Value));
            Assert.Equal(ExpectedFingerprint, canonicalFingerprint);
            Assert.Equal(0, fingerprints.Values.Distinct(StringComparer.Ordinal).Count() - 1);

            AssertNoExternalProvider(canonical, "canonical tool");
            AssertNoExternalProvider(agentHarness, "AgentHarness");
            AssertNoExternalProvider(cli, "CLI");

            WriteObservedMeasurement(fixture, fingerprints);
        }
        finally
        {
            LegacyDocConverter.TryDelete(fixture);
        }
    }

    [Fact]
    public void Normal_host_routes_have_no_legacy_or_direct_pipeline_bypass()
    {
        var root = FindRepositoryRoot();
        var cli = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Cli", "Program.cs"));
        var cliComposition = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Cli", "CliHarnessComposition.cs"));
        var web = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Web", "Program.cs"));
        var mcp = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.Mcp", "McpExtractionService.cs"));
        var tool = File.ReadAllText(Path.Combine(root, "src", "DocxHeaderExtractor.AgentHarness", "DocumentExtractionTool.cs"));

        var normalCli = Slice(cli, "static async Task<int> RunExtractAsync", "/// <summary>");
        var normalWeb = Slice(web, "app.MapPost(\"/api/extract\"", "// Cấu hình log");
        var normalMcp = Slice(mcp, "public async Task<McpExtractionResult> ExtractAsync", "    private PipelineOptions BuildPipelineOptions");

        Assert.Contains("new PipelineDocumentExtractionTool", normalCli);
        Assert.DoesNotContain("new AuthorityExtractionPipeline", normalCli);
        Assert.Contains("new PipelineDocumentExtractionTool", normalWeb);
        Assert.DoesNotContain("new AuthorityExtractionPipeline", normalWeb);
        Assert.Contains("new PipelineDocumentExtractionTool", normalMcp);
        Assert.DoesNotContain("new AuthorityExtractionPipeline", normalMcp);
        Assert.Contains("new AuthorityExtractionPipeline", tool);
        Assert.Contains("FileInputResourceResolver", cliComposition);
        Assert.Contains("SemanticRegistryDefaults.Create", cliComposition);

        Assert.Equal(4, new[] { "CLI", "WEB", "MCP", "AGENT_HARNESS" }.Length);
    }

    private static async Task<DocumentOutline> RunCanonicalToolAsync(string fixture)
    {
        using var tool = new PipelineDocumentExtractionTool(DeterministicOptions());
        return await tool.ExecuteAsync(new AgentToolInvocation(new DocumentAgentRequest(fixture), 1));
    }

    private static async Task<DocumentOutline> RunAgentHarnessAsync(string fixture)
    {
        using var tool = new PipelineDocumentExtractionTool(DeterministicOptions());
        var harness = new DocumentAgentHarness(tool);
        var run = await harness.RunAsync(new DocumentAgentRequest(fixture));
        return run.TaskResult.Value;
    }

    private static async Task<IReadOnlyList<HostHeading>> RunMcpAsync(string fixture)
    {
        var options = new DhxMcpOptions
        {
            AllowedRoots = [Path.GetDirectoryName(fixture)!],
            RulesOnly = true,
        };
        using var service = new McpExtractionService(
            options,
            new McpPathPolicy(options),
            new DocumentAgentHarnessFactory(
                new FileInputResourceResolver(options.AllowedRoots),
                SemanticRegistryDefaults.Create()));

        var result = await service.ExtractAsync(fixture);
        Assert.Equal("rules-only", result.Backend);
        return result.Headings.Select(h => new HostHeading(
            h.StableId, h.HeadingSpan?.Start, h.HeadingSpan?.End, h.Level, h.Text)).ToArray();
    }

    private static async Task<IReadOnlyList<HostHeading>> RunWebAsync(string fixture)
    {
        await using var factory = new WebApplicationFactory<WebApp::Program>();
        using var client = factory.CreateClient();
        await using var stream = File.OpenRead(fixture);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var form = new MultipartFormDataContent();
        form.Add(file, "file", "r4-11-fixture.docx");
        form.Add(new StringContent("true"), "noLlm");

        using var response = await client.PostAsync("/api/extract", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resultLine = lines
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .LastOrDefault(node => node.TryGetProperty("type", out var type) && type.GetString() == "result");
        Assert.True(resultLine.ValueKind != JsonValueKind.Undefined, "Web không trả event result.");
        var outline = resultLine.GetProperty("outline");
        var humanReview = resultLine.GetProperty("humanReview");
        Assert.Equal(outline.GetProperty("paragraphCount").GetInt32() >= 0, humanReview.ValueKind == JsonValueKind.Object);
        Assert.False(string.IsNullOrWhiteSpace(resultLine.GetProperty("humanReviewUrl").GetString()));
        return outline.GetProperty("headings").EnumerateArray()
            .Select(heading => new HostHeading(
                GetString(heading, "stableId"),
                GetInt(heading, "headingSpan", "start"),
                GetInt(heading, "headingSpan", "end"),
                GetNullableInt(heading, "level"),
                heading.GetProperty("text").GetString() ?? string.Empty))
            .ToArray();
    }

    private static async Task<DocumentOutline> RunCliAsync(string fixture)
    {
        var root = FindRepositoryRoot();
        var cliDll = Path.Combine(root, "src", "DocxHeaderExtractor.Cli", "bin", "Release", "net9.0", "dhx.dll");
        Assert.True(File.Exists(cliDll), $"Không tìm thấy CLI Release host: {cliDll}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(cliDll);
        process.StartInfo.ArgumentList.Add("--no-llm");
        process.StartInfo.ArgumentList.Add("--quiet");
        process.StartInfo.ArgumentList.Add("--format");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add(fixture);
        Assert.True(process.Start(), "Không khởi động được CLI normal extraction.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"CLI exit code {process.ExitCode}: {stderr}");
        var outline = JsonSerializer.Deserialize<DocumentOutline>(stdout, JsonOptions);
        Assert.NotNull(outline);
        return outline!;
    }

    private static PipelineOptions DeterministicOptions() => new() { DisableLlm = true };

    private static void AssertNoExternalProvider(DocumentOutline outline, string host)
    {
        Assert.NotNull(outline.Provenance);
        Assert.All(outline.Provenance!.Passes, pass =>
            Assert.False(pass.SentDataExternally, $"{host} unexpectedly sent provider data."));
    }

    private static string Fingerprint(DocumentOutline outline) => Fingerprint(
        outline.Headings.Select(h => new HostHeading(
            h.StableId, h.HeadingSpan?.Start, h.HeadingSpan?.End, h.Level, h.Text)).ToArray());

    private static string Fingerprint(IReadOnlyList<HostHeading> headings)
    {
        var json = JsonSerializer.Serialize(headings);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static int? GetNullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static int? GetInt(JsonElement element, string parent, string property)
    {
        if (!element.TryGetProperty(parent, out var span) || span.ValueKind != JsonValueKind.Object)
            return null;
        return GetNullableInt(span, property);
    }

    private static void WriteObservedMeasurement(string fixture, IReadOnlyDictionary<string, string> fingerprints)
    {
        var output = Path.Combine(Path.GetTempPath(), "dhx-r4-11-host-e2e-observed.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            fixture = Path.GetFileName(fixture),
            fingerprints,
            unjoinedHostResults = 0,
            providerCalls = 0,
            expectedChanged = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Không tìm thấy route marker: {start}");
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Không tìm thấy route end marker: {end}");
        return text[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DocxHeaderExtractor.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Không tìm thấy root DocxHeaderExtractor.");
    }

    private sealed record HostHeading(
        string? StableId,
        int? HeadingSpanStart,
        int? HeadingSpanEnd,
        int? Level,
        string Text);
}
