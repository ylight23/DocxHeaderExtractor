using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace DocxHeaderExtractor.Sources.Docx.Changes;

/// <summary>
/// A comparison key for a paragraph's direct numbering that does not depend on the numId and abstractNumId values, which Word renumbers when it
/// saves a file. The key is the paragraph's level plus, for every level of the list it points to, these attributes only: the number format, the level
/// text pattern, the start value, the legal-numbering (on or off) and restart settings, and every level override of the instance. Every level
/// is included because a level's label can show its parents' numbers (%1.%2): a change to any level of the list changes the key of every
/// paragraph in it. Other level attributes (indentation, alignment, run formatting of the label) are not read, and numbering a paragraph
/// gets through its paragraph style is not covered here. It is not the displayed label, which changes when an item is added or removed above it. When the definition cannot be read (no numbering part, a missing instance, abstract or level, or a list linked
/// through a numbering style), the key is unresolved and the caller falls back to the raw numId, so such a paragraph compares as changed, never
/// as the same.
/// </summary>
internal sealed class NumberingKeys
{
    readonly Dictionary<int, OpenXmlElement> abstracts = [];
    readonly Dictionary<int, (int AbstractId, Dictionary<int, string> Overrides)> instances = [];

    public NumberingKeys(MainDocumentPart main)
    {
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return;
        foreach (var a in numbering.ChildElements.Where(e => e.LocalName == "abstractNum"))
            if (int.TryParse(Attr(a, "abstractNumId"), out var id)) abstracts.TryAdd(id, a);
        foreach (var n in numbering.ChildElements.Where(e => e.LocalName == "num"))
        {
            if (!int.TryParse(Attr(n, "numId"), out var numId) ||
                !int.TryParse(Attr(n.ChildElements.FirstOrDefault(e => e.LocalName == "abstractNumId"), "val"), out var abstractId)) continue;
            var overrides = new Dictionary<int, string>();
            foreach (var o in n.ChildElements.Where(e => e.LocalName == "lvlOverride"))
            {
                if (!int.TryParse(Attr(o, "ilvl"), out var level)) continue;
                var start = Attr(o.ChildElements.FirstOrDefault(e => e.LocalName == "startOverride"), "val");
                var lvl = o.ChildElements.FirstOrDefault(e => e.LocalName == "lvl");
                overrides[level] = $"startOverride={start}|lvl={(lvl is null ? "" : LevelKey(lvl))}";
            }
            instances.TryAdd(numId, (abstractId, overrides));
        }
    }

    /// <summary>
    /// The key for a direct numId/ilvl: null when the paragraph has no direct numbering, "none" for numId 0 (numbering removed), otherwise the
    /// definition key. <paramref name="resolved"/> is false when the definition could not be read.
    /// </summary>
    public string? Key(string? numId, int? ilvl, out bool resolved)
    {
        resolved = true;
        if (numId is null) return null;
        if (numId == "0") return "none";
        resolved = false;
        if (!int.TryParse(numId, out var id) || !instances.TryGetValue(id, out var instance) ||
            !abstracts.TryGetValue(instance.AbstractId, out var abstractNum)) return null;
        // A list defined through a numbering style keeps its levels in another abstractNum: not read here.
        if (abstractNum.ChildElements.Any(e => e.LocalName is "numStyleLink" or "styleLink")) return null;
        var index = ilvl ?? 0;
        var levels = abstractNum.ChildElements.Where(e => e.LocalName == "lvl").ToList();
        if (!levels.Any(e => Attr(e, "ilvl") == index.ToString())) return null;
        resolved = true;
        var definition = string.Join(";", levels.OrderBy(e => int.TryParse(Attr(e, "ilvl"), out var n) ? n : int.MaxValue)
            .Select(e => $"[{Attr(e, "ilvl")}]{LevelKey(e)}"));
        var overrides = string.Join(";", instance.Overrides.OrderBy(o => o.Key).Select(o => $"[{o.Key}]{o.Value}"));
        return $"ilvl={index}|{definition}|override={overrides}";
    }

    /// <summary>The abstract list a numId points to, or null. Ids are compared only inside one file, never across versions.</summary>
    public int? AbstractOf(string? numId) => int.TryParse(numId, out var id) && instances.TryGetValue(id, out var instance) ? instance.AbstractId : null;

    /// <summary>
    /// How each numbered paragraph is linked to the other paragraphs whose list has the same definition (all levels and overrides), counted only
    /// inside that sequence of same-definition paragraphs: "inst" is how many of them lie between this paragraph and the previous one of the
    /// same list instance (numId), "abs" the same for the same abstract list; "-" when there is none. Each paragraph thus points to its
    /// predecessor in its instance and in its abstract list, which fixes how the sequence is split into lists up to renaming the ids: a
    /// consistent renumbering by Word keeps every link, and moving a paragraph to another list (a split, a merge, or a change inside interleaved
    /// lists such as 1, 2, 1 becoming 1, 2, 3) changes at least one. A paragraph whose definition is unresolved has no link; it is already
    /// unverified.
    /// </summary>
    public static IReadOnlyList<ParagraphFingerprint> Relate(IReadOnlyList<ParagraphFingerprint> paragraphs, NumberingKeys keys)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);                   // same-definition paragraphs seen, per definition
        var lastInstance = new Dictionary<(string List, string? NumId), int>();             // position of the last one of an instance
        var lastAbstract = new Dictionary<(string List, int? AbstractId), int>();           // position of the last one of an abstract list
        var result = new List<ParagraphFingerprint>(paragraphs.Count);
        foreach (var p in paragraphs)
        {
            if (!p.NumberingResolved || p.NumberingKey is not { } key || key == "none") { result.Add(p); continue; }
            var list = key[(key.IndexOf('|') + 1)..];
            var position = count.GetValueOrDefault(list);
            var abstractId = keys.AbstractOf(p.NumId);
            var instance = lastInstance.TryGetValue((list, p.NumId), out var i) ? (position - i - 1).ToString() : "-";
            var abstractList = lastAbstract.TryGetValue((list, abstractId), out var j) ? (position - j - 1).ToString() : "-";
            lastInstance[(list, p.NumId)] = position;
            lastAbstract[(list, abstractId)] = position;
            count[list] = position + 1;
            result.Add(p with { NumberingRelation = $"inst={instance}|abs={abstractList}" });
        }
        return result;
    }

    static string LevelKey(OpenXmlElement level) =>
        $"fmt={Val(level, "numFmt")}|text={Val(level, "lvlText")}|start={Val(level, "start")}|legal={OnOff(level.ChildElements.FirstOrDefault(e => e.LocalName == "isLgl"))}" +
        $"|restart={Val(level, "lvlRestart")}";

    /// <summary>A WordprocessingML on/off element: absent is off; present is on unless its val says 0, false or off.</summary>
    static bool OnOff(OpenXmlElement? element) =>
        element is not null && Attr(element, "val") is not ("0" or "false" or "off");

    static string? Val(OpenXmlElement parent, string child) => Attr(parent.ChildElements.FirstOrDefault(e => e.LocalName == child), "val");

    static string? Attr(OpenXmlElement? element, string name) =>
        element?.GetAttributes().FirstOrDefault(a => a.LocalName == name).Value;
}
