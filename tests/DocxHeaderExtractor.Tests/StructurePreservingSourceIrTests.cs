using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

public sealed class StructurePreservingSourceIrTests
{
    [Fact]
    public void Packet_keeps_all_lines_visible_and_aliases_have_no_semantic_meaning()
    {
        var ir = Ir([
            Line("L000001", "Chapter", 0, 7, false, true, "occ", "p1"),
            Line("L000002", "Article", 0, 7, false, true, "occ2", "p2"),
        ]);
        var packet = StructurePreservingSourceIrBuilder.BuildPacket(ir);

        Assert.Equal(18, packet.SourceTextCharacters);
        Assert.Equal(2, packet.Packet.Lines.Count);
        Assert.Equal("L000001", packet.Packet.Lines[0].Alias);
        Assert.DoesNotContain("sourceId", packet.SerializedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate", packet.SerializedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Line_binder_supports_same_line_subline_and_cross_line_spans_without_fuzzy_lookup()
    {
        var ir = Ir([
            Line("L000001", "Chapter one!", 0, 12, true, false, "occ", "p1"),
            Line("L000002", "Article one", 13, 24, false, true, "occ", "p1"),
        ]);
        var packet = StructurePreservingSourceIrBuilder.BuildPacket(ir);
        var bound = StructurePreservingLineBinder.Bind([
            new("L000001", 0, "L000001", 7, "CHAPTER"),
            new("L000001", 8, "L000001", 12, "SECTION"),
            new("L000001", 8, "L000002", 7, "ARTICLE"),
            new("L999999", 0, "L000001", 2, "ARTICLE"),
        ], packet);

        Assert.Equal(3, bound.Count);
        Assert.Equal((0, 7), (bound[0].Start, bound[0].End));
        Assert.Equal((8, 20), (bound[2].Start, bound[2].End));
    }

    [Fact]
    public void Atom_roundtrip_and_canonical_offsets_are_exact_for_the_real_doc0205_source()
    {
        var root = Root();
        var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "eval", "a99-dataset", "document-inventory.v1.json")));
        var row = inventory.RootElement.GetProperty("documents").EnumerateArray().Single(x => x.GetProperty("documentId").GetString() == "DOC-0205");
        var path = Path.Combine(root, row.GetProperty("sourcePath").GetString()!.Replace('\\', Path.DirectorySeparatorChar));
        var source = new OpenXmlDocumentSource().Read(path) with { DocumentId = "DOC-0205" };
        var ir = StructurePreservingSourceIrBuilder.Build(source);

        Assert.Contains(ir.Occurrences.SelectMany(x => x.Atoms), x => x.Kind == "w:br");
        Assert.Equal(source.Paragraphs.Count, ir.Occurrences.Count);
        Assert.All(ir.Occurrences, occurrence =>
            Assert.Equal(occurrence.CanonicalText, string.Concat(occurrence.Atoms.Select(atom => atom.CanonicalText))));
        Assert.Equal(ir.Lines.Count, ir.Lines.Select(x => x.Alias).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("L000001", ir.Lines[0].Alias);
    }

    [Fact]
    public void Response_parser_requires_line_addresses_and_rejects_unknown_or_negative_offsets()
    {
        var parsed = StructurePreservingSemanticResponseParser.Parse("""
            {"headings":[{"startLineAlias":"L000001","startOffset":0,"endLineAlias":"L000001","endOffset":5,"role":"ARTICLE"}]}
            """);
        Assert.Single(parsed.Headings);
        Assert.Throws<FormatException>(() => StructurePreservingSemanticResponseParser.Parse("""
            {"headings":[{"startLineAlias":"L000001","startOffset":-1,"endLineAlias":"L000001","endOffset":5,"role":"ARTICLE"}]}
            """));
    }

    private static StructurePreservingSourceIr Ir(IReadOnlyList<LogicalSourceLine> lines) => new()
    {
        DocumentId = "test", SourcePath = "test.docx", Lines = lines,
        Occurrences = [new StructurePreservingOccurrence
        {
            SourceOccurrenceId = "occ", SourceId = "p1", SourceOrdinal = 0,
            CanonicalText = "Chapter one", Atoms = [], Lines = lines.Where(x => x.SourceOccurrenceId == "occ").ToArray(),
        }, new StructurePreservingOccurrence
        {
            SourceOccurrenceId = "occ2", SourceId = "p2", SourceOrdinal = 1,
            CanonicalText = "Article", Atoms = [], Lines = lines.Where(x => x.SourceOccurrenceId == "occ2").ToArray(),
        }],
        CanonicalTextSha256 = "test",
    };

    private static LogicalSourceLine Line(string alias, string text, int start, int end, bool breakAfter, bool paragraphBoundaryAfter, string occurrence, string sourceId) => new()
    {
        Alias = alias, Text = text, CanonicalStart = start, CanonicalEnd = end,
        BreakAfter = breakAfter, ParagraphBoundaryAfter = paragraphBoundaryAfter,
        SourceOccurrenceId = occurrence, SourceId = sourceId, SourceOrdinal = occurrence == "occ" ? 0 : 1,
    };

    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
