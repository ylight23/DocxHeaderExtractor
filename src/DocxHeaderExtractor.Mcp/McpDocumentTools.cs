using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DocxHeaderExtractor.Mcp;

[McpServerToolType]
public sealed class McpDocumentTools(
    McpExtractionService service,
    McpExtractionJobQueue jobs)
{
    [McpServerTool(
        Name = "get_docx_extractor_status",
        Title = "Kiểm tra DocxHeaderExtractor",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Kiểm tra LM Studio API, model đang chọn và các thư mục DOCX mà tool được phép đọc.")]
    public Task<McpBackendStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        service.GetStatusAsync(cancellationToken);

    [McpServerTool(
        Name = "extract_docx_headings",
        Title = "Trích xuất heading từ DOCX",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Bắt đầu job trích xuất heading trong nền và trả jobId ngay để tránh timeout MCP. " +
        "Tài liệu thường mất vài phút tùy model và GPU; không chờ đồng bộ trong lượt gọi này. " +
        "Sau đó gọi get_docx_extraction_result bằng đúng jobId cho tới khi state là Completed hoặc Failed. " +
        "inputPath phải là đường dẫn tuyệt đối hoặc tương đối hợp lệ trong DHX_MCP_ALLOWED_ROOTS; " +
        "tool không tự sửa, đổi tên hoặc đoán đường dẫn. Chỉ đọc file, không sửa tài liệu.")]
    public McpJobStartResult ExtractDocxHeadings(
        [Required, MaxLength(1024)]
        [Description(
            "Đường dẫn tuyệt đối tới .docx/.docm/.doc/.rtf/.odt trong thư mục được phép, " +
            "hoặc đường dẫn tương đối tính từ root đầu tiên.")]
        string inputPath)
    {
        try
        {
            return jobs.Start(inputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or
                                   FileNotFoundException or DirectoryNotFoundException or
                                   InvalidOperationException or HttpRequestException or TimeoutException or
                                   JsonException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(
        Name = "get_docx_extraction_result",
        Title = "Lấy trạng thái hoặc kết quả trích xuất DOCX",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Lấy snapshot của job đã tạo bởi extract_docx_headings. Nếu state là Queued hoặc Running, " +
        "đợi recommendedPollSeconds (thường 15 giây) rồi gọi lại. Khi Completed, đọc result; " +
        "khi Failed, đọc error. 30 phút chỉ là thời gian lưu snapshot sau khi hoàn tất, " +
        "không phải thời gian xử lý. Không tạo job mới khi job cũ còn Running.")]
    public McpJobStatusResult GetDocxExtractionResult(
        [Required, MaxLength(64)]
        [Description("Job ID do extract_docx_headings trả về.")]
        string jobId)
    {
        try
        {
            return jobs.Get(jobId);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
