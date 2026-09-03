using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;

/// <summary>Một đoạn trong document.xml kèm địa chỉ ổn định và ngữ cảnh duyệt.</summary>
public sealed record WalkedParagraph(
    Paragraph Element,
    string StableId,
    int TableDepth,
    int SectionIndex);

/// <summary>
/// Thứ tự duyệt paragraph của document.xml — nguồn duy nhất sinh ra <c>index</c> và
/// <c>stableId</c>. Cả đường đọc source-native và đường ghi
/// (<see cref="OutlineWriteback"/>) đều dùng bộ duyệt này, nên chỉ số mà model trả về luôn
/// trỏ đúng phần tử XML khi ghi ngược. Tách đôi hai bộ duyệt là cách chắc chắn nhất để
/// writeback đặt heading sai chỗ sau một thay đổi nhỏ ở một bên.
/// </summary>
public static class ParagraphWalker
{
    public static IEnumerable<WalkedParagraph> Enumerate(OpenXmlElement body, ExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);
        return Walk(body, new WalkState(), tableDepth: 0, path: "body[1]", options);
    }

    private sealed class WalkState
    {
        public int SectionIndex;
    }

    private static IEnumerable<WalkedParagraph> Walk(
        OpenXmlElement parent,
        WalkState state,
        int tableDepth,
        string path,
        ExtractionOptions options)
    {
        var ordinalByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in parent.ChildElements)
        {
            var name = child.LocalName;
            var ordinal = ordinalByName.GetValueOrDefault(name) + 1;
            ordinalByName[name] = ordinal;
            var childPath = $"{path}/{name}[{ordinal}]";

            switch (child)
            {
                case Paragraph p:
                    yield return new WalkedParagraph(p, childPath, tableDepth, state.SectionIndex);

                    // Textbox (w:txbxContent) nằm lồng trong drawing của paragraph. Nếu chỉ lấy
                    // Paragraph cấp ngoài thì text trong hộp bị nối vào đoạn neo hoặc biến mất.
                    // Tách mỗi textbox thành các paragraph riêng, ngay sau neo.
                    var textBoxes = p.Descendants<TextBoxContent>()
                        .Where(t => !t.Ancestors<TextBoxContent>().Any())
                        .ToList();
                    for (var box = 0; box < textBoxes.Count; box++)
                        foreach (var nested in Walk(
                                     textBoxes[box], state, tableDepth,
                                     $"{childPath}/txbxContent[{box + 1}]", options))
                            yield return nested;

                    if (p.ParagraphProperties?.SectionProperties is not null) state.SectionIndex++;
                    break;

                case Table when !options.IncludeTables:
                    break;

                case Table:
                    foreach (var nested in Walk(child, state, tableDepth + 1, childPath, options))
                        yield return nested;
                    break;

                case SectionProperties:
                    state.SectionIndex++;
                    break;

                default:
                    // sdt, customXml, TableRow, TableCell, bookmark container… – đệ quy nếu còn đoạn bên trong.
                    if (child.HasChildren && child.Descendants<Paragraph>().Any())
                        foreach (var nested in Walk(child, state, tableDepth, childPath, options))
                            yield return nested;
                    break;
            }
        }
    }
}
