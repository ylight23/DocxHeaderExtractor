using DocxHeaderExtractor.App;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Giá trị mặc định đọc thẳng từ Core và môi trường server,
/// để giao diện không tự chép hằng số rồi lệch khỏi CLI khi Core đổi.
/// </summary>
public sealed record Defaults(bool OpenRouterAvailable, string OpenRouterModel)
{
    public static Defaults Current() => new(OpenRouterProvider.FromEnvironment().HasKey, OpenRouterProvider.Model);
}
