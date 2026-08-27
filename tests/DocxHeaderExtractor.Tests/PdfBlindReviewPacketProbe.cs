using System.Text;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Lane A: blind, source-first review packets for the B1 benchmark-round-2 documents. Emits every raw
/// source line (page, geometry, text - the same <see cref="PdfCandidateProvenance.LineId"/> identity
/// used throughout A0-A3) in reading order, and nothing else - no candidate id, no rank, no selected
/// status, no structural scope, no domain role, no model output. A reviewer assigning HEADING /
/// NOT_HEADING / UNCERTAIN here cannot be anchored by what the pipeline already decided.
/// <para>
/// The sample population is every source line, not the candidate pool: this probe calls
/// <see cref="PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot"/> only for its <c>Lines</c>
/// field and deliberately never reads <c>CandidateBlocks</c>, <c>Provenance</c>, or <c>Annotations</c>
/// from the same snapshot. A document whose candidate construction missed an occurrence still shows
/// that occurrence's text here, so it cannot silently vanish from the eventual recall denominator the
/// way sampling from the candidate pool would let it.
/// </para>
/// <para>
/// Occurrence identity for anything a reviewer marks HEADING must be recorded as (page, lineId), never
/// a candidate id (ids are discovery-order and not stable across a revision - this project has hit
/// that mistake once already) and never text alone (duplicate titles exist within one document, e.g. a
/// table of contents next to the body heading it lists).
/// </para>
/// </summary>
public sealed class PdfBlindReviewPacketProbe
{
    [Fact]
    public void Report()
    {
        var docxPath = Environment.GetEnvironmentVariable("BENCH_PACKET_DOCX");
        var output = Environment.GetEnvironmentVariable("BENCH_PACKET_REPORT");
        if (string.IsNullOrWhiteSpace(docxPath) || string.IsNullOrWhiteSpace(output)) return;

        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docxPath);

        var report = new StringBuilder();
        void Line(string value) => report.AppendLine(value);

        Line($"BLIND SOURCE REVIEW PACKET - {Path.GetFileName(docxPath)}");
        Line($"lines={snapshot.Lines.Count}. No candidate/rank/scope/model information below - source only.");
        Line("Label each line HEADING / NOT_HEADING / UNCERTAIN. If HEADING, record its exact source span.");
        Line("Occurrence identity = (page, lineId) shown per row - never a candidate id, never text alone.");
        Line(new string('-', 100));

        for (var index = 0; index < snapshot.Lines.Count; index++)
        {
            var l = snapshot.Lines[index];
            var lineId = PdfCandidateProvenance.LineId(l);
            Line($"[{index}] page={l.Page} lineId={lineId}");
            Line($"      text: {l.Text}");
        }

        File.WriteAllText(output, report.ToString());
    }
}
