using System.Diagnostics;
using System.Runtime.Versioning;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>
/// OpenXML SDK chỉ đọc được định dạng OOXML (.docx). File .doc là định dạng nhị phân CFB đời cũ,
/// nên phải chuyển đổi trước. Ưu tiên LibreOffice (không cần Word), sau đó tới Word COM.
/// </summary>
public static class LegacyDocConverter
{
    private static readonly string[] LibreOfficeCandidates =
    [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        "/usr/bin/soffice",
        "/usr/bin/libreoffice",
        "/Applications/LibreOffice.app/Contents/MacOS/soffice",
    ];

    public sealed record ConversionResult(string Path, bool IsTemporary, string? Converter);

    /// <summary>
    /// Trả về đường dẫn .docx đọc được. Nếu đầu vào đã là .docx thì trả về nguyên trạng.
    /// Bên gọi phải xoá file tạm khi <see cref="ConversionResult.IsTemporary"/> = true.
    /// </summary>
    public static ConversionResult EnsureDocx(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Không tìm thấy file: {path}", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".docx" or ".docm")
            return new ConversionResult(path, false, null);

        if (ext is not ".doc" and not ".rtf" and not ".odt")
            throw new NotSupportedException($"Định dạng không hỗ trợ: {ext}");

        var outDir = Path.Combine(Path.GetTempPath(), "dhx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        if (TryLibreOffice(path, outDir, out var loPath))
            return new ConversionResult(loPath, true, "LibreOffice");

        if (OperatingSystem.IsWindows() && TryWordInterop(path, outDir, out var wordPath))
            return new ConversionResult(wordPath, true, "Microsoft Word");

        TryDelete(outDir);
        throw new InvalidOperationException(
            $"""
             Không chuyển đổi được '{Path.GetFileName(path)}' sang .docx.
             OpenXML SDK không đọc trực tiếp được định dạng .doc nhị phân.
             Hãy cài LibreOffice (soffice) hoặc Microsoft Word, hoặc tự lưu file sang .docx.
             """);
    }

    private static bool TryLibreOffice(string input, string outDir, out string output)
    {
        output = "";
        var exe = FindLibreOffice();
        if (exe is null) return false;

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--norestore");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("docx:MS Word 2007 XML");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(input);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(180_000)) { proc.Kill(true); return false; }
        }
        catch (Exception)
        {
            return false;
        }

        var expected = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".docx");
        if (File.Exists(expected)) { output = expected; return true; }

        var any = Directory.EnumerateFiles(outDir, "*.docx").FirstOrDefault();
        if (any is not null) { output = any; return true; }

        return false;
    }

    private static string? FindLibreOffice()
    {
        foreach (var c in LibreOfficeCandidates)
            if (File.Exists(c)) return c;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "soffice.exe", "soffice" })
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), name);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { /* PATH có ký tự lạ */ }
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryWordInterop(string input, string outDir, out string output)
    {
        output = "";
        var progId = Type.GetTypeFromProgID("Word.Application");
        if (progId is null) return false;

        dynamic? app = null;
        dynamic? doc = null;
        try
        {
            app = Activator.CreateInstance(progId);
            if (app is null) return false;

            app.Visible = false;
            app.DisplayAlerts = 0;

            // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles)
            doc = app.Documents.Open(input, false, true, false);

            var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".docx");
            const int wdFormatXMLDocument = 12;
            doc.SaveAs2(target, wdFormatXMLDocument);

            output = target;
            return File.Exists(target);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { doc?.Close(0); } catch { /* ignore */ }
            try { app?.Quit(0); } catch { /* ignore */ }
        }
    }

    public static void TryDelete(string pathOrDir)
    {
        try
        {
            if (Directory.Exists(pathOrDir)) Directory.Delete(pathOrDir, true);
            else if (File.Exists(pathOrDir)) File.Delete(pathOrDir);
        }
        catch (IOException) { /* file tạm, kệ */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>Xoá thư mục tạm chứa file đã convert.</summary>
    public static void Cleanup(ConversionResult result)
    {
        if (!result.IsTemporary) return;
        var dir = Path.GetDirectoryName(result.Path);
        if (!string.IsNullOrEmpty(dir)) TryDelete(dir);
    }
}
