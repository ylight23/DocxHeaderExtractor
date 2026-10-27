using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using UglyToad.PdfPig;

var pdf = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
using var document = PdfDocument.Open(pdf);
var assembly = Assembly.Load("DocxHeaderExtractor.Sources");
var extraction = assembly.GetType("DocxHeaderExtractor.Sources.Pdf.PdfLineExtraction", throwOnError: true)!;
var extract = extraction.GetMethod("ExtractLines", BindingFlags.Public | BindingFlags.Static)!;
var lines = (System.Collections.IEnumerable)extract.Invoke(null, new object[] { document })!;
var rows = new List<object>();
var prevPage = -1;
var prevRight = 0d;
var prevTop = 0d;
var prevBottom = 0d;
var row = 0;
var segment = 0;
var ordinal = 0;
foreach (var item in lines)
{
    var t = item!.GetType();
    object Get(string name) => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(item)!;
    var page = (int)Get("Page");
    var left = (double)Get("Left");
    var right = (double)Get("Right");
    var top = (double?)Get("Top") ?? 0;
    var bottom = (double?)Get("Bottom") ?? 0;
    var text = Get("Projection").GetType().GetProperty("VerbatimText")!.GetValue(Get("Projection"))!.ToString()!;
    if (ordinal > 0)
    {
        if (page == prevPage && left > prevRight && top > prevBottom && bottom < prevTop) segment++;
        else { row++; segment = 0; }
    }
    var box = new { Left = Math.Round(left, 2), Bottom = Math.Round(bottom, 2), Right = Math.Round(right, 2), Top = Math.Round(top, 2) };
    rows.Add(new { alias = $"L{row:0000}:S{segment}", sourceId = $"row{row:0000}-seg{segment}", ordinal, page, block = $"row{row:0000}-seg{segment}", text, bbox = box });
    prevPage = page; prevRight = right; prevTop = top; prevBottom = bottom; ordinal++;
}
var raw = JsonSerializer.Serialize(new { sourcePdf = pdf, sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pdf))), extraction = "PdfLineExtraction.ExtractLines + PdfSegmentAtomCatalog.Beside-compatible aliases", count = rows.Count, rows }, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(output, raw);
Console.WriteLine($"lines={rows.Count} sha256={Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))}");
