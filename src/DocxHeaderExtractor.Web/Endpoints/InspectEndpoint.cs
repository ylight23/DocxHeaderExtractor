namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>
/// <c>POST /api/inspect</c>: source inspection is local only: no OCR, model or provider call. The uploaded copy is removed after the
/// response.
/// </summary>
internal static class InspectEndpoint
{
    public static async Task<IResult> HandleAsync(HttpRequest req, CancellationToken ct)
    {
        if (!req.HasFormContentType) return Results.BadRequest(new { message = "Cần multipart/form-data." });
        string? work = null;
        try
        {
            var form = await req.ReadFormAsync(ct);
            var upload = form.Files["file"];
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { message = "Chưa chọn file." });
            work = Path.Combine(Path.GetTempPath(), "dhx-inspect", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var path = Path.Combine(work, Uploads.SafeName(upload.FileName));
            await using (var stream = File.Create(path)) await upload.CopyToAsync(stream, ct);
            return Results.Json(SourceInspection.Read(path, ct), WebJson.Options);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Json(new { message = $"File quá lớn (trần {Uploads.MaxBytes / (1024 * 1024)} MB)." }, statusCode: 413);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.BadRequest(new { message = "Không đọc được cấu trúc file. Hãy kiểm tra file có hợp lệ và không bị khóa." });
        }
        finally
        {
            if (work is not null) { try { Directory.Delete(work, recursive: true); } catch (IOException) { } }
        }
    }
}
