using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: GatherReviewTrial <goal.txt> <source.docx> <prior-run-result.json> <new-output-directory>");
    return 2;
}
var goal = File.ReadAllText(args[0]).Trim();
var sourcePath = Path.GetFullPath(args[1]);
var inputPath = Path.GetFullPath(args[2]);
var sourceHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)));
var goalHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(goal)));
var inputBytes = File.ReadAllBytes(inputPath);
using var previous = JsonDocument.Parse(inputBytes);
using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(inputPath)!, "run-metadata.json")));
if (metadata.RootElement.GetProperty("sourceHash").GetString() != sourceHash || metadata.RootElement.GetProperty("goalSha256").GetString() != goalHash)
    throw new InvalidDataException("INPUTS_DO_NOT_MATCH_PRIOR_RUN");
if (metadata.RootElement.GetProperty("outlineMode").GetString() != "style-fallback")
    throw new InvalidDataException("OUTLINE_REPLAY_NOT_IMPLEMENTED");
var source = GatherUnits.Read(sourcePath, "sha256:" + sourceHash);
if (sourceHash != Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)))) throw new InvalidDataException("SOURCE_CHANGED_DURING_READ");
var run = previous.RootElement.GetProperty("result").Deserialize<GatherRun>() ?? throw new InvalidDataException("RESULT_MISSING");
var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENROUTER_API_KEY_MISSING");
var output = Path.GetFullPath(args[3]);
if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
Directory.CreateDirectory(output);
using var model = new GatherOpenRouterModel(key, 1, 1.0, diagnosticsPath: Path.Combine(output, "provider-events.jsonl"));
var clock = System.Diagnostics.Stopwatch.StartNew();
var review = await GatherSemanticReview.ReviewAsync(goal, run, [source], model);
var report = new { priorResultSha256 = Convert.ToHexStringLower(SHA256.HashData(inputBytes)), sourceHash, goalHash,
    reviewVersion = GatherSemanticReview.Version, elapsedSeconds = clock.Elapsed.TotalSeconds, review, attempts = model.Attempts };
File.WriteAllText(Path.Combine(output, "review-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"verdicts={review.Verdicts.Count} error={review.Error ?? "none"} previouslyRefused={review.PreviouslyRefused} seconds={clock.Elapsed.TotalSeconds:F1}");
return review.Error is null ? 0 : 1;
