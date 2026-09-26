namespace DocxHeaderExtractor.Tests.GenericAudit.V1_4;

/// <summary>
/// One source part of a hypothesis: a whole occurrence (<see cref="Verbatim"/> null) or its exact
/// leading text - the label of a line whose remainder is body.
/// </summary>
internal sealed record HypothesisPart(string Alias, string? Verbatim);

/// <summary>A semantic hypothesis on OCCURRENCE_SEMANTIC_AXES_V3: no repeatStatus - a repeat is a relation between resolved claims.</summary>
internal sealed record SemanticHypothesis(
    HypothesisPart[] Parts,
    string Text,
    string[] SemanticFunctions,
    string? PrimaryFunction,
    string? Scope,
    string[] OccurrenceRoles,
    string TitleRelation,
    string? InformationType,
    string[] Evidence,
    string ProposedIsHeading,
    int Prominence);

/// <summary>
/// Layer B - GENERIC_AUDIT_ENGINE_V1.4: V1.3 (below, frozen at 81bcb33) with the generic gaps SRC-095's held-out residuals
/// classified as B_NEW (c587694), and nothing else:
/// 23. an assembled label is judged whole - a section number set apart from its title in its own segment is a part of
///     the label, so a shape test (figures only) reads the label's text, not its first part's - and so does the
///     region scan, where the next such row is a peer label, not a row of figures, and the furniture test, where
///     such a number at a page's foot is no page number;
/// 24. cross-reference lists - a line of references separated by semicolons ("Section 4.1, Paragraph 14; Table 4") is
///     no caption, no contents line (its last number is a label's, not a page's) and no label, even where a bold
///     reference begins it;
/// 25. contact blocks - a short unnumbered label set directly under a stronger label, over nothing but short lines
///     among them a contact line (e-mail, web address, phone), names the person or body of an address block: metadata.
/// <para>
/// V1.3: V1.2 (below) over PDF_SOURCE_FACTS_V3, with the generic gaps SRC-054's residuals classified as B_NEW
/// (96e9e5e), and nothing else:
/// 13. table-local labels - a row of dot leaders is a table row; a table or figure region ends only at prose or at a
///     margin label that leads into prose; a label whose region holds table rows and no prose labels that table;
/// 14. labels marked by layout alone - a short standalone line at the text margin, set at body size, between a
///     finished block and a line of prose, opens that prose;
/// 15. a parent before its child or its caption - a numbered caption does not end a region, and a label set as the
///     one before it (or differing only by slant) directly under it does not make it "open nothing";
/// 16. mixed glyph sizes - line sizes are PDF_SOURCE_FACTS_V3's dominant glyph size (small caps no longer split a title);
/// 17. period lines - "As of ...", "For the ... ended ...", dates joined by "and" are metadata like a date;
/// 18. centred wraps - two stacked centred lines of one setting on one axis are one title;
/// 19. one chunk, two texts - a title line that ends in a date contributes its head; a bold lead a column away from
///     the rest of its line is a label of its own, not a run-in lead;
/// 20. letterheads - a label set right of the page centre over short lines at its own left edge is an address block;
/// 21. a sentence goes on - a line that begins in lower case continues the line above;
/// 22. a parenthetical status after a title is part of the title (evidence layer).
/// </para>
/// <para>
/// No document, text, Gold count or page number is known to it; every constant is below.
/// </para>
/// <para>
/// V1.2, as frozen:
/// </para>
/// <para>
/// V1.1 (GenericAudit/V1_1/, frozen with the SRC-041 pre-registration) with the generic gaps its held-out
/// SRC-041 residuals classified as B (a757221), and nothing else:
/// 7. prominence overrides repetition - a line set larger than the body that recurs at the top of pages is
///    a continued title, not page furniture;
/// 8. a pointer points up - an occurrence is a contents pointer only to a later occurrence that is set more
///    prominently; an equally set later occurrence is a repeat (known gap B1);
/// 9. a column is text - the second segment of a two-segment row stands alone only where its column goes on
///    as prose, not where it is a panel of a chart beside the text;
/// 10. figure and table regions - the lines under a numbered caption belong to its figure or table until
///    prose, or a strongly set label at the text margin, resumes (chart labels, a caption's second line);
/// 11. deliberate title breaks - a line that is only a connective word ("and", "or"), or that ends in a
///    function word ("... SUBSCRIPTIONS TO"), does not end a title;
/// 12. contents lines are complete - a line whose row carries a page number neither wraps nor is a wrap.
/// V1.1 carried V1's cascade with the six generic gaps SRC029_BLIND_GENERALIZATION_AUDIT_V1 found:
/// 1. wrapped / multipart claim assembly - a label is followed down its own column, across the lines of
///    another column that interleave with it, while the next line of that column continues it;
/// 2. run-in heading span - the bold lead of a line whose remainder is body text (or a bracketed
///    instruction) is a part of its own, so a label is proposed without the body it shares a line with;
/// 3. a parent opener followed directly by its first child - a numbered structural label, or one
///    followed by the first item of a different enumeration, opens its region;
/// 4. multi-line instruction blocks - lines inside an open bracket, and runs of italic lines that
///    together make prose, are instructions, however each line looks alone;
/// 5. two-column and table topology - each segment of a two-segment row stands alone in its column, a
///    bare enumerator joins the title beside it, a colon-ended cell sharing its row is a field label;
/// 6. embedded-artifact boundary - a title at a page boundary whose region holds fill-in fields names
///    a form.
/// repeatStatus is not proposed (OCCURRENCE_SEMANTIC_AXES_V3). The engine knows no document: no
/// identifiers, paths, heading texts, Gold totals or page numbers; every constant is below.
/// </para>
/// </summary>
internal static class SemanticAuditEngine
{
    public const string EngineId = "GENERIC_AUDIT_ENGINE_V1.4";

    /// <summary>The engine's only numeric constants, relative and document-independent. V1's seven, V1.1's seven, V1.2's one, V1.3's four, V1.4's one.</summary>
    public static readonly IReadOnlyDictionary<string, double> Constants = new Dictionary<string, double>
    {
        ["largerThanBodyRatio"] = 1.08,
        ["bodySizeToleranceRatio"] = 0.97,
        ["shortLabelMaxWords"] = 20,
        ["compositeMaxParts"] = 6,
        ["furnitureMinPages"] = 3,
        ["furnitureMinBandShare"] = 0.8,
        ["navigationRunMin"] = 3,
        // V1.1
        ["lineGapMaxFontRatio"] = 1.8,
        ["sameSizeRatioTolerance"] = 0.02,
        ["bracketSpanMaxLines"] = 15,
        ["formMinFillInFields"] = 2,
        ["averageCharWidthFontRatio"] = 0.5,
        ["centredMarginToleranceRatio"] = 0.05,
        ["columnGutterFontRatio"] = 1.0,
        // V1.2
        ["figureRegionMaxLines"] = 60,
        // V1.3
        ["layoutLabelMaxWords"] = 8,
        ["proseMinWords"] = 8,
        ["leadColumnGapFontRatio"] = 3.0,
        ["centredWrapMinFillRatio"] = 0.75,
        // V1.4
        ["addressLineMaxWords"] = 8,
    };

    private const string True = "TRUE";
    private const string False = "FALSE";
    private const string Review = "NEEDS_REVIEW";

    /// <summary>An occurrence, or the lead of one.</summary>
    private sealed record Fragment(int Index, string? Lead);

    public static IReadOnlyList<SemanticHypothesis> Propose(SourceEvidenceProfile profile)
    {
        var occ = profile.Occurrences;
        var n = occ.Count;
        var prominence = occ.Select(o => Prominence(o, profile)).ToArray();
        var furniture = occ.Select(o => IsFurniture(o, profile)).ToArray();
        // Gap 13: a row of dot leaders runs to its figure - a table row (or a contents line, which navigation reads first).
        var tabular = occ.Select(o => o.TableDepth > 0 || o.RowHasFigures || (o.RowSegmentCount >= 3 && o.Media == "PDF")
            || LexicalShape.HasDotLeaders(o.Text)).ToArray();
        var pageExtent = occ.Where(o => o.Left is not null && o.Right is not null).GroupBy(o => o.Page)
            .ToDictionary(g => g.Key, g => (Left: g.Min(o => o.Left!.Value), Right: g.Max(o => o.Right!.Value)));
        var (navigation, pointsUp) = NavigationRuns(occ, prominence, furniture, tabular);
        var (insideBracket, italicBlock) = InstructionBlocks(occ, profile, furniture, prominence);
        var firstOnPage = FirstContentOnPage(occ, furniture, profile);
        var figureInternal = FigureRegions(occ, profile, prominence, furniture, pointsUp);
        var layoutLabel = occ.Select((_, k) => LayoutMarkedLabel(occ, profile, k, furniture, figureInternal, tabular, pageExtent)).ToArray();
        var columnGoesOnAsProse = occ.Select((_, k) => ColumnGoesOnAsProse(occ, k)).ToArray();
        var consumed = new bool[n];
        var result = new List<SemanticHypothesis>();

        SourceOccurrence Occ(Fragment f) => f.Lead is null ? occ[f.Index] : occ[f.Index] with { Text = f.Lead, Bold = true, BoldLead = null };
        (int Strength, int Rank) Prom(Fragment f) => f.Lead is null ? prominence[f.Index] : Prominence(Occ(f), profile);

        for (var i = 0; i < n; i++)
        {
            if (consumed[i]) continue;
            var start = StartFragment(occ[i], i, prominence[i]);
            var parts = Assemble(occ, profile, start, Occ, Prom, prominence, furniture, insideBracket, italicBlock);
            foreach (var p in parts) consumed[p.Index] = true;

            var o = Occ(parts[0]);
            var evidence = new List<string>();
            var (strength, rank) = Prom(parts[0]);
            if (strength > 0) evidence.Add(strength == 2 ? "set apart strongly from the body text" : "set apart weakly from the body text");
            var text = string.Join(" ", parts.Select(p => Occ(p).Text));
            if (parts.Count > 1) evidence.Add($"one label over {parts.Count} source occurrences");
            // Gap 19: a lead set a column's width away from the rest of its line only shares an extraction chunk with it.
            var runIn = parts.Any(p => p.Lead is not null && !LeadStandsApart(occ, p.Index));
            if (runIn) evidence.Add("the label is the lead of a line that continues as body text");

            void Emit(string proposal, string[] functions, string? primary, string? scope, string[] roles,
                string titleRelation, string? information = null)
            {
                result.Add(new SemanticHypothesis(
                    parts.Select(p => new HypothesisPart(occ[p.Index].Alias, p.Lead)).ToArray(), text, functions, primary, scope, roles,
                    titleRelation, information, evidence.ToArray(), proposal, rank));
            }

            var last = parts[^1].Index;
            // The rest of a lead's line is body: the region after a run-in label begins on that line.
            var region = Region(occ, prominence, furniture, navigation, tabular, last, rank, Occ(parts[^1]), pointsUp);
            var standalone = o.RowSegmentCount == 1 || (o.RowSegmentIndex == 0 && !o.RowHasFigures)
                || (o.RowSegmentCount == 2 && !o.RowHasFigures && columnGoesOnAsProse[i])
                || (parts[0].Lead is not null && !runIn);
            var lastText = Occ(parts[^1]).Text;
            var shortLabel = Occ(parts[^1]).WordCount <= Constants["shortLabelMaxWords"]
                && !LexicalShape.EndsSentence(lastText) && !lastText.TrimEnd().EndsWith(';');
            if (region.Body > 0) evidence.Add($"followed by {region.Body} body line(s) before a peer");
            if (region.Headings > 0) evidence.Add($"followed by {region.Headings} lesser label(s) before a peer");
            if (region.Tabular > 0) evidence.Add($"followed by {region.Tabular} table/figure item(s) before a peer");

            if (furniture[i])
            {
                evidence.Add("repeats in the page's top/bottom band across pages, or is a page number there");
                Emit(False, ["INFORMATION"], "INFORMATION", "DOCUMENT", ["PAGE_FURNITURE"], "NONE");
            }
            else if (navigation[i] == Nav.Entry)
            {
                evidence.Add("contents-list entry: its text reappears elsewhere as the occurrence it points to");
                Emit(False, ["STRUCTURE"], "STRUCTURE", "TOC", ["NAVIGATION"], "NONE");
            }
            else if (figureInternal[i])
            {
                evidence.Add("under a numbered caption, inside its figure or table: prose has not resumed");
                Emit(False, ["INFORMATION"], "INFORMATION", "TABLE", ["LOCAL_LABEL"], "NONE");
            }
            else if (LexicalShape.IsReferenceList(occ[i].Text))
            {
                // Gap 24: the whole line is a list of cross-references; a bold reference that begins it is emphasis.
                evidence.Add("its line is a list of cross-references separated by semicolons: locators, not a label");
                Emit(False, ["INFORMATION"], "INFORMATION", "DOCUMENT", ["BODY_CONTENT"], "NONE");
            }
            else if (insideBracket[i] || (italicBlock[i] && parts[0].Lead is null))
            {
                evidence.Add(insideBracket[i] ? "inside a bracketed instruction that spans several lines" : "one line of a multi-line italic block that reads as prose");
                Emit(False, ["INFORMATION"], "INFORMATION", "NOTE", ["BODY_CONTENT"], "NONE");
            }
            // Gap 23: the assembled label's text - a section number in its own segment is only a part of it.
            else if (LexicalShape.IsFiguresOnly(text))
                Emit(False, ["INFORMATION"], "INFORMATION", tabular[i] ? "TABLE" : "DOCUMENT", ["BODY_CONTENT"], "NONE", "QUANTITY");
            else if (LexicalShape.IsNumberedCaption(text))
            {
                evidence.Add("numbered caption shape");
                if (region.FirstIsTabular || region.Body == 0) Emit(False, ["IDENTITY"], "IDENTITY", "TABLE", ["CAPTION"], "TITLE");
                else
                {
                    evidence.Add("but prose, not a table or figure, follows it");
                    Emit(Review, ["IDENTITY"], "IDENTITY", "TABLE", ["CAPTION", "REGION_OPENER"], "TITLE");
                }
            }
            else if (LexicalShape.IsFillInField(text))
            {
                evidence.Add("fill-in placeholder");
                Emit(False, ["INFORMATION"], "INFORMATION", "FORM_FIELD", ["FIELD_LABEL"], "NONE");
            }
            else if (LexicalShape.IsBracketedNote(text))
            {
                evidence.Add("whole occurrence in brackets: an editorial note");
                Emit(False, ["INFORMATION"], "INFORMATION", "NOTE", ["BODY_CONTENT"], "NONE");
            }
            else if (LexicalShape.IsParentheticalStatus(text))
            {
                evidence.Add("parenthetical status shape");
                Emit(False, ["INFORMATION"], "INFORMATION", "DOCUMENT", ["METADATA"], "NONE", "STATUS_METADATA");
            }
            else if (LexicalShape.IsFootnoteLead(o.Text) && (strength == 0 || Smaller(o, profile)) && !LexicalShape.IsStructuralLabel(o.Text))
            {
                evidence.Add("lettered or starred note lead");
                Emit(False, ["INFORMATION"], "INFORMATION", "NOTE", ["BODY_CONTENT"], "NONE");
            }
            else if (LexicalShape.IsPeriodLine(text))
            {
                // Gap 17: a period line ("As of ...", "For the ... ended ...", dates joined by "and") is a date's kind.
                evidence.Add(LexicalShape.IsDate(text) ? "date shape" : "period-line shape: states a reporting period or dates");
                if (region.Body > 0 && standalone)
                {
                    evidence.Add("stands alone over its own prose");
                    Emit(Review, ["INFORMATION", "STRUCTURE"], "INFORMATION", "REVISION_ENTRY", ["REGION_OPENER"], "NONE", "TEMPORAL_METADATA");
                }
                else Emit(False, ["INFORMATION"], "INFORMATION", "DOCUMENT", ["METADATA"], "NONE", "TEMPORAL_METADATA");
            }
            else if (navigation[i] == Nav.Opener && standalone && strength > 0 && !runIn)
            {
                evidence.Add("opens a run of contents entries");
                Emit(True, ["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", ["REGION_OPENER"], "TITLE");
            }
            else if (parts.Count == 1 && o.RowSegmentCount >= 2 && LexicalShape.EndsWithColon(text) && strength > 0)
            {
                evidence.Add("a colon-ended label sharing its row with other cells: a field label");
                Emit(False, ["INFORMATION"], "INFORMATION", "FORM_FIELD", ["FIELD_LABEL"], "NONE");
            }
            else if (runIn && parts.Count == 1 && !LexicalShape.IsStructuralLabel(text))
            {
                if (LexicalShape.RemainderIsBracketed(occ[i].Text, text))
                {
                    evidence.Add("an unnumbered lead followed by a bracketed instruction: a label or a reference, for review");
                    Emit(Review, ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER", "LOCAL_LABEL"], "TITLE");
                }
                else
                {
                    evidence.Add("an unnumbered bold lead inside its line, with no instruction after it: emphasis, not a label");
                    Emit(False, ["INFORMATION"], "INFORMATION", "DOCUMENT", ["BODY_CONTENT"], "NONE");
                }
            }
            else if (strength == 0 && layoutLabel[i] && parts.Count == 1 && !runIn && region.FillIns == 0)
            {
                // Gap 14: no glyph fact sets it apart; its place does - between a finished block and its own prose.
                evidence.Add("a short standalone line at the text margin, set at body size, between a finished block and a line of prose: a label marked by layout alone");
                Emit(True, ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE");
            }
            else if (strength == 0 || !standalone || !shortLabel)
                Emit(False, ["INFORMATION"], "INFORMATION", tabular[i] ? "TABLE" : "DOCUMENT", [tabular[i] ? "LOCAL_LABEL" : "BODY_CONTENT"], "NONE");
            else if (o.Media == "PDF" && !tabular[i] && !Larger(o, profile) && region.FirstIsRow && region.Sentences == 0
                     && region.Body <= region.Tabular && region.Tabular > 0 && !LexicalShape.IsStructuralLabel(text)
                     && !LexicalShape.IsNumberedCaption(text))
            {
                // Gap 13: every line under it, to the next peer, is a table row: it labels the table, not a region.
                evidence.Add($"followed by table rows ({region.Tabular}) and no running sentence before a peer: a label local to its table");
                Emit(False, ["INFORMATION"], "INFORMATION", "TABLE", ["LOCAL_LABEL"], "NONE");
            }
            else if (IsLetterhead(occ, i, pageExtent))
            {
                // Gap 20: set right of the page centre over short lines at its own left edge: a letterhead or address.
                evidence.Add("set right of the page centre over short lines at its own left edge: a letterhead or address block");
                Emit(False, ["INFORMATION"], "INFORMATION", "DOCUMENT", ["METADATA"], "NONE", "AUTHORSHIP_METADATA");
            }
            else if (ContactBlock(occ, prominence, furniture, i, last, rank, text))
            {
                // Gap 25: the name over an address block, directly under the block's own label.
                evidence.Add("directly under a stronger label, over nothing but short lines among them a contact line: the name of an address block");
                Emit(False, ["INFORMATION"], "INFORMATION", "SECTION", ["METADATA"], "NONE", "AUTHORSHIP_METADATA");
            }
            else if (tabular[i] && o.DeclaredHeadingLevel is null && !LexicalShape.IsStructuralLabel(text))
            {
                evidence.Add("inside a table or a row of figures");
                if (region.Body > 0 && region.Body >= region.Tabular && strength == 2)
                {
                    evidence.Add("yet opens prose, not table rows");
                    Emit(Review, ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER", "LOCAL_LABEL"], "TITLE");
                }
                else Emit(False, ["INFORMATION"], "INFORMATION", "TABLE", ["LOCAL_LABEL"], "NONE");
            }
            else if (!region.Opens && !(strength == 2 && OpensDespiteNextLabel(text, last)))
            {
                evidence.Add("a peer or stronger label follows directly: opens nothing");
                Emit(Review, ["STRUCTURE"], "STRUCTURE", "SECTION", ["LOCAL_LABEL"], "TITLE");
            }
            else if (LexicalShape.EndsWithColon(text) && o.DeclaredHeadingLevel is null)
            {
                evidence.Add("colon-ended label");
                Emit(Review, ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER", "FIELD_LABEL"], "TITLE");
            }
            else if (strength == 1)
                Emit(Review, ["STRUCTURE"], "STRUCTURE", "SECTION", ["REGION_OPENER"], "TITLE");
            else
            {
                var isDocumentTitle = rank >= prominence.Max(p => p.Rank) && i < n / 10;
                var structural = LexicalShape.IsStructuralLabel(text);
                var form = !isDocumentTitle && region.FillIns >= Constants["formMinFillInFields"]
                    && (firstOnPage[i] || o.PageBreakBefore);
                if (!region.Opens) evidence.Add(LexicalShape.IsNumberedStructuralLabel(text)
                    ? "a numbered structural label: it opens its region even where a stronger label follows"
                    : "followed directly by the first item of a different enumeration: its first sub-label");
                if (structural) evidence.Add("numbered structural label shape");
                if (isDocumentTitle) evidence.Add("the document's most prominent setting, near its start");
                if (firstOnPage[i] || o.PageBreakBefore) evidence.Add("begins its page: an artifact boundary");
                if (region.FillIns > 0) evidence.Add($"its region holds {region.FillIns} fill-in field(s)");
                if (form) evidence.Add("a page-initial title over fill-in fields: the title of an embedded form");
                Emit(True,
                    isDocumentTitle ? ["IDENTITY"] : form ? ["IDENTITY", "STRUCTURE"] : structural ? ["STRUCTURE", "IDENTITY"] : ["STRUCTURE"],
                    isDocumentTitle || form ? "IDENTITY" : "STRUCTURE",
                    isDocumentTitle ? "DOCUMENT" : form ? "FORM" : "SECTION",
                    ["REGION_OPENER"], "TITLE");
            }
        }
        return result;

        // Gap 3: a numbered structural label opens its region; so does a label whose next label begins
        // a different enumeration at its first item.
        bool OpensDespiteNextLabel(string text, int last)
        {
            if (LexicalShape.IsNumberedStructuralLabel(text)) return true;
            for (var j = last + 1; j < n; j++)
            {
                if (furniture[j]) continue;
                var next = occ[j].Text;
                return prominence[j].Strength > 0 && LexicalShape.IsFirstEnumeratorItem(next)
                    && LexicalShape.EnumeratorFamilyOf(next) != LexicalShape.EnumeratorFamilyOf(text);
            }
            return false;
        }
    }

    /// <summary>
    /// Gap 2: where a label begins. A line whose bold lead is followed by text that is not bold starts
    /// with that lead; so does a strongly set line that runs into a bracketed instruction.
    /// </summary>
    private static Fragment StartFragment(SourceOccurrence o, int index, (int Strength, int Rank) prominence)
    {
        if (o.BoldLead is { } lead && lead.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= Constants["shortLabelMaxWords"])
            return new Fragment(index, lead);
        if (prominence.Strength == 2 && LexicalShape.LabelBeforeBracket(o.Text) is { } head
            && head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= Constants["shortLabelMaxWords"])
            return new Fragment(index, head);
        return new Fragment(index, null);
    }

    /// <summary>
    /// Gaps 1 and 5: the source parts of one label. DOCX keeps V1's paragraph continuation. PDF joins a
    /// bare enumerator or bare structural label to the segment beside it, then follows the label down
    /// its own column - past lines of another column that interleave with it - while the next line of
    /// that column is set the same way, sits one line below, and does not start a label of its own.
    /// </summary>
    private static List<Fragment> Assemble(
        IReadOnlyList<SourceOccurrence> occ, SourceEvidenceProfile profile, Fragment start,
        Func<Fragment, SourceOccurrence> fragOcc, Func<Fragment, (int Strength, int Rank)> prom,
        (int Strength, int Rank)[] prominence, bool[] furniture, bool[] insideBracket, bool[] italicBlock)
    {
        var parts = new List<Fragment> { start };
        var n = occ.Count;
        var max = (int)Constants["compositeMaxParts"];

        if (profile.Media == "DOCX")
        {
            if (start.Lead is not null) return parts;
            while (parts.Count < max && parts[^1].Index + 1 < n && ContinuesDocx(occ, prominence, parts[^1].Index, parts[^1].Index + 1))
                parts.Add(new Fragment(parts[^1].Index + 1, null));
            return parts;
        }

        var head = occ[start.Index];
        if ((LexicalShape.IsBareEnumerator(head.Text) || LexicalShape.IsBareStructuralLabel(head.Text)) && start.Index + 1 < n)
        {
            var beside = occ[start.Index + 1];
            if (beside.Page == head.Page && beside.Row == head.Row && beside.RowSegmentIndex == head.RowSegmentIndex + 1
                && !furniture[start.Index + 1] && !LexicalShape.IsStructuralLabel(beside.Text) && !LexicalShape.IsFiguresOnly(beside.Text))
            {
                var f = beside.BoldLead is { } lead ? new Fragment(start.Index + 1, lead) : new Fragment(start.Index + 1, null);
                if (prom(f).Strength > 0) parts.Add(f);
            }
        }

        // A lead continues too: a margin label fused with a line of the body column still wraps onto
        // the next margin line. The next part needs a bold fragment of its own, so body lines never do.
        while (parts.Count < max)
        {
            var current = parts[^1];
            var j = NextInColumn(occ, current, fragOcc);
            if (j < 0 || furniture[j] || insideBracket[j] || italicBlock[j]) break;
            var candidate = occ[j];
            // Gap 19: a title line that runs on into a date ("... REPORTS JUNE 30, 2025") gives the title its head only.
            var next = candidate.BoldLead is { } lead ? new Fragment(j, lead)
                : LexicalShape.BeforeTrailingDate(candidate.Text) is { } beforeDate ? new Fragment(j, beforeDate)
                : new Fragment(j, null);
            if (!ContinuesInColumn(occ, fragOcc(current), prom(current), fragOcc(next), prom(next), current.Index, j)) break;
            parts.Add(next);
        }
        return parts;
    }

    /// <summary>The first later line on the same page, below this one, whose horizontal extent overlaps this fragment's.</summary>
    private static int NextInColumn(IReadOnlyList<SourceOccurrence> occ, Fragment f, Func<Fragment, SourceOccurrence> fragOcc)
    {
        var o = occ[f.Index];
        var (left, right) = Extent(o, fragOcc(f).Text);
        for (var j = f.Index + 1; j < occ.Count; j++)
        {
            var c = occ[j];
            if (c.Page != o.Page) return -1;
            if (c.Row <= o.Row || c.Left is not { } l || c.Right is not { } r) continue;
            if (Math.Max(left, l) < Math.Min(right, r)) return j;
        }
        return -1;
    }

    /// <summary>A fragment's horizontal extent: a lead's is its share of the line's width.</summary>
    private static (double Left, double Right) Extent(SourceOccurrence o, string fragmentText)
    {
        var left = o.Left ?? 0;
        var right = o.Right ?? 0;
        return fragmentText.Length >= o.Text.Length || o.Text.Length == 0
            ? (left, right)
            : (left, left + (right - left) * fragmentText.Length / o.Text.Length);
    }

    private static bool ContinuesInColumn(IReadOnlyList<SourceOccurrence> occ, SourceOccurrence x, (int Strength, int Rank) px,
        SourceOccurrence y, (int Strength, int Rank) py, int xi, int yi)
    {
        if (px.Strength == 0 || py.Strength == 0) return false;
        if (x.FontSize is not { } sx || y.FontSize is not { } sy) return false;
        var tolerance = sx * Constants["sameSizeRatioTolerance"];
        // A head cut from before a trailing date is set at the line's smaller size when the date is set larger.
        var headSize = y.Text.Length < occ[yi].Text.Trim().Length && y.MinFontSize is { } m ? m : sy;
        if (Math.Abs(sx - sy) > tolerance && Math.Abs(sx - headSize) > tolerance) return false;
        if (x.Bold != y.Bold || x.Italic != y.Italic) return false;
        if (occ[xi].Y is not { } yx || occ[yi].Y is not { } yy) return false;
        var gap = yx - yy;
        if (gap <= 0 || gap > sx * Constants["lineGapMaxFontRatio"]) return false;
        if ((LexicalShape.EndsSentence(x.Text) && !LexicalShape.IsBareEnumerator(x.Text)) || LexicalShape.EndsWithColon(x.Text)) return false;
        if (LexicalShape.IsPeriodLine(y.Text) || LexicalShape.IsParentheticalStatus(y.Text) || LexicalShape.IsFiguresOnly(y.Text)) return false;
        // Gap 21: a line that begins in lower case continues the sentence above, however long.
        if (LexicalShape.StartsLowerCase(y.Text) && !LexicalShape.EndsSentence(x.Text)) return true;
        if (LexicalShape.IsStructuralLabel(y.Text) || y.WordCount > Constants["shortLabelMaxWords"]) return false;
        // Gap 12: a line whose row carries a page number is a complete contents line.
        if (occ[xi].RowHasFigures || occ[yi].RowHasFigures) return false;
        // Gap 11: a connective-only line, or a line ending in a function word, is a deliberate break inside one title.
        if (LexicalShape.IsConnectiveLine(x.Text) || LexicalShape.IsConnectiveLine(y.Text) || LexicalShape.EndsWithFunctionWord(x.Text)) return true;
        // A label joined to the title under it ("Sub-Clause 4.2" / "Notice") needs no wrap.
        if (LexicalShape.IsBareStructuralLabel(x.Text) || LexicalShape.IsBareEnumerator(x.Text)) return true;
        // Gap 18: two stacked centred lines of one setting on one axis are one title, wherever the setter broke it.
        if (CentredPair(occ, xi, yi, sx)) return true;
        return WrapIsForced(occ, xi, x, y, sx);
    }

    /// <summary>Gap 18: both lines centred in their column, on the same axis.</summary>
    private static bool CentredPair(IReadOnlyList<SourceOccurrence> occ, int xi, int yi, double size)
    {
        var (a, b) = (occ[xi], occ[yi]);
        if (a.Left is not { } al || a.Right is not { } ar || b.Left is not { } bl || b.Right is not { } br) return false;
        var (columnLeft, columnRight, _) = ColumnBounds(occ, xi, size);
        var width = columnRight - columnLeft;
        if (width <= 0) return false;
        bool Centred(double l, double r) => Math.Abs((l - columnLeft) - (columnRight - r)) <= width * Constants["centredMarginToleranceRatio"]
            && l - columnLeft > width * Constants["centredMarginToleranceRatio"];
        // A setter wraps a centred title only when its first line nearly fills the column.
        return Centred(al, ar) && Centred(bl, br) && Math.Abs((al + ar) / 2 - (bl + br) / 2) <= width * Constants["centredMarginToleranceRatio"]
            && ar - al >= width * Constants["centredWrapMinFillRatio"];
    }

    /// <summary>
    /// A line wraps only when the next line's first word would not have fitted on it: past the column's
    /// right bound (the left edge of the nearest line of another column beside it, else the page's text
    /// extent); for a centred line, past the column's width. A line that stops short of its column is
    /// complete, and the line under it is a separate label however it is set.
    /// </summary>
    private static bool WrapIsForced(IReadOnlyList<SourceOccurrence> occ, int xi, SourceOccurrence x, SourceOccurrence y, double size)
    {
        var line = occ[xi];
        if (line.Left is not { } left || x.Right is not { } _) return true;
        var (_, right) = Extent(line, x.Text);
        var (columnLeft, columnRight, bounded) = ColumnBounds(occ, xi, size);
        // Beside another column the column's own right edge is not observable (the gutter varies with
        // the other column's indents): the fit cannot be judged, and the line is taken to wrap.
        if (bounded) return true;
        var word = y.Text.TrimStart().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var needed = (word.Length + 1) * size * Constants["averageCharWidthFontRatio"];
        var width = columnRight - columnLeft;
        var centred = Math.Abs((left - columnLeft) - (columnRight - right)) <= width * Constants["centredMarginToleranceRatio"]
            && left - columnLeft > width * Constants["centredMarginToleranceRatio"];
        return centred ? right - left + needed > width : right + needed > columnRight;
    }

    /// <summary>The horizontal bounds of the column a line sits in: nearest lines of other columns beside it, else the page's text extent.</summary>
    private static (double Left, double Right, bool Bounded) ColumnBounds(IReadOnlyList<SourceOccurrence> occ, int xi, double size)
    {
        var x = occ[xi];
        var window = size * Constants["lineGapMaxFontRatio"];
        double pageLeft = double.MaxValue, pageRight = double.MinValue, besideLeft = double.MaxValue, besideRight = double.MinValue;
        for (var j = xi; j >= 0 && occ[j].Page == x.Page; j--) Visit(j);
        for (var j = xi + 1; j < occ.Count && occ[j].Page == x.Page; j++) Visit(j);
        // A column ends a gutter before the column beside it; the page's text extent is its own bound.
        var gutter = size * Constants["columnGutterFontRatio"];
        return (besideRight > double.MinValue ? besideRight + gutter : pageLeft,
            besideLeft < double.MaxValue ? besideLeft - gutter : pageRight,
            besideLeft < double.MaxValue || besideRight > double.MinValue);

        void Visit(int j)
        {
            var o = occ[j];
            if (o.Left is not { } l || o.Right is not { } r || o.Y is not { } y || x.Y is not { } xy) return;
            pageLeft = Math.Min(pageLeft, l);
            pageRight = Math.Max(pageRight, r);
            if (j == xi || Math.Abs(y - xy) > window) return;
            if (l > x.Right) besideLeft = Math.Min(besideLeft, l);
            if (r < x.Left) besideRight = Math.Max(besideRight, r);
        }
    }

    /// <summary>V1's continuation for paragraphs (DOCX), unchanged.</summary>
    private static bool ContinuesDocx(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, int a, int b)
    {
        var x = occ[a];
        var y = occ[b];
        if (x.Page != y.Page || prominence[a].Strength == 0 || prominence[b].Strength == 0) return false;
        if (LexicalShape.IsDate(y.Text) || LexicalShape.IsParentheticalStatus(y.Text) || LexicalShape.IsFiguresOnly(y.Text)) return false;
        if (LexicalShape.EndsSentence(x.Text) || y.WordCount > Constants["shortLabelMaxWords"]) return false;
        var sameSetting = x.FontSize == y.FontSize && x.Bold == y.Bold && x.Italic == y.Italic && x.StyleName == y.StyleName;
        if (LexicalShape.IsBareStructuralLabel(x.Text) && !LexicalShape.IsStructuralLabel(y.Text)) return true;
        if (!sameSetting || LexicalShape.IsStructuralLabel(y.Text)) return false;
        if (x.Centered && y.Centered) return true;
        var wrapped = char.IsLower(y.Text.TrimStart().FirstOrDefault()) || x.Text.TrimEnd().EndsWith(',') || x.Text.TrimEnd().EndsWith('-');
        return wrapped;
    }

    /// <summary>
    /// Gap 4: lines inside a bracket opened on an earlier line and not yet closed (an unbalanced bracket
    /// stops counting after a bounded span), and runs of consecutive italic lines, in a document whose
    /// body is not italic, that together are longer than a label. A strongly set line inside either is
    /// a label within the instructions, not a line of them, and is not marked.
    /// </summary>
    private static (bool[] InsideBracket, bool[] ItalicBlock) InstructionBlocks(
        IReadOnlyList<SourceOccurrence> occ, SourceEvidenceProfile profile, bool[] furniture, (int Strength, int Rank)[] prominence)
    {
        var n = occ.Count;
        var inside = new bool[n];
        var depth = 0;
        var span = 0;
        for (var i = 0; i < n; i++)
        {
            if (furniture[i]) continue;
            var (open, close) = LexicalShape.Brackets(occ[i].Text);
            inside[i] = prominence[i].Strength < 2 && (depth > 0 || (occ[i].Text.TrimStart().StartsWith('[') && open > close));
            depth = Math.Max(0, depth + open - close);
            span = depth > 0 ? span + 1 : 0;
            if (span > Constants["bracketSpanMaxLines"]) { depth = 0; span = 0; }
        }

        var italic = new bool[n];
        if (!profile.BodyItalic)
        {
            var k = 0;
            while (k < n)
            {
                if (!occ[k].Italic || furniture[k]) { k++; continue; }
                var end = k;
                while (end + 1 < n && (occ[end + 1].Italic || furniture[end + 1])) end++;
                var members = Enumerable.Range(k, end - k + 1).Where(x => !furniture[x]).ToArray();
                if (members.Length >= 2 && members.Sum(x => occ[x].WordCount) > Constants["shortLabelMaxWords"])
                    foreach (var x in members) italic[x] = prominence[x].Strength < 2;
                k = end + 1;
            }
        }
        return (inside, italic);
    }

    /// <summary>
    /// Gap 6: the first occurrence on its page that is neither page furniture nor a line whose text
    /// recurs on several pages (a running header the band test missed) - an artifact boundary (PDF).
    /// </summary>
    private static bool[] FirstContentOnPage(IReadOnlyList<SourceOccurrence> occ, bool[] furniture, SourceEvidenceProfile profile)
    {
        var first = new bool[occ.Count];
        int? page = null;
        var seen = false;
        for (var i = 0; i < occ.Count; i++)
        {
            if (occ[i].Page != page) { page = occ[i].Page; seen = false; }
            if (occ[i].Page is null || furniture[i] || seen) continue;
            if (profile.Repetition[LexicalShape.RepetitionKey(occ[i].Text)].Pages >= Constants["furnitureMinPages"]) continue;
            first[i] = true;
            seen = true;
        }
        return first;
    }

    /// <summary>
    /// Gap 10: the lines under a numbered caption (PDF), on its page, until a line of prose or a strongly set
    /// standalone label at the page's text margin - the figure or table the caption names.
    /// </summary>
    private static bool[] FigureRegions(IReadOnlyList<SourceOccurrence> occ, SourceEvidenceProfile profile,
        (int Strength, int Rank)[] prominence, bool[] furniture, bool[] pointsUp)
    {
        var inside = new bool[occ.Count];
        if (profile.Media != "PDF") return inside;
        var margin = occ.Where((o, k) => !furniture[k] && o.Left is not null).GroupBy(o => o.Page)
            .ToDictionary(g => g.Key, g => g.Min(o => o.Left!.Value));
        for (var k = 0; k < occ.Count; k++)
        {
            if (furniture[k] || !LexicalShape.IsNumberedCaption(occ[k].Text)) continue;
            // Gap 13: a caption with dot leaders or a page number is a contents entry pointing at a caption, not one.
            if (LexicalShape.HasDotLeaders(occ[k].Text) || ContentsLike(occ, k) || pointsUp[k]) continue;
            var caption = occ[k];
            for (var j = k + 1; j < occ.Count && j - k <= Constants["figureRegionMaxLines"] && occ[j].Page == occ[k].Page; j++)
            {
                var o = occ[j];
                if (furniture[j]) continue;
                if (LexicalShape.IsNumberedCaption(o.Text)) continue; // a caption beside it: a second figure
                // The line right under the caption, set as the caption is, is the caption's own second line.
                if (j == k + 1 && o.FontSize == caption.FontSize && o.Bold == caption.Bold && o.Y is { } y && caption.Y is { } cy
                    && caption.FontSize is { } cs && cy - y > 0 && cy - y <= cs * Constants["lineGapMaxFontRatio"])
                {
                    inside[j] = true;
                    continue;
                }
                var prose = IsProse(o);
                var atMargin = o.Left is { } l && margin.TryGetValue(o.Page, out var m) && o.FontSize is { } size
                    && l - m <= size * Constants["columnGutterFontRatio"];
                // Gap 13: a strongly set margin label ends the region only where prose follows it; a bold row label does not.
                if (prose || (atMargin && o.RowSegmentCount == 1 && prominence[j].Strength == 2
                    && (LeadsToProse(occ, j, furniture) || NextIsCaption(occ, j, furniture)))) break;
                inside[j] = true;
            }
        }
        return inside;
    }

    /// <summary>
    /// Gap 9: whether the lines below an occurrence in its own column include prose (within three lines). A line
    /// counts only if it starts inside the column: a full-width line further down is not this column's text.
    /// </summary>
    private static bool ColumnGoesOnAsProse(IReadOnlyList<SourceOccurrence> occ, int k)
    {
        var o = occ[k];
        if (o.Left is not { } left || o.FontSize is not { } size) return false;
        var current = new Fragment(k, null);
        for (var step = 0; step < 3; step++)
        {
            var j = NextInColumn(occ, current, f => occ[f.Index]);
            if (j < 0 || occ[j].Left is not { } l || l < left - size * Constants["columnGutterFontRatio"]) return false;
            if (occ[j].WordCount >= 8 || LexicalShape.EndsSentence(occ[j].Text)) return true;
            current = new Fragment(j, null);
        }
        return false;
    }

    /// <summary>V1.3: a line of running text - a whole row of enough words, not a row of dot leaders.</summary>
    private static bool IsProse(SourceOccurrence o) =>
        o.WordCount >= Constants["proseMinWords"] && o.RowSegmentCount == 1 && !LexicalShape.HasDotLeaders(o.Text);

    /// <summary>V1.3: within three lines, past labels, prose follows (the label opens running text, not table rows).</summary>
    private static bool LeadsToProse(IReadOnlyList<SourceOccurrence> occ, int j, bool[] furniture)
    {
        var seen = 0;
        for (var k = j + 1; k < occ.Count && seen < 3; k++)
        {
            if (furniture[k]) continue;
            seen++;
            if (occ[k].Page != occ[j].Page && seen > 1) return false;
            if (IsProse(occ[k])) return true;
            if (LexicalShape.HasDotLeaders(occ[k].Text) || occ[k].RowHasFigures || occ[k].RowSegmentCount >= 3) return false;
        }
        return false;
    }

    private static bool SameSetting(SourceOccurrence a, SourceOccurrence b) =>
        a.FontSize is { } x && b.FontSize is { } y && Math.Abs(x - y) <= x * Constants["sameSizeRatioTolerance"]
        && a.Bold == b.Bold && a.Italic == b.Italic;

    private static bool SlantOnly(SourceOccurrence child, SourceOccurrence parent) =>
        child.FontSize is { } x && parent.FontSize is { } y && Math.Abs(x - y) <= x * Constants["sameSizeRatioTolerance"]
        && child.Bold == parent.Bold && child.Italic && !parent.Italic;

    /// <summary>
    /// Gap 19: the lead is a column's width away from the rest of its line, and its own column goes on as prose - a
    /// label beside another column's text. A lead whose column holds no prose below it is the first cell of a table row.
    /// </summary>
    private static bool LeadStandsApart(IReadOnlyList<SourceOccurrence> occ, int k)
    {
        var o = occ[k];
        if (o.LeadGap is not { } gap || o.FontSize is not { } size || gap < size * Constants["leadColumnGapFontRatio"] || o.BoldLead is not { } lead) return false;
        var next = NextInColumn(occ, new Fragment(k, lead), f => occ[f.Index] with { Text = f.Lead ?? occ[f.Index].Text });
        if (next < 0 || occ[next].Left is not { } l || occ[next].Right is not { } r || o.Left is not { } left) return false;
        // The line below stays in the lead's column - it ends before the text the lead shares its line with begins.
        var (_, leadRight) = Extent(o, lead);
        return Math.Abs(l - left) <= size && r <= leadRight + gap && occ[next].BoldLead is null
            && occ[next].WordCount >= Constants["proseMinWords"] / 2 && !LexicalShape.HasDotLeaders(occ[next].Text);
    }

    /// <summary>A contents line: its only figure is a page number, at the end of its text or alone in the row's last segment.</summary>
    private static bool ContentsLike(IReadOnlyList<SourceOccurrence> occ, int j)
    {
        var o = occ[j];
        // A trailing number is a page number, not the year that ends a date ("... as of March 31, 2025").
        if (LexicalShape.HasTrailingPageNumber(o.Text) && !o.RowHasFigures && LexicalShape.BeforeTrailingDate(o.Text) is null
            && int.TryParse(o.Text.TrimEnd().Split(' ')[^1].TrimStart('.'), out var number) && number < 1000) return true;
        if (!o.RowHasFigures || o.RowSegmentCount != 2) return false;
        var other = j + 1 < occ.Count && occ[j + 1].Row == o.Row && occ[j + 1].Page == o.Page ? occ[j + 1] : null;
        return other is not null && int.TryParse(other.Text.Trim(), out var page) && page is > 0 and < 10000;
    }

    /// <summary>The next line after it (past furniture) is a numbered caption.</summary>
    private static bool NextIsCaption(IReadOnlyList<SourceOccurrence> occ, int j, bool[] furniture)
    {
        for (var k = j + 1; k < occ.Count; k++)
            if (!furniture[k]) return LexicalShape.IsNumberedCaption(occ[k].Text);
        return false;
    }

    /// <summary>
    /// Gap 14: a short standalone line at the text margin (PDF), at body size, whose line above ends a block (a finished
    /// sentence, a short last line, a label, a page top) and whose next line in its column is prose that starts a sentence.
    /// </summary>
    private static bool LayoutMarkedLabel(IReadOnlyList<SourceOccurrence> occ, SourceEvidenceProfile profile, int k, bool[] furniture,
        bool[] figureInternal, bool[] tabular, IReadOnlyDictionary<int?, (double Left, double Right)> pageExtent)
    {
        var o = occ[k];
        if (o.Media != "PDF" || furniture[k] || figureInternal[k] || tabular[k] || o.RowSegmentCount != 1) return false;
        // A text that recurs on other pages is a running header or a repeated address line, not a label of this place.
        if (profile.Repetition[LexicalShape.RepetitionKey(o.Text)].Pages >= 2) return false;
        if (o.WordCount < 1 || o.WordCount > Constants["layoutLabelMaxWords"] || !char.IsUpper(o.Text.TrimStart().FirstOrDefault())) return false;
        if (LexicalShape.EndsSentence(o.Text) || o.Text.TrimEnd().EndsWith(',') || o.Text.TrimEnd().EndsWith(';')) return false;
        // A colon-ended line leads into what follows; a line ending in a function word stops mid-phrase.
        if (LexicalShape.EndsWithColon(o.Text) || LexicalShape.EndsWithFunctionWord(o.Text)) return false;
        if (LexicalShape.IsFiguresOnly(o.Text) || LexicalShape.IsPeriodLine(o.Text) || LexicalShape.IsNumberedCaption(o.Text)
            || LexicalShape.IsFootnoteLead(o.Text) || LexicalShape.IsStructuralLabel(o.Text)) return false;
        if (o.FontSize is not { } size || profile.BodyFontSize is not { } body || Math.Abs(size - body) > body * (1 - Constants["bodySizeToleranceRatio"])) return false;
        if (o.Left is not { } left || !pageExtent.TryGetValue(o.Page, out var extent) || left - extent.Left > size * Constants["columnGutterFontRatio"]) return false;
        if (o.Right is { } right && right > extent.Left + (extent.Right - extent.Left) * 0.6) return false; // a full-width line is text
        // The line above ends a block.
        var above = -1;
        for (var j = k - 1; j >= 0; j--) { if (!furniture[j]) { above = j; break; } }
        if (above >= 0 && occ[above].Page == o.Page)
        {
            var a = occ[above];
            var endsBlock = LexicalShape.EndsSentence(a.Text) || LexicalShape.EndsWithColon(a.Text)
                || (a.Right is { } ar && ar < extent.Left + (extent.Right - extent.Left) * 0.8);
            if (!endsBlock || LexicalShape.EndsWithFunctionWord(a.Text)) return false;
        }
        // The next line of its column is prose that starts a sentence.
        var next = NextInColumn(occ, new Fragment(k, null), f => occ[f.Index]);
        if (next < 0) return false;
        var n = occ[next];
        return IsProse(n) && char.IsUpper(n.Text.TrimStart().FirstOrDefault());
    }

    /// <summary>Gap 20: right of the page centre, over a short line that starts at the same left edge.</summary>
    private static bool IsLetterhead(IReadOnlyList<SourceOccurrence> occ, int k, IReadOnlyDictionary<int?, (double Left, double Right)> pageExtent)
    {
        var o = occ[k];
        if (o.Media != "PDF" || o.Left is not { } left || o.FontSize is not { } size || !pageExtent.TryGetValue(o.Page, out var extent)) return false;
        if (left <= (extent.Left + extent.Right) / 2) return false;
        var next = NextInColumn(occ, new Fragment(k, null), f => occ[f.Index]);
        if (next < 0) return false;
        var n = occ[next];
        return n.Left is { } nl && Math.Abs(nl - left) <= size * 0.5 && n.WordCount < Constants["proseMinWords"] && !LexicalShape.EndsSentence(n.Text);
    }

    /// <summary>V1.3: set larger than the body - a title's setting, never a table's row or group label.</summary>
    private static bool Larger(SourceOccurrence o, SourceEvidenceProfile p) =>
        o.FontSize is { } s && p.BodyFontSize is { } b && b > 0 && s / b >= Constants["largerThanBodyRatio"];

    private static bool Smaller(SourceOccurrence o, SourceEvidenceProfile p) =>
        o.FontSize is { } s && p.BodyFontSize is { } b && s < b * Constants["bodySizeToleranceRatio"];

    /// <summary>How far an occurrence is set apart from the body: strength 0/1/2 and a comparable rank.</summary>
    private static (int Strength, int Rank) Prominence(SourceOccurrence o, SourceEvidenceProfile p)
    {
        var ratio = o.FontSize is { } s && p.BodyFontSize is { } b && b > 0 ? s / b : 1.0;
        var strong = o.DeclaredHeadingLevel is not null
            || ratio >= Constants["largerThanBodyRatio"]
            || (o.Bold && !p.BodyBold && ratio >= Constants["bodySizeToleranceRatio"]);
        var weak = !strong && ((o.Bold && !p.BodyBold) || (o.Italic && !p.BodyItalic) || (o.AllCaps && o.WordCount <= 12) || o.Centered);
        var strength = strong ? 2 : weak ? 1 : 0;
        var rank = strength * 1000 + (int)Math.Round(ratio * 100) + (o.DeclaredHeadingLevel is { } l ? (10 - l) * 20 : 0)
            + (o.Bold ? 4 : 0) + (o.AllCaps ? 2 : 0) + (o.Italic ? 1 : 0);
        return (strength, rank);
    }

    private static bool IsFurniture(SourceOccurrence o, SourceEvidenceProfile p)
    {
        if (o.PageBand is not { } band) return false;
        var inBand = band <= 0.06 || band >= 0.94;
        // Gap 23: a numbered label's number, its title beside it in the row, is no page number.
        var labelNumber = o.RowSegmentCount == 2 && o.RowSegmentIndex == 0 && LexicalShape.IsBareEnumerator(o.Text) && o.Text.TrimEnd() is [.., '.' or ')'];
        if (inBand && LexicalShape.IsFiguresOnly(o.Text) && !labelNumber) return true;
        var r = p.Repetition[LexicalShape.RepetitionKey(o.Text)];
        // Gap 7: a line set larger than the body that recurs at page tops is a continued title, not furniture.
        var larger = o.FontSize is { } s && p.BodyFontSize is { } b && b > 0 && s / b >= Constants["largerThanBodyRatio"];
        return inBand && !larger && r.Pages >= Constants["furnitureMinPages"] && r.BandShare >= Constants["furnitureMinBandShare"];
    }

    /// <summary>
    /// Gap 25: a short unnumbered label directly under a stronger label, whose own region - to the next label - is only
    /// short lines, one of them a contact line: the name heading an address block, not a region of the document.
    /// </summary>
    private static bool ContactBlock(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture,
        int first, int last, int rank, string text)
    {
        if (LexicalShape.IsStructuralLabel(text) || LexicalShape.EndsSentence(text)) return false;
        var above = first - 1;
        while (above >= 0 && furniture[above]) above--;
        if (above < 0 || prominence[above].Strength == 0 || prominence[above].Rank <= rank) return false;
        int lines = 0, contacts = 0;
        for (var j = last + 1; j < occ.Count; j++)
        {
            if (furniture[j]) continue;
            if (prominence[j].Strength > 0) break;
            if (occ[j].WordCount > Constants["addressLineMaxWords"] || LexicalShape.EndsSentence(occ[j].Text)) return false;
            lines++;
            if (LexicalShape.IsContactLine(occ[j].Text)) contacts++;
        }
        return lines > 0 && contacts > 0;
    }

    /// <summary>
    /// Gap 23: a bare enumerator ("9.", "A.2.", "(b)") in the first of two segments of its row and the title in the second,
    /// joined - one label - or null.
    /// </summary>
    private static SourceOccurrence? SplitNumberedLabel(IReadOnlyList<SourceOccurrence> occ, int j)
    {
        var o = occ[j];
        if (!LexicalShape.IsBareEnumerator(o.Text) || o.RowSegmentCount != 2 || o.RowSegmentIndex != 0 || j + 1 >= occ.Count) return null;
        var title = occ[j + 1];
        return title.Page == o.Page && title.Row == o.Row && title.RowSegmentIndex == 1 ? o with { Text = o.Text + " " + title.Text } : null;
    }

    private enum Nav { None, Entry, Opener }

    /// <summary>
    /// V1's contents-list runs; V1.2 makes a pointer point to a more prominent occurrence only (gap 8). V1.3 also returns
    /// which occurrences point up, whether or not they form a run.
    /// </summary>
    private static (Nav[] Runs, bool[] PointsUp) NavigationRuns(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture, bool[] tabular)
    {
        var n = occ.Count;
        var target = new Dictionary<string, (int Index, int Rank)>(StringComparer.Ordinal);
        for (var k = n - 1; k >= 0; k--)
        {
            var key = LexicalShape.WithoutTrailingPageNumber(occ[k].Text);
            if (!tabular[k] && !furniture[k] && !target.ContainsKey(key)) target[key] = (k, prominence[k].Rank);
        }
        var index = 0;
        var indexOf = occ.ToDictionary(o => o, _ => index++);
        bool Points(SourceOccurrence o)
        {
            if (o.InTableOfContents) return true;
            if (LexicalShape.IsFiguresOnly(o.Text) || o.WordCount < 2) return false;
            // Gap 24: the last number of a list of cross-references is a label's number, not a page number.
            if (LexicalShape.IsReferenceList(o.Text)) return false;
            if (LexicalShape.HasTrailingPageNumber(o.Text)) return true;
            var i = indexOf[o];
            // Gap 8: a pointer points up; an equally set later occurrence is a repeat, not the target of a pointer.
            return target.TryGetValue(LexicalShape.WithoutTrailingPageNumber(o.Text), out var t) && t.Index > i && t.Rank > prominence[i].Rank;
        }
        var result = new Nav[n];
        var i = 0;
        while (i < n)
        {
            if (!Points(occ[i]) || furniture[i]) { i++; continue; }
            var j = i;
            var pointers = 0;
            while (j < n && (Points(occ[j]) || LexicalShape.IsFiguresOnly(occ[j].Text) || furniture[j]))
            {
                if (Points(occ[j])) pointers++;
                j++;
            }
            if (pointers >= Constants["navigationRunMin"] || occ[i].InTableOfContents)
            {
                for (var k = i; k < j; k++) if (!furniture[k]) result[k] = Nav.Entry;
                if (i > 0 && result[i - 1] == Nav.None) result[i - 1] = Nav.Opener;
            }
            i = j;
        }
        // A line ending in a date's year points nowhere: the number is part of the date.
        return (result, occ.Select(o => !furniture[indexOf[o]] && Points(o) && !LexicalShape.EndsWithYear(o.Text)).ToArray());
    }

    private sealed record RegionSummary(bool Opens, int Body, int Headings, int Tabular, bool FirstIsTabular, int FillIns)
    {
        /// <summary>V1.3: the first line under it is a table row - not a caption, not a contents entry.</summary>
        public bool FirstIsRow { get; init; }

        /// <summary>V1.3: body lines that end a sentence - running text, not a wrapped row label.</summary>
        public int Sentences { get; init; }
    }

    /// <summary>The content after an occurrence up to the next peer or stronger standalone label; V1 plus the fill-in count.</summary>
    private static RegionSummary Region(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture,
        Nav[] navigation, bool[] tabular, int last, int rank, SourceOccurrence self, bool[] pointsUp)
    {
        int body = 0, headings = 0, table = 0, fillIns = 0, sentences = 0;
        bool? firstTabular = null;
        bool? firstRow = null;
        var first = true;
        for (var j = last + 1; j < occ.Count; j++)
        {
            if (furniture[j]) continue;
            var o = occ[j];
            // Gap 23: a bare enumerator in its own segment and the title beside it are one label, judged whole, as set
            // at the enumerator: where that label is a peer, the region ends; otherwise each part counts as before.
            if (SplitNumberedLabel(occ, j) is { } whole && prominence[j].Strength > 0 && prominence[j].Rank >= rank
                && whole.WordCount <= Constants["shortLabelMaxWords"] && !LexicalShape.EndsSentence(whole.Text)
                && navigation[j] != Nav.Entry && !LexicalShape.IsNumberedCaption(whole.Text)) break;
            var labelLike = prominence[j].Strength > 0 && o.WordCount <= Constants["shortLabelMaxWords"] && !LexicalShape.EndsSentence(o.Text);
            // Gap 15: a numbered caption names an object inside the region; a label set as this one, or differing only by
            // slant, directly under it is its first child - unless nothing but labels follows it (then they are peers).
            var caption = LexicalShape.IsNumberedCaption(o.Text);
            // An identically set label directly under it stays ambiguous (sibling or child) and keeps the review fail-safe.
            var child = first && labelLike && self.Media == "PDF" && SlantOnly(o, self) && LeadsToProse(occ, j, furniture);
            first = false;
            if (labelLike && prominence[j].Rank >= rank && !tabular[j] && navigation[j] != Nav.Entry && !caption && !child) break;
            if (LexicalShape.IsFillInField(o.Text)) fillIns++;
            var isTable = (tabular[j] && (o.WordCount < 8 || LexicalShape.HasDotLeaders(o.Text))) || LexicalShape.IsFiguresOnly(o.Text)
                || LexicalShape.IsNumberedCaption(o.Text);
            firstTabular ??= isTable;
            // A contents entry is not a table row: its text reappears later, set more prominently (gap 8's pointer).
            firstRow ??= isTable && !LexicalShape.IsNumberedCaption(o.Text) && navigation[j] != Nav.Entry && !pointsUp[j] && !ContentsLike(occ, j);
            if (isTable) table++;
            else if (labelLike) headings++;
            else
            {
                body++;
                if (LexicalShape.EndsSentence(o.Text)) sentences++;
            }
            if (body + headings + table >= 40) break;
        }
        return new RegionSummary(body + headings + table > 0, body, headings, table, firstTabular ?? false, fillIns) { FirstIsRow = firstRow ?? false, Sentences = sentences };
    }
}
