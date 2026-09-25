namespace DocxHeaderExtractor.Tests.GenericAudit.V1_1;

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
/// Layer B - GENERIC_AUDIT_ENGINE_V1.1.
/// <para>
/// V1's cascade (GenericAudit/SemanticAuditEngine.cs, frozen with the SRC-029 pre-registration) with the
/// six generic gaps SRC029_BLIND_GENERALIZATION_AUDIT_V1 found, and nothing else:
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
    public const string EngineId = "GENERIC_AUDIT_ENGINE_V1.1";

    /// <summary>The engine's only numeric constants, relative and document-independent. V1's seven, plus seven.</summary>
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
        var tabular = occ.Select(o => o.TableDepth > 0 || o.RowHasFigures || (o.RowSegmentCount >= 3 && o.Media == "PDF")).ToArray();
        var navigation = NavigationRuns(occ, prominence, furniture, tabular);
        var (insideBracket, italicBlock) = InstructionBlocks(occ, profile, furniture, prominence);
        var firstOnPage = FirstContentOnPage(occ, furniture, profile);
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
            var runIn = parts.Any(p => p.Lead is not null);
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
            var region = Region(occ, prominence, furniture, navigation, tabular, last, rank);
            var standalone = o.RowSegmentCount == 1 || (o.RowSegmentIndex == 0 && !o.RowHasFigures)
                || (o.RowSegmentCount == 2 && !o.RowHasFigures);
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
            else if (insideBracket[i] || (italicBlock[i] && parts[0].Lead is null))
            {
                evidence.Add(insideBracket[i] ? "inside a bracketed instruction that spans several lines" : "one line of a multi-line italic block that reads as prose");
                Emit(False, ["INFORMATION"], "INFORMATION", "NOTE", ["BODY_CONTENT"], "NONE");
            }
            else if (LexicalShape.IsFiguresOnly(o.Text))
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
            else if (LexicalShape.IsDate(text))
            {
                evidence.Add("date shape");
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
            else if (strength == 0 || !standalone || !shortLabel)
                Emit(False, ["INFORMATION"], "INFORMATION", tabular[i] ? "TABLE" : "DOCUMENT", [tabular[i] ? "LOCAL_LABEL" : "BODY_CONTENT"], "NONE");
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
            var next = candidate.BoldLead is { } lead ? new Fragment(j, lead) : new Fragment(j, null);
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
        if (x.FontSize is not { } sx || y.FontSize is not { } sy || Math.Abs(sx - sy) > sx * Constants["sameSizeRatioTolerance"]) return false;
        if (x.Bold != y.Bold || x.Italic != y.Italic) return false;
        if (occ[xi].Y is not { } yx || occ[yi].Y is not { } yy) return false;
        var gap = yx - yy;
        if (gap <= 0 || gap > sx * Constants["lineGapMaxFontRatio"]) return false;
        if ((LexicalShape.EndsSentence(x.Text) && !LexicalShape.IsBareEnumerator(x.Text)) || LexicalShape.EndsWithColon(x.Text)) return false;
        if (LexicalShape.IsDate(y.Text) || LexicalShape.IsParentheticalStatus(y.Text) || LexicalShape.IsFiguresOnly(y.Text)) return false;
        if (LexicalShape.IsStructuralLabel(y.Text) || y.WordCount > Constants["shortLabelMaxWords"]) return false;
        // A label joined to the title under it ("Sub-Clause 4.2" / "Notice") needs no wrap.
        if (LexicalShape.IsBareStructuralLabel(x.Text) || LexicalShape.IsBareEnumerator(x.Text)) return true;
        return WrapIsForced(occ, xi, x, y, sx);
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
        if (inBand && LexicalShape.IsFiguresOnly(o.Text)) return true;
        var r = p.Repetition[LexicalShape.RepetitionKey(o.Text)];
        return inBand && r.Pages >= Constants["furnitureMinPages"] && r.BandShare >= Constants["furnitureMinBandShare"];
    }

    private enum Nav { None, Entry, Opener }

    /// <summary>V1's contents-list runs, unchanged (B1/B2 are not among V1.1's six gaps).</summary>
    private static Nav[] NavigationRuns(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture, bool[] tabular)
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
            if (LexicalShape.HasTrailingPageNumber(o.Text)) return true;
            var i = indexOf[o];
            return target.TryGetValue(LexicalShape.WithoutTrailingPageNumber(o.Text), out var t) && t.Index > i && t.Rank >= prominence[i].Rank;
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
        return result;
    }

    private sealed record RegionSummary(bool Opens, int Body, int Headings, int Tabular, bool FirstIsTabular, int FillIns);

    /// <summary>The content after an occurrence up to the next peer or stronger standalone label; V1 plus the fill-in count.</summary>
    private static RegionSummary Region(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture,
        Nav[] navigation, bool[] tabular, int last, int rank)
    {
        int body = 0, headings = 0, table = 0, fillIns = 0;
        bool? firstTabular = null;
        for (var j = last + 1; j < occ.Count; j++)
        {
            if (furniture[j]) continue;
            var o = occ[j];
            var labelLike = prominence[j].Strength > 0 && o.WordCount <= Constants["shortLabelMaxWords"] && !LexicalShape.EndsSentence(o.Text);
            if (labelLike && prominence[j].Rank >= rank && !tabular[j] && navigation[j] != Nav.Entry) break;
            if (LexicalShape.IsFillInField(o.Text)) fillIns++;
            var isTable = (tabular[j] && o.WordCount < 8) || LexicalShape.IsFiguresOnly(o.Text) || LexicalShape.IsNumberedCaption(o.Text);
            firstTabular ??= isTable;
            if (isTable) table++;
            else if (labelLike) headings++;
            else body++;
            if (body + headings + table >= 40) break;
        }
        return new RegionSummary(body + headings + table > 0, body, headings, table, firstTabular ?? false, fillIns);
    }
}
