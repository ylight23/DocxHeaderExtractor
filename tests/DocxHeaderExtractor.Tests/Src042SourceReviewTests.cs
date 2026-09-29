using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC042_SOURCE_ONLY_GOLD_REVIEW_V1 - protocol step 3 of SRC042_BLIND_GENERALIZATION_AUDIT_V1.
/// <para>
/// The reviewer read only the original PDF (sha256 21a847d9...fabf) through its structured-lane atoms and
/// their layout facts (atom-layout-facts.tsv), with no model call, without opening the engine's committed
/// blind proposals (committed before this review), SRC-042's Gold file or any count it holds, or any converted DOCX.
/// review_tool.py records the reading. Decisions the user took on SRC-041 (the IBRD annual report, this IDA
/// report's sibling) and DOC-0133, and the frozen financial policy, are applied as precedent and say so. The reviewer also
/// designed the engine, so this is not a double-blind annotation.
/// </para>
/// <para>
/// This test checks the review against the source - every part binds through the production binder in the
/// PDF atom universe - and freezes its summary. It writes no Gold.
/// </para>
/// </summary>
public sealed class Src042SourceReviewTests
{
    internal const string Pdf = SourcePdfCorpus.Src042;
    private const string Dir = "eval/a99-closed-loop/source-review-v1/SRC-042";

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

        FreezeArtifact.AssertJson("eval/a99-closed-loop/source-review-v1", "SRC-042.source-review.v1.json", new
        {
            artifactKind = "a99_source_only_gold_review",
            study = "SRC042_SOURCE_ONLY_GOLD_REVIEW_V1",
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
                membershipTotal = 253,
                arithmetic = "253 clear + 0 S042_A1 = 253",
                principle = "CLASSIFY OCCURRENCES, NOT STRINGS",
                patterns = new[]
                {
                    new { pattern = "S042_A1_BOX_OF_PROSE", decision = "FALSE x1: the region is opened by the section heading 'Financing Principles' (already a heading), whose body refers to the box ('described in Box 2'); 'Box 2: Financing Principles' names that boxed object - object title / caption, scope EMBEDDED_ARTIFACT. Title that opens a document/section region -> heading; title that only names an embedded object (table / figure / box / callout) -> non-heading. Decided on structural ownership, not on the 'Box N:' prefix" },
                },
                revision = new
                {
                    previous = new { commit = "1b4f7bd", decision = "TRUE", membershipTotal = 254 },
                    revisedAt = "2026-09-25, before the Gold freeze and before any reveal",
                    reason = "the first adjudication rested on a description of the box's content alone; the full page shows the section heading above it and the body's reference to the box. The description omitted that context",
                    engineProposalsOpened = false,
                },
            },
            bindingFailures = failures,
        });
    }
}
