using System.Reflection;
using System.Text.Json;
using DocxHeaderExtractor.Sources.Pdf;
var pdf = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var type = typeof(PdfSourceFactsBuilder).Assembly.GetType("DocxHeaderExtractor.Sources.Pdf.PdfSourceAdapter")!;
var method = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "BuildWithDetails" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
var built = method.Invoke(null, new object[] { pdf })!;
var snapshot = built.GetType().GetProperty("Snapshot")!.GetValue(built)!;
var details = built.GetType().GetProperty("Details")!.GetValue(built)!;
var atoms = (System.Collections.IEnumerable)snapshot.GetType().GetProperty("Atoms")!.GetValue(snapshot)!;
var blockMap = (System.Collections.IDictionary)details.GetType().GetProperty("LayoutBlockByAtom")!.GetValue(details)!;
var blockList = (System.Collections.IEnumerable)details.GetType().GetProperty("Blocks")!.GetValue(details)!;
var lines = new Dictionary<string, object>();
foreach(var block in blockList) {
  var bl = block!.GetType().GetProperty("Lines")!.GetValue(block) as System.Collections.IEnumerable;
  foreach(var line in bl!) {
    var id = line!.GetType().GetProperty("SourceId")?.GetValue(line)?.ToString();
    if (id is null) id = typeof(PdfSourceFactsBuilder).Assembly.GetType("DocxHeaderExtractor.Sources.Pdf.PdfLineIdentity")!.GetMethod("Of", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new[]{line})!.ToString();
    lines[id!] = line;
  }
}
var result = new List<object>();
foreach(var atom in atoms) {
  var t = atom!.GetType();
  string Get(string name) => t.GetProperty(name)!.GetValue(atom)?.ToString() ?? "";
  var alias=Get("Alias"); var sourceId=Get("SourceId"); var line=lines[sourceId]; var lt=line.GetType();
  object Val(string name) => lt.GetProperty(name)!.GetValue(line)!;
  double left=Convert.ToDouble(Val("Left")), right=Convert.ToDouble(Val("Right"));
  double bottom=Convert.ToDouble(Val("Bottom")), top=Convert.ToDouble(Val("Top"));
  result.Add(new { alias, sourceId, ordinal=Convert.ToInt32(Get("Ordinal")), page=Convert.ToInt32(Get("Page")), block=blockMap[sourceId]?.ToString() ?? sourceId, text=Get("Text"), bbox=new { Left=Math.Round(left,2), Bottom=Math.Round(bottom,2), Right=Math.Round(right,2), Top=Math.Round(top,2) } });
}
File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine($"source atoms={result.Count} sha256={snapshot.GetType().GetProperty("SourceSha256")!.GetValue(snapshot)}");
