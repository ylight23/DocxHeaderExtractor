using DocxHeaderExtractor.Tests.GenericAudit.V1_4;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// GENERIC_AUDIT_ENGINE_V1.4's new lexical shapes on constructed texts - shapes, never a list of headings - so each gap's
/// evidence is pinned apart from any document.
/// </summary>
public sealed class GenericAuditEngineV14MechanismTests
{
    [Theory]
    [InlineData("Section 3.2, Paragraph 4; Section 8.1; Table 4", true)]
    [InlineData("Table 2; Appendix A.1, Paragraph 3; Appendix A.2.3, Paragraph 1; Appen", true)]
    [InlineData("Paragraph 7; Section 4.4, Paragraph 8; Section 4.6, Paragraph 12; Tabl", true)]
    [InlineData("Article 12; Article 14, Clause 2", true)]
    [InlineData("Section 4.6", false)]
    [InlineData("Table 2: Initial Frame Types", false)]
    [InlineData("Scope; purpose; definitions", false)]
    [InlineData("See Section 4; the rest of this chapter describes it; Table 3", false)]
    public void A_list_of_cross_references_is_recognized_by_its_items(string text, bool list) =>
        Assert.Equal(list, LexicalShape.IsReferenceList(text));

    [Theory]
    [InlineData("Table 2: Initial Frame Types", true)]
    [InlineData("Figure 3 HTTP/3 Frame Format", true)]
    [InlineData("Table 2; Appendix A.1, Paragraph 3; Appendix A.2.3, Paragraph 1", false)]
    public void A_list_of_cross_references_is_no_caption(string text, bool caption) =>
        Assert.Equal(caption, LexicalShape.IsNumberedCaption(text));

    [Theory]
    [InlineData("Email: someone@example.org", true)]
    [InlineData("someone@example.org", true)]
    [InlineData("URI: https://example.org/", true)]
    [InlineData("Phone: +1 555 0100", true)]
    [InlineData("Example Corporation", false)]
    [InlineData("The contact office answers questions.", false)]
    public void A_contact_line_is_an_address_or_a_labelled_contact_field(string text, bool contact) =>
        Assert.Equal(contact, LexicalShape.IsContactLine(text));
}
