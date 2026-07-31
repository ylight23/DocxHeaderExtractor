using System.Text;

namespace DocxHeaderExtractor.Core.Llm;

/// <summary>
/// Prompt và GBNF grammar cho tác vụ: từ XML tinh gọn, chọn ra đoạn nào là tiêu đề và ở cấp mấy.
/// Mô hình chỉ trả về chỉ số + cấp; phần văn bản luôn lấy lại từ OpenXML để tránh bịa nội dung.
/// </summary>
public static class HeaderPrompt
{
    public const string System = """
        Bạn là bộ phân tích cấu trúc tài liệu Word. Đầu vào là XML rút gọn từ file .docx.

        Mỗi <p> là một đoạn ứng viên với các thuộc tính:
        - i: chỉ số đoạn (BẮT BUỘC dùng đúng giá trị này khi trả lời)
        - s: tên style trong Word (vd Heading1, Normal, TieuDe)
        - out: giá trị w:outlineLvl (0 = cấp 1)
        - lvl: cấp tiêu đề do bộ lọc đoán trước (có thể sai)
        - b=1 in đậm, caps=1 chữ hoa, it=1 in nghiêng, u=1 gạch chân
        - sz: cỡ chữ (point), al: canh lề, num: danh sách đánh số
        - kn=1 giữ cùng trang với đoạn sau, pb=1 ngắt trang trước, tbl: nằm trong bảng
        <n c="k"/> nghĩa là k đoạn thân bài đã bị lược bỏ.
        <ctx> là đoạn văn ngay sau ứng viên, dùng làm ngữ cảnh.

        Nhiệm vụ: với MỖI thẻ <p>, quyết định nó có phải tiêu đề (heading) hay không,
        và nếu phải thì ở cấp mấy (1..9). Gán l=0 nếu không phải tiêu đề.

        Quy tắc:
        1. Style có "Heading"/"Title"/"Tiêu đề" hoặc có thuộc tính out LUÔN là tiêu đề (l ≥ 1),
           TRỪ khi nội dung là câu hoàn chỉnh kết thúc bằng dấu chấm, hoặc là dòng liệt kê mở đầu
           bằng "-", "+", "•" — tài liệu thật có đoạn thân bài bị gán nhầm outline level.
        2. RẤT NHIỀU tài liệu không dùng style Heading mà định dạng thủ công. Vẫn là tiêu đề khi
           đoạn NGẮN (dưới ~15 từ), có b=1 hoặc caps=1 hoặc al="center" hoặc sz lớn hơn thân bài,
           KHÔNG kết thúc bằng dấu chấm, và đoạn <ctx> ngay sau nó là văn xuôi dài.
           Đây là trường hợp quan trọng nhất — đừng bỏ sót.
        3. Thuộc tính lvl là cấp do bộ lọc đoán trước: dùng làm gợi ý mạnh, chỉ sửa khi thấy sai.
        4. Cấp phải nhất quán: "1." cấp 1, "1.1" cấp 2, "1.1.1" cấp 3; "Chương/Phần/Phụ lục"
           là cấp 1; "Điều/Mục" thấp hơn một cấp so với phần chứa nó.
        5. Gán l=0 cho: câu văn hoàn chỉnh, chú thích ảnh/bảng ("Hình 1:", "Bảng 2:"),
           dòng mục lục có số trang, tên tác giả, ngày tháng, số trang, gạch đầu dòng liệt kê
           trong thân bài.
        6. tbl=1 (nằm trong bảng) LUÔN l=0 — kể cả khi in đậm, viết hoa, canh giữa và trông hệt
           số hiệu mục ("II.1", "III.2") hay tên cột ("Ký hiệu", "Giải thích"). Ô bảng là dữ liệu:
           thứ nó gọi tên nằm ở ô bên cạnh, không nằm ở phần văn bản sau nó.
        7. Trả lời theo ĐÚNG thứ tự các <p> xuất hiện, mỗi <p> đúng một mục.
        8. Không giải thích. Chỉ in JSON.

        Ví dụ:
        Đầu vào
          <p i="0" s="Heading1" out="0" lvl="1" b="1">Chương 1. Quy định chung</p>
          <n c="4"/>
          <p i="6" s="Normal" lvl="1" b="1" caps="1" al="center" sz="14">PHỤ LỤC B – BIỂU MẪU</p>
            <ctx>Các biểu mẫu dưới đây áp dụng thống nhất cho toàn bộ đơn vị trực…</ctx>
          <p i="9" s="Normal" lvl="2" b="1" sz="13">2.1 Trình tự thực hiện</p>
          <p i="11" s="Normal" b="1" caps="1" al="center" tbl="1">II.3</p>
            <ctx>Hệ thống văn kiện diễn tập</ctx>
          <p i="12" s="Normal" b="1">Hình 3: Sơ đồ khối của hệ thống.</p>
          <p i="14" s="Normal" out="3" sz="14">- Kích thước dữ liệu: khoảng 200 GB trong 5 năm đầu.</p>
        Đầu ra
          {"h":[{"i":0,"l":1},{"i":6,"l":1},{"i":9,"l":2},{"i":11,"l":0},{"i":12,"l":0},{"i":14,"l":0}]}
        """;

    public static string BuildUser(string chunkXml) =>
        $"""
         XML tài liệu:
         {chunkXml}

         Trả lời cho từng <p> theo đúng thứ tự.
         """;

    /// <summary>
    /// GBNF liệt kê: ép mô hình sinh đúng một mục cho mỗi ứng viên, đúng thứ tự, với chỉ số
    /// đã cố định sẵn trong grammar. Mô hình chỉ còn tự do chọn một chữ số cấp,
    /// nên không thể bỏ sót hay bịa chỉ số — điểm yếu chính của mô hình 3B ở tác vụ này.
    /// </summary>
    public static string BuildEnumeratedGbnf(IReadOnlyList<int> indexes)
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

        sb.Append("lvl ::= [0-9]\n");
        return sb.ToString();
    }

    /// <summary>
    /// GBNF tự do: đúng lược đồ nhưng để mô hình tự chọn liệt kê bao nhiêu mục.
    /// Dùng khi bật <c>--free-grammar</c>; kém tin cậy hơn bản liệt kê ở trên.
    /// </summary>
    public const string Gbnf = """
        root   ::= "{" ws "\"h\"" ws ":" ws items ws "}"
        items  ::= "[" ws "]" | "[" ws item (ws "," ws item)* ws "]"
        item   ::= "{" ws "\"i\"" ws ":" ws int ws "," ws "\"l\"" ws ":" ws lvl ws "}"
        int    ::= [0-9] | [1-9] [0-9]{0,4}
        lvl    ::= [0-9]
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
