namespace DocxHeaderExtractor.Core.V5;

/// <summary>
/// Canonical newlines for code-authored protocol and system prompts. A raw-string prompt takes its
/// line endings from how the source file was checked out (CRLF under core.autocrlf on Windows), so
/// without this the same commit could put different bytes on the wire. Apply it only to prompts the
/// code authors: a user message carries document text, whose CR/LF may be real data.
/// </summary>
public static class QualifiedPromptText
{
    public static string Canonicalize(string value) => value.ReplaceLineEndings("\n");
}
