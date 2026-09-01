using System.Globalization;
using System.Text;

namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Document-family ontology derived from source facts. It is deliberately a small vocabulary of
/// structural roles, not a list of templates or document names. The model may disagree, but this
/// policy is the authority for roles that are never document-outline nodes.
/// </summary>
public enum PdfDomainRole
{
    Unknown,
    LegalPart,
    LegalChapter,
    LegalSection,
    LegalArticle,
    LegalClause,
    LegalPoint,
    ProcurementPart,
    ProcurementSection,
    ProcurementGroup,
    ProcurementClause,
    ProcurementSubclause,
    FinancialSection,
    FinancialNote,
    MeetingSession,
    MeetingAgenda,
    AmendmentAnnotation,
    EditorialInstruction,
    InlineClauseReference,
    FormFieldLabel,
    OutlineReference,
    TableTitle,
    FigureOrBoxCaption,
    RunningArtifact,
}

internal static class DocumentDomainPolicy
{
    public static DomainStructuralEvidence Observe(PdfSourceFacts source, string documentRegime)
    {
        var role = ClassifyRole(source, documentRegime);
        return EvidenceForRole(role);
    }

    public static DomainStructuralEvidence EvidenceForRole(PdfDomainRole role, string basis = "document-domain-detector") =>
        new(role, ProposedLevel(role), ProposesOutlineExclusion(role), IsStructuralRole(role), $"{basis}:{role}");

    public static string InferRegime(IEnumerable<string> texts, string fallback = "document_body")
    {
        var samples = texts.Take(600).Select(Fold).ToArray();
        var legalSignals = samples.Count(text => StartsWithAny(text, "PHAN ", "CHUONG ", "MUC ", "DIEU ", "KHOAN ", "DIEM "));
        if (legalSignals >= 3) return "legal";

        var procurementSignals = samples.Count(text => StartsWithAny(text, "PART ", "SECTION ") &&
            (text.Contains("BIDD", StringComparison.Ordinal) || text.Contains("CONTRACT", StringComparison.Ordinal) ||
             text.Contains("PROCUREMENT", StringComparison.Ordinal) || text.Contains("REQUEST FOR", StringComparison.Ordinal)));
        if (procurementSignals >= 2) return "procurement";
        if (samples.Any(text => text.Contains("NOTES TO FINANCIAL", StringComparison.Ordinal)) ||
            samples.Count(text => text.Contains("FINANCIAL STATEMENT", StringComparison.Ordinal)) >= 2) return "financial";
        if (samples.Count(text => text.StartsWith("MINUTES", StringComparison.Ordinal) ||
            System.Text.RegularExpressions.Regex.IsMatch(text, "^D\\d+\\.\\d+\\s*[-:]") ||
            text.StartsWith("SESSION ", StringComparison.Ordinal)) >= 2) return "meeting";
        return fallback;
    }

    public static PdfDomainRole Classify(PdfSourceFacts source, string documentRegime)
        => ClassifyRole(source, documentRegime);

    private static PdfDomainRole ClassifyRole(PdfSourceFacts source, string documentRegime)
    {
        if (source.StructuralScope == "running_page_artifact") return PdfDomainRole.RunningArtifact;
        if (source.StructuralScope == "table_of_contents") return PdfDomainRole.OutlineReference;
        if (source.StructuralScope is "table" or "appendix_table") return PdfDomainRole.TableTitle;

        var text = Fold(source.RawText);
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^(?:TABLE|FIGURE|BOX)\\s+\\d+"))
            return PdfDomainRole.FigureOrBoxCaption;
        if (ContainsAny(text, "DUOC SUA DOI", "DUOC BO SUNG", "SUA DOI, BO SUNG", "SUA DOI BO SUNG"))
            return PdfDomainRole.AmendmentAnnotation;
        if (ContainsAny(text, " IS REPLACED WITH ", " IS AMENDED BY ", " IS MODIFIED BY "))
            return PdfDomainRole.EditorialInstruction;
        if (LooksLikeFormField(text)) return PdfDomainRole.FormFieldLabel;
        if (LooksLikeInlineReference(text)) return PdfDomainRole.InlineClauseReference;

        if (documentRegime is "VietnameseLegal" or "legal") return LegalRole(text);
        if (documentRegime == "procurement") return ProcurementRole(text);
        if (documentRegime == "financial") return FinancialRole(text);
        if (documentRegime == "meeting") return MeetingRole(text);
        return PdfDomainRole.Unknown;
    }

    private static int? ProposedLevel(PdfDomainRole role) => role switch
    {
        PdfDomainRole.LegalPart or PdfDomainRole.ProcurementPart => 1,
        PdfDomainRole.LegalChapter or PdfDomainRole.ProcurementSection => 2,
        PdfDomainRole.LegalSection or PdfDomainRole.ProcurementGroup => 3,
        PdfDomainRole.LegalArticle or PdfDomainRole.ProcurementClause => 4,
        PdfDomainRole.LegalClause or PdfDomainRole.ProcurementSubclause => 5,
        PdfDomainRole.LegalPoint => 6,
        PdfDomainRole.FinancialSection or PdfDomainRole.MeetingSession => 1,
        PdfDomainRole.FinancialNote or PdfDomainRole.MeetingAgenda => 2,
        _ => null,
    };

    private static bool ProposesOutlineExclusion(PdfDomainRole role) => role is
        PdfDomainRole.AmendmentAnnotation or PdfDomainRole.EditorialInstruction or
        PdfDomainRole.InlineClauseReference or PdfDomainRole.FormFieldLabel or
        PdfDomainRole.OutlineReference or PdfDomainRole.TableTitle or PdfDomainRole.FigureOrBoxCaption or
        PdfDomainRole.RunningArtifact;

    private static bool IsStructuralRole(PdfDomainRole role) => role is
        PdfDomainRole.LegalPart or PdfDomainRole.LegalChapter or PdfDomainRole.LegalSection or
        PdfDomainRole.LegalArticle or PdfDomainRole.LegalClause or PdfDomainRole.LegalPoint or
        PdfDomainRole.ProcurementPart or PdfDomainRole.ProcurementSection or PdfDomainRole.ProcurementGroup or
        PdfDomainRole.ProcurementClause or PdfDomainRole.ProcurementSubclause or
        PdfDomainRole.FinancialSection or PdfDomainRole.FinancialNote or
        PdfDomainRole.MeetingSession or PdfDomainRole.MeetingAgenda;

    private static PdfDomainRole LegalRole(string text)
    {
        if (StartsWithAny(text, "PHAN ", "PART ")) return PdfDomainRole.LegalPart;
        if (StartsWithAny(text, "CHUONG ", "CHAPTER ")) return PdfDomainRole.LegalChapter;
        if (StartsWithAny(text, "MUC ")) return PdfDomainRole.LegalSection;
        if (StartsWithAny(text, "DIEU ", "ARTICLE ")) return PdfDomainRole.LegalArticle;
        if (StartsWithAny(text, "KHOAN ", "CLAUSE ")) return PdfDomainRole.LegalClause;
        if (StartsWithAny(text, "DIEM ", "POINT ")) return PdfDomainRole.LegalPoint;
        return PdfDomainRole.Unknown;
    }

    private static PdfDomainRole ProcurementRole(string text)
    {
        if (StartsWithAny(text, "PART ")) return PdfDomainRole.ProcurementPart;
        if (StartsWithAny(text, "SECTION ")) return PdfDomainRole.ProcurementSection;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Z][.)]\\s+")) return PdfDomainRole.ProcurementGroup;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^\\d{1,3}\\.\\d{1,3}[.)]?\\s+")) return PdfDomainRole.ProcurementSubclause;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^\\d{1,3}[.)]\\s+")) return PdfDomainRole.ProcurementClause;
        return PdfDomainRole.Unknown;
    }

    private static PdfDomainRole FinancialRole(string text)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^SECTION\\s+[IVXLCDM]+[.: -]")) return PdfDomainRole.FinancialSection;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^NOTE\\s+[A-Z0-9]+(?:[.: -]|$)")) return PdfDomainRole.FinancialNote;
        return PdfDomainRole.Unknown;
    }

    private static PdfDomainRole MeetingRole(string text)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^SESSION\\s+[IVXLCDM0-9]+[.: -]")) return PdfDomainRole.MeetingSession;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Z]\\d+\\.\\d+\\s*[-:]")) return PdfDomainRole.MeetingAgenda;
        return PdfDomainRole.Unknown;
    }

    private static bool LooksLikeFormField(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Z][A-Z ]{1,48}:\\s*(?:\\[|$)");

    private static bool LooksLikeInlineReference(string text) =>
        ((text.Contains("SUB-CLAUSE ", StringComparison.Ordinal) || text.Contains("KHOAN ", StringComparison.Ordinal)) &&
         !System.Text.RegularExpressions.Regex.IsMatch(text, "^(?:SUB-CLAUSE|KHOAN)\\s+\\d+(?:\\.\\d+)?[.)]?")) ||
        (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:SECTION|APPENDIX)\s+(?:[A-Z]|\d+(?:\.\d+)*)\b") &&
         !System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:SECTION|APPENDIX)\s+(?:[A-Z]|\d+(?:\.\d+)*)[.):-]?\s+[^.]{1,180}$"));

    private static bool StartsWithAny(string value, params string[] prefixes) => prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));
    private static bool ContainsAny(string value, params string[] fragments) => fragments.Any(fragment => value.Contains(fragment, StringComparison.Ordinal));

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character is '\u0110' or '\u0111' ? 'D' : char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }
}

/// <summary>
/// Domain detection output is evidence and proposal input. It is intentionally not a validated
/// structural element and cannot authorize a structural graph by itself.
/// </summary>
internal sealed record DomainStructuralEvidence(
    PdfDomainRole Role,
    int? ProposedLevel,
    bool ProposesOutlineExclusion,
    bool IsStructuralRole,
    string Basis)
{
    public static readonly DomainStructuralEvidence Unknown = new(
        PdfDomainRole.Unknown, null, false, false, "no-domain-evidence");
}

/// <summary>Public, source-text-only regime inference for diagnostics and scheduler benchmarks.</summary>
public static class PdfDocumentRegime
{
    public static string Infer(IEnumerable<string> texts) => DocumentDomainPolicy.InferRegime(texts);
}
