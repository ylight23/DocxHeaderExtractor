using System.Text.Json;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Asks, for each of 057's 23 undelivered decisionRelevant occurrences, the question the follow-up
/// review posed directly: can an existing source-preserving representation - the occurrence's own
/// required source lines only, not the `WindowFragment`'s full composite text - ground and align
/// without inventing anything? The two sub-owners are kept apart, not merged into one fix:
/// <list type="bullet">
/// <item>the 14 <c>GROUNDING_VALIDATOR_REJECTION</c> cases are tested against
/// <see cref="PdfBlockGrounder"/>'s own <c>LooksGroundableText</c> shape (replicated here verbatim,
/// since it is private) using the required-lines-only text instead of the composite window text;</item>
/// <item>the 9 <c>NO_DOCX_SOURCE_ANCHOR</c> cases are tested for whether the required-lines-only
/// canonical text exists at all in the DOCX haystack the live alignment matcher searched - a necessary
/// condition for any matcher to find it, checked without reimplementing the matcher.</item>
/// </list>
/// No provider call, no candidate construction change, no production code change.
/// </summary>
public sealed class PdfN2S057RepresentationAuditProbe
{
    [Fact]
    public void AuditRequiredLineOnlyRepresentationForUndeliveredTargets()
    {
        var output = Environment.GetEnvironmentVariable("N2S_057_REPRESENTATION_AUDIT_REPORT");
        if (string.IsNullOrWhiteSpace(output)) return;

        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var report = BuildReport(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void CommittedAuditReproducesFromCommittedTraceAndSilver()
    {
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var path = Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-representation-audit.v1.json");
        if (!File.Exists(path)) return;

        var expected = JsonSerializer.Serialize(BuildReport(root), new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    private static object BuildReport(string root)
    {
        using var trace = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "n2-s", "counterfactual", "057-grounding-alignment-trace.v1.json")));
        var targets = trace.RootElement.GetProperty("targets").EnumerateArray()
            .Select(t => (
                StableId: t.GetProperty("StableId").GetString()!,
                SourceFactId: t.GetProperty("SourceFactId").GetString()!,
                Owner: t.GetProperty("trace").GetProperty("owner").GetString()!))
            .ToArray();

        using var silver = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "eval", "benchmark-n0", "silver-labels", "057-n1.2-silver-model-assisted.v1.json")));
        var lineIdsByStableId = silver.RootElement.GetProperty("headingOccurrences").EnumerateArray()
            .ToDictionary(
                o => o.TryGetProperty("goldStableId", out var g) ? g.GetString()! : o.GetProperty("silverStableId").GetString()!,
                o => o.GetProperty("sourceLineIds").EnumerateArray().Select(l => l.GetString()!).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var docx = Path.Combine(root, "todo10_8", "heading_corpus_95_word", "04_giao_trinh",
            "057_Quantitative_Methods_in_Finance_Lecture_Notes.docx");
        var snapshot = PdfLayoutEvidenceOutline.BuildCandidateRankingSnapshot(docx);
        var candidatesById = snapshot.CandidateBlocks.ToDictionary(b => b.Id, StringComparer.Ordinal);

        // Same population BuildBroadAlignmentForCandidateIds searched during the live trace - reused
        // here only to test literal presence of the required-lines-only text, not to reimplement the
        // matcher's own branch selection.
        var slim = new DocxSlimExtractor().Extract(docx);
        var allCandidateIds = snapshot.CandidateBlocks.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var alignment = PdfLayoutEvidenceOutline.BuildBroadAlignmentForCandidateIds(docx, PolicyStateFixture.FromSlim(slim), allCandidateIds);
        var haystackTexts = alignment.Haystacks.Select(h => h.CanonicalText).ToArray();

        var rows = targets.Select(target =>
        {
            var block = candidatesById[target.SourceFactId];
            var required = lineIdsByStableId[target.StableId];
            var requiredLines = block.Lines.Where(line => required.Contains(PdfCandidateProvenance.LineId(line))).ToArray();
            var requiredOnlyText = string.Join(' ', requiredLines.Select(line => line.Text));

            var compositeGroundable = LooksGroundableText(block.Text);
            var requiredOnlyGroundable = LooksGroundableText(requiredOnlyText);

            var requiredOnlyCanonical = PdfTextUtilities.CanonicalForMatch(requiredOnlyText);
            var existsVerbatimInDocx = requiredOnlyCanonical.Length > 0 &&
                haystackTexts.Any(h => h.Contains(requiredOnlyCanonical, StringComparison.Ordinal));

            return new
            {
                target.StableId,
                target.SourceFactId,
                target.Owner,
                compositeLineCount = block.Lines.Count,
                requiredLineCount = requiredLines.Length,
                compositeText = Truncate(block.Text),
                requiredOnlyText = Truncate(requiredOnlyText),
                compositeGroundable,
                requiredOnlyGroundable,
                requiredOnlyRepresentationWouldHelp = target.Owner == "GROUNDING_VALIDATOR_REJECTION" && !compositeGroundable && requiredOnlyGroundable,
                requiredOnlyTextExistsVerbatimInDocx = existsVerbatimInDocx,
            };
        }).ToArray();

        var groundingTargets = rows.Where(r => r.Owner == "GROUNDING_VALIDATOR_REJECTION").ToArray();
        var anchorTargets = rows.Where(r => r.Owner == "NO_DOCX_SOURCE_ANCHOR").ToArray();

        return new
        {
            schemaVersion = 1,
            artifactKind = "n2s_057_representation_audit",
            usesModel = false,
            groundingRejectionSubOwner = new
            {
                count = groundingTargets.Length,
                requiredOnlyRepresentationWouldPassTextShapeGate = groundingTargets.Count(r => r.requiredOnlyRepresentationWouldHelp),
                note = "requiredOnlyRepresentationWouldHelp asks only whether the required-lines-only text would clear PdfBlockGrounder's LooksGroundableText gate, replicated verbatim here - it does not by itself prove the candidate would still align or ground correctly, only that the composite window text's extra content (TOC dot leaders, trailing page numbers, adjacent lines) is what trips the text-shape rejection.",
            },
            noAnchorSubOwner = new
            {
                count = anchorTargets.Length,
                requiredOnlyTextFoundVerbatimInDocx = anchorTargets.Count(r => r.requiredOnlyTextExistsVerbatimInDocx),
                note = "requiredOnlyTextExistsVerbatimInDocx tests literal canonical presence in the same DOCX paragraph population the live alignment matcher searched. A false here means the source text itself was never contiguous DOCX prose (a construction/segmentation question, not an alignment-matcher bug); a true here means the matcher's own search strategy - not text absence - is the open question.",
            },
            rows,
        };
    }

    /// <summary>Verbatim copy of PdfBlockGrounder's private LooksGroundableText - not accessible from here.</summary>
    private static bool LooksGroundableText(string text)
    {
        var t = PdfTextUtilities.HeadingReadable(text);
        if (t.Length is < 3 or > 180) return false;
        if (!t.Any(char.IsLetter)) return false;
        if (t.Count(c => c is '.' or ';') >= 2) return false;
        if (t.Length >= 80 && t.EndsWith('.')) return false;
        return true;
    }

    private static string Truncate(string value) => value.Length <= 140 ? value : value[..140] + "...";
}
