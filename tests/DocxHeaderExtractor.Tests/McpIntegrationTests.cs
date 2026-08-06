using DocxHeaderExtractor.Mcp;
using ModelContextProtocol.Client;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Tests;

public sealed class McpPathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dhx-mcp-test-" + Guid.NewGuid().ToString("N"));

    public McpPathPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Resolves_relative_file_inside_allowed_root()
    {
        var path = Path.Combine(_root, "inside.docx");
        File.WriteAllBytes(path, [1, 2, 3]);
        var policy = NewPolicy();

        var resolved = policy.ResolveReadableDocument("inside.docx");

        Assert.Equal(Path.GetFullPath(path), resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Rejects_path_traversal_outside_allowed_root()
    {
        var policy = NewPolicy();

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            policy.ResolveReadableDocument(Path.Combine("..", "outside.docx")));

        Assert.Contains("DHX_MCP_ALLOWED_ROOTS", error.Message);
    }

    [Fact]
    public void Rejects_file_over_size_limit()
    {
        var path = Path.Combine(_root, "large.docx");
        File.WriteAllBytes(path, new byte[5]);
        var policy = new McpPathPolicy(new DhxMcpOptions
        {
            AllowedRoots = [_root],
            MaxInputBytes = 4,
        });

        Assert.Throws<InvalidOperationException>(() => policy.ResolveReadableDocument(path));
    }

    [Theory]
    [InlineData("C:\\safe\\file.docx", "C:\\safe", true)]
    [InlineData("C:\\safe-other\\file.docx", "C:\\safe", false)]
    public void Windows_root_comparison_respects_directory_boundary(
        string candidate,
        string root,
        bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, McpPathPolicy.IsWithin(candidate, root));
    }

    private McpPathPolicy NewPolicy() => new(new DhxMcpOptions
    {
        AllowedRoots = [_root],
        MaxInputBytes = 1024,
    });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed class McpContractSerializationTests
{
    [Fact]
    public void Pending_job_keeps_required_nullable_fields_in_structured_output()
    {
        var status = new McpJobStatusResult(
            JobId: "job-1",
            State: "Running",
            File: "sample.docx",
            CreatedAt: DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
            StartedAt: null,
            CompletedAt: null,
            Result: null,
            Error: null,
            RecommendedPollSeconds: 15);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status, options));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("startedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("error").ValueKind);
    }
}

public sealed class McpStdioIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dhx-mcp-stdio-" + Guid.NewGuid().ToString("N"));

    public McpStdioIntegrationTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Trần 90 s chứ không phải 20 s: test này spawn một tiến trình `dotnet` thật rồi bắt tay MCP
    /// qua stdio, nên nó đo cả thời gian khởi động runtime. Trên máy đang chạy suy luận 7B cục bộ,
    /// 20 s bị vượt và test đỏ vì HẾT GIỜ chứ không phải vì sai — đã gặp 3 lần trong một phiên, và
    /// lần nào chạy lại một mình cũng xanh. Một test đỏ theo tải máy là test dạy người ta bỏ qua
    /// màu đỏ, nên nới trần chứ không giữ để "nhắc nhở".
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task Stdio_server_advertises_only_the_three_read_only_tools()
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["DHX_MCP_ALLOWED_ROOTS"] = _root;
        environment["DHX_MCP_RULES_ONLY"] = "true";

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "dhx-mcp-test",
            Command = "dotnet",
            Arguments = [FindMcpDll()],
            WorkingDirectory = FindRepositoryRoot(),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["extract_docx_headings", "get_docx_extraction_result", "get_docx_extractor_status"],
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(tools, tool => Assert.NotNull(tool.Description));

        var result = await client.CallToolAsync("get_docx_extractor_status");
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);

        var input = Path.Combine(_root, "mau.docx");
        File.Copy(Path.Combine(FindRepositoryRoot(), "samples", "mau.docx"), input);
        var extraction = await client.CallToolAsync(
            "extract_docx_headings",
            new Dictionary<string, object?> { ["inputPath"] = input });
        Assert.NotEqual(true, extraction.IsError);
        Assert.NotNull(extraction.StructuredContent);

        var jobId = extraction.StructuredContent.Value.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));

        string? state = null;
        for (var attempt = 0; attempt < 50 && state != "Completed"; attempt++)
        {
            var poll = await client.CallToolAsync(
                "get_docx_extraction_result",
                new Dictionary<string, object?> { ["jobId"] = jobId });
            Assert.NotEqual(true, poll.IsError);
            Assert.NotNull(poll.StructuredContent);
            state = poll.StructuredContent.Value.GetProperty("state").GetString();
            if (state is "Failed" or "Cancelled")
                Assert.Fail($"MCP background job kết thúc ở state {state}: {poll.StructuredContent}");
            if (state != "Completed") await Task.Delay(100);
        }

        Assert.Equal("Completed", state);
    }

    private static string FindMcpDll()
    {
        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var path = Path.Combine(
            FindRepositoryRoot(), "src", "DocxHeaderExtractor.Mcp", "bin", configuration, "net9.0", "dhx-mcp.dll");
        Assert.True(File.Exists(path), $"Không tìm thấy MCP test host: {path}");
        return path;
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
        throw new DirectoryNotFoundException("Không tìm thấy root DocxHeaderExtractor từ test output.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
