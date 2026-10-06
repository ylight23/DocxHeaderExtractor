using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC089_SOURCE_ONLY_GOLD_REVIEW_V1 - protocol step 3 of SRC089_BLIND_GENERALIZATION_AUDIT_V1.
/// <para>
/// The reviewer read only the original PDF (Decree 195/2013/ND-CP on publishing, English translation) through its
/// structured-lane atoms, their layout facts (atom-layout-facts.tsv) and what their glyphs say (atom-glyph-facts.tsv),
/// with no model call, without opening the engine's committed blind proposals (committed before this review), any
/// converted DOCX, the outline JSON or the count in handoff.md; no Gold of this source exists. review_tool.py records the
/// reading. Decisions the user took on SRC-089, SRC-054, SRC-053, SRC-041 and SRC-029, and the frozen financial policy,
/// are applied as precedent and say so. The reviewer also designed the engine, so this is not a double-blind annotation.
/// </para>
/// <para>
/// This test checks the review against the source - every part binds through the production binder in the
/// PDF atom universe - and freezes its summary. It writes no Gold.
/// </para>
/// </summary>
public sealed class Src089SourceReviewTests
{
    internal const string Pdf = SourcePdfCorpus.Src089;
    private const string Dir = "eval/a99-closed-loop/source-review-v1/SRC-089";

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
        var glyphs = File.ReadAllLines(TestRepository.Path($"{Dir}/atom-glyph-facts.tsv")).Select(l => l.Split('\t')).ToArray();
        Assert.Equal(atoms.Select(a => (a.Alias, a.Text.Replace('\t', ' '))), glyphs.Select(f => (f[0], f[9])));
    }

    [Fact]
    public void Freeze_the_source_review()
    {
        var atoms = PdfSourceOccurrenceAdapter.Build(TestRepository.Path(Pdf)).Atoms;
        var items = Items();
        var bindings = items.Select(i => (Item: i, Binding: Bind(atoms, i))).ToArray();
        var failures = bindings.Where(b => !b.Binding.IsBound).Select(b => new { b.Item.Text, b.Item.Pattern, reason = b.Binding.Reason }).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/source-review-v1", "SRC-089.source-review.v1.json", new
        {
            artifactKind = "a99_source_only_gold_review",
            study = "SRC089_SOURCE_ONLY_GOLD_REVIEW_V1",
            protocol = new
            {
                authoritativeSource = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)) },
                modelProviderVlmCalls = 0,
                engineProposalsRead = false,
                goldFileRead = false,
                countOnlyTotalUsed = false,
                oldDocxOccurrencesUsed = false,
                goldWritten = false,
                independence = "the reviewer designed the engine; its blind proposals were committed before the review and not opened; not a double-blind annotation",
            },
            inputs = new
            {
                reviewTool = new { path = $"{Dir}/review_tool.py", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/review_tool.py")) },
                atomLayoutFacts = new { path = $"{Dir}/atom-layout-facts.tsv", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/atom-layout-facts.tsv")) },
                atomGlyphFacts = new { path = $"{Dir}/atom-glyph-facts.tsv", sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path($"{Dir}/atom-glyph-facts.tsv")), writer = "ScaleUpSurveyDiagnosticTests.Dump_pdf_atom_glyph_facts" },
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
                decidedAt = "2026-09-27",
                membershipTotal = 36,
                arithmetic = "26 articles + 1 S089_Q1 title + 5 S089_Q2 chapters + 4 S089_Q3 clause labels = 36",
                principle = "CLASSIFY OCCURRENCES, NOT STRINGS",
                patterns = new[]
                {
                    new { pattern = "S089_Q1_DECREE_TITLE_BLOCK", decision = "one title of three lines ('DECREE' + 'DETAILING ... IMPLEMENTING,' + 'THE PUBLICATION LAW (*)'); retain the whole source atom because the mark belongs to the title cluster: IDENTITY / DOCUMENT / TITLE" },
                    new { pattern = "S089_Q2_CHAPTER_LABEL_OVER_TITLE", decision = "one claim of two parts per chapter: the label 'Chapter N' and its title line (5 claims)" },
                    new { pattern = "S089_Q3_COLON_CLAUSE_LABEL", decision = "TRUE x4: a numbered clause that is only a short noun phrase ending in a colon is a sub-heading of its article (not the reviewer's proposal)" },
                },
            },
            bindingFailures = failures,
        });
    }
}
