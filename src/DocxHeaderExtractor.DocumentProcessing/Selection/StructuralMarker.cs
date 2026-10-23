using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.DocumentProcessing.Selection;

/// <summary>
/// The start of a structural heading in Vietnamese legal text: Chương, Mục, a numbered Điều, or Phụ lục, at the start of a line. A group of
/// selected lines may not hide one of these behind another (see SelectionAuthority).
/// </summary>
internal static class StructuralMarker
{
    static readonly Regex Start = new(
        @"^\s*(Chương\s*[IVXLCDM]+|CHƯƠNG\s*[IVXLCDM]+|Mục\s*\d+|MỤC\s*\d+|Điều\s*\d+\.|ĐIỀU\s*\d+\.|Phụ lục|PHỤ LỤC)",
        RegexOptions.Compiled);

    public static bool Matches(string text) => Start.IsMatch(text);
}
