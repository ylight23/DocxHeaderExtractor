using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC0133_CANONICAL_OCCURRENCE_AUDIT_V1: DOC-0133 (IBRD MD&amp;A and condensed quarterly financial
/// statements, March 2025, PDF) under FINANCIAL_PROCUREMENT_HEADING_POLICY_V1, its financial
/// distinctions and OCCURRENCE_SEMANTIC_AXES_V2. 0 model calls, no Gold write.
/// <para>
/// Every source atom of the PDF structured lane is placed in one category from what the source shows:
/// its font size and weight, its margin, its row and segment, its text. The document sets its own
/// hierarchy in type - 14pt sections and statement titles, 12pt bold subsections, 11pt bold note and
/// sub-note headings at the margin, 11pt regular subheadings - and those classes are read as heading
/// candidates. Table depth is evidence, not the gate: a caption set at heading size or a title block
/// is surfaced as ambiguous for the user, not decided by where it sits. The old Gold (123, a count)
/// and the 851 model proposals are not targets; the model run is read only at the end, to compare.
/// </para>
/// </summary>
public sealed partial class Doc0133StructuralAuditTests
{
    private const string Source = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/048_IBRD_Financial_Statements_March_2025.pdf";
    private const string ModelRun = "eval/a99-closed-loop/scaleup-real-harness-v1/DOC-0133/pdf-structured-r1.v1.json";
    private const int OldGold = 123;

    internal sealed record Atom(SemanticSourceAtom Source, double FontSize, bool Bold, bool Italic, double Left)
    {
        public string Alias => Source.Alias;
        public string Text => Source.Text.Trim();
        public int Page => Source.Page;
    }

    /// <summary>A heading candidate: one or more atoms, with its axes and verdict.</summary>
    internal sealed record Candidate(string[] Aliases, string Text, string Verdict, string Pattern, string Reason,
        string[] SemanticFunctions, string PrimaryFunction, string Scope, string[] OccurrenceRoles, string TitleRelation,
        string? InformationType = null);

    [GeneratedRegex(@"^[\s$€£(),.%\d—–\-+*/]+$")] private static partial Regex NumericOnly();
    [GeneratedRegex(@"^(Table|Figure)\s*[A-Z]?\d+(\.\d+)?")] private static partial Regex Caption();
    [GeneratedRegex(@"^(\(?[a-z]\)|[a-z]\.|[a-z]\s|ᵃ|ᵇ|ᶜ|ᵈ|ᵉ|\*|Note:|Notes:|Source:|Sources:)")] private static partial Regex FootnoteLead();
    [GeneratedRegex(@"^(19|20)\d\d$|^(March|June|December|September) \d{1,2}, (19|20)\d\d$|^\$|^In (millions|billions)|^\(\$ in|^%|^Amount$|^Total$", RegexOptions.IgnoreCase)] private static partial Regex ColumnHeaderText();

    private static readonly string[] RunningFooters =
    [
        "Management's Discussion and Analysis: March 31, 2025",
        "Condensed Quarterly Financial Statements: March 31, 2025 (Unaudited)",
        "IBRD FINANCIAL STATEMENTS: March 31, 2025",
        "The Notes to the Condensed QuarterlyFinancial Statements are an integralpart",
    ];

    internal static IReadOnlyList<Atom> ReadAtoms()
    {
        var pdf = TestRepository.Path(Source);
        var atoms = PdfStructuredSourceAuthorityBuilder.Build(pdf).Atoms;
        Dictionary<string, PdfLine> lines;
        using (var document = UglyToad.PdfPig.PdfDocument.Open(pdf))
            lines = PdfLineExtraction.ExtractLines(document)
                .GroupBy(PdfLineIdentity.Of, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return atoms.Select(a => new Atom(a, Math.Round(lines[a.SourceId].FontSize, 1), lines[a.SourceId].BoldRatio >= 0.5, lines[a.SourceId].ItalicRatio >= 0.5, lines[a.SourceId].Left)).ToArray();
    }

    /// <summary>The heading candidates, decided and ambiguous, in source order.</summary>
    internal static List<Candidate> Candidates(IReadOnlyList<Atom> atoms)
    {
        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        Atom A(string alias) => byAlias[alias];
        var list = new List<Candidate>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        void Add(Candidate c)
        {
            foreach (var a in c.Aliases) Assert.True(claimed.Add(a), $"{a} decided twice");
            list.Add(c);
        }
        void Named(string[] aliases, string expected, string verdict, string pattern, string reason,
            string[] functions, string primary, string scope, string[] roles, string titleRelation, string? info = null)
        {
            var text = string.Join(" ", aliases.Select(a => A(a).Text));
            Assert.True(Squash(text) == Squash(expected), $"{aliases[0]}: expected '{expected}', found '{text}'");
            Add(new(aliases, text, verdict, pattern, reason, functions, primary, scope, roles, titleRelation, info));
        }

        // The seven patterns the user decided on 2026-09-25, occurrence by occurrence.
        const string U = "user decision 2026-09-25: ";
        // P1 - the cover decomposes: the title lines are one heading claim; issuer, date and audit status are metadata.
        Named(["L0000:S0", "L0001:S0"], "International Bank forReconstruction and Development", "NON_HEADING", "P1_COVER_TITLE_BLOCK",
            U + "issuer above the cover title", ["INFORMATION"], "INFORMATION", "DOCUMENT", ["METADATA"], "NONE", "ISSUER_METADATA");
        Named(["L0002:S0", "L0003:S0", "L0004:S0"], "Management’s Discussion & Analysis and Condensed Quarterly Financial Statements", "HEADING", "P1_COVER_TITLE_BLOCK",
            U + "the cover title, one claim of three lines", ["IDENTITY"], "IDENTITY", "DOCUMENT", ["REGION_OPENER"], "TITLE");
        Named(["L0005:S0"], "March 31, 2025", "NON_HEADING", "P1_COVER_TITLE_BLOCK",
            U + "reporting date under the cover title", ["INFORMATION"], "INFORMATION", "DOCUMENT", ["METADATA"], "NONE", "TEMPORAL_METADATA");
        Named(["L0006:S0"], "(Unaudited)", "NON_HEADING", "P1_COVER_TITLE_BLOCK",
            U + "audit status under the cover title", ["INFORMATION"], "INFORMATION", "DOCUMENT", ["METADATA"], "NONE", "STATUS_METADATA");
        // P4 - the MD&A part's title on its contents page; the running headers of later pages are other occurrences.
        Named(["L0007:S0"], "Management’s Discussion and Analysis", "HEADING", "P4_PART_TITLE_ON_CONTENTS_PAGE",
            U + "part title opening the MD&A contents page, not a running header", ["IDENTITY"], "IDENTITY", "DOCUMENT_PART", ["REGION_OPENER"], "TITLE");
        // P2 - page 31: issuer and date are metadata; the statements title opens its own navigation group.
        Named(["L1048:S0", "L1049:S0"], "INTERNATIONAL BANK FOR RECONSTRUCTION AND DEVELOPMENT (IBRD)", "NON_HEADING", "P2_PART_TITLE_BLOCK",
            U + "issuer/context label above the contents", ["INFORMATION"], "INFORMATION", "DOCUMENT_PART", ["METADATA"], "NONE", "ISSUER_METADATA");
        Named(["L1051:S0"], "March 31, 2025", "NON_HEADING", "P2_PART_TITLE_BLOCK",
            U + "period date on the contents page", ["INFORMATION"], "INFORMATION", "DOCUMENT_PART", ["METADATA"], "NONE", "TEMPORAL_METADATA");
        Named(["L1052:S0"], "CONDENSED QUARTERLY FINANCIAL STATEMENTS (UNAUDITED)", "HEADING", "P2_PART_TITLE_BLOCK",
            U + "opens the navigation group of the statements, notes and report; not an ordinary contents entry",
            ["IDENTITY", "STRUCTURE"], "IDENTITY", "DOCUMENT_PART", ["REGION_OPENER"], "TITLE");
        // P3, P5 - the title of a table object is a caption, whatever its size.
        Named(["L0051:S0"], "Table 1: Selected Financial Data", "NON_HEADING", "P3_HEADING_SIZED_CAPTION",
            U + "12pt caption; a unit line and table data follow", ["IDENTITY"], "IDENTITY", "TABLE", ["CAPTION"], "TITLE");
        Named(["L0655:S0"], "Changes in Surplus", "NON_HEADING", "P5_UNNUMBERED_TABLE_TITLE",
            U + "names the small table directly below it", ["IDENTITY"], "IDENTITY", "TABLE", ["CAPTION"], "TITLE");
        // P6 - a standalone label opening its own paragraph.
        Named(["L1978:S0"], "Pension and Other Postretirement Benefits", "HEADING", "P6_MINOR_LOCAL_HEADING",
            U + "10pt bold standalone label opening a narrative paragraph inside Note H", ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE");

        // P7 - standalone labels inside the notes, each opening coherent prose or data below it. Here
        // they share one visual grammar (10pt italic at the margin); the grammar is evidence, the
        // decision is standalone + opens a region. Long italic lines set inside a paragraph (standard
        // names: "In November 2024, the FASB ...") do not stand alone and are not candidates; the one
        // label that wraps is named with both its lines.
        Named(["L2045:S0", "L2046:S0"], "Securities purchased under resale agreements, Securities sold under repurchase agreements, and Securities lent undersecurities lendingagreements",
            "HEADING", "P7_NOTE_LOCAL_LABEL", U + "standalone label of two lines opening its paragraph inside Note J",
            ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE");
        foreach (var atom in atoms.Where(a => !claimed.Contains(a.Alias) && a.Italic && a.FontSize is >= 9.9 and <= 10.1 && a.Left <= 74
            && a.Text.Length <= 70 && !a.Text.StartsWith("In ", StringComparison.Ordinal) && !a.Text.StartsWith("Expressed", StringComparison.Ordinal)
            && !char.IsDigit(a.Text[0]) && a.Page >= 37).ToArray())
            Add(new([atom.Alias], atom.Text, "HEADING", "P7_NOTE_LOCAL_LABEL",
                U + "standalone label at the margin opening the prose or data below it",
                ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE"));

        foreach (var atom in atoms)
        {
            if (claimed.Contains(atom.Alias)) continue;
            var t = atom.Text;
            if (Caption().IsMatch(t) || NumericOnly().IsMatch(t)) continue;
            var margin = atom.Left <= 76;
            if (atom.FontSize >= 13.9 && atom.FontSize <= 14.1 && atom.Bold)
            {
                var toc = t is "Contents" or "Tables" or "Figures";
                Add(new([atom.Alias], t, "HEADING", toc ? "TOC_OPENER" : "SECTION_14PT", toc ? "opens a contents list" : "14pt bold MD&A section",
                    toc ? ["IDENTITY", "STRUCTURE"] : ["STRUCTURE", "IDENTITY"], toc ? "IDENTITY" : "STRUCTURE", toc ? "TOC" : "SECTION", ["REGION_OPENER"], "TITLE"));
            }
            else if (atom.FontSize >= 13.9 && atom.FontSize <= 14.1)
            {
                var (pattern, reason, functions, primary, scope) = t switch
                {
                    "CONTENTS" => ("TOC_OPENER", "opens the statements' contents list", new[] { "IDENTITY", "STRUCTURE" }, "IDENTITY", "TOC"),
                    "NOTES TO CONDENSED QUARTERLY FINANCIAL STATEMENTS" => ("NOTES_PART", "opens the notes", new[] { "IDENTITY", "STRUCTURE" }, "STRUCTURE", "DOCUMENT_PART"),
                    "INDEPENDENTAUDITOR'S REVIEW REPORT" => ("EMBEDDED_REPORT", "the review report's title", new[] { "IDENTITY" }, "IDENTITY", "EMBEDDED_ARTIFACT"),
                    _ => ("STATEMENT_TITLE", "14pt title of a financial statement", new[] { "IDENTITY" }, "IDENTITY", "FINANCIAL_STATEMENT"),
                };
                Add(new([atom.Alias], t, "HEADING", pattern, reason, functions, primary, scope, ["REGION_OPENER"], "TITLE"));
            }
            else if (atom.FontSize >= 11.9 && atom.FontSize <= 12.1 && atom.Bold && margin)
                Add(new([atom.Alias], t, "HEADING", "SUBSECTION_12PT", "12pt bold MD&A subsection", ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE"));
            else if (atom.FontSize >= 10.9 && atom.FontSize <= 11.1 && margin && t.StartsWith("NOTE ", StringComparison.Ordinal))
                Add(new([atom.Alias], t, "HEADING", "NOTE_TITLE", "11pt bold note title", ["STRUCTURE", "IDENTITY"], "STRUCTURE", "NOTE", ["REGION_OPENER"], "TITLE"));
            else if (atom.FontSize >= 10.9 && atom.FontSize <= 11.1 && margin && atom.Bold)
                Add(new([atom.Alias], t, "HEADING", atom.Page >= 37 ? "NOTE_SUBHEADING" : "SUBSECTION_11PT_BOLD",
                    atom.Page >= 37 ? "11pt bold sub-heading inside a note" : "11pt bold MD&A sub-subsection", ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE"));
            else if (atom.FontSize >= 10.9 && atom.FontSize <= 11.1 && margin)
                Add(new([atom.Alias], t, "HEADING", "SUBSECTION_11PT_REGULAR", "11pt regular standalone line over body text: the MD&A's lowest heading level",
                    ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE"));
        }
        return list.OrderBy(c => atoms.TakeWhile(a => a.Alias != c.Aliases[0]).Count()).ToList();
    }

    /// <summary>Every atom's category, from what the source shows. Descriptive for non-headings.</summary>
    internal static Dictionary<string, (string Category, string Reason)> Categorize(IReadOnlyList<Atom> atoms, IReadOnlyList<Candidate> candidates)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var c in candidates)
            foreach (var a in c.Aliases)
                result[a] = (c.Verdict switch
                {
                    "HEADING" => "HEADING_CANDIDATE",
                    "AMBIGUOUS" => "AMBIGUOUS",
                    _ => c.OccurrenceRoles.Contains("CAPTION") ? "TABLE_CAPTION" : "METADATA",
                }, c.Pattern);

        var rows = atoms.GroupBy(a => (a.Page, a.Source.Row)).ToDictionary(g => g.Key, g => g.ToArray());
        var tocPages = candidates.Where(c => c.Pattern == "TOC_OPENER").Select(c => atoms.First(a => a.Alias == c.Aliases[0]).Page).ToHashSet();
        var captionContinues = false;
        foreach (var atom in atoms)
        {
            if (result.ContainsKey(atom.Alias)) { captionContinues = false; continue; }
            var t = atom.Text;
            var row = rows[(atom.Page, atom.Source.Row)];
            (string, string) category;
            if (RunningFooters.Any(f => Squash(t).Contains(Squash(f), StringComparison.Ordinal))
                || (atom.Bold && atom.FontSize is >= 8.9 and <= 9.6 && t.StartsWith("Management’s Discussion and Analysis", StringComparison.Ordinal)))
                category = ("REPEATED_HEADER", "running header/footer, page number or repeated statement footer");
            else if (tocPages.Contains(atom.Page))
                category = ("NAVIGATION", "contents-list entry");
            else if (Caption().IsMatch(t))
                category = ("TABLE_CAPTION", "numbered table or figure caption");
            else if (captionContinues && atom.Bold && atom.FontSize is >= 8.9 and <= 10.1)
                category = ("TABLE_CAPTION", "second line of a caption");
            else if (atom.FontSize <= 8.1 && FootnoteLead().IsMatch(t) && !NumericOnly().IsMatch(t))
                category = ("FOOTNOTE", "lettered note, source line or note under a table");
            else if (NumericOnly().IsMatch(t))
                category = ("TABLE_VALUE", "number, amount or blank dash");
            else if (t.StartsWith("Expressed", StringComparison.Ordinal) || t.StartsWith("In millions", StringComparison.Ordinal) || t.StartsWith("In billions", StringComparison.Ordinal))
                category = ("TABLE_COLUMN_HEADER", "unit line over a table or statement");
            else if (atom.FontSize >= 9.8 && !atom.Bold)
                category = ("BODY_TEXT", "10pt body text");
            else if (atom.FontSize >= 9.8)
                category = ("OTHER_NON_HEADING", "bold run-in phrase in body text");
            else if (row.Any(o => o != atom && NumericOnly().IsMatch(o.Text) && o.Text.Any(char.IsDigit)) && atom.Source.Segment == row.Min(o => o.Source.Segment))
                category = ("TABLE_ROW_LABEL", "leftmost text of a row carrying figures");
            else if (ColumnHeaderText().IsMatch(t) || (row.Length >= 2 && row.All(o => !o.Text.Any(char.IsDigit) || ColumnHeaderText().IsMatch(o.Text)) && atom.Left > 150))
                category = ("TABLE_COLUMN_HEADER", "period, unit or column label above figures");
            else if (atom.Left <= 110)
                category = ("TABLE_ROW_LABEL", "row or row-group label at the table's left edge");
            else
                category = ("TABLE_COLUMN_HEADER", "label inside the table body away from the left edge (column head or chart label)");
            result[atom.Alias] = category;
            captionContinues = category.Item1 == "TABLE_CAPTION";
        }
        return result;
    }

    [Fact]
    public void Audit()
    {
        var atoms = ReadAtoms();
        var candidates = Candidates(atoms);
        var categories = Categorize(atoms, candidates);
        var index = atoms.Select((a, i) => (a.Alias, i)).ToDictionary(x => x.Alias, x => x.i, StringComparer.Ordinal);

        // The model run, read only now: every isHeading proposal, as the atoms it names.
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ModelRun)));
        var proposals = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var call in run.RootElement.GetProperty("ledger").EnumerateArray())
        {
            using var response = JsonDocument.Parse(call.GetProperty("RawResponse").GetString()!);
            foreach (var h in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                if (!h.TryGetProperty("isHeading", out var isHeading) || !isHeading.GetBoolean()) continue;
                if (!h.TryGetProperty("sourceParts", out var parts)) continue;
                var aliases = parts.EnumerateArray().Select(p => p.GetProperty("sourceAlias").GetString()!).Where(index.ContainsKey).ToArray();
                if (aliases.Length > 0) proposals.TryAdd(string.Join("+", aliases), aliases);
            }
        }
        var proposedAliases = proposals.Values.SelectMany(a => a).ToHashSet(StringComparer.Ordinal);
        var byProposalCategory = proposals.Values.GroupBy(a => categories[a[0]].Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());

        var repeats = RepeatStatuses(candidates);
        var headings = candidates.Where(c => c.Verdict == "HEADING").ToArray();
        var ambiguous = candidates.Where(c => c.Verdict == "AMBIGUOUS").ToArray();
        var decidedNonHeadings = candidates.Where(c => c.Verdict == "NON_HEADING").ToArray();
        int Count(string category) => atoms.Count(a => categories[a.Alias].Category == category);

        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy-audit", "DOC-0133.structural-audit.v1.json", new
        {
            artifactKind = "a99_structural_heading_audit",
            documentId = "DOC-0133",
            policy = FinancialProcurementHeadingPolicyTests.PolicyId,
            policyAddendum = "eval/a99-closed-loop/policy/financial-occurrence-distinctions.v1.json",
            ontology = OccurrenceSemanticAxesTests.OntologyId,
            source = new { path = Source, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Source)) },
            modelEvidence = new { path = ModelRun, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ModelRun)) },
            modelCalls = 0,
            goldWritten = false,
            summary = new
            {
                SOURCE_OCCURRENCES_REVIEWED = atoms.Count,
                STRUCTURAL_CANDIDATES = headings.Count(c => c.PrimaryFunction == "STRUCTURE"),
                IDENTITY_CANDIDATES = headings.Count(c => c.PrimaryFunction == "IDENTITY"),
                INFORMATION_REGION_OPENERS = headings.Count(c => c.PrimaryFunction == "INFORMATION"),
                TABLE_CAPTIONS = Count("TABLE_CAPTION"),
                TABLE_ROW_LABELS = Count("TABLE_ROW_LABEL"),
                TABLE_COLUMN_HEADERS = Count("TABLE_COLUMN_HEADER"),
                REPEATED_HEADERS = Count("REPEATED_HEADER"),
                FOOTNOTES = Count("FOOTNOTE"),
                OTHER_NON_HEADINGS = Count("BODY_TEXT") + Count("TABLE_VALUE") + Count("NAVIGATION") + Count("METADATA") + Count("OTHER_NON_HEADING"),
                otherBreakdown = new { bodyText = Count("BODY_TEXT"), tableValues = Count("TABLE_VALUE"), navigation = Count("NAVIGATION"), metadata = Count("METADATA"), other = Count("OTHER_NON_HEADING") },
                OLD_GOLD_TOTAL = OldGold,
                MODEL_PROPOSALS = proposals.Count,
                CLEAR_HEADING = headings.Length,
                CLEAR_NON_HEADING = atoms.Count - candidates.Where(c => c.Verdict != "NON_HEADING").Sum(c => c.Aliases.Length),
                AMBIGUOUS = ambiguous.Length,
                PROPOSED_FINAL_TOTAL = headings.Length,
                PROPOSED_FINAL_RANGE = new { min = headings.Length, max = headings.Length + ambiguous.Length },
                GOLD_WRITE = false,
                USER_APPROVAL_REQUIRED = true,
                note = "Non-heading categories are descriptive (font, margin, row and segment evidence). Heading membership: 92 clear by the document's type hierarchy plus the seven patterns the user decided occurrence by occurrence on 2026-09-25. Old Gold 123 is a count with no items, so no item-level KEEP/REMOVE against it exists.",
            },
            headingsByPattern = headings.GroupBy(c => c.Pattern).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            userDecidedPatterns = candidates.Where(c => c.Pattern.StartsWith('P')).GroupBy(c => c.Pattern).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new
            {
                pattern = g.Key,
                items = g.Select(c => new { c.Aliases, c.Text, c.Verdict, c.Reason, axes = Axes(c, repeats), proposedByModel = c.Aliases.Any(proposedAliases.Contains) }).ToArray(),
            }).ToArray(),
            modelCrossCheck = new
            {
                distinctProposals = proposals.Count,
                byCategoryOfFirstAtom = byProposalCategory,
                clearHeadingsProposed = headings.Count(c => c.Aliases.Any(proposedAliases.Contains)),
                clearHeadingsMissed = headings.Where(c => !c.Aliases.Any(proposedAliases.Contains)).Select(c => new { alias = c.Aliases[0], c.Text, c.Pattern }).ToArray(),
            },
            headings = headings.Select(c => new { c.Aliases, c.Text, c.Pattern, c.Reason, axes = Axes(c, repeats) }).ToArray(),
            nonHeadingSamples = categories.Values.Select(v => v.Category).Distinct().Order(StringComparer.Ordinal)
                .Where(k => k is not "HEADING_CANDIDATE" and not "AMBIGUOUS")
                .ToDictionary(k => k, k => atoms.Where(a => categories[a.Alias].Category == k).Take(8).Select(a => new { alias = a.Alias, page = a.Page, text = a.Text }).ToArray()),
        });
    }

    /// <summary>REPEATED when an earlier candidate carries the same text (whitespace and case aside).</summary>
    internal static Dictionary<Candidate, string> RepeatStatuses(IReadOnlyList<Candidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return candidates.ToDictionary(c => c, c => seen.Add(Squash(c.Text)) ? "FIRST" : "REPEATED");
    }

    private static object Axes(Candidate c, IReadOnlyDictionary<Candidate, string> repeats) => new
    {
        semanticFunctions = c.SemanticFunctions,
        primaryFunction = c.PrimaryFunction,
        scope = c.Scope,
        occurrenceRoles = c.OccurrenceRoles,
        titleRelation = c.TitleRelation,
        repeatStatus = repeats[c],
        informationType = c.InformationType,
    };

    private static string Squash(string value) => Regex.Replace(value, @"\s+", "").Replace('\'', '’');
}
