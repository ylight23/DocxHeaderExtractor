namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>What an upload may be: its size cap and the file name it is saved under.</summary>
internal static class Uploads
{
    // Trần upload phải đặt ở CẢ HAI tầng, nếu không tầng thấp hơn quyết định trong im lặng: Kestrel
    // mặc định chặn ở 30 MB và ném BadHttpRequestException TRƯỚC khi handler chạy, nên trình duyệt chỉ
    // thấy ERR_CONNECTION_RESET chứ không nhận được thông báo lỗi nào. Một hằng số cho cả hai để chúng
    // không đi lệch nhau.
    public const long MaxBytes = 128L * 1024 * 1024;

    // Tên file người dùng tải lên không được phép thoát khỏi thư mục làm việc.
    public static string SafeName(string name)
    {
        var bare = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
        return string.IsNullOrWhiteSpace(bare) ? "upload.docx" : bare;
    }
}
