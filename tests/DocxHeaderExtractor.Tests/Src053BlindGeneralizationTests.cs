namespace DocxHeaderExtractor.Tests;

/// <summary>
/// SRC053_BLIND_GENERALIZATION_AUDIT_V1 - the same frozen GENERIC_AUDIT_ENGINE_V1.2 (ad44d78) on its third
/// held-out source, after SRC-044 passed its gate (b744f28): an information statement, a different document type.
/// </summary>
public sealed class Src053BlindGeneralizationTests
{
    internal const string Id = "SRC-053";
    internal const string Study = "SRC053_BLIND_GENERALIZATION_AUDIT_V1";
    internal const string Pdf = "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/053_IDA_Information_Statement_FY25.pdf";

    [Fact]
    public void Preregister_src053() => HeldOutProtocolV12.Preregister(Id, Study, Pdf, "ad44d78",
    [
        new("B2", "TOC sequence continuity evidence"),
        new("B4", "outline level on list item"),
        new("K1", "a title continued across a page break is not assembled"),
        new("K2", "stacked title lines of different sizes are not assembled"),
        new("K3", "standalone connective lines between alternatives have no FALSE shape"),
        new("K4", "DOCX page-break boundary evidence is not read"),
        new("K5", "FORM scope needs fill-in fields"),
        new("K6", "a chart with no numbered caption has no figure region"),
        new("K7", "an unprefixed table-part label has no caption shape"),
        new("N1", "SRC-042: a metadata line set like the title below it counts as a peer (METADATA_LINE_COUNTED_AS_PEER)"),
        new("N2", "SRC-042: a bold bullet glyph alone in its segment (BARE_BULLET_GLYPH)"),
        new("N3", "SRC-042: the figure region stops at a figure's small subtitle line (FIGURE_REGION_STOPPED_BY_SUBTITLE)"),
        new("N4", "SRC-042: a page notice ('This page left intentionally blank') reads as a page title (PAGE_NOTICE)"),
        new("A", "colon-ended labels over prose go to review; scope and IDENTITY vs STRUCTURE are meaning decisions; one occurrence of a running-header text that opens a part; continued titles over image pages"),
    ],
        "SRC-053's Gold file is not opened; any count-only total it holds is neither engine input nor target. Occurrence Gold is authored on the original PDF after the blind proposals are committed.");

    [Fact]
    public void Blind_src053()
    {
        // Runs when triggered (A99_BLIND=SRC-053), and afterwards re-verifies the committed run.
        var ran = File.Exists(TestRepository.Path($"{HeldOutProtocolV12.Root}/{Id}.proposals.v1_2.json"));
        if (!ran && Environment.GetEnvironmentVariable("A99_BLIND") != Id) return;
        HeldOutProtocolV12.Blind(Id, Study, Pdf);
    }
}
