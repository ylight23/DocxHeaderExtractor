namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>Public manifest fingerprint for the retired PDF analyst prompt profile.</summary>
public static class PdfStagePromptProfile
{
    // Kept verbatim while the old analyst implementation is removed. The profile is no longer a
    // runtime route, but historical manifests still identify these exact prompt bytes.
    private const string RoleSystemPrompt =
        "You classify candidate PDF text blocks for document outline extraction.\n" +
        "Deterministic code has already removed obvious page numbers, repeated headers/footers, and numeric table noise.\n" +
        "For each block, choose exactly one closed semantic role: document_title, section_heading, topic_heading, local_subheading, legal_chapter, legal_section, legal_article, legal_clause, legal_point, appendix_heading, meeting_section, agenda_item, note_heading, table_title, table_header, figure_title, figure_caption, list_item_topic, running_header, running_footer, form_label, signature_label, translation_notice, body_text, or unknown.\n" +
        "A domain_role_hint is parser evidence, not a request to generate text. Treat amendment_annotation, inline_clause_reference, form_field_label, outline_reference, table_title, and running_artifact as non-heading roles even when visually prominent.\n" +
        "Do not mark a block heading_topic merely because it is bold/uppercase. Prefer heading_topic for concise topic labels such as 'AVAILABILITY OF INFORMATION'.\n" +
        "Classify numbered or indented prose as list_item_topic only when the source facts show a list marker or list layout; numbering alone must not authorize a structural element. This is role pass only. Do not infer heading text, pointer spans, levels, or parents.\n" +
        "Return one compact strict JSON object for every input id. Omit explanations unless needed.\n" +
        "Format: {\"blocks\":[{\"id\":\"b1\",\"role\":\"closed_role\",\"confidence\":0.0}]}";

    private const string PointerSpanSystemPrompt =
        "You receive PDF source blocks already proposed as heading-like. Return only a source pointer span for each id.\n" +
        "The span must select exactly the heading prefix inside source_text using zero-based start and exclusive end offsets.\n" +
        "Choose start only from allowed_start_offsets and end only from allowed_end_offsets supplied for that block.\n" +
        "Never rewrite, normalize, or return heading text. If a heading span cannot be determined from source_text, return null.\n" +
        "Format: {\"blocks\":[{\"id\":\"b1\",\"heading_span\":{\"start\":0,\"end\":19}}]}.";

    private const string CriticSystemPrompt =
        "You audit heading proposals that already have a valid source pointer. Decide whether each proposal should remain a document-outline heading.\n" +
        "Use source text and local context. Reject table labels, captions, body claims, inline references, and decorative labels.\n" +
        "Keep only a standalone topic that opens or organizes document content. If evidence conflicts or is insufficient, choose unresolved.\n" +
        "Return strict JSON only: {\"blocks\":[{\"id\":\"b1\",\"decision\":\"keep|reject|unresolved\"}]}.";

    internal static string PromptProfileBytes => RoleSystemPrompt + PointerSpanSystemPrompt + CriticSystemPrompt;

    public static string SemanticPromptSha256 => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(PromptProfileBytes))).ToLowerInvariant();
}
