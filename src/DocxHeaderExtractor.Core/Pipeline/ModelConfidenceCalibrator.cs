namespace DocxHeaderExtractor.Core.Pipeline;

/// <summary>
/// Thang confidence cho quyết định ngữ nghĩa của model. Đây là evidence tier, không giả làm
/// xác suất token: một lượt ổn định đạt 0.80, hai lượt đồng thuận đạt 0.85, bất đồng chỉ 0.75.
/// </summary>
public static class ModelConfidenceCalibrator
{
    public static double FromPasses(bool builtInStyle, bool twoPass, bool passA, bool passB)
    {
        if (builtInStyle) return 1.00;
        if (!twoPass) return passA ? 0.80 : 0.00;
        if (passA && passB) return 0.85;
        return passA || passB ? 0.75 : 0.00;
    }

    /// <summary>Lượt critic dùng prompt đối nghịch vẫn xác nhận lại giả thuyết yếu.</summary>
    public const double CriticConfirmed = 0.85;
}
