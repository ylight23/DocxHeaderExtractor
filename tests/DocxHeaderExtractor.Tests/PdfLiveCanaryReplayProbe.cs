using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// M11-B4.2 offline replay. Reads the authority artifact a single live run produced and drives the
/// product chain from it - projection, decisions, serialization - without calling the model again.
/// <para>
/// The model is deliberately not re-run. B2 fixed determinism at the validated authority boundary, so
/// a second inference would test something this project has explicitly declined to claim, and would
/// confuse model variance with product variance.
/// </para>
/// </summary>
public sealed class PdfLiveCanaryReplayProbe
{
    [Fact]
    public void Report()
    {
        var artifact = Environment.GetEnvironmentVariable("M11_B42_ARTIFACT");
        var output = Environment.GetEnvironmentVariable("M11_B42_REPORT");
        if (string.IsNullOrWhiteSpace(artifact) || string.IsNullOrWhiteSpace(output)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(artifact));
        var generation = document.RootElement.GetProperty("generation");
        var lines = new List<string>
        {
            $"backend={generation.GetProperty("backend").GetString()} " +
            $"model={generation.GetProperty("model").GetString()}",
            $"usesGold={document.RootElement.GetProperty("usesGold").GetBoolean()}",
        };

        foreach (var rowElement in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var row = rowElement.Deserialize<PdfHierarchyFactsRow>()!;
            var facts = row.Items.Select(item => item.ToFactAudit()).ToArray();

            var final = PdfFinalStructureProjection.Project(
                row.SourceDocumentSha256, row.ValidatedStructures, facts, row.CanonicalGroundings);
            var decisions = PdfOutputDecisionPolicy.Decide(final);
            var product = PdfProductOutputSerializer.Serialize(final, decisions);

            // Replayed a second time from the same frozen values: the product must not move.
            var againFinal = PdfFinalStructureProjection.Project(
                row.SourceDocumentSha256, row.ValidatedStructures, facts, row.CanonicalGroundings);
            var againProduct = PdfProductOutputSerializer.Serialize(
                againFinal, PdfOutputDecisionPolicy.Decide(againFinal));

            lines.Add("");
            lines.Add($"-- {row.File}");
            lines.Add($"   sourceSha={row.SourceDocumentSha256[..12]} validated={row.ValidatedStructures.Count} " +
                      $"facts={facts.Length} groundings={row.CanonicalGroundings.Count}");
            lines.Add($"   finalHeadings={final.Headings.Count} " +
                      $"grounded={final.Headings.Count(h => h.GroundingStatus == "grounded")} " +
                      $"ungrounded={final.Headings.Count(h => h.GroundingStatus != "grounded")}");
            lines.Add($"   emit={decisions.Count(d => d.Emit)} requiresReview={decisions.Count(d => d.RequiresReview)}");
            lines.Add($"   productRecords={product.Headings.Count} " +
                      $"levelResolved={product.Headings.Count(h => h.Level is not null)} " +
                      $"parentResolved={product.Headings.Count(h => h.ParentId is not null)}");
            lines.Add($"   productSha={product.SourceDocumentSha256[..12]} " +
                      $"matchesRow={string.Equals(product.SourceDocumentSha256, row.SourceDocumentSha256, StringComparison.OrdinalIgnoreCase)}");

            // Contract checks, stated as pass/fail rather than asserted, because this probe reports.
            var ungroundedEmitted = final.Headings
                .Join(decisions, h => h.Id, d => d.HeadingId, (h, d) => (h, d))
                .Count(pair => pair.d.Emit && pair.h.SourceAnchor is null);
            lines.Add($"   CONTRACT ungrounded-but-emitted={ungroundedEmitted} (must be 0)");
            lines.Add($"   CONTRACT replay-stable-fingerprint=" +
                      $"{final.FinalStructureFingerprint == againFinal.FinalStructureFingerprint}");
            lines.Add($"   CONTRACT replay-stable-product=" +
                      $"{product.Headings.Select(h => (h.Id, h.Level, h.ParentId, h.Text))
                          .SequenceEqual(againProduct.Headings.Select(h => (h.Id, h.Level, h.ParentId, h.Text)))}");
            lines.Add($"   CONTRACT no-record-without-anchor=" +
                      $"{product.Headings.All(h => !string.IsNullOrWhiteSpace(h.StableId))}");
            lines.Add($"   CONTRACT level-in-range-or-null=" +
                      $"{product.Headings.All(h => h.Level is null or (>= 1 and <= 9))}");
        }

        File.WriteAllLines(output, lines);
    }
}
