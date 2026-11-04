using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>Replays the saved outlines from an e2e trial; never calls a provider.</summary>
static class E2eReplay
{
    public static int Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("e2e-replay <trial-directory>");
        var root = Path.GetFullPath(args[1]);
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "e2e.json")));
        var key = run.RootElement.GetProperty("configKey").GetString()!;
        var store = new OutlineStore(Path.Combine(root, "outline-store"));
        string PathFor(string phase, string id) => Path.Combine(root, id == "S1" && phase is "body-edit" or "heading-edit" ? phase : "inputs", id switch { "target" => "10.docx", "S1" => "09.docx", _ => "08.docx" });
        DocxHeaderExtractor.DocumentProcessing.Output.HeadingRecord[] Headings(string path)
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            return store.Find(sha, key)?.Headings.ToArray() ?? throw new InvalidDataException("OUTLINE_NOT_FOUND:" + path);
        }
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var phase in new[] { "cold", "body-edit", "heading-edit" })
        {
            var target = PathFor(phase, "target");
            var sources = new[] { "S1", "S2" }.Select(id => { var p = PathFor(phase, id); return new GatherSourceFile(id, p, Headings(p)); }).ToList();
            var plan = GatherByHeading.Build(target, Headings(target), sources, addMissingHeadings: true);
            File.WriteAllText(Path.Combine(root, phase + "-plan.json"), JsonSerializer.Serialize(plan, json));
            Console.WriteLine($"{phase}: sections={plan.Sections.Count}, uncertain={plan.Uncertain.Count}, excluded={plan.Excluded.Count}");
            foreach (var g in plan.Uncertain.GroupBy(u => u.Kind)) Console.WriteLine($"  {g.Key}: {g.Count()}");
        }
        return 0;
    }
}
