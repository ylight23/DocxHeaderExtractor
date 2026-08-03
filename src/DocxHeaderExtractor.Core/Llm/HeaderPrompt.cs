using System.Text;

namespace DocxHeaderExtractor.Core.Llm;

/// <summary>
/// Prompt và GBNF grammar cho tác vụ: từ view trung lập của tài liệu, chọn đoạn nào là tiêu đề.
/// Mô hình chỉ trả về chỉ số + cấp; phần văn bản luôn lấy lại từ OpenXML để tránh bịa nội dung.
/// </summary>
public static class HeaderPrompt
{
    /// <summary>
    /// Phần luật phân loại, KHÔNG phụ thuộc định dạng đầu ra. Hai biến thể prompt (JSON và chữ số)
    /// dùng chung nguyên văn phần này, nên khi so tốc độ giữa hai lược đồ thì luật không đổi.
    /// </summary>
    private const string Rules = """
        Bạn là bộ phân tích cấu trúc tài liệu Word. Đầu vào là DOCUMENT_VIEW trung lập được chiếu
        từ OOXML; nó không phải dữ liệu chuẩn để ghi ngược và không đánh dấu sẵn heading bằng #/##.

        Mỗi BLOCK gồm metadata JSON và content nguyên văn:
        - i: chỉ số đoạn; requested=true nghĩa là BẮT BUỘC trả đúng một quyết định cho i đó
        - requested=false chỉ là context, tuyệt đối không đưa i của nó vào output
        - stableId: địa chỉ nguồn ổn định trong OOXML
        - source: paragraph hoặc table_cell; tableDepth là độ sâu bảng
        - styleId/styleName; outlineLevel (0 = cấp 1); guessedLevel có thể sai
        - bold/allCaps/italic/underline; boldSpans là các khoảng ký tự in đậm
        - verifiedHeadingSpan/verifiedBodySpan: code đã xác minh ranh giới heading/body; dùng làm
          evidence mạnh và không đưa phần body vào tên heading
        - fontSizePt, alignment, numberingId/numberingLevel/numberLabel
        - keepNext, pageBreakBefore, sectionIndex, inTableOfContents
        OMITTED_NORMAL_BLOCKS chỉ cho biết số đoạn thân bài đã lược bỏ.
        VERIFIED_EXAMPLES chứa correction do người dùng từng sửa thật sự. Đây chỉ là ví dụ tham
        khảo: không có i cần trả lời và không được sao chép máy móc.
        Các BLOCK kề nhau là ngữ cảnh của nhau; hãy dùng chuỗi đoạn trước/sau, không quyết định
        chỉ từ một kiểu chữ hoặc một từ khoá.

        Nhiệm vụ: với MỖI BLOCK được yêu cầu, phân loại vai trò rồi quyết định có đưa vào cây heading không.
        Mã vai trò: h=heading; d=document_title; t=table_header; f=form_label;
        s=signature_label; c=caption; n=normal_text; u=uncertain.
        Chỉ r=h mới có l=1..9; mọi vai trò khác có l=0. Document title là tiêu đề ngữ nghĩa
        nhưng không tự động thuộc cây mục lục.

        Quy tắc:
        1. Style, outlineLevel, guessedLevel, định dạng, numbering và tableDepth chỉ là BẰNG CHỨNG,
           không phải luật tuyệt đối.
           File Word thường gán nhầm outline level hoặc dùng bảng để dàn trang.
        2. Heading có thể không đậm, không đánh số, không khác cỡ chữ. Hãy nhận ra nó từ vai trò
           trong luồng tài liệu: mở một chủ đề, sau đó là nội dung chi tiết, hoặc thuộc một chuỗi
           đề mục cùng cấp.
        3. Gán l=0 cho metadata/biểu mẫu, câu hướng dẫn, nơi nhận, chữ ký, ô dữ liệu, caption,
           mục lục và list item. Chỉ dựa vào ngữ nghĩa cùng vị trí trong luồng, không dựa vào
           một từ khoá duy nhất.
        3a. Phân biệt phần đầu văn bản với cây đề mục thân bài:
            - Dấu phân loại bảo mật/khẩn, tên cơ quan, số hiệu, nơi nhận, ngày tháng và nhãn hành
              chính là front-matter/form_label (r=f,l=0), dù ngắn, in hoa, đậm hoặc căn giữa.
            - Tên chính của báo cáo/văn bản là document_title (r=d,l=0), dù trông nổi bật như H1.
            - Chỉ dùng r=h khi đoạn mở một phần nội dung và có vai trò tổ chức thân bài. Định dạng
              nổi bật hoặc vị trí gần đầu tài liệu tự nó không đủ biến nhãn/title thành heading.
        4. source=table_cell là bằng chứng yếu: bảng đầu/cuối thường là biểu mẫu hoặc chữ ký, nhưng
           bảng giữa thân bài vẫn có thể chứa heading thật.
        5. Cấp phải nhất quán với ngữ cảnh. "1." không luôn là H1: sau "I." nó thường là H2;
           "3.1" thường là con của "3". lvl chỉ là gợi ý, có thể sai.
        6. Trả lời theo ĐÚNG thứ tự BLOCK xuất hiện, mỗi BLOCK được hỏi đúng một mục. Không giải thích,
           chỉ in JSON.
        """;

    /// <summary>Ví dụ one-shot, dùng chung; chỉ dòng "Đầu ra" khác nhau giữa hai lược đồ.</summary>
    private const string ExampleInput = """
        Ví dụ:
        Đầu vào
          BLOCK metadata: {"i":0,"requested":true,"styleId":"Heading1","outlineLevel":0,"guessedLevel":1,"bold":true}
          content: Chương 1. Quy định chung
          OMITTED_NORMAL_BLOCKS {"count":4}
          BLOCK metadata: {"i":6,"requested":true,"styleId":"Normal","guessedLevel":1,"bold":true,"allCaps":true,"alignment":"center","fontSizePt":14}
          content: PHỤ LỤC B – BIỂU MẪU
          BLOCK metadata: {"i":9,"requested":true,"styleId":"Normal","guessedLevel":2,"bold":true,"fontSizePt":13}
          content: 2.1 Trình tự thực hiện
          BLOCK metadata: {"i":11,"requested":true,"source":"table_cell","tableDepth":1,"bold":true,"allCaps":true,"alignment":"center"}
          content: II.3
          BLOCK metadata: {"i":12,"requested":true,"bold":true}
          content: Hình 3: Sơ đồ khối của hệ thống.
          BLOCK metadata: {"i":14,"requested":true,"outlineLevel":3,"fontSizePt":14}
          content: - Kích thước dữ liệu: khoảng 200 GB trong 5 năm đầu.
        """;

    // $$ để dấu ngoặc nhọn của ví dụ JSON là ký tự thật; chỗ nội suy dùng {{…}}.
    /// <summary>Lược đồ JSON: mỗi ứng viên một object {"i":…,"l":…}.</summary>
    public static readonly string System = $$"""
        {{Rules}}
        7. Trả lời theo ĐÚNG thứ tự các BLOCK xuất hiện, mỗi BLOCK được hỏi đúng một mục.
        8. Không giải thích. Chỉ in JSON.

        {{ExampleInput}}
        Đầu ra
          {"h":[{"i":0,"r":"h","l":1},{"i":6,"r":"h","l":1},{"i":9,"r":"h","l":2},{"i":11,"r":"t","l":0},{"i":12,"r":"c","l":0},{"i":14,"r":"n","l":0}]}
        """;

    // ĐÃ THỬ VÀ BỎ: lược đồ đầu ra chỉ gồm dãy chữ số, chỉ số suy từ vị trí (1 token/ứng viên
    // thay vì ~16). Đo trên tài liệu thật: precision 100% → 73,3%, và KHÔNG nhanh hơn một giây
    // nào (742 s so với 738 s). Mô hình mất luôn nhiệm vụ — có khối nó chép thẳng chỉ số đoạn
    // ra làm đáp án ("445448551552"). Con số đó cũng bác bỏ giả thuyết "sinh token là nút cổ
    // chai": cắt 90% token sinh ra mà tổng thời gian đứng yên ⇒ thời gian nằm ở khâu nạp prompt.

    public static string BuildUser(string documentView) =>
        $"""
         View tài liệu trung lập:
         {documentView}

         Trả lời cho từng BLOCK được hỏi theo đúng thứ tự.
         """;


    /// <summary>
    /// Prompt phản biện chỉ dùng cho các mục model-only yếu. Không tiết lộ câu trả lời lượt đầu
    /// để tránh hiệu ứng neo; yêu cầu chủ động tìm vai trò ngoài cây heading.
    /// </summary>
    public const string CriticSystem = """
        Bạn là bộ phản biện cấu trúc tài liệu Word. Một mô hình khác đã nghi rằng các đoạn đầu
        vào là heading, nhưng bằng chứng định dạng/cấu trúc của chúng yếu. Hãy đánh giá lại từ
        đầu và CHỦ ĐỘNG tìm phản ví dụ cho giả thuyết heading.

        Mã vai trò: h=heading; d=document_title; t=table_header; f=form_label;
        s=signature_label; c=caption; n=normal_text; u=uncertain.
        Chỉ r=h mới có l=1..9; mọi vai trò khác có l=0.

        Heading phải thật sự tổ chức thân bài: mở một phần nội dung có phạm vi bên dưới hoặc là
        thành viên của chuỗi đề mục cùng cấp. Dòng điều phối hành chính, địa chỉ/người gửi-người
        nhận, lời chuyển/kính gửi, tên cơ quan, mã biểu mẫu, dấu mật/khẩn, ngày tháng, chữ ký,
        caption và tiêu đề chính của văn bản không tự động thuộc cây heading, dù ngắn, đậm,
        viết hoa hoặc đứng đầu trang. Hãy quyết định theo toàn bộ ngữ cảnh trước/sau, không theo
        một từ khóa riêng lẻ. Nếu cả hai cách hiểu còn hợp lý và không đủ bằng chứng, trả r=u.

        Không đồng ý chỉ vì mô hình trước đã chọn. Với MỖI BLOCK được hỏi, trả đúng một quyết định theo
        đúng i và thứ tự; không giải thích, chỉ JSON.
        """;

    public static string BuildCriticUser(string documentView) =>
        $"""
         View tài liệu trung lập (BLOCK được hỏi là giả thuyết cần phản biện; các block khác là ngữ cảnh):
         {documentView}

         Phân loại lại từng BLOCK được hỏi độc lập với quyết định trước.
         """;

    /// <summary>
    /// GBNF liệt kê: ép mô hình sinh đúng một mục cho mỗi ứng viên, đúng thứ tự, với chỉ số
    /// đã cố định sẵn trong grammar. Mô hình chỉ còn tự do chọn một chữ số cấp,
    /// nên không thể bỏ sót hay bịa chỉ số — điểm yếu chính của mô hình 3B ở tác vụ này.
    /// </summary>
    public static string BuildEnumeratedGbnf(IReadOnlyList<int> indexes, bool allowZero = true)
    {
        if (indexes.Count == 0) return "root ::= \"{\\\"h\\\":[]}\"\n";

        var sb = new StringBuilder();
        sb.Append("root ::= \"{\\\"h\\\":[\"");
        for (int k = 0; k < indexes.Count; k++)
        {
            if (k > 0) sb.Append(" \",\"");
            sb.Append(" it").Append(k);
        }
        sb.Append(" \"]}\"\n");

        for (int k = 0; k < indexes.Count; k++)
            sb.Append($"it{k} ::= \"{{\\\"i\\\":{indexes[k]},\\\"l\\\":\" lvl \"}}\"\n");

        sb.Append(allowZero ? "lvl ::= [0-9]\n" : "lvl ::= [1-9]\n");
        return sb.ToString();
    }

    /// <summary>Grammar đa nhãn: heading buộc có cấp 1..9, mọi vai trò ngoài cây buộc l=0.</summary>
    public static string BuildRoleEnumeratedGbnf(IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0) return "root ::= \"{\\\"h\\\":[]}\"\n";

        var sb = new StringBuilder("root ::= \"{\\\"h\\\":[\"");
        for (var k = 0; k < indexes.Count; k++)
        {
            if (k > 0) sb.Append(" \",\"");
            sb.Append(" it").Append(k);
        }
        sb.Append(" \"]}\"\n");
        for (var k = 0; k < indexes.Count; k++)
        {
            var prefix = $"{{\\\"i\\\":{indexes[k]},\\\"r\\\":\\\"";
            sb.Append($"it{k} ::= \"{prefix}h\\\",\\\"l\\\":\" hlvl \"}}\" | ")
              .Append($"\"{prefix}\" nonheading \"\\\",\\\"l\\\":0}}\"\n");
        }
        sb.Append("hlvl ::= [1-9]\n");
        sb.Append("nonheading ::= [dtfscnu]\n");
        return sb.ToString();
    }

    /// <summary>Prompt lượt hai: chỉ dựng lại hierarchy từ danh sách heading đã được chọn.</summary>
    public const string HierarchySystem = """
        Bạn dựng cây heading cho tài liệu Word. Đầu vào chỉ gồm các heading đã được chọn theo thứ tự.
        Với MỖI <h>, trả về cấp 1..9. Hãy nhìn TOÀN BỘ danh sách: cấp là tương đối trong tài liệu,
        không phải số dấu chấm đơn lẻ. Style built-in và out là bằng chứng mạnh; lvl và num chỉ là
        gợi ý. Không được loại mục nào, không được giải thích, chỉ trả JSON.
        """;

    public static string BuildHierarchyUser(IReadOnlyList<HierarchyItem> headings) =>
        BuildHierarchyUser([], headings);

    /// <summary>Context là các heading ngay trước batch, đã có cấp tạm thời, chỉ để làm mốc.</summary>
    public static string BuildHierarchyUser(
        IReadOnlyList<HierarchyItem> context,
        IReadOnlyList<HierarchyItem> headings)
    {
        var sb = new StringBuilder("<outline>\n");
        foreach (var h in context)
        {
            sb.Append("<ctx i=\"").Append(h.Index).Append('\"');
            if (h.HintLevel is { } hint) sb.Append(" lvl=\"").Append(hint).Append('\"');
            sb.Append('>').Append(h.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"))
              .Append("</ctx>\n");
        }
        foreach (var h in headings)
        {
            sb.Append("<h i=\"").Append(h.Index).Append('\"');
            if (h.StyleLevel is { } style) sb.Append(" style=\"").Append(style).Append('\"');
            if (h.OutlineLevel is { } outline) sb.Append(" out=\"").Append(outline).Append('\"');
            if (h.HintLevel is { } hint) sb.Append(" lvl=\"").Append(hint).Append('\"');
            if (!string.IsNullOrEmpty(h.Numbering)) sb.Append(" num=\"").Append(h.Numbering).Append('\"');
            sb.Append('>').Append(h.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"))
              .Append("</h>\n");
        }
        sb.Append("</outline>");
        return $"Danh sách heading:\n{sb}\nTrả lời cho từng <h> theo đúng thứ tự.";
    }

    /// <summary>
    /// GBNF tự do: đúng lược đồ nhưng để mô hình tự chọn liệt kê bao nhiêu mục.
    /// Dùng khi bật <c>--free-grammar</c>; kém tin cậy hơn bản liệt kê ở trên.
    /// </summary>
    public const string Gbnf = """
        root   ::= "{" ws "\"h\"" ws ":" ws items ws "}"
        items  ::= "[" ws "]" | "[" ws item (ws "," ws item)* ws "]"
        item   ::= heading | nonheading
        heading ::= "{" ws "\"i\"" ws ":" ws int ws "," ws "\"r\"" ws ":" ws "\"h\"" ws "," ws "\"l\"" ws ":" ws hlvl ws "}"
        nonheading ::= "{" ws "\"i\"" ws ":" ws int ws "," ws "\"r\"" ws ":" ws "\"" nonrole "\"" ws "," ws "\"l\"" ws ":" ws "0" ws "}"
        int    ::= [0-9] | [1-9] [0-9]{0,4}
        hlvl   ::= [1-9]
        nonrole ::= [dtfscnu]
        ws     ::= [ \t\n]{0,4}
        """;

    public const string GrammarRoot = "root";

    /// <summary>
    /// Chat template Llama 3.x dựng tay – dùng khi file GGUF không kèm template.
    /// Không chèn &lt;|begin_of_text|&gt;: StatelessExecutor tokenize với addBos = true nên BOS
    /// đã được thêm sẵn; chèn thêm sẽ thành BOS kép và làm lệch phân phối.
    /// </summary>
    public static string BuildLlama3Prompt(string system, string user)
    {
        var sb = new StringBuilder();
        sb.Append("<|start_header_id|>system<|end_header_id|>\n\n");
        sb.Append(system).Append("<|eot_id|>");
        sb.Append("<|start_header_id|>user<|end_header_id|>\n\n");
        sb.Append(user).Append("<|eot_id|>");
        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    public static readonly IReadOnlyList<string> AntiPrompts =
    [
        "<|eot_id|>", "<|end_of_text|>", "<|start_header_id|>",
    ];
}
