using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.V6Qualification.P7;

/// <summary>
/// The minimal redaction used for a document the owner supplies from outside the public corpus (D4.11j): telephone numbers and e-mail addresses
/// are replaced by a fixed placeholder before any text goes into a request or a tool result. Nothing else is changed: names, titles, dates and
/// layout stay, so the document still reads as itself. The original source identity is never replaced by the redacted one: the plan records
/// the file hash (the original), the hash of the redacted line texts, and how many spans were replaced on which lines (REDACTED_COPY OF ORIGINAL).
/// </summary>
internal static class P7F1V6Redaction
{
    public const string Version = "P7_F1_V6_MINIMAL_CONTACT_REDACTION_V1";
    public const string PhonePlaceholder = "[PHONE]";
    public const string EmailPlaceholder = "[EMAIL]";
    public const string Rules = "telephone numbers (a leading 0 or +84 followed by 8 to 10 digits, optionally separated by spaces, dots or hyphens) become [PHONE]; e-mail addresses become [EMAIL]; nothing else is changed";

    static readonly Regex Phone = new(@"(?<![\d])(?:\+84|0)(?:[ .\-]?\d){8,10}(?![\d])", RegexOptions.CultureInvariant);
    static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);

    public static string Apply(string text) => Apply(text, out _);

    public static string Apply(string text, out int replaced)
    {
        var n = 0;
        var e = Email.Replace(text, _ => { n++; return EmailPlaceholder; });
        var p = Phone.Replace(e, _ => { n++; return PhonePlaceholder; });
        replaced = n;
        return p;
    }
}
