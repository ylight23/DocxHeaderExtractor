namespace DocxHeaderExtractor.Tests.GenericAudit;

/// <summary>A semantic hypothesis about one occurrence (or one composite of occurrences), on the ontology's axes.</summary>
internal sealed record SemanticHypothesis(
    string[] Aliases,
    string Text,
    string[] SemanticFunctions,
    string? PrimaryFunction,
    string? Scope,
    string[] OccurrenceRoles,
    string TitleRelation,
    string RepeatStatus,
    string? InformationType,
    string[] Evidence,
    string ProposedIsHeading,
    int Prominence);

/// <summary>
/// Layer B - Generic Semantic Audit Engine (GENERIC_AUDIT_ENGINE_V1).
/// <para>
/// Normalizes a <see cref="SourceEvidenceProfile"/> and expresses, for every occurrence, a hypothesis in
/// OCCURRENCE_SEMANTIC_AXES_V2 with the evidence behind it and a proposal - TRUE, FALSE or NEEDS_REVIEW.
/// The proposal is not an authority: it is frozen before any Gold is read, and the reviewer decides.
/// </para>
/// <para>
/// The engine knows no document: no identifiers, paths, heading texts, Gold totals or page numbers.
/// Typography enters only relative to the document's own body text, through the constants below, which
/// are the same for every document. Where evidence cannot settle meaning - a weakly set standalone
/// label, a date over prose, a strongly set line inside a table, a colon-ended label over prose - the
/// proposal is NEEDS_REVIEW, because that is the meaning a semantic reviewer (later an LLM) decides.
/// </para>
/// </summary>
internal static class SemanticAuditEngine
{
    public const string EngineId = "GENERIC_AUDIT_ENGINE_V1";

    /// <summary>The engine's only numeric constants, relative and document-independent.</summary>
    public static readonly IReadOnlyDictionary<string, double> Constants = new Dictionary<string, double>
    {
        ["largerThanBodyRatio"] = 1.08,
        ["bodySizeToleranceRatio"] = 0.97,
        ["shortLabelMaxWords"] = 20,
        ["compositeMaxParts"] = 6,
        ["furnitureMinPages"] = 3,
        ["furnitureMinBandShare"] = 0.8,
        ["navigationRunMin"] = 3,
    };

    private const string True = "TRUE";
    private const string False = "FALSE";
    private const string Review = "NEEDS_REVIEW";

    public static IReadOnlyList<SemanticHypothesis> Propose(SourceEvidenceProfile profile)
    {
        var occ = profile.Occurrences;
        var n = occ.Count;
        var prominence = occ.Select(o => Prominence(o, profile)).ToArray();
        var furniture = occ.Select(o => IsFurniture(o, profile)).ToArray();
        var tabular = occ.Select(o => o.TableDepth > 0 || o.RowHasFigures || (o.RowSegmentCount >= 3 && o.Media == "PDF")).ToArray();
        var navigation = NavigationRuns(occ, prominence, furniture, tabular);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SemanticHypothesis>();

        for (var i = 0; i < n; i++)
        {
            var o = occ[i];
            var evidence = new List<string>();
            var (strength, rank) = prominence[i];
            if (strength > 0) evidence.Add(strength == 2 ? "set apart strongly from the body text" : "set apart weakly from the body text");

            // Composite: a bare structural label with its title, a centered title block, or a wrapped label.
            var parts = new List<int> { i };
            while (parts.Count < Constants["compositeMaxParts"] && parts[^1] + 1 < n && Continues(occ, prominence, parts[^1], parts[^1] + 1, profile))
                parts.Add(parts[^1] + 1);
            var text = string.Join(" ", parts.Select(k => occ[k].Text));
            var key = LexicalShape.RepetitionKey(text);
            var repeat = seen.Add(key) ? "FIRST" : "REPEATED";
            if (parts.Count > 1) evidence.Add($"one label over {parts.Count} source occurrences");

            SemanticHypothesis Emit(string proposal, string[] functions, string? primary, string? scope, string[] roles,
                string titleRelation, string? information = null)
            {
                var h = new SemanticHypothesis(parts.Select(k => occ[k].Alias).ToArray(), text, functions, primary, scope, roles,
                    titleRelation, repeat, information, evidence.ToArray(), proposal, rank);
                result.Add(h);
                return h;
            }

            var last = parts[^1];
            var region = Region(occ, prominence, furniture, navigation, tabular, last, rank);
            var standalone = o.RowSegmentCount == 1 || (o.RowSegmentIndex == 0 && !o.RowHasFigures);
            var shortLabel = occ[last].WordCount <= Constants["shortLabelMaxWords"]
                && !LexicalShape.EndsSentence(occ[last].Text) && !occ[last].Text.TrimEnd().EndsWith(';');
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
            else if (navigation[i] == Nav.Opener && standalone && strength > 0)
            {
                evidence.Add("opens a run of contents entries");
                Emit(True, ["IDENTITY", "STRUCTURE"], "IDENTITY", "TOC", ["REGION_OPENER"], "TITLE");
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
            else if (!region.Opens)
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
                if (LexicalShape.IsStructuralLabel(text)) evidence.Add("numbered structural label shape");
                if (isDocumentTitle) evidence.Add("the document's most prominent setting, near its start");
                Emit(True,
                    isDocumentTitle ? ["IDENTITY"] : LexicalShape.IsStructuralLabel(text) ? ["STRUCTURE", "IDENTITY"] : ["STRUCTURE"],
                    isDocumentTitle ? "IDENTITY" : "STRUCTURE",
                    isDocumentTitle ? "DOCUMENT" : "SECTION",
                    ["REGION_OPENER"], "TITLE");
            }

            i = last;
        }
        return result;
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

    /// <summary>
    /// Contents lists: runs of consecutive occurrences that each point at another occurrence (a trailing
    /// page number, or text that reappears elsewhere). The occurrence just before a run opens it.
    /// </summary>
    private static Nav[] NavigationRuns(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture, bool[] tabular)
    {
        var n = occ.Count;
        // The latest occurrence of each text that stands outside a table, with its prominence: what an
        // earlier, weaker copy of the same text can point to. The target itself points nowhere.
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

    /// <summary>Whether the next occurrence continues the current one as a single label.</summary>
    private static bool Continues(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, int a, int b, SourceEvidenceProfile p)
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

    private sealed record RegionSummary(bool Opens, int Body, int Headings, int Tabular, bool FirstIsTabular);

    /// <summary>The content after an occurrence up to the next peer or stronger standalone label.</summary>
    private static RegionSummary Region(IReadOnlyList<SourceOccurrence> occ, (int Strength, int Rank)[] prominence, bool[] furniture,
        Nav[] navigation, bool[] tabular, int last, int rank)
    {
        int body = 0, headings = 0, table = 0;
        bool? firstTabular = null;
        for (var j = last + 1; j < occ.Count; j++)
        {
            if (furniture[j]) continue;
            var o = occ[j];
            var labelLike = prominence[j].Strength > 0 && o.WordCount <= Constants["shortLabelMaxWords"] && !LexicalShape.EndsSentence(o.Text);
            if (labelLike && prominence[j].Rank >= rank && !tabular[j] && navigation[j] != Nav.Entry) break;
            // A table used for layout holds prose: a sentence-length cell counts as body, not as a table row.
            var isTable = (tabular[j] && o.WordCount < 8) || LexicalShape.IsFiguresOnly(o.Text) || LexicalShape.IsNumberedCaption(o.Text);
            firstTabular ??= isTable;
            if (isTable) table++;
            else if (labelLike) headings++;
            else body++;
            if (body + headings + table >= 40) break;
        }
        return new RegionSummary(body + headings + table > 0, body, headings, table, firstTabular ?? false);
    }
}
