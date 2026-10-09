using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record F1QClaimCheck(string Claim, string Status, string Observation);
internal sealed record F1QClaimAuditResult(string Status, IReadOnlyList<F1QClaimCheck> Checks);

/// <summary>
/// Deterministic, heuristic audit of layout/typography claims made in a decision's observedRole and
/// interpretation (English keywords), checked against the parser observations of the decision's OWN
/// source alias. It does not judge semantics. Status per decision: CONTRADICTED (any checked claim false),
/// LAYOUT_CLAIMS_CONSISTENT (≥1 checked, none false) or NO_CHECKABLE_LAYOUT_CLAIM.
/// </summary>
internal static class P7F1QClaimAudit
{
    public const string Version = "P7_F1Q_LAYOUT_CLAIM_AUDIT_V1_HEURISTIC_ENGLISH_KEYWORDS";
    private const double CenterBand = 0.12, TopBand = 0.2, BottomBand = 0.12;

    public static F1QClaimAuditResult Check(string observedRole, string interpretation, string subjectAlias, P7F1QEvidenceTools tools)
    {
        var text = (observedRole + " || " + interpretation).ToLowerInvariant();
        var line = tools.LineOf(subjectAlias);
        var checks = new List<F1QClaimCheck>();
        var box = Box(line.Bbox);
        var pageBoxes = line.Page is { } page ? tools.LinesOnPage(page).Select(l => Box(l.Bbox)).Where(b => b is not null).Select(b => b!.Value).ToArray() : [];
        double minL = pageBoxes.Length > 0 ? pageBoxes.Min(b => b.L) : 0, maxR = pageBoxes.Length > 0 ? pageBoxes.Max(b => b.R) : 0;
        double minB = pageBoxes.Length > 0 ? pageBoxes.Min(b => b.B) : 0, maxT = pageBoxes.Length > 0 ? pageBoxes.Max(b => b.T) : 0;
        double width = Math.Max(1, maxR - minL), height = Math.Max(1, maxT - minB), mid = (minL + maxR) / 2;

        void Add(string claim, bool? ok, string observation) =>
            checks.Add(new(claim, ok is null ? "NOT_CHECKABLE" : ok.Value ? "CONSISTENT" : "CONTRADICTED", observation));
        string Where() => box is { } b ? $"x {b.L:0.#}-{b.R:0.#} of {minL:0.#}-{maxR:0.#}, y {b.B:0.#}-{b.T:0.#} of {minB:0.#}-{maxT:0.#}" : "bbox not observed";

        if (Has(text, @"\b(top[- ])?cent(er|re)(ed|d)?\b|\bcent(er|re)-aligned\b"))
            Add("centered", box is { } b ? Math.Abs((b.L + b.R) / 2 - mid) <= CenterBand * width : null, Where());
        if (Has(text, @"\bleft[- ]aligned\b|\bflush left\b"))
            Add("left-aligned", box is { } b ? b.L <= minL + 0.08 * width : null, Where());
        if (Has(text, @"\bleft (column|side|half)\b|\bon the left\b|\b(top|upper)[- ]left\b"))
            Add("left side", box is { } b ? (b.L + b.R) / 2 < mid - 0.05 * width : null, Where());
        if (Has(text, @"\bright[- ]aligned\b|\bflush right\b"))
            Add("right-aligned", box is { } b ? b.R >= maxR - 0.08 * width : null, Where());
        if (Has(text, @"\bright (column|side|half)\b|\bon the right\b|\b(top|upper)[- ]right\b"))
            Add("right side", box is { } b ? (b.L + b.R) / 2 > mid + 0.05 * width : null, Where());
        if (Has(text, @"\btop[- ](of|center|centre|left|right)\b|\bat the top\b|\btop of (the )?page\b"))
            Add("top of page", box is { } b ? b.T >= maxT - TopBand * height : null, Where());
        if (Has(text, @"\bbottom of (the )?page\b|\bat the bottom\b|\bfooter\b"))
            Add("bottom of page", box is { } b ? b.B <= minB + BottomBand * height : null, Where());
        if (Has(text, @"\b(running|page) header\b|\brepeated (page )?header\b"))
            Add("running/page header", box is { } b ? b.T >= maxT - TopBand * height && tools.RepeatPageCount(subjectAlias) >= 2 : null,
                $"{Where()}; normalized text on {tools.RepeatPageCount(subjectAlias)} page(s)");
        if (Has(text, @"\b(running|page) footer\b"))
            Add("running/page footer", box is { } b ? b.B <= minB + BottomBand * height && tools.RepeatPageCount(subjectAlias) >= 2 : null,
                $"{Where()}; normalized text on {tools.RepeatPageCount(subjectAlias)} page(s)");
        var bold = P7F1QEvidenceTools.Bold(line.Typography);
        if (Has(text, @"\b(not bold|non-bold|unbolded|regular weight|plain weight)\b"))
            Add("not bold", bold is { } x ? x < 0.5 : null, $"boldRatio {bold?.ToString("0.##") ?? "n/a"}");
        else if (Has(text, @"\bbold(ed|face)?\b"))
            Add("bold", bold is { } x ? x >= 0.5 : null, $"boldRatio {bold?.ToString("0.##") ?? "n/a"}");
        if (Has(text, @"\b(upper ?case|all[- ]caps|all capitals|capitali[sz]ed)\b"))
        {
            var letters = line.Text.Where(char.IsLetter).ToArray();
            Add("uppercase", letters.Length == 0 ? null : letters.Count(char.IsUpper) >= 0.8 * letters.Length, $"{letters.Count(char.IsUpper)}/{letters.Length} letters uppercase");
        }
        if (Has(text, @"\b(larger|large|bigger) (font|type|size|text)\b"))
        {
            var sizes = line.Page is { } p2 ? tools.LinesOnPage(p2).Select(l => Size(l.Typography)).Where(s => s is not null).Select(s => s!.Value).Order().ToArray() : [];
            var size = Size(line.Typography);
            Add("larger font", size is null || sizes.Length == 0 ? null : size > sizes[sizes.Length / 2], $"size {size?.ToString("0.#") ?? "n/a"} vs page median {(sizes.Length == 0 ? "n/a" : sizes[sizes.Length / 2].ToString("0.#"))}");
        }
        if (Has(text, @"\bcolumn (label|header|heading|title)\b|\btable header row\b"))
        {
            var peers = box is { } b && line.Page is { } p3 ? tools.LinesOnPage(p3).Count(l => l.Alias != line.Alias && Box(l.Bbox) is { } o && Math.Abs(o.B - b.B) <= P7F1QEvidenceTools.SameBaselineTolerance) : (int?)null;
            Add("column label (shares a row)", peers is null ? null : peers >= 1, $"{peers?.ToString() ?? "n/a"} other line(s) on the same baseline");
        }
        var status = checks.Any(c => c.Status == "CONTRADICTED") ? "CONTRADICTED"
            : checks.Any(c => c.Status == "CONSISTENT") ? "LAYOUT_CLAIMS_CONSISTENT" : "NO_CHECKABLE_LAYOUT_CLAIM";
        return new(status, checks);
    }

    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern);
    private static (double L, double R, double B, double T)? Box(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Object } b ? (b.GetProperty("left").GetDouble(), b.GetProperty("right").GetDouble(), b.GetProperty("bottom").GetDouble(), b.GetProperty("top").GetDouble()) : null;
    private static double? Size(JsonElement? t) =>
        t is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty("fontSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
}
