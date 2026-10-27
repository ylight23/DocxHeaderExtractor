using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: <docx> <new-artifact-dir> [snap-radius, default 8]");

var docx = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var radius = args.Length == 3 ? int.Parse(args[2]) : 8;
if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITE_ARTIFACT_DIRECTORY");
Directory.CreateDirectory(output);

var fixedSource = DocxAgentSource.Load(docx, null, out var fixedFailure)
    ?? throw new InvalidOperationException(fixedFailure);
var snappedSource = DocxAgentSource.Load(docx, null, out var snappedFailure, radius)
    ?? throw new InvalidOperationException(snappedFailure);

var result = new
{
    input = new { docx, snapRadius = radius, providerCalls = 0, goldRead = false },
    fixed48 = Measure(fixedSource),
    snapped = Measure(snappedSource),
};
File.WriteAllText(Path.Combine(output, "offline-measurement.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(result));

static object Measure(DocxAgentSource source)
{
    var preferred = DocxAgentSource.PreferredCutBefore(source.Paragraphs);
    var requests = Enumerable.Range(0, source.Cores.Count).Select(segment =>
    {
        var core = source.Cores[segment];
        var scope = source.Scope(segment);
        var request = AgentUnit.PrepareFirstRequest(source, segment, DocxAgentProtocol.Instruction);
        var bytes = Encoding.UTF8.GetByteCount(request.Body);
        var terminalHasForwardContext = core.End == source.Lines.Count || scope.Shown.Contains(core.End);
        return new
        {
            segment,
            core,
            targets = scope.Decide.Length,
            shown = scope.Shown.Length,
            requestBytes = bytes,
            requestSha256 = request.Sha256,
            coreWithinParagraphCap = core.End - core.Start <= DocxAgentSource.Unit,
            terminalHasForwardContext,
            cutAtObservedBoundary = core.End < source.Lines.Count && preferred[core.End],
            observedBoundariesStrictlyInsideCore = preferred.Skip(core.Start + 1).Take(Math.Max(0, core.End - core.Start - 1)).Count(x => x),
        };
    }).ToArray();
    var owned = source.Cores.SelectMany(c => Enumerable.Range(c.Start, c.End - c.Start)).ToArray();
    return new
    {
        source.SourceSha256,
        paragraphs = source.Lines.Count,
        requests = requests.Length,
        requestBytesTotal = requests.Sum(x => x.requestBytes),
        requestBytesMax = requests.Max(x => x.requestBytes),
        coresOverParagraphCap = requests.Count(x => !x.coreWithinParagraphCap),
        coreCutsWithoutForwardContext = requests.Count(x => !x.terminalHasForwardContext),
        ownershipIsExactPartition = owned.SequenceEqual(Enumerable.Range(0, source.Lines.Count)),
        observedBoundaryCount = preferred.Count(x => x),
        cutsAtObservedBoundary = requests.Count(x => x.cutAtObservedBoundary),
        observedBoundariesCrossedInsideCore = requests.Sum(x => x.observedBoundariesStrictlyInsideCore),
        segments = requests,
        // Production line order and each core's [start, end): lets a reader map any paragraph id to its window.
        lineAliases = source.Lines.Select(l => l.Alias).ToArray(),
        lineSourceIds = source.Paragraphs.Select(p => p.SourceId).ToArray(),
        cores = source.Cores.Select(c => new[] { c.Start, c.End }).ToArray(),
    };
}
