using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxHeaderExtractor.HeadingAgent;
using Xunit.Abstractions;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The offline checkpoint for compact facts. get_line_facts gives every target its core facts and only a summary of the heavy ones (the pieces
/// between line breaks, every format run, the other places of a repeated text); the detail stays one get_source_span or get_repeated_occurrences
/// call away. These tests measure the real size of the core facts of a unit over ordinary and unusual documents, and hold the design target:
/// the core facts of every target of a unit arrive in at most 32 KB of tool messages, leaving the rest of the unit's 64 KB for detail.
/// </summary>
public sealed class DocxCoreFactsBudgetTests(ITestOutputHelper output) : IDisposable
{
    public const int CoreTargetBytes = 32768;
    readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-core-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    string Build(string name, IEnumerable<Func<Paragraph>> paragraphs)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".docx");
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(paragraphs.Select(p => (DocumentFormat.OpenXml.OpenXmlElement)p()).ToArray()));
        return path;
    }

    static Run R(string text, bool bold = false, bool italic = false) =>
        new(new RunProperties(bold ? new Bold() : null!, italic ? new Italic() : null!), new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });

    /// <summary>A paragraph of <paramref name="breaks"/> line breaks and <paramref name="runs"/> alternating formats over about <paramref name="chars"/> characters.</summary>
    static Paragraph Heavy(int seed, int chars, int breaks, int runs)
    {
        var p = new Paragraph();
        var piece = Math.Max(4, chars / Math.Max(1, runs));
        for (var k = 0; k < runs; k++)
        {
            p.Append(R($"s{seed}r{k} " + new string((char)('a' + (seed + k) % 26), piece), bold: k % 2 == 0, italic: k % 3 == 0));
            if (breaks > 0 && k % Math.Max(1, runs / breaks) == 0) p.Append(new Run(new Break()));
        }
        return p;
    }

    static Paragraph Plain(string text, bool bold = false) => new(R(text, bold));

    /// <summary>Ordinary text with a heading every few paragraphs, and unusual paragraphs mixed in: very long, many line breaks, many format
    /// runs, and one text repeated everywhere.</summary>
    string Unusual(int count) => Build("unusual", Enumerable.Range(0, count).Select<int, Func<Paragraph>>(i => () => (i % 11) switch
    {
        0 => Plain($"Article {i}. Heading {i}", bold: true),
        3 => Heavy(i, 3500, breaks: 40, runs: 60),
        5 => Heavy(i, 1200, breaks: 25, runs: 30),
        7 => Plain("Contents"),
        _ => Plain($"{i}. Body paragraph number {i} with an ordinary sentence of text."),
    }));

    /// <summary>Reads every core fact of one unit the way the model does: get_line_facts over the unit's targets, page after page.</summary>
    static (int Bytes, int Calls, HashSet<string> Covered) ReadCore(DocxAgentSource source, int segment)
    {
        var tools = DocxAgentFlavor.Instance.CreateTools(source);
        var (decide, _) = source.Scope(segment);
        int bytes = 0, calls = 0, offset = 0;
        while (true)
        {
            var r = tools.Execute($"E1.{++calls:00}", "get_line_facts",
                JsonSerializer.Serialize(new { first = decide[0], last = decide[^1], offset }));
            Assert.Equal("OK", r.Status);
            bytes += r.Content.Length;
            var result = JsonDocument.Parse(r.Content).RootElement.GetProperty("result");
            if (!result.GetProperty("hasMore").GetBoolean()) break;
            offset = result.GetProperty("nextOffset").GetInt32();
        }
        return (bytes, calls, tools.FactsReturnedFor.ToHashSet());
    }

    void Report(string name, DocxAgentSource source)
    {
        var rows = Enumerable.Range(0, source.Cores.Count).Select(n => (n, Read: ReadCore(source, n), Targets: source.Scope(n).Decide.Length)).ToArray();
        var sizes = rows.Select(r => r.Read.Bytes).Order().ToArray();
        double Pct(double q) => sizes[(int)Math.Min(sizes.Length - 1, Math.Floor(q * sizes.Length))];
        output.WriteLine($"{name}: paragraphs={source.Lines.Count} units={rows.Length} targets/unit min={rows.Min(r => r.Targets)} max={rows.Max(r => r.Targets)} " +
            $"coreBytes min={sizes[0]} p50={Pct(.5)} p90={Pct(.9)} max={sizes[^1]} calls/unit max={rows.Max(r => r.Read.Calls)}");
    }

    // ---------------- measurement and design target ----------------

    [Fact]
    public void The_core_facts_of_an_ordinary_unit_of_48_fit_the_target_and_the_units_stay_48()
    {
        var source = DocxAgentSource.Load(DocxTestDocument.CreateLong(Path.Combine(Directory.CreateDirectory(dir).FullName, "long.docx"), 480), null, out _)!;
        Report("ordinary", source);

        Assert.All(source.Cores, c => Assert.Equal(DocxAgentSource.Unit, c.End - c.Start));
        for (var n = 0; n < source.Cores.Count; n++)
        {
            var (bytes, _, covered) = ReadCore(source, n);
            Assert.True(bytes <= CoreTargetBytes, $"unit {n}: {bytes} bytes of core facts");
            Assert.Equal(source.Scope(n).Decide.ToHashSet(), covered);
        }
    }

    [Fact]
    public void The_core_facts_of_every_unit_of_an_unusual_document_fit_the_target_and_cover_every_target()
    {
        var source = DocxAgentSource.Load(Unusual(600), null, out _)!;
        Report("unusual", source);

        for (var n = 0; n < source.Cores.Count; n++)
        {
            var (bytes, _, covered) = ReadCore(source, n);
            Assert.True(bytes <= CoreTargetBytes, $"unit {n}: {bytes} bytes of core facts");
            Assert.Equal(source.Scope(n).Decide.ToHashSet(), covered);
        }
    }

    /// <summary>Real documents of the corpus (a statute, a tender document, a textbook), when the corpus is in the repository. The whole corpus was
    /// measured once, offline: 72 documents, 3,718 units, core facts p50 27.3 KB, p99 29.1 KB, max 30.9 KB, none over 32 KB, two calls each, and no
    /// unit cut short by size (the 72 short units are the last unit of each document).</summary>
    [Theory]
    [InlineData("samples/mau.docx")]
    [InlineData("todo10_8/generated-docx/01_phap_quy/001_Bo_luat_Dan_su_91-2015-QH13.docx")]
    [InlineData("todo10_8/generated-docx/02_hop_dong_mua_sam/030_WB_RFP_Consulting_Services_2019.docx")]
    [InlineData("todo10_8/generated-docx/04_giao_trinh/065_Numerical_Linear_Algebra_Lecture_Notes.docx")]
    public void Real_documents_fit_the_target_and_cover_every_target(string relative)
    {
        var path = TestRepository.Path(relative);
        if (!File.Exists(path)) { output.WriteLine("not in the repository: " + relative); return; }
        var source = DocxAgentSource.Load(path, null, out _)!;
        Report(relative, source);

        for (var n = 0; n < source.Cores.Count; n++)
        {
            var (bytes, _, covered) = ReadCore(source, n);
            Assert.True(bytes <= CoreTargetBytes, $"unit {n}: {bytes} bytes of core facts");
            Assert.Equal(source.Scope(n).Decide.ToHashSet(), covered);
        }
    }

    // ---------------- adaptive units ----------------

    [Fact]
    public void Units_are_cut_by_the_size_of_the_core_facts_and_stay_a_partition()
    {
        var source = DocxAgentSource.Load(Unusual(600), null, out _)!;
        var cores = source.Cores;

        Assert.Equal(0, cores[0].Start);
        Assert.Equal(source.Lines.Count, cores[^1].End);
        for (var i = 1; i < cores.Count; i++) Assert.Equal(cores[i - 1].End, cores[i].Start);
        Assert.All(cores, c => Assert.InRange(c.End - c.Start, 1, DocxAgentSource.Unit));
        // Every paragraph is a target of exactly one unit, and the context of a unit reaches over its edges.
        var targets = Enumerable.Range(0, cores.Count).SelectMany(n => source.Scope(n).Decide).ToArray();
        Assert.Equal(source.Lines.Select(l => l.Alias), targets);
        Assert.Contains(source.Scope(1).Shown, i => i < cores[1].Start);
    }

    [Fact]
    public void Only_a_unit_of_large_paragraphs_is_cut_short_and_a_single_oversized_paragraph_is_a_unit_of_its_own()
    {
        var sizes = Enumerable.Repeat(300, 100).ToList();
        Assert.All(DocxAgentSource.CoresOf(sizes), c => Assert.True(c.End - c.Start <= DocxAgentSource.Unit));
        Assert.Equal(48, DocxAgentSource.CoresOf(sizes)[0].End);

        var big = new[] { 300, 300, 40000, 300, 300 };
        Assert.Equal([(0, 2), (2, 3), (3, 5)], DocxAgentSource.CoresOf(big));
        Assert.Equal([], DocxAgentSource.CoresOf([]));
    }

    // ---------------- what compact facts keep and what moves to detail ----------------

    [Fact]
    public void A_paragraph_with_breaks_and_formats_is_summarised_in_line_facts_and_complete_in_the_span_record()
    {
        var path = Build("detail", [() => Plain("Intro"), () => Heavy(1, 600, breaks: 12, runs: 16), () => Plain("Intro")]);
        var source = DocxAgentSource.Load(path, null, out _)!;
        var tools = DocxAgentFlavor.Instance.CreateTools(source);
        var heavy = source.Lines[1].Alias;

        var line = JsonNode.Parse(tools.Execute("E1.01", "get_line_facts", JsonSerializer.Serialize(new { first = source.Lines[0].Alias, last = source.Lines[2].Alias, offset = 0 })).Content)!;
        var compact = line["result"]!["facts"]!.AsArray().Single(f => f!["alias"]!.GetValue<string>() == heavy)!;
        var text = compact["text"]!.AsObject();
        Assert.Null(text["pieces"]);
        Assert.Null(text["formatRuns"]);
        Assert.Equal(DocxAgentSource.MaxCompactOffsets, text["lineBreakOffsets"]!.AsArray().Count);
        Assert.True(text["lineBreakOffsetsNotShown"]!.GetValue<int>() > 0 || text["lineBreaks"]!.GetValue<int>() <= DocxAgentSource.MaxCompactOffsets);
        Assert.True(text["formatRunCount"]!.GetValue<int>() > 1);
        Assert.NotEmpty(text["formatChangeOffsets"]!.AsArray());

        var span = JsonNode.Parse(tools.Execute("E1.02", "get_source_span", JsonSerializer.Serialize(new { aliases = new[] { heavy } })).Content)!;
        var full = span["result"]!["records"]![0]!["facts"]!["text"]!.AsObject();
        Assert.NotEmpty(full["pieces"]!.AsArray());
        Assert.NotEmpty(full["formatRuns"]!.AsArray());
        Assert.True(full["lineBreakOffsets"]!.AsArray().Count >= text["lineBreakOffsets"]!.AsArray().Count);
    }

    [Fact]
    public void A_repeated_text_is_summarised_by_count_and_two_aliases_and_the_places_come_from_the_repetition_tool()
    {
        var path = Build("repeat", Enumerable.Range(0, 6).Select<int, Func<Paragraph>>(i => () => Plain(i == 0 ? "First" : "Contents")));
        var source = DocxAgentSource.Load(path, null, out _)!;
        var tools = DocxAgentFlavor.Instance.CreateTools(source);

        var facts = JsonNode.Parse(tools.Execute("E1.01", "get_line_facts", JsonSerializer.Serialize(new { first = source.Lines[0].Alias, last = source.Lines[5].Alias, offset = 0 })).Content)!["result"]!["facts"]!.AsArray();

        Assert.Null(facts[0]!["repetition"]);
        var repetition = facts[1]!["repetition"]!;
        Assert.Equal(4, repetition["otherOccurrences"]!.GetValue<int>());
        Assert.Equal(2, repetition["sampleAliases"]!.AsArray().Count);
        Assert.Null(facts[1]!["repeatedText"]);
        // The sample aliases are shown aliases the model may cite, and the places are one call away.
        Assert.Superset(repetition["sampleAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToHashSet(), tools.ObservedAliases.ToHashSet());
        var places = JsonNode.Parse(tools.Execute("E1.02", "get_repeated_occurrences", JsonSerializer.Serialize(new { alias = source.Lines[1].Alias, maxResults = 20 })).Content)!;
        Assert.Equal("OK", places["status"]!.GetValue<string>());
    }

    [Fact]
    public void The_compact_facts_of_one_paragraph_are_bounded_whatever_the_paragraph_holds()
    {
        var path = Build("giant", [() => Heavy(1, 20000, breaks: 400, runs: 300)]);
        var source = DocxAgentSource.Load(path, null, out _)!;

        Assert.InRange(DocxLineFacts.CompactBytes(source.Facts[source.Lines[0].Alias], source.Lines[0].Alias), 1, 2000);
        Assert.Single(source.Cores);
        Assert.True(DocxLineFacts.Compact(source.Facts[source.Lines[0].Alias])["text"]!["lineBreakOffsetsNotShown"]!.GetValue<int>() > 0);
        var full = source.Facts[source.Lines[0].Alias]["text"]!.AsObject();
        Assert.Equal(DocxAgentSource.MaxFullOffsets, full["lineBreakOffsets"]!.AsArray().Count);
        Assert.True(full["lineBreakOffsetsNotShown"]!.GetValue<int>() > 0);
    }
}
