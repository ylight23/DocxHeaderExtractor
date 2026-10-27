using System.Reflection;
using System.Text.Json;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;

/// <summary>Offline, no provider call: the production PDF SELECT requests with and without cropped page images, and per page proof that the
/// kept band holds every parser line box and every inked row.</summary>
internal static class VisualCropAudit
{
    sealed class NoAgents : IModelProvider { public T? Create<T>(string role) where T : class => null; }

    static AgentSource Load(string pdf, bool crop)
    {
        var selector = (PdfSelector)HeadingAgentSetup.Create(new PipelineOptions { PdfVisualCropMargins = crop }, new NoAgents()).Pdf!;
        var load = typeof(PdfSelector).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string), typeof(CancellationToken)])!;
        return (AgentSource)load.Invoke(selector, [pdf, CancellationToken.None])!;
    }

    internal static void Run(string pdf, string output)
    {
        Directory.CreateDirectory(output);
        var plain = Load(pdf, false);
        var cropped = Load(pdf, true);
        long Requests(AgentSource s) => Enumerable.Range(0, s.Cores.Count)
            .Sum(n => (long)System.Text.Encoding.UTF8.GetByteCount(PdfUnitSelectionTask.PrepareFirstRequest(s, n, PdfMembershipInstructionV12.Instruction).Body));
        var pages = cropped.VisualCrops.Values.OrderBy(c => c.Page).Select(c =>
        {
            // Every parser box of the page, mapped to render pixels, must lie inside the kept band.
            var boxes = cropped.Lines.Where(l => l.Page == c.Page && l.Bbox is not null).Select(l =>
            {
                var top = (c.PageHeightPt - l.Bbox!.Value.GetProperty("top").GetDouble()) * 120 / 72d;
                var bottom = (c.PageHeightPt - l.Bbox!.Value.GetProperty("bottom").GetDouble()) * 120 / 72d;
                return new { l.Alias, topPx = Math.Round(top, 1), bottomPx = Math.Round(bottom, 1), inside = top >= c.TopPx && bottom <= c.BottomPx };
            }).ToArray();
            var inkInside = c.InkTopPx is null || (c.InkTopPx >= c.TopPx && c.InkBottomPx <= c.BottomPx);
            return new
            {
                c.Page, c.Verified, c.Cropped, c.PageHeightPx, c.TopPx, c.BottomPx, c.BleedPx, c.InkTopPx, c.InkBottomPx, c.BoxTopPx, c.BoxBottomPx,
                c.TopPt, c.BottomPt, keptShare = Math.Round((c.BottomPx - c.TopPx) / (double)c.PageHeightPx, 4),
                boxes = boxes.Length, boxesOutsideBand = boxes.Where(b => !b.inside).ToArray(), inkInsideBand = inkInside,
                imageBytesPlain = plain.VisualImages[c.Page].Length, imageBytesCropped = cropped.VisualImages[c.Page].Length,
            };
        }).ToArray();
        var summary = new
        {
            pdf = Path.GetFileName(pdf), providerCalls = 0, goldRead = false,
            pages = pages.Length, croppedPages = pages.Count(p => p.Cropped), unverifiedPages = pages.Count(p => !p.Verified),
            boxesOutsideBand = pages.Sum(p => p.boxesOutsideBand.Length), pagesWithInkOutsideBand = pages.Count(p => !p.inkInsideBand),
            meanKeptShare = Math.Round(pages.Average(p => p.keptShare), 4),
            imageBytesPlain = pages.Sum(p => (long)p.imageBytesPlain), imageBytesCropped = pages.Sum(p => (long)p.imageBytesCropped),
            selectRequests = plain.Cores.Count, selectRequestsCropped = cropped.Cores.Count,
            selectBytesPlain = Requests(plain), selectBytesCropped = Requests(cropped),
        };
        File.WriteAllText(Path.Combine(output, "visual-crop-audit.json"),
            JsonSerializer.Serialize(new { summary, pages }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary));
    }
}
