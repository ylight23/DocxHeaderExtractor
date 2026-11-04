using System.Diagnostics;

namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>
/// <c>POST /api/admin/render-pdf</c>: the uploaded DOCX printed to PDF by LibreOffice, for the admin page's print view. One conversion at a time,
/// with a LibreOffice profile of its own (so it never touches a LibreOffice the user has open), kept between conversions because creating one
/// takes most of the first conversion's time; a time limit; and the uploaded and converted files removed.
/// The upload is not kept. 501 when LibreOffice is not installed (DHX_SOFFICE names it when it is not in the usual place).
/// </summary>
internal static class LibreOfficeRender
{
    static readonly SemaphoreSlim One = new(1, 1);
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    static string? Soffice()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DHX_SOFFICE"),
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            "/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/Applications/LibreOffice.app/Contents/MacOS/soffice",
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    public static async Task<IResult> HandleAsync(HttpRequest req, CancellationToken ct)
    {
        if (Soffice() is not { } soffice)
            return Results.Problem("Máy chủ chưa cài LibreOffice (đặt DHX_SOFFICE nếu cài ở chỗ khác).", statusCode: StatusCodes.Status501NotImplemented);
        if (!req.HasFormContentType) return Results.BadRequest(new { message = "Cần multipart/form-data." });
        var form = await req.ReadFormAsync(ct);
        if (form.Files.GetFile("file") is not { Length: > 0 } file || Path.GetExtension(file.FileName).ToLowerInvariant() is not (".docx" or ".docm"))
            return Results.BadRequest(new { message = "Cần một file Word .docx." });

        var work = Path.Combine(Path.GetTempPath(), "dhx-ui", "pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        await One.WaitAsync(ct);
        try
        {
            var input = Path.Combine(work, "document.docx");
            await using (var fs = File.Create(input)) await file.CopyToAsync(fs, ct);
            var profile = new Uri(Path.Combine(Path.GetTempPath(), "dhx-ui", "libreoffice-profile")).AbsoluteUri;
            var start = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-env:UserInstallation=" + profile, "--headless", "--norestore", "--nologo", "--convert-to", "pdf", "--outdir", work, input },
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("SOFFICE_DID_NOT_START");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Limit);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return Results.Problem("LibreOffice không chuyển xong trong 90 giây.", statusCode: StatusCodes.Status504GatewayTimeout);
            }
            var pdf = Path.Combine(work, "document.pdf");
            if (process.ExitCode != 0 || !File.Exists(pdf))
                return Results.Problem("LibreOffice không chuyển được file sang PDF.", statusCode: StatusCodes.Status502BadGateway);
            return Results.File(await File.ReadAllBytesAsync(pdf, ct), "application/pdf");
        }
        finally
        {
            One.Release();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
