using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Tests;

/// <summary>Word renumbers numId and abstractNumId when it saves a file; the paragraph signature must follow the list definition instead.</summary>
public sealed class NumberingKeyTests
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    static string Level(int ilvl, string format, string text, int start = 1) =>
        $"<w:lvl w:ilvl=\"{ilvl}\"><w:start w:val=\"{start}\"/><w:numFmt w:val=\"{format}\"/><w:lvlText w:val=\"{text}\"/></w:lvl>";

    static string Numbering(params (int NumId, int AbstractId, string Levels, string Override)[] lists) =>
        $"<w:numbering xmlns:w=\"{W}\">" +
        string.Concat(lists.Select(l => $"<w:abstractNum w:abstractNumId=\"{l.AbstractId}\">{l.Levels}</w:abstractNum>")) +
        string.Concat(lists.Select(l => $"<w:num w:numId=\"{l.NumId}\"><w:abstractNumId w:val=\"{l.AbstractId}\"/>{l.Override}</w:num>")) +
        "</w:numbering>";

    static string Item(string text, int numId, int ilvl = 0) =>
        $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"{ilvl}\"/><w:numId w:val=\"{numId}\"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";

    static string Docx(string body, string? numbering)
    {
        var path = Path.Combine(Path.GetTempPath(), "dhx-numkey-" + Guid.NewGuid().ToString("N") + ".docx");
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        using (var stream = main.GetStream(FileMode.Create))
            stream.Write(Encoding.UTF8.GetBytes($"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>"));
        if (numbering is not null)
        {
            var part = main.AddNewPart<NumberingDefinitionsPart>();
            using var stream = part.GetStream(FileMode.Create);
            stream.Write(Encoding.UTF8.GetBytes(numbering));
        }
        return path;
    }

    static IReadOnlyList<ParagraphFingerprint> Read(string body, string? numbering)
    {
        var path = Docx(body, numbering);
        try { return DocxVersionReader.Read(path).Paragraphs; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Renumbered_ids_with_the_same_definition_keep_the_signature()
    {
        var levels = Level(0, "upperLetter", "%1.") + Level(1, "bullet", "•");
        var before = Read(Item("Truyền thông:", 2) + Item("Mục con", 2, 1), Numbering((2, 45, levels, "")));
        var after = Read(Item("Truyền thông:", 17) + Item("Mục con", 17, 1), Numbering((17, 3, levels, "")));
        Assert.All(before.Concat(after), p => Assert.True(p.NumberingResolved));
        Assert.NotEqual(before[0].NumId, after[0].NumId);
        Assert.Equal(before.Select(OutlineStore.Signature), after.Select(OutlineStore.Signature));
    }

    [Fact]
    public void A_changed_definition_level_or_override_changes_the_signature()
    {
        var levels = Level(0, "upperLetter", "%1.");
        var baseline = OutlineStore.Signature(Read(Item("Mục", 1), Numbering((1, 0, levels, "")))[0]);
        Assert.NotEqual(baseline, OutlineStore.Signature(Read(Item("Mục", 1), Numbering((1, 0, Level(0, "bullet", "•"), "")))[0]));
        Assert.NotEqual(baseline, OutlineStore.Signature(Read(Item("Mục", 1), Numbering((1, 0, levels, "<w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"3\"/></w:lvlOverride>")))[0]));
        Assert.NotEqual(baseline, OutlineStore.Signature(Read(Item("Mục", 1, 1), Numbering((1, 0, levels + Level(1, "upperLetter", "%2."), "")))[0]));
    }

    [Fact]
    public void An_unreadable_definition_is_unresolved_and_compares_by_the_raw_id()
    {
        // No instance 9 in the part, and no numbering part at all: the key is unresolved, so two different raw ids never compare as the same.
        var missing = Read(Item("Mục", 9), Numbering((1, 0, Level(0, "decimal", "%1."), "")))[0];
        var noPart = Read(Item("Mục", 4), null)[0];
        Assert.False(missing.NumberingResolved);
        Assert.False(noPart.NumberingResolved);
        Assert.NotEqual(OutlineStore.Signature(missing), OutlineStore.Signature(noPart));
        var removed = Read(Item("Mục", 0), null)[0];
        Assert.True(removed.NumberingResolved);
        Assert.Equal("none", removed.NumberingKey);
    }

    [Fact]
    public void Two_unresolved_paragraphs_with_the_same_id_are_never_read_as_unchanged()
    {
        // The same raw numId 9 on both sides and no definition for it: equal signatures are not evidence of an unchanged paragraph.
        var numbering = Numbering((1, 0, Level(0, "decimal", "%1."), ""));
        var before = Read(Item("Mục", 9), numbering);
        var after = Read(Item("Mục", 9), numbering);
        Assert.False(before[0].NumberingResolved);
        Assert.Equal(OutlineStore.Signature(before[0]), OutlineStore.Signature(after[0]));
        var stored = new StoredParagraph("body[1]/p[1]", 0, OutlineStore.Signature(before[0]), !before[0].NumberingResolved);
        var current = new StoredParagraph("body[1]/p[1]", 0, OutlineStore.Signature(after[0]), !after[0].NumberingResolved);
        Assert.False(StoredParagraph.Same(stored, current));
    }

    [Fact]
    public void A_list_defined_through_a_numbering_style_is_unresolved()
    {
        // The abstractNum only links to a numbering style; its levels live elsewhere and are not read here.
        var linked = $"<w:numbering xmlns:w=\"{W}\"><w:abstractNum w:abstractNumId=\"0\"><w:numStyleLink w:val=\"Kieu1\"/></w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";
        var paragraph = Read(Item("Mục", 1), linked)[0];
        Assert.False(paragraph.NumberingResolved);
        Assert.Null(paragraph.NumberingKey);
    }

    [Fact]
    public void A_change_to_a_parent_level_changes_the_key_of_a_child_level()
    {
        // The child label "%1.%2" shows the parent's number: changing the parent level's format changes what the child shows.
        var child = Level(1, "decimal", "%1.%2");
        var before = Read(Item("Mục con", 1, 1), Numbering((1, 0, Level(0, "decimal", "%1.") + child, "")))[0];
        var after = Read(Item("Mục con", 1, 1), Numbering((1, 0, Level(0, "upperRoman", "%1.") + child, "")))[0];
        Assert.NotEqual(OutlineStore.Signature(before), OutlineStore.Signature(after));
    }

    [Fact]
    public void Legal_numbering_is_read_as_on_or_off()
    {
        static string Legal(string isLgl) =>
            $"<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/>{isLgl}<w:lvlText w:val=\"%1.\"/></w:lvl>";
        string Key(string isLgl) => Read(Item("Mục", 1), Numbering((1, 0, Legal(isLgl), "")))[0].NumberingKey!;
        Assert.Equal(Key(""), Key("<w:isLgl w:val=\"0\"/>"));
        Assert.Equal(Key(""), Key("<w:isLgl w:val=\"false\"/>"));
        Assert.Equal(Key("<w:isLgl/>"), Key("<w:isLgl w:val=\"1\"/>"));
        Assert.NotEqual(Key(""), Key("<w:isLgl/>"));
    }

    [Fact]
    public void Splitting_or_merging_a_list_changes_the_signature_and_a_consistent_renumbering_does_not()
    {
        var levels = Level(0, "decimal", "%1.");
        string Second(IReadOnlyList<ParagraphFingerprint> p) => OutlineStore.Signature(p[1]);
        // One list: both items in instance 1.
        var one = Read(Item("Một", 1) + Item("Hai", 1), Numbering((1, 0, levels, "")));
        // Word renumbered every id consistently: still one list.
        var renumbered = Read(Item("Một", 7) + Item("Hai", 7), Numbering((7, 4, levels, "")));
        // Split into another instance of the same abstract list.
        var splitSameAbstract = Read(Item("Một", 1) + Item("Hai", 2),
            $"<w:numbering xmlns:w=\"{W}\"><w:abstractNum w:abstractNumId=\"0\">{levels}</w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>");
        // Split into a copy of the list (another abstract list with the same definition).
        var splitCopy = Read(Item("Một", 1) + Item("Hai", 2), Numbering((1, 0, levels, ""), (2, 1, levels, "")));
        Assert.All(new[] { one, renumbered, splitSameAbstract, splitCopy }.SelectMany(p => p), p => Assert.True(p.NumberingResolved));
        Assert.Equal(Second(one), Second(renumbered));
        Assert.Equal(OutlineStore.Signature(one[0]), OutlineStore.Signature(splitCopy[0]));
        Assert.NotEqual(Second(one), Second(splitSameAbstract));
        Assert.NotEqual(Second(one), Second(splitCopy));
        Assert.NotEqual(Second(splitSameAbstract), Second(splitCopy));
        Assert.Equal(["inst=-|abs=-", "inst=0|abs=0"], one.Select(p => p.NumberingRelation));
    }

    [Fact]
    public void A_change_inside_interleaved_lists_changes_the_signature()
    {
        // Three items, each list with the same definition. Old: lists 1, 2, 1. New: lists 1, 2, 3. The third item leaves list 1 although it
        // differs from the list just before it in both versions.
        var levels = Level(0, "decimal", "%1.");
        var lists = Numbering((1, 0, levels, ""), (2, 1, levels, ""), (3, 2, levels, ""));
        var before = Read(Item("Một", 1) + Item("Hai", 2) + Item("Ba", 1), lists);
        var after = Read(Item("Một", 1) + Item("Hai", 2) + Item("Ba", 3), lists);
        Assert.All(before.Concat(after), p => Assert.True(p.NumberingResolved));
        Assert.Equal(OutlineStore.Signature(before[0]), OutlineStore.Signature(after[0]));
        Assert.Equal(OutlineStore.Signature(before[1]), OutlineStore.Signature(after[1]));
        Assert.NotEqual(OutlineStore.Signature(before[2]), OutlineStore.Signature(after[2]));

        // The same interleaving with every id renumbered consistently keeps every signature.
        var renamed = Read(Item("Một", 8) + Item("Hai", 5) + Item("Ba", 8), Numbering((8, 6, levels, ""), (5, 9, levels, "")));
        Assert.Equal(before.Select(OutlineStore.Signature), renamed.Select(OutlineStore.Signature));
    }

    [Fact]
    public void Signatures_follow_how_the_paragraphs_are_split_into_lists_and_nothing_else()
    {
        // Random assignments of 2 to 6 same-definition items to up to 3 list instances, each instance on its own abstract list (sharing an
        // abstract list is covered above): renaming every id consistently keeps all signatures; a different split (not a renaming) changes at
        // least one, and the same split under other ids changes none.
        var levels = Level(0, "decimal", "%1.");
        var random = new Random(20261106);
        static string Canonical(IEnumerable<int> ids) { var seen = new Dictionary<int, int>(); return string.Join(",", ids.Select(i => seen.TryAdd(i, seen.Count) ? seen[i] : seen[i])); }
        IReadOnlyList<string> Signatures(int[] lists, Func<int, int> abstractOf, Func<int, int> rename) =>
            Read(string.Concat(lists.Select((l, i) => Item("Mục " + i, rename(l)))),
                Numbering(lists.Distinct().Select(l => (rename(l), abstractOf(l) + 10 * rename(l), levels, "")).ToArray()))
            .Select(OutlineStore.Signature).ToList();
        for (var round = 0; round < 60; round++)
        {
            var n = random.Next(2, 7);
            var a = Enumerable.Range(0, n).Select(_ => random.Next(1, 4)).ToArray();
            var b = Enumerable.Range(0, n).Select(_ => random.Next(1, 4)).ToArray();
            int Own(int l) => 0;
            var renamed = Signatures(a, Own, l => 10 + (l * 7) % 5);
            Assert.Equal(Signatures(a, Own, l => l), renamed);
            if (Canonical(a) != Canonical(b)) Assert.NotEqual(Signatures(a, Own, l => l), Signatures(b, Own, l => l));
            else Assert.Equal(Signatures(a, Own, l => l), Signatures(b, Own, l => l));
        }
    }
}
