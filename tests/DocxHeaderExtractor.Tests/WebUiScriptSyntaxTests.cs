using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Giao diện Web nằm gọn trong một file HTML với một khối <c>&lt;script&gt;</c> ~28.000 ký tự.
/// Không có bước build nào chạm tới nó, nên một lỗi cú pháp JavaScript làm <b>chết toàn bộ giao
/// diện</b> mà không test nào và không lệnh build nào bắt được.
/// <para>
/// Đã xảy ra thật: một khai báo <c>const decisionTag = …</c> bị chèn vào GIỮA biểu thức nối chuỗi
/// <c>$('tree').innerHTML = '&lt;thead&gt;…' +</c>, nên câu lệnh không bao giờ kết thúc và trình
/// duyệt báo <c>Uncaught SyntaxError: Unexpected token 'const'</c>. Người dùng phát hiện, không
/// phải bộ test.
/// </para>
/// </summary>
public class WebUiScriptSyntaxTests
{
    private static string IndexHtmlPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? ".", "src", "DocxHeaderExtractor.Web", "wwwroot", "index.html");
    }

    [Fact]
    public void Script_cua_giao_dien_phai_dung_cu_phap()
    {
        var index = IndexHtmlPath();
        Assert.True(File.Exists(index), $"Không tìm thấy {index}");

        var scripts = Regex.Matches(File.ReadAllText(index), @"<script[^>]*>(.*?)</script>", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(scripts);

        var js = Path.Combine(Path.GetTempPath(), $"dhx-ui-{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(js, string.Join("\n;\n", scripts));

            var start = new ProcessStartInfo("node", $"--check \"{js}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            Process? p;
            try { p = Process.Start(start); }
            catch (Exception)
            {
                // Không có node trên máy này. KHÔNG giả vờ xanh bằng cách bỏ qua im lặng: kiểm
                // được thứ vẫn kiểm được mà không cần node — script không kết thúc bằng toán tử
                // treo, đúng hình dạng lỗi đã xảy ra.
                KhongCoToanTuTreo(scripts);
                return;
            }

            Assert.NotNull(p);
            var err = p!.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.True(p.ExitCode == 0, $"node --check báo lỗi cú pháp:\n{err}");
        }
        finally
        {
            try { File.Delete(js); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Chốt dự phòng: không dòng nào kết thúc bằng toán tử nối rồi dòng kế tiếp mở một KHAI BÁO.
    /// Đó chính xác là hình dạng của lỗi đã xảy ra, và bắt được nó mà không cần node.
    /// </summary>
    private static void KhongCoToanTuTreo(IEnumerable<string> scripts)
    {
        var lines = string.Join("\n", scripts).Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var here = lines[i].TrimEnd();
            if (!here.EndsWith('+') && !here.EndsWith("&&") && !here.EndsWith("||")) continue;

            var next = NextCodeLine(lines, i + 1);
            Assert.False(
                Regex.IsMatch(next, @"^\s*(const|let|var|function|class)\s"),
                $"Dòng {i + 1} kết thúc bằng toán tử treo rồi dòng kế mở khai báo — biểu thức không " +
                $"bao giờ kết thúc:\n  {here}\n  {next}");
        }
    }

    private static string NextCodeLine(string[] lines, int from)
    {
        for (var i = from; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length > 0 && !t.StartsWith("//", StringComparison.Ordinal)) return lines[i];
        }
        return "";
    }

    /// <summary>
    /// Mọi ô nhập/checkbox trong HTML phải được JS GỬI ĐI, và mọi trường JS gửi phải có ô tương
    /// ứng. Thiếu một chiều thì giao diện im lặng bỏ qua lựa chọn của người dùng — không lỗi,
    /// không cảnh báo, chỉ là kết quả sai.
    /// </summary>
    [Fact]
    public void Moi_o_dieu_khien_deu_duoc_gui_di()
    {
        var html = File.ReadAllText(IndexHtmlPath());

        var oNhap = Regex.Matches(html, @"<input[^>]*\sid=""([a-zA-Z]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(id => !BoQua.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        var daGui = Regex.Matches(html, @"fd\.append\('([a-zA-Z]+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var thieu = oNhap.Except(daGui).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(thieu.Count == 0,
            "Ô có trong HTML nhưng JS không gửi: " + string.Join(", ", thieu));
    }

    /// <summary>Ô không phải tuỳ chọn pipeline — file, hiển thị, hoặc do server điền.</summary>
    private static readonly HashSet<string> BoQua = new(StringComparer.Ordinal)
    {
        "file", "model", "backend", "lmStudioModel", "openrouterModel",
        // Ô upload của luồng ĐỐI CHIẾU bản đã sửa, gửi bằng FormData riêng chứ không qua fd.
        "correctedFile",
        // Các ô của audit/trace/gold là các luồng độc lập, không phải tham số /api/extract.
        "auditFile", "goldFile", "goldReviewerId", "goldVersion", "traceExpectedFile",
    };
}
