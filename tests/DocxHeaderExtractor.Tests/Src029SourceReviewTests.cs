using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC029_SOURCE_ONLY_GOLD_REVIEW_V1 - Layer C, before any decision is revealed to the engine side.
/// <para>
/// The reviewer read only the original PDF (sha256 08f459b8..0994c) through its structured-lane atoms and
/// their layout facts (atom-layout-facts.tsv), with no model call, without opening the engine's frozen
/// proposals (dc98144), and without the retired DOCX or its approved total 356. review_tool.py records
/// the reading: which occurrences are headings, which set-apart ones are not, and which patterns are
/// ambiguous for the user. Decisions the user already took on DOC-0123 are applied as precedent and say
/// so; the reviewer designed the engine too, so this is not a double-blind annotation.
/// </para>
/// <para>
/// This test checks the review against the source - every part binds through the production binder in the
/// PDF atom universe - and freezes its summary. It writes no Gold.
/// </para>
/// </summary>
public sealed class Src029SourceReviewTests
{
    internal const string Pdf = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/029_WB_RFP_Works_DesignBuild_2021.pdf";
    internal const string Dir = "eval/a99-closed-loop/source-review-v1/SRC-029";

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
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Pdf)).Atoms;
        var facts = File.ReadAllLines(TestRepository.Path($"{Dir}/atom-layout-facts.tsv")).Select(l => l.Split('\t')).ToArray();
        Assert.Equal(atoms.Count, facts.Length);
        Assert.Equal(atoms.Select(a => (a.Alias, a.Text.Replace('\t', ' '))), facts.Select(f => (f[0], f[10])));
    }

    [Fact]
    public void Freeze_the_source_review()
    {
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(Pdf)).Atoms;
        var items = Items();
        var bindings = items.Select(i => (Item: i, Binding: Bind(atoms, i))).ToArray();
        var failures = bindings.Where(b => !b.Binding.IsBound).Select(b => new { b.Item.Text, b.Item.Pattern, reason = b.Binding.Reason }).ToArray();

        FreezeArtifact.AssertJson("eval/a99-closed-loop/source-review-v1", "SRC-029.source-review.v1.json", new
        {
            artifactKind = "a99_source_only_gold_review",
            study = "SRC029_SOURCE_ONLY_GOLD_REVIEW_V1",
            protocol = new
            {
                authoritativeSource = new { path = Pdf, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Pdf)) },
                modelProviderVlmCalls = 0,
                engineProposalsRead = false,
                oldGoldTotal356Used = false,
                oldDocxOccurrencesUsed = false,
                goldWritten = false,
                independence = "the reviewer designed the engine; proposals committed before review (dc98144) and not opened; not a double-blind annotation",
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
                note = "every other atom is a non-heading by the reviewer's reading (body, table cells, fields, notes, contents entries, page furniture); only set-apart non-headings are listed",
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
            ambiguous = items.Where(i => i.Verdict == "AMBIGUOUS").Select(i => new { i.Pattern, i.Page, aliases = i.Parts.Select(p => p.SourceAlias).ToArray(), i.Text, i.Reason, axes = i.Axes }).ToArray(),
            userDecisions = new
            {
                decidedAt = "2026-09-25",
                membershipTotal = 374,
                patterns = new[]
                {
                    new { pattern = "S029_A1_COVER_APPLICABILITY_QUALIFIER", decision = "FALSE: INFORMATION / DOCUMENT / METADATA, informationType APPLICABILITY_METADATA, titleRelation NONE (not a title part of a claim it is not in)" },
                    new { pattern = "S029_A2_LIST_LEAD_IN_SIBLING", decision = "TRUE: STRUCTURE / LIST / REGION_OPENER + LOCAL_LABEL; sentence-like wording is not body prose automatically" },
                    new { pattern = "S029_A3_INNER_FORM_TITLE", decision = "both TRUE: the repeated declaration title keeps heading status (repeatStatus is independent of isHeading); the CON-4 title claim is the title line only, its qualification lines are INFORMATION with titleRelation NONE" },
                    new { pattern = "S029_A4_BULLETED_SUBHEADING", decision = "all three TRUE: STRUCTURE / SECTION / REGION_OPENER + LOCAL_LABEL; the bullet is not a decision gate" },
                    new { pattern = "T1/T2 binder limits", decision = "not partial binding: fix the binder generically (GENERIC_MULTIPART_BINDER_V2) and bind the five headings in full before freezing Gold" },
                },
            },
            pageBreakHeadings = new[]
            {
                new { claim = "Sub-Clause 14.3", titleOnNextPage = "Application for Interim Payment", pages = "188 -> 189" },
                new { claim = "Sub-Clause 21.10", titleOnNextPage = "Dissatisfaction with DAAB's decision on SEA/SH Referrals", pages = "196 -> 197" },
            },
            bindingFailures = failures,
        });
    }
}
