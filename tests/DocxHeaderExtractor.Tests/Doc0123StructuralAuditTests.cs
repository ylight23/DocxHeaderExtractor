using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// DOC-0123 under FINANCIAL_PROCUREMENT_HEADING_POLICY_V1: a structural review read off the source,
/// and every qwen3.7-flash proposal from the frozen step-3 run placed in one of the policy's eight
/// categories. 0 model calls, no Gold write - the user approves the result.
/// <para>
/// The source is a World Bank Standard Procurement Document, and its template marks structure with
/// its own styles (Head11b sections, HeadingSPD02 ITP clauses, SPDForm2 forms, S9Header contract
/// forms...). Those are read as headings; bold Normal local headings, which the template does not
/// style, are named one by one below, each with the text it must carry. Nothing is fitted to a total.
/// </para>
/// <para>
/// The old Gold (362) is a count with a per-section breakdown and no items, so KEEP/REMOVE/ADD against
/// it are itemised only where that breakdown and its recorded deltas identify the old set exactly;
/// elsewhere the comparison is by section count, and says so.
/// </para>
/// </summary>
public sealed partial class Doc0123StructuralAuditTests
{
    private const string Source = "todo10_8/heading_corpus_100/02_hop_dong_mua_sam/038_WB_Works_DB_SingleStage_NoSEASH_2025.docx";
    private const string ModelRun = "eval/a99-closed-loop/scaleup-real-harness-v1/DOC-0123/docx-r1.v1.json";

    /// <summary>Section starts, by the alias of the paragraph that opens each (asserted by text).</summary>
    private static readonly (string Section, string Alias, string Text)[] Sections =
    [
        ("FRONT_MATTER", "S0001", "STANDARD PROCUREMENT DOCUMENT"),
        ("PART_1", "S0131", "PART 1 – Request for Proposal Procedures"),
        ("SECTION_I", "S0132", "Section I - Instructions to Proposers (ITP)"),
        ("SECTION_II", "S0541", "Section II - Proposal Data Sheet (PDS)"),
        ("SECTION_III", "S0758", "Section III. Evaluation and Qualification Criteria"),
        ("SECTION_IV", "S0875", "Section IV - Proposal Forms"),
        ("SECTION_V", "S1714", "Section V - Eligible Countries"),
        ("SECTION_VI", "S1719", "Section VI - Fraud and Corruption"),
        ("PART_2", "S1738", "PART 2 –Employer’s Requirements"),
        ("SECTION_VII", "S1739", "Section VII. Employer’s Requirements"),
        ("PART_3", "S1994", "PART 3 – Conditions of Contract and Contract Forms"),
        ("SECTION_VIII", "S1995", "Section VIII - General Conditions (GC)"),
        ("SECTION_IX", "S2007", "Section IX - Particular Conditions (PC)"),
        ("SECTION_X", "S2953", "Section X - Contract Forms"),
    ];

    /// <summary>The old Gold's per-section counts (canonical-semantic-gold-vnext, sourceOnlyAudit).</summary>
    private static readonly Dictionary<string, int> OldBySection = new()
    {
        ["FRONT_MATTER"] = 25, ["PART_1"] = 1, ["SECTION_I"] = 69, ["SECTION_II"] = 11, ["SECTION_III"] = 20,
        ["SECTION_IV"] = 59, ["SECTION_V"] = 2, ["SECTION_VI"] = 3, ["PART_2"] = 1, ["SECTION_VII"] = 25,
        ["PART_3"] = 1, ["SECTION_VIII"] = 1, ["SECTION_IX"] = 125, ["SECTION_X"] = 19,
    };

    /// <summary>Sections whose old items the breakdown and its recorded delta identify exactly.</summary>
    private static readonly HashSet<string> OldItemised =
        ["PART_1", "SECTION_I", "SECTION_II", "SECTION_III", "SECTION_IV", "SECTION_V", "SECTION_VI", "PART_2", "SECTION_VII", "PART_3", "SECTION_VIII"];

    /// <summary>Template styles that mark a heading in this document.</summary>
    private static readonly HashSet<string> HeadingStyles =
    [
        "Title", "Head0", "Head11b", "Heading1", "HeadingSPD010", "HeadingSPD02", "SEC3h1", "SEC3h2", "Heading4",
        "SPDForms1", "SPDForm2", "SPD3EmployersRequirement", "SectionVHeading2", "S9Header", "Subtitle2", "SectionXHeading",
    ];

    private enum Verdict { Keep, Remove, Ambiguous }

    /// <summary>
    /// A decided occurrence. Aliases lists the paragraphs it spans; Text is what it must read.
    /// InOld: whether the old Gold counted it (null where the old set is not itemised).
    /// </summary>
    private sealed record Item(string Section, string[] Aliases, string Text, Verdict Verdict, string Category, string Rule, string Reason, bool? InOld);

    /// <summary>Bold local headings the template leaves unstyled, and composites. Each is asserted by text.</summary>
    private static readonly (string[] Aliases, string Text, Verdict Verdict, string Reason, bool? InOld)[] Named =
    [
        // FRONT_MATTER - old items not identified, so InOld is null throughout.
        (["S0001", "S0002", "S0003", "S0004", "S0005"], "STANDARD PROCUREMENT DOCUMENT Request for Proposals Works Design and Build (Single-Stage Request for Proposals, after Initial Selection)", Verdict.Keep, "cover title block: the document title", null),
        (["S0011"], "Revisions", Verdict.Keep, "opens the revision history", null),
        (["S0012"], "March 2025", Verdict.Ambiguous, "revision entry date: heads that revision's note (policy 9) or is metadata (exclude 12)", null),
        (["S0014"], "July 2023", Verdict.Ambiguous, "revision entry date", null),
        (["S0019"], "January 2021", Verdict.Ambiguous, "revision entry date", null),
        (["S0021"], "December 2019", Verdict.Ambiguous, "revision entry date", null),
        (["S0023"], "Preface", Verdict.Keep, "opens the preface", null),
        (["S0043"], "Summary", Verdict.Keep, "opens the summary", null),
        (["S0044"], "Specific Procurement Notice", Verdict.Keep, "summary entry heading its explanatory block", null),
        (["S0045"], "Specific Procurement Notice - Request for Proposal (RFP) to Initially Selected Proposers", Verdict.Keep, "summary entry heading its explanatory block", null),
        (["S0048"], "PART 1 – REQUEST FOR PROPOSAL PROCEDURES", Verdict.Keep, "summary part label heading its block", null),
        (["S0049"], "Section I - Instructions to Proposers (ITP)", Verdict.Keep, "summary section label heading its explanatory block", null),
        (["S0051"], "Section II - Proposal Data Sheet (PDS)", Verdict.Keep, "summary section label", null),
        (["S0053"], "Section III - Evaluation and Qualification Criteria", Verdict.Keep, "summary section label", null),
        (["S0055"], "Section IV - Proposal Forms", Verdict.Keep, "summary section label", null),
        (["S0057"], "Section V - Eligible Countries", Verdict.Keep, "summary section label", null),
        (["S0059"], "Section VI - Fraud and Corruption", Verdict.Keep, "summary section label", null),
        (["S0061"], "PART 2 – EMPLOYER’S REQUIREMENTS", Verdict.Keep, "summary part label", null),
        (["S0062"], "Section VII - Employer’s Requirements", Verdict.Keep, "summary section label", null),
        (["S0065"], "PART 3 – CONDITIONS OF CONTRACT AND CONTRACT FORMS", Verdict.Keep, "summary part label", null),
        (["S0066"], "Section VIII - General Conditions (GC)", Verdict.Keep, "summary section label", null),
        (["S0068"], "Section IX - Particular Conditions (PC)", Verdict.Keep, "summary section label", null),
        (["S0070"], "Section X - Contract Forms", Verdict.Keep, "summary section label", null),
        (["S0072"], "Notice of Request for Proposals", Verdict.Keep, "opens the notice template", null),
        (["S0073", "S0074", "S0075", "S0076"], "Request for Proposals Works (Design and Build) (After Initial Selection)", Verdict.Ambiguous, "the notice's own title block, directly under its heading: a second title or part of it", null),
        (["S0104", "S0105", "S0106", "S0107"], "Request for Proposals Works Design and Build (Single-Stage RFP after Initial Selection)", Verdict.Keep, "cover title of the RFP document proper", null),
        (["S0117"], "Table of Content", Verdict.Keep, "opens the table of contents region", null),
        // SECTION_II - the PDS groups A-K; the "ITP n.n" cells beside them are row references (exclude 1).
        (["S0546"], "A. General", Verdict.Keep, "PDS group heading", true),
        (["S0568"], "B. RFP Document", Verdict.Keep, "PDS group heading", true),
        (["S0591"], "C. Preparation of Proposals", Verdict.Keep, "PDS group heading", true),
        (["S0649"], "D. Submission of Proposals", Verdict.Keep, "PDS group heading", true),
        (["S0669"], "E. Opening of Technical Parts of Proposals", Verdict.Keep, "PDS group heading", true),
        (["S0682"], "G. Evaluation of Technical Parts of Proposals", Verdict.Keep, "PDS group heading", true),
        (["S0707"], "H. Opening of Financial Parts", Verdict.Keep, "PDS group heading", true),
        (["S0710"], "I. Evaluation of Financial Part", Verdict.Keep, "PDS group heading", true),
        (["S0731"], "J. Evaluation of Combined Technical and Financial Part", Verdict.Keep, "PDS group heading", true),
        (["S0741"], "K. Award of Contract", Verdict.Keep, "PDS group heading", true),
        // SECTION_III
        (["S0759"], "Contents", Verdict.Keep, "opens the section's local contents", true),
        (["S0770"], "1.1 Update of Information", Verdict.Keep, "numbered criterion heading", true),
        (["S0772"], "1.2 Financial Resources", Verdict.Keep, "numbered criterion heading (mis-styled Footer)", true),
        (["S0777"], "1.3 Contractor’s Representative and Key Personnel", Verdict.Keep, "numbered criterion heading (mis-styled Footer)", true),
        (["S0780"], "1.4 Equipment", Verdict.Keep, "numbered criterion heading", true),
        (["S0783"], "1.5 Subcontractors", Verdict.Keep, "numbered criterion heading", true),
        (["S0804"], "TECHINICAL PROPOSAL SCORING METHOLOGY", Verdict.Keep, "opens the scoring methodology", true),
        // SECTION_IV
        (["S0876"], "Table of Forms", Verdict.Keep, "opens the forms list region", true),
        (["S1144"], "General`", Verdict.Keep, "daywork local heading", true),
        (["S1146"], "Daywork Labour", Verdict.Keep, "daywork local heading", true),
        (["S1155"], "Daywork Materials", Verdict.Keep, "daywork local heading", true),
        (["S1162"], "Daywork Contractor’s Equipment", Verdict.Keep, "daywork local heading", true),
        (["S1341"], "CODE OF CONDUCT FOR CONTRACTOR’S PERSONNEL", Verdict.Ambiguous, "the code's own title inside its form, directly under the form heading", false),
        (["S1346"], "REQUIRED CONDUCT", Verdict.Keep, "code of conduct local heading", true),
        (["S1364"], "RAISING CONCERNS", Verdict.Keep, "code of conduct local heading", true),
        (["S1370"], "CONSEQUENCES OF VIOLATING THE CODE OF CONDUCT", Verdict.Keep, "code of conduct local heading", true),
        (["S1380", "S1381"], "ATTACHMENT 1 TO THE CODE OF CONDUCT FORM BEHAVIORS CONSTITUTING SEXUAL EXPLOITATION AND ABUSE (SEA) AND BEHAVIORS CONSTITUTING SEXUAL HARASSMENT (SH", Verdict.Keep, "attachment heading (two lines, one heading)", true),
        (["S1389"], "Examples of sexual harassment in a work context", Verdict.Ambiguous, "introduces the list beneath it: local heading (policy 9) or list lead-in", false),
        (["S1468"], "Declaration", Verdict.Keep, "opens the declaration part of the resume form", true),
        // SECTION_V, SECTION_VI
        (["S1715"], "Eligibility for the Provision of Goods, Works and non-consulting Services in Bank-Financed Procurement", Verdict.Keep, "section's only subheading", true),
        (["S1721"], "Purpose", Verdict.Keep, "numbered F&C heading", true),
        (["S1723"], "Requirements", Verdict.Keep, "numbered F&C heading", true),
        // SECTION_VII
        (["S1751"], "Notes on preparing the Employer’s Requirements", Verdict.Keep, "opens the preparation notes", true),
        (["S1884"], "Management and Safety of Hazardous Materials", Verdict.Keep, "ES requirement heading", true),
        (["S1886"], "Resource Efficiency and Pollution Prevention and Management", Verdict.Keep, "ES requirement heading", true),
        (["S1888"], "Resource efficiency", Verdict.Keep, "ES requirement subheading", true),
        (["S1893"], "Pollution prevention and management", Verdict.Keep, "ES requirement subheading", true),
        (["S1897"], "Biodiversity Conservation and Sustainable Management of Living Natural Resources", Verdict.Keep, "ES requirement heading", true),
        (["S1903"], "Road Safety", Verdict.Keep, "ES requirement heading", true),
        (["S1905"], "SPECIFIED PROVISIONAL SUMS for ES OUTCOMES", Verdict.Keep, "ES option 1 heading", true),
        (["S1913"], "Suggested content for an Environmental and Social Policy (Statement)", Verdict.Keep, "ES option 2 heading", true),
        (["S1928"], "Minimum Content of ES requirements", Verdict.Keep, "ES option 2 heading", true),
        (["S1939"], "SPECIFIED PROVISIONAL SUMS for ES OUTCOMES", Verdict.Keep, "ES option 2 heading (repeat occurrence)", true),
        (["S1962"], "Contractor’s Representative and Key Personnel", Verdict.Ambiguous, "repeats the styled heading just above and names the table below it: table title (exclude 3) or heading", true),
        (["S1971"], "Key Personnel for Design", Verdict.Remove, "row-group label inside the personnel table (exclude 1)", true),
        (["S1977"], "Key Personnel for Construction", Verdict.Remove, "row-group label inside the personnel table (exclude 1)", true),
        // SECTION_IX - old items not identified exactly.
        (["S2009"], "Particular Conditions", Verdict.Ambiguous, "repeated document title above each Part: a heading or part of the Part heading", null),
        (["S2010"], "Part A – Contract Data", Verdict.Keep, "part heading", null),
        (["S2251"], "Part B – Special Provisions", Verdict.Keep, "part heading", null),
        (["S2493"], "4.24.1 Forced Labour", Verdict.Keep, "numbered sub-clause heading", null),
        (["S2495"], "4.24.2 Child labour", Verdict.Keep, "numbered sub-clause heading", null),
        (["S2497"], "4.24.3 Serious Safety Issues", Verdict.Keep, "numbered sub-clause heading", null),
        (["S2499"], "4.24.4 Obtaining natural resource materials in relation to supplier", Verdict.Keep, "numbered sub-clause heading", null),
        (["S2823"], "Appendix- General Conditions of DAAB Agreement", Verdict.Keep, "appendix heading", null),
        (["S2824"], "Definitions", Verdict.Keep, "DAAB clause heading", null),
        (["S2826"], "General provisions", Verdict.Keep, "DAAB clause heading", null),
        (["S2828"], "Warranties", Verdict.Keep, "DAAB clause heading", null),
        (["S2838"], "7. Confidentiality", Verdict.Keep, "DAAB clause heading", null),
        (["S2842"], "9. Fees and Expenses", Verdict.Keep, "DAAB clause heading", null),
        (["S2845"], "Particular Conditions", Verdict.Ambiguous, "repeated document title above Part C", null),
        (["S2846"], "Part C- Fraud and Corruption", Verdict.Keep, "part heading", null),
        (["S2848"], "1. Purpose", Verdict.Keep, "numbered F&C heading", null),
        (["S2850"], "2. Requirements", Verdict.Keep, "numbered F&C heading", null),
        (["S2865"], "Particular Conditions", Verdict.Ambiguous, "repeated document title above Part D", null),
        (["S2866"], "Part D- Environmental and Social (ES)", Verdict.Keep, "part heading", null),
        (["S2867"], "Metrics for Progress Reports", Verdict.Keep, "opens the metrics list", null),
        (["S2869"], "Metrics for regular reporting:", Verdict.Keep, "opens the regular-reporting metrics", null),
        (["S2920"], "Particular Conditions", Verdict.Ambiguous, "repeated document title above Part E", null),
        (["S2921"], "Part E- Sexual Exploitation and Abuse (SEA) and/or Sexual Harassment Performance Declaration for Subcontractors", Verdict.Keep, "part heading", null),
        (["S2924"], "SEA and/or SH Declaration", Verdict.Keep, "declaration heading inside Part E", null),
        // SECTION_X
        (["S2973"], "Notification of Intention to Award", Verdict.Ambiguous, "the letter's own title repeated under the form heading", null),
        (["S2983"], "The successful Proposer", Verdict.Keep, "numbered local heading 1 of the notification", true),
        (["S2992"], "Other Proposers", Verdict.Keep, "numbered local heading 2 (bold lead-in before the instruction)", true),
        (["S3024"], "Reason/s why your Proposal was unsuccessful", Verdict.Keep, "numbered local heading 3 (bold lead-in before the instruction)", true),
        (["S3026"], "How to request a debriefing", Verdict.Keep, "numbered local heading 4", true),
        (["S3038"], "How to make a complaint", Verdict.Keep, "numbered local heading 5", true),
        (["S3054"], "Standstill Period", Verdict.Keep, "numbered local heading 6", true),
        (["S3083"], "Details of beneficial ownership", Verdict.Keep, "opens the ownership details", true),
        (["S3170"], "ES Demand Guarantee", Verdict.Ambiguous, "subtitle of the security form: second title or part of it", null),
        (["S3184"], "Demand Guarantee", Verdict.Ambiguous, "subtitle of the security form", null),
        (["S3201"], "Demand Guarantee", Verdict.Ambiguous, "subtitle of the security form", null),
    ];

    /// <summary>Styled paragraphs the policy reads differently from the template.</summary>
    private static readonly Dictionary<string, (Verdict Verdict, string Category, string Reason)> StyledOverrides = new(StringComparer.Ordinal)
    {
        ["S1027"] = (Verdict.Ambiguous, "TABLE_CAPTION", "\"Table A. Local Currency\" names the one table below it (exclude 3) though styled as a form heading"),
        ["S1046"] = (Verdict.Ambiguous, "TABLE_CAPTION", "\"Table B. Foreign Currency (FC)\" names the one table below it"),
        ["S1067"] = (Verdict.Ambiguous, "TABLE_CAPTION", "\"Table C. Summary of Payment Currencies\" names the tables below it"),
        ["S1109"] = (Verdict.Ambiguous, "TABLE_CAPTION", "\"Sample Schedule of Priced Activities Table\" names the one sample table below it"),
        ["S1125"] = (Verdict.Ambiguous, "TABLE_CAPTION", "\"Sample Schedule of Priced Sub-activities Table\" names the one sample table below it"),
    };

    [GeneratedRegex(@"^Sub-?Clause\s+[\d.\s]+(\([a-z]\))?(\s+\S.*)?$")] private static partial Regex ClauseHead();
    [GeneratedRegex(@"\b(is replaced|is deleted|are added|is amended)\b|:")] private static partial Regex ClauseBodyMarker();
    [GeneratedRegex(@"^Sub-?Clause\s+[\d.\s]+$")] private static partial Regex ClauseNumberOnly();

    [Fact]
    public void Audit()
    {
        var document = new OpenXmlDocumentSource().Read(TestRepository.Path(Source));
        var byId = document.Paragraphs.ToDictionary(p => p.SourceId, StringComparer.Ordinal);
        var aliases = SemanticSourceAliasCatalog.FromCatalog(DocumentSourceCatalogBuilder.FromSourceDocument(document)).ToArray();
        var index = aliases.Select((a, i) => (a.Alias, i)).ToDictionary(x => x.Alias, x => x.i, StringComparer.Ordinal);
        string TextOf(int i) => aliases[i].Text.Trim();
        SourceParagraph ParagraphOf(int i) => byId[aliases[i].SourceId];

        foreach (var (_, alias, text) in Sections) Assert.Equal(Squash(text), Squash(TextOf(index[alias])));
        var starts = Sections.Select(s => (s.Section, Start: index[s.Alias])).ToArray();
        string SectionOf(int i) => starts.Last(s => s.Start <= i).Section;

        var items = new List<Item>();
        var claimed = new HashSet<int>();
        void Add(Item item)
        {
            foreach (var a in item.Aliases) Assert.True(claimed.Add(index[a]), $"{a} decided twice");
            items.Add(item);
        }

        // 1. Named decisions, each checked against the text it must carry.
        foreach (var (named, text, verdict, reason, inOld) in Named)
        {
            var joined = Squash(string.Join(" ", named.Select(a => TextOf(index[a]))));
            Assert.True(joined.StartsWith(Squash(text), StringComparison.Ordinal), $"{named[0]}: expected '{text}', found '{joined}'");
            var category = verdict == Verdict.Remove ? "TABLE_LABEL" : "STRUCTURAL_HEADING";
            Add(new(SectionOf(index[named[0]]), named, text, verdict, category, "named", reason, inOld));
        }

        // 2. Template heading styles.
        for (var i = 0; i < aliases.Length; i++)
        {
            if (claimed.Contains(i)) continue;
            var p = ParagraphOf(i);
            if (p.InTableOfContents || !HeadingStyles.Contains(p.Style.StyleId ?? "")) continue;
            var section = SectionOf(i);
            var (verdict, category, reason) = StyledOverrides.TryGetValue(aliases[i].Alias, out var o)
                ? o
                : (Verdict.Keep, "STRUCTURAL_HEADING", $"template heading style {p.Style.StyleId}");
            if (section == "SECTION_IX" && ClauseNumberOnly().IsMatch(TextOf(i))) continue; // taken with its title below
            Add(new(section, [aliases[i].Alias], TextOf(i), verdict, category, $"style:{p.Style.StyleId}", reason,
                OldItemised.Contains(section) ? true : null));
        }

        // 3. Section IX clause heads in Part B: "Sub-Clause n" and its title, or both in one paragraph.
        var partB = index["S2251"];
        var appendix = index["S2823"];
        for (var i = partB + 1; i < appendix; i++)
        {
            var text = TextOf(i);
            if (claimed.Contains(i) || !ClauseHead().IsMatch(text) || ClauseBodyMarker().IsMatch(text) || text.Length > 80) continue;
            if (ClauseNumberOnly().IsMatch(text))
            {
                // The title is the next paragraph, and runs on through further Heading3 paragraphs
                // when it wraps (4.2.1: "Contractor's" / "obligations").
                var end = i + 2;
                while (end < appendix && ParagraphOf(end).Style.StyleId == "Heading3" && !ClauseHead().IsMatch(TextOf(end))
                    && !ClauseBodyMarker().IsMatch(TextOf(end))) end++; // not "The Sub-Clause is replaced with:"
                var span = Enumerable.Range(i, end - i).ToArray();
                Assert.DoesNotContain(span, claimed.Contains);
                Add(new("SECTION_IX", span.Select(k => aliases[k].Alias).ToArray(), string.Join(" ", span.Select(TextOf)), Verdict.Keep,
                    "STRUCTURAL_HEADING", "clause-head", "PC sub-clause number and title (consecutive paragraphs, one heading)", null));
            }
            else
            {
                Add(new("SECTION_IX", [aliases[i].Alias], text, Verdict.Keep, "STRUCTURAL_HEADING", "clause-head",
                    "PC sub-clause number and title in one paragraph", null));
            }
        }

        // 4. Every model proposal, placed in a category.
        using var run = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ModelRun)));
        var proposals = new Dictionary<string, (string[] Aliases, string Text, string Role)>(StringComparer.Ordinal);
        foreach (var call in run.RootElement.GetProperty("ledger").EnumerateArray())
        {
            using var response = JsonDocument.Parse(call.GetProperty("RawResponse").GetString()!);
            foreach (var h in response.RootElement.GetProperty("headings").EnumerateArray())
            {
                if (!h.TryGetProperty("isHeading", out var isHeading) || !isHeading.GetBoolean()) continue;
                // sourceAliases often spans a whole region (heading plus the content under it), so
                // the heading is the primary alias plus any listed alias carrying a verbatim part.
                var primary = h.GetProperty("sourceAlias").GetString()!;
                var parts = h.TryGetProperty("verbatimParts", out var vp) && vp.ValueKind == JsonValueKind.Array
                    ? vp.EnumerateArray().Select(x => Squash(x.GetString() ?? "")).Where(x => x.Length > 0).ToArray()
                    : [];
                var listed = h.TryGetProperty("sourceAliases", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(x => x.GetString()!).ToArray()
                    : [];
                var named = new[] { primary }
                    .Concat(listed.Where(a => a != primary && index.ContainsKey(a) && parts.Any(part => Squash(TextOf(index[a])).Contains(part, StringComparison.Ordinal))))
                    .Where(index.ContainsKey).Distinct().ToArray();
                if (named.Length == 0) continue;
                proposals.TryAdd(string.Join("+", named), (named, h.TryGetProperty("verbatimText", out var v) ? v.GetString() ?? "" : "",
                    h.TryGetProperty("semanticRole", out var r) ? r.GetString() ?? "" : ""));
            }
        }
        var itemByIndex = items.SelectMany(item => item.Aliases.Select(a => (index[a], item))).ToDictionary(x => x.Item1, x => x.item);
        var classified = proposals.Values.Select(p =>
        {
            var hit = p.Aliases.Select(a => itemByIndex.GetValueOrDefault(index[a])).FirstOrDefault(x => x is not null);
            var category = hit switch
            {
                { Verdict: Verdict.Keep } => "STRUCTURAL_HEADING",
                { Verdict: Verdict.Ambiguous } => hit.Category,
                { Verdict: Verdict.Remove } => hit.Category,
                _ => Classify(ParagraphOf(index[p.Aliases[0]]), TextOf(index[p.Aliases[0]])).Category,
            };
            var reason = hit?.Reason ?? Classify(ParagraphOf(index[p.Aliases[0]]), TextOf(index[p.Aliases[0]])).Reason;
            return new
            {
                aliases = p.Aliases,
                text = p.Text.Length > 0 ? p.Text : TextOf(index[p.Aliases[0]]),
                modelRole = p.Role,
                section = SectionOf(index[p.Aliases[0]]),
                category,
                reason,
                ambiguous = hit is { Verdict: Verdict.Ambiguous },
            };
        }).OrderBy(x => index[x.aliases[0]]).ToArray();

        bool Proposed(Item item) => item.Aliases.Any(a => proposals.Values.Any(p => p.Aliases.Contains(a)));

        var keep = items.Where(i => i.Verdict == Verdict.Keep).ToArray();
        var ambiguous = items.Where(i => i.Verdict == Verdict.Ambiguous).ToArray();
        var remove = items.Where(i => i.Verdict == Verdict.Remove).ToArray();
        var bySection = Sections.Select(s =>
        {
            var k = keep.Count(i => i.Section == s.Section);
            var a = ambiguous.Count(i => i.Section == s.Section);
            return new
            {
                section = s.Section,
                oldGold = OldBySection[s.Section],
                structuralKeep = k,
                ambiguous = a,
                removedFromOld = remove.Count(i => i.Section == s.Section && i.InOld == true),
                oldItemised = OldItemised.Contains(s.Section),
                reconciliation = k + a == OldBySection[s.Section] ? "keep+ambiguous = old"
                    : k == OldBySection[s.Section] ? "keep = old"
                    : $"keep {k}, ambiguous {a}, old {OldBySection[s.Section]}",
            };
        }).ToArray();

        var keepFromOld = keep.Count(i => i.InOld == true);
        var removeFromOld = items.Count(i => i.InOld == true && i.Verdict == Verdict.Remove);
        var addToOld = keep.Count(i => i.InOld == false);
        var ambiguousOld = ambiguous.Count(i => i.InOld == true);

        FreezeArtifact.AssertJson("eval/a99-closed-loop/policy-audit", "DOC-0123.structural-audit.v1.json", new
        {
            artifactKind = "a99_structural_heading_audit",
            documentId = "DOC-0123",
            policy = FinancialProcurementHeadingPolicyTests.PolicyId,
            source = new { path = Source, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(Source)) },
            modelEvidence = new { path = ModelRun, sha256 = CanonicalArtifactHash.OfTextFile(TestRepository.Path(ModelRun)) },
            modelCalls = 0,
            goldWritten = false,
            summary = new
            {
                OLD_GOLD_TOTAL = OldBySection.Values.Sum(),
                STRUCTURAL_REVIEW_TOTAL = keep.Length,
                KEEP_FROM_OLD = keepFromOld,
                REMOVE_FROM_OLD = removeFromOld,
                ADD_TO_OLD = addToOld,
                AMBIGUOUS = ambiguous.Length,
                AMBIGUOUS_OF_OLD = ambiguousOld,
                FINAL_CANDIDATE_TOTAL = keep.Length,
                FINAL_CANDIDATE_RANGE = new { min = keep.Length, max = keep.Length + ambiguous.Length },
                USER_APPROVAL_REQUIRED = true,
                oldItemisedSections = OldItemised.Order(StringComparer.Ordinal).ToArray(),
                note = "KEEP/REMOVE/ADD_FROM_OLD and AMBIGUOUS_OF_OLD count items known to be in the old Gold: every item of the itemised sections, plus old items named in its recorded vnext delta (Section X). FRONT_MATTER and SECTION_IX compare by section count only (bySection).",
            },
            bySection,
            axesOntology = OccurrenceSemanticAxesTests.OntologyId,
            roleByVerdict = items.GroupBy(i => AxesOf(i).OccurrenceRole).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new
            {
                keep = g.Count(i => i.Verdict == Verdict.Keep),
                ambiguous = g.Count(i => i.Verdict == Verdict.Ambiguous),
                remove = g.Count(i => i.Verdict == Verdict.Remove),
            }),
            modelCrossCheck = new
            {
                distinctProposals = classified.Length,
                byCategory = FinancialProcurementHeadingPolicyTests.AuditCategories.ToDictionary(c => c, c => classified.Count(x => x.category == c)),
                ambiguousProposed = classified.Count(x => x.ambiguous),
                structuralKeepProposedByModel = keep.Count(Proposed),
                structuralKeepMissedByModel = keep.Where(i => !Proposed(i)).Select(i => new { i.Section, alias = i.Aliases[0], i.Text }).ToArray(),
            },
            removals = remove.Select(i => new { i.Section, i.Aliases, i.Text, i.Category, i.Reason, i.InOld, axes = AxesOf(i) }).ToArray(),
            ambiguousItems = ambiguous.Select(i => new { i.Section, i.Aliases, i.Text, i.Category, i.Reason, i.InOld, axes = AxesOf(i) }).ToArray(),
            structuralReview = keep.Select(i => new { i.Section, i.Aliases, i.Text, i.Rule, i.Reason, i.InOld, axes = AxesOf(i) }).ToArray(),
            proposals = classified,
        });
    }

    /// <summary>
    /// An item on the OCCURRENCE_SEMANTIC_AXES_V1 axes. These describe the occurrence; they do not
    /// decide isHeading, which stays the item's verdict (and, for the ambiguous ones, the user's call).
    /// </summary>
    private sealed record Axes(string[] SemanticFunctions, string PrimaryFunction, string Scope, string OccurrenceRole, string? InformationType = null);

    private static readonly Dictionary<string, Axes> AxesByAlias = new(StringComparer.Ordinal)
    {
        // Titles of the document and of the documents embedded in it.
        ["S0001"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REGION_OPENER"),
        ["S0042"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REGION_OPENER"),
        ["S0104"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REGION_OPENER"),
        ["S0072"] = new(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT", "REGION_OPENER"),
        // Navigation regions: the opener identifies the table of contents / forms.
        ["S0117"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", "REGION_OPENER"),
        ["S0133"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", "REGION_OPENER"),
        ["S0759"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", "REGION_OPENER"),
        ["S0876"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", "REGION_OPENER"),
        ["S1740"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", "REGION_OPENER"),
        ["S2954"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", "REGION_OPENER"),
        // Parts.
        ["S0048"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S0061"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S0065"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S2010"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S2251"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S2846"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S2866"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S2921"] = new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER"),
        ["S2823"] = new(["IDENTITY", "STRUCTURE"], "IDENTITY", "EMBEDDED_ARTIFACT", "REGION_OPENER"),
        // Ambiguous and removed items - each described, membership left to the verdict.
        ["S0012"] = new(["INFORMATION"], "INFORMATION", "REVISION_ENTRY", "REGION_OPENER", "TEMPORAL_METADATA"),
        ["S0014"] = new(["INFORMATION"], "INFORMATION", "REVISION_ENTRY", "REGION_OPENER", "TEMPORAL_METADATA"),
        ["S0019"] = new(["INFORMATION"], "INFORMATION", "REVISION_ENTRY", "REGION_OPENER", "TEMPORAL_METADATA"),
        ["S0021"] = new(["INFORMATION"], "INFORMATION", "REVISION_ENTRY", "REGION_OPENER", "TEMPORAL_METADATA"),
        ["S0073"] = new(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT", "SUBTITLE"),
        ["S1341"] = new(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT", "REGION_OPENER"),
        ["S1389"] = new(["STRUCTURE"], "STRUCTURE", "LIST", "LOCAL_LABEL"),
        ["S1962"] = new(["IDENTITY"], "IDENTITY", "TABLE", "CAPTION"),
        ["S2009"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REPEATED_TITLE"),
        ["S2845"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REPEATED_TITLE"),
        ["S2865"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REPEATED_TITLE"),
        ["S2920"] = new(["IDENTITY"], "IDENTITY", "DOCUMENT", "REPEATED_TITLE"),
        ["S2973"] = new(["IDENTITY"], "IDENTITY", "EMBEDDED_ARTIFACT", "REPEATED_TITLE"),
        ["S3170"] = new(["IDENTITY"], "IDENTITY", "FORM", "SUBTITLE"),
        ["S3184"] = new(["IDENTITY"], "IDENTITY", "FORM", "SUBTITLE"),
        ["S3201"] = new(["IDENTITY"], "IDENTITY", "FORM", "SUBTITLE"),
        ["S1027"] = new(["IDENTITY"], "IDENTITY", "TABLE", "CAPTION"),
        ["S1046"] = new(["IDENTITY"], "IDENTITY", "TABLE", "CAPTION"),
        ["S1067"] = new(["IDENTITY"], "IDENTITY", "TABLE", "CAPTION"),
        ["S1109"] = new(["IDENTITY"], "IDENTITY", "TABLE", "CAPTION"),
        ["S1125"] = new(["IDENTITY"], "IDENTITY", "TABLE", "CAPTION"),
        ["S1971"] = new(["STRUCTURE"], "STRUCTURE", "TABLE", "LOCAL_LABEL"),
        ["S1977"] = new(["STRUCTURE"], "STRUCTURE", "TABLE", "LOCAL_LABEL"),
    };

    private static readonly HashSet<string> FormStyles = ["SPDForms1", "SPDForm2", "S9Header", "SectionXHeading", "SectionVHeading2"];

    private static Axes AxesOf(Item item)
    {
        if (AxesByAlias.TryGetValue(item.Aliases[0], out var named)) return named;
        var style = item.Rule.StartsWith("style:", StringComparison.Ordinal) ? item.Rule["style:".Length..] : "";
        if (style is "Head0") return new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "DOCUMENT_PART", "REGION_OPENER");
        if (style is "Head11b" || item.Reason.StartsWith("summary section label", StringComparison.Ordinal))
            return new(["IDENTITY", "STRUCTURE"], "STRUCTURE", "SECTION", "REGION_OPENER");
        if (FormStyles.Contains(style)) return new(["IDENTITY", "STRUCTURE"], "IDENTITY", "FORM", "REGION_OPENER");
        if (item.Rule == "clause-head") return new(["STRUCTURE"], "STRUCTURE", "CLAUSE", "REGION_OPENER");
        return new(["STRUCTURE"], "STRUCTURE", "SECTION", "REGION_OPENER");
    }

    /// <summary>A proposal no structural rule claimed, placed by what the source shows about it.</summary>
    private static (string Category, string Reason) Classify(SourceParagraph p, string text)
    {
        var style = p.Style.StyleId ?? "";
        if (p.InTableOfContents || style.StartsWith("TOC", StringComparison.OrdinalIgnoreCase)) return ("NAVIGATION", "table-of-contents entry");
        if (style is "Header" or "Footer" && !p.Style.Bold) return ("RUNNING_HEADER_FOOTER", "page header/footer");
        if (style.Contains("footnote", StringComparison.OrdinalIgnoreCase) || text.StartsWith('*') || text.StartsWith('['))
            return ("FOOTNOTE_NOTE", "footnote or bracketed editorial note");
        if ((text.StartsWith('(') && text.EndsWith(')')) || text.StartsWith("INSTRUCTIONS", StringComparison.Ordinal))
            return ("FOOTNOTE_NOTE", "parenthetical or boxed instruction to the user of the template");
        if (Regex.IsMatch(text, @"^(Table|Figure)\b", RegexOptions.IgnoreCase)) return ("TABLE_CAPTION", "names the table or figure below it");
        if ((text.EndsWith(':') && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6) || Regex.IsMatch(text, @"^[A-Z][\w’'/ .()-]{0,40}:\s*(\[.*\])?$")
            || Regex.IsMatch(text, @"^[^:\[]{1,120}(:\s*)?\*?\[insert", RegexOptions.IgnoreCase))
            return ("FIELD_LABEL", "a field to fill in");
        if (Regex.IsMatch(text, @"^Form\s+[A-Z]{2,4}\b")) return ("OTHER_NON_HEADING", "form code line above the styled form title, which is the heading");
        if (p.Layout.TableDepth > 0) return ("TABLE_LABEL", "table cell label");
        return ("OTHER_NON_HEADING", "body text, date or qualifier line");
    }

    private static string Squash(string value) => Regex.Replace(value, @"\s+", " ").Replace(" -", "-").Replace("- ", "-").Trim();
}
