using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.DocumentProcessing.Source.Scan;

/// <summary>
/// The external tools of the OCR preflight: Tesseract (recognition, TSV output with word boxes and confidences) and ImageMagick (the
/// preprocessing of a second view). Both are optional: when one is missing the preflight records why a view could not be made. Like
/// <c>OfficeDocumentConverter</c>, they run as child processes with their arguments passed one by one and a time limit.
/// </summary>
public static class TesseractOcr
{
    public const string EngineName = "tesseract";
    public const int TimeoutMs = 180_000;

    public static string? FindTesseract() => Find("tesseract", "tesseract.exe", @"C:\Program Files\Tesseract-OCR\tesseract.exe");
    public static string? FindImageMagick() => Find("magick", "magick.exe", "convert");

    static string? Find(params string[] names)
    {
        foreach (var name in names)
        {
            if (Path.IsPathRooted(name)) { if (File.Exists(name)) return name; continue; }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { var full = Path.Combine(dir.Trim(), name); if (File.Exists(full)) return full; }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>The engine's own version line, for example "tesseract 5.3.4".</summary>
    public static string Version(string exe)
    {
        var (code, stdout, stderr) = Run(exe, ["--version"], null);
        var text = (stdout + "\n" + stderr).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("tesseract", StringComparison.OrdinalIgnoreCase));
        return code == 0 && text is not null ? text : throw new InvalidOperationException("TESSERACT_VERSION_UNREADABLE");
    }

    /// <summary>The languages the engine has data for.</summary>
    public static IReadOnlySet<string> Languages(string exe)
    {
        var (_, stdout, _) = Run(exe, ["--list-langs"], null);
        return stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains(' ')).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Recognises one image and returns the raw TSV. One thread per process, so pages can run side by side.</summary>
    /// <remarks>The image goes to the engine on its standard input, not as a path: Tesseract on Windows cannot open a path with characters
    /// outside the system code page (a user folder such as C:\Users\Tuấn), and .NET reads any path. The output is the same byte for byte.</remarks>
    public static string RecognizeTsv(string exe, string imagePath, string languages, int psm)
    {
        var (code, stdout, stderr) = Run(exe, ["stdin", "stdout", "-l", languages, "--psm", psm.ToString(CultureInfo.InvariantCulture), "tsv"],
            new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" }, File.ReadAllBytes(imagePath));
        if (code != 0) throw new InvalidOperationException("TESSERACT_FAILED:" + stderr.Trim().Split('\n').LastOrDefault());
        return stdout;
    }

    /// <summary>The second view's image: grey, scaled. Returns false when ImageMagick is missing or fails.</summary>
    public static bool Upscale(string magick, string input, string output, double scale) =>
        Magick(magick, input, ["-colorspace", "Gray", "-resize", (scale * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"], output);

    /// <summary>
    /// The words of a TSV as lines: the engine's level-5 rows grouped by block, paragraph and line, in its order. Boxes are divided by
    /// <paramref name="scale"/> so that every view speaks in the pixels of the original image. Empty words are left out.
    /// </summary>
    public static IReadOnlyList<(string Key, IReadOnlyList<OcrWord> Words)> ParseTsv(string tsv, double scale)
    {
        var lines = new List<(string, List<OcrWord>)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var f = row.TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5") continue;
            var text = f[11].Trim();
            if (text.Length == 0) continue;
            int I(int k) => (int)Math.Round(int.Parse(f[k], CultureInfo.InvariantCulture) / scale);
            var word = new OcrWord(text, new PixelBox(I(6), I(7), I(8), I(9)), double.Parse(f[10], CultureInfo.InvariantCulture));
            var key = f[2] + "." + f[3] + "." + f[4];
            if (!index.TryGetValue(key, out var at)) { index[key] = at = lines.Count; lines.Add((key, new List<OcrWord>())); }
            lines[at].Item2.Add(word);
        }
        return lines.Select(l => (l.Item1, (IReadOnlyList<OcrWord>)l.Item2)).ToArray();
    }

    /// <summary>The width and height of a PNG or JPEG from its header; (0, 0) for another format.</summary>
    public static (int Width, int Height) ImageSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            for (int i = 2; i + 9 < b.Length;)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                var len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + len;
            }
        return (0, 0);
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The share of grey above which a pixel turns white in the black-and-white images a vision model sees.</summary>
    public const string BilevelThreshold = "55%";

    /// <summary>A black-and-white PNG of the page at <paramref name="width"/> pixels wide, for a vision model that sees the whole page. A scan
    /// in grey JPEG is several times larger (paper texture); black and white keeps letters, weight and diacritics.</summary>
    public static bool Downscale(string magick, string input, string output, int width) =>
        Magick(magick, input, ["-colorspace", "Gray", "-resize", width.ToString(CultureInfo.InvariantCulture) + "x", "-threshold", BilevelThreshold, "-type", "bilevel"], output);

    /// <summary>A black-and-white PNG of one region of the page at full resolution, with <paramref name="pad"/> pixels around it.</summary>
    public static bool Crop(string magick, string input, string output, PixelBox box, int pad)
    {
        var x = Math.Max(0, box.Left - pad); var y = Math.Max(0, box.Top - pad);
        var geometry = string.Create(CultureInfo.InvariantCulture, $"{box.Width + 2 * pad}x{box.Height + 2 * pad}+{x}+{y}");
        return Magick(magick, input, ["-crop", geometry, "+repage", "-colorspace", "Gray", "-threshold", BilevelThreshold, "-type", "bilevel"], output);
    }

    /// <summary>Runs ImageMagick on one image and writes a PNG. The image goes in on standard input and comes out on standard output, and .NET
    /// reads and writes the files, so no path ever reaches the tool (a path outside the system code page fails on Windows).</summary>
    static bool Magick(string magick, string input, IEnumerable<string> operations, string output)
    {
        var all = new List<string>();
        if (Path.GetFileNameWithoutExtension(magick).Equals("magick", StringComparison.OrdinalIgnoreCase)) all.Add("convert");
        all.Add("-");
        all.AddRange(operations);
        all.Add("png:-");
        var (code, bytes, _) = RunRaw(magick, all, null, File.ReadAllBytes(input));
        if (code != 0 || bytes.Length == 0) return false;
        File.WriteAllBytes(output, bytes);
        return true;
    }

    static (int Code, string Stdout, string Stderr) Run(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env, byte[]? stdin = null)
    {
        var (code, stdout, stderr) = RunRaw(exe, args, env, stdin);
        return (code, Encoding.UTF8.GetString(stdout), stderr);
    }

    static (int Code, byte[] Stdout, string Stderr) RunRaw(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env, byte[]? stdin)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("PROCESS_NOT_STARTED:" + exe);
        // Both outputs are read before the input is written, so a child that writes while it reads cannot block on a full pipe.
        var output = new MemoryStream();
        var stdout = proc.StandardOutput.BaseStream.CopyToAsync(output);
        var stderr = proc.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            try
            {
                proc.StandardInput.BaseStream.Write(stdin);
                proc.StandardInput.BaseStream.Flush();
            }
            catch (IOException) { /* the child closed its input early; its exit code and stderr say why */ }
            finally { proc.StandardInput.Close(); }
        }
        if (!proc.WaitForExit(TimeoutMs)) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException("PROCESS_TIMEOUT:" + exe); }
        stdout.Wait();
        return (proc.ExitCode, output.ToArray(), stderr.Result);
    }
}
