using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC041_SOURCE_ONLY_GOLD_REVIEW_V1 - protocol step 3 of SRC041_BLIND_GENERALIZATION_AUDIT_V1.
/// <para>
/// The reviewer read only the original PDF (sha256 bcd55336...dc38) through its structured-lane atoms and
/// their layout facts (atom-layout-facts.tsv), with no model call, without opening the engine's committed
/// blind proposals (10b3317), SRC-041's Gold file or its count-only total (a review of this same PDF, with no
/// occurrence list), or any converted DOCX.
/// review_tool.py records the reading. Decisions the user took on DOC-0133 - this report's quarterly
/// sibling - and the frozen financial policy are applied as precedent and say so. The reviewer also
/// designed the engine, so this is not a double-blind annotation.
/// </para>
/// <para>
/// This test checks the review against the source - every part binds through the production binder in the
/// PDF atom universe - and freezes its summary. It writes no Gold.
/// </para>
/// </summary>
public sealed class Src041SourceReviewTests
{
    internal const string Pdf = SourcePdfCorpus.Src041;
    private const string Dir = "eval/a99-closed-loop/source-review-v1/SRC-041";

    internal sealed record Part(string SourceAlias, string SelectionMode, string? VerbatimText, int? Occurrence);

    internal sealed record Item(string Section, Part[] Parts, string Text, string Verdict, string Pattern, string Reason, JsonElement Axes, int Page);

    internal static IReadOnlyList<Item> Items()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Dir}/review-items.json")));
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(i => new Item(
            i.GetProperty("section").GetString()!,
            i.GetProperty("parts").EnumerateArray().Select(p => new Part(
                p.GetProperty("sourceAlias").GetString()!, p.GetProperty("selectionMode").GetString()!,
                p.GetProperty("verbatimText").ValueKind == JsonValueKind.Null ? null : p.GetProperty("verbatimText").GetString(),
                p.GetProperty("occurrence").ValueKind == JsonValueKind.Null ? null : p.GetProperty("occurrence").GetInt32())).ToArray(),
            i.GetProperty("text").GetString()!,
            i.GetProperty("verdict").GetString()!,
            i.GetProperty("pattern").GetString()!,
            i.GetProperty("reason").GetString()!,
            i.GetProperty("axes").Clone(),
            i.GetProperty("page").GetInt32())).ToArray();
    }

    internal static SemanticSourcePartsBinding Bind(IReadOnlyList<SemanticSourceAtom> atoms, Item item) =>
        SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(
            item.Parts.Select(p => new SemanticSourcePart(p.SourceAlias, p.SelectionMode, p.VerbatimText, p.Occurrence)).ToArray()));

    [Fact]
    public void The_layout_facts_are_this_pdf_atoms()
    {
        var atoms = PdfSourceOccurrenceAdapter.Build(TestRepository.Path(Pdf)).Atoms;
        var facts = File.ReadAllLines(TestRepository.Path($"{Dir}/atom-layout-facts.tsv")).Select(l => l.Split('\t')).ToArray();
        Assert.Equal(atoms.Count, facts.Length);
        Assert.Equal(atoms.Select(a => (a.Alias, a.Text.Replace('\t', ' '))), facts.Select(f => (f[0], f[10])));
    }

    [Fact]
    public void Freeze_the_source_review()
    {
        var atoms = PdfSourceOccurrenceAdapter.Build(TestRepository.Path(Pdf)).Atoms;
        var items = Items();
        var bindings = items.Select(i => (Item: i, Binding: Bind(atoms, i))).ToArray();
        var failures = bindings.Where(b => !b.Binding.IsBound).Select(b => new { b.Item.Text, b.Item.Pattern, reason = b.Binding.Reason }).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/source-review-v1", "SRC-041.source-review.v1.json", new
        {
            artifactKind = "a99_source_only_gold_review",
            study = "SRC041_SOURCE_ONLY_GOLD_REVIEW_V1",
            protocol = new
            {
                authoritativeSource = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)) },
                modelProviderVlmCalls = 0,
                engineProposalsRead = false,
                goldFileRead = false,
                countOnlyTotalUsed = false,
                oldDocxOccurrencesUsed = false,
                goldWritten = false,
                independence = "the reviewer designed the engine; its blind proposals were committed before the review (10b3317) and not opened; not a double-blind annotation",
            },
            inputs = new
            {
                reviewTool = new { path = $"{Dir}/review_tool.py", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/review_tool.py")) },
                atomLayoutFacts = new { path = $"{Dir}/atom-layout-facts.tsv", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/atom-layout-facts.tsv")) },
                reviewItems = new { path = $"{Dir}/review-items.json", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/review-items.json")) },
            },
            summary = new
            {
                SOURCE_OCCURRENCES = atoms.Count,
                CLEAR_HEADING = items.Count(i => i.Verdict == "HEADING"),
                CLEAR_NON_HEADING_SET_APART = items.Count(i => i.Verdict == "NON_HEADING"),
                AMBIGUOUS = items.Count(i => i.Verdict == "AMBIGUOUS"),
                PROPOSED_RANGE = new
                {
                    min = items.Count(i => i.Verdict == "HEADING"),
                    max = items.Count(i => i.Verdict is "HEADING" or "AMBIGUOUS"),
                },
                BINDING_FAILURES = failures.Length,
                USER_APPROVAL_REQUIRED = true,
                note = "every other atom is a non-heading by the reviewer's reading (body, table cells and rows, chart values, notes, contents entries, page furniture); only set-apart non-headings are listed",
            },
            bySection = items.GroupBy(i => i.Section).Select(g => new
            {
                section = g.Key,
                headings = g.Count(i => i.Verdict == "HEADING"),
                nonHeadings = g.Count(i => i.Verdict == "NON_HEADING"),
                ambiguous = g.Count(i => i.Verdict == "AMBIGUOUS"),
            }).ToArray(),
            headingsByPattern = items.Where(i => i.Verdict == "HEADING").GroupBy(i => i.Pattern)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            nonHeadingsByPattern = items.Where(i => i.Verdict == "NON_HEADING").GroupBy(i => i.Pattern)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            ambiguous = items.Where(i => i.Verdict == "AMBIGUOUS").Select(i => new { i.Pattern, i.Page, aliases = i.Parts.Select(p => p.SourceAlias).ToArray(), i.Text, i.Reason, axes = i.Axes }).ToArray(),
            userDecisions = new
            {
                decidedAt = "2026-09-25",
                membershipTotal = 280,
                arithmetic = "273 clear + 5 A2 + 1 A3 + 0 A4 + 1 A5 = 280",
                principle = "CLASSIFY OCCURRENCES, NOT STRINGS",
                patterns = new[]
                {
                    new { pattern = "S041_A2_CONTINUED_TITLE", decision = "TRUE x5: real display-title occurrences of a continuing statement or report; REPEAT / CONTINUATION is derived later by identity resolution (semantic claims -> identity resolution -> semanticNodeId -> PRIMARY / REPEAT / CONTINUATION) and does not affect isHeading; no repeatStatus is written" },
                    new { pattern = "S041_A3_PART_LABEL_ON_CONTENTS_PAGE", decision = "TRUE x1: this occurrence opens the MD&A part and its contents region; the same text atop p4-p5 is a different occurrence (running header, FALSE). Repetition elsewhere does not make this one page furniture" },
                    new { pattern = "S041_A4_APPENDIX_TABLE_TITLE", decision = "FALSE x1: its scope covers only the list/object directly below it and opens no independent region - an ordinary local object title, equivalent to a caption. A decision on this occurrence's scope, not a rule that titles over lists are false" },
                    new { pattern = "S041_A5_CONTENTS_GROUP_LABEL", decision = "TRUE x1: not a navigation entry pointing elsewhere; it opens a sub-group of two indented contents entries, like the Tables / Figures / Boxes openers" },
                },
            },
            bindingFailures = failures,
        });
    }
}
