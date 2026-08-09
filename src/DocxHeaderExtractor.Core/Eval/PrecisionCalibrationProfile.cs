using System.Text.Encodings.Web;
using System.Text.Json;
using System.Globalization;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.Pipeline;

namespace DocxHeaderExtractor.Core.Eval;

/// <summary>
/// Precision đo trên holdout theo từng chữ ký evidence. Chỉ profile này mới biến evidence score
/// thành confidence đã calibration; dữ liệu train/rule không được dùng làm holdout.
/// </summary>
public sealed class PrecisionCalibrationProfile
{
    public const string CurrentFormat = "dhx-precision-calibration/v1";
    // Bumped after critic contradiction handling and CRITIC_ANCHORS changed the prediction
    // distribution; old holdout precision must not silently calibrate this pipeline.
    public const string CurrentPipelineSignature = "dhx-semantic-precision/2026-08-04-v2";

    public string FormatVersion { get; init; } = CurrentFormat;
    public string PipelineSignature { get; init; } = CurrentPipelineSignature;
    public string ConfigurationSignature { get; init; } = "";
    public string? Model { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public int Documents { get; init; }
    public IReadOnlyList<PrecisionCalibrationBucket> Buckets { get; init; } = [];

    public PrecisionCalibrationBucket? Find(string signature) =>
        Buckets.FirstOrDefault(x => string.Equals(x.Signature, signature, StringComparison.Ordinal));

    public static PrecisionCalibrationProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<PrecisionCalibrationProfile>(File.ReadAllText(path), JsonOptions)
            ?? throw new FormatException("Calibration profile rỗng.");
        if (profile.FormatVersion != CurrentFormat)
            throw new FormatException($"Không hỗ trợ calibration profile '{profile.FormatVersion}'.");
        if (profile.PipelineSignature != CurrentPipelineSignature)
            throw new FormatException(
                $"Calibration profile thuộc pipeline '{profile.PipelineSignature}', cần '{CurrentPipelineSignature}'. " +
                "Phải chạy lại holdout sau khi đổi prompt/rule quan trọng.");
        return profile;
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static double WilsonLowerBound(int correct, int total, double z = 1.96)
    {
        if (total <= 0) return 0;
        var p = (double)correct / total;
        var z2 = z * z;
        var denominator = 1 + z2 / total;
        var center = p + z2 / (2 * total);
        var margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * total)) / total);
        return Math.Max(0, (center - margin) / denominator);
    }

    /// <summary>
    /// Các option có thể làm đổi phân phối dự đoán phải khóa cùng profile.
    /// <para>
    /// <b>Trục tái lập của RUNTIME cũng nằm ở đây, không chỉ trục thuật toán.</b> Thiếu chúng thì
    /// hai lượt chạy ở hai mức offload GPU sinh ra CÙNG một chữ ký, nên profile dựng ở mức này được
    /// coi là còn hiệu lực ở mức kia — trong khi §3.7 đo được `-ngl 20` cho đúng cấp 100% còn
    /// `-ngl 99` cho 85,7%, tái lập ở cả hai lượt. Đó là hai cấu hình đo khác nhau, và chữ ký phải
    /// nói ra điều đó thay vì để tài liệu nhắc suông.
    /// </para>
    /// </summary>
    public static string ConfigurationFor(PipelineOptions o) => string.Join('|',
        $"backend={o.Backend}",
        $"twoPass={o.TwoPass}",
        $"rollingOutline={o.RollingOutline}",
        $"highPrecision={o.HighPrecisionMode}",
        $"trustStyles={o.TrustStyles}",
        $"skipStyled={o.SkipStyledCandidates}",
        $"styleAutoAssign={o.StyleAutoAssign}",
        $"reviewAll={o.ReviewAllParagraphs}",
        $"globalHierarchy={o.GlobalHierarchy}",
        $"normalizeLevels={o.NormalizeLevels}",
        $"levelFromOutline={o.LevelFromOutline}",
        $"audit={o.AuditNumbering}",
        $"recover={o.RecoverNumberedSiblings}",
        $"useLexical={o.Extraction.UseLexicalRules}",
        $"includeTables={o.Extraction.IncludeTables}",
        $"includeContext={o.Extraction.IncludeFollowingContext}",
        $"ctx={o.Llama.ContextSize}",
        $"chunkTokens={o.Chunking.TokenBudget}",
        $"chunkCandidates={o.Chunking.MaxCandidatesPerChunk}",
        $"overlap={o.Chunking.Overlap}",
        $"grammar={o.Llama.GrammarMode}",
        $"temperature={o.Llama.Temperature.ToString("R", CultureInfo.InvariantCulture)}",
        $"seed={o.Llama.Seed}",
        $"gpuLayers={o.Llama.GpuLayerCount}",
        $"threshold={o.Extraction.CandidateThreshold.ToString("R", CultureInfo.InvariantCulture)}",
        $"standaloneLines={o.Extraction.PromoteStandaloneLines}",
        $"skipContentControls={o.Extraction.SkipContentControls}",
        $"bareLabels={o.Extraction.AllowBareLabelledNumbers}");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed record PrecisionCalibrationBucket(
    string Signature,
    int Samples,
    int Correct,
    double Precision,
    double WilsonLower95);

/// <summary>Thu kết quả dự đoán trên các tài liệu holdout đã gán nhãn đầy đủ.</summary>
public sealed class PrecisionCalibrationBuilder
{
    private readonly Dictionary<string, (int Samples, int Correct)> _counts = new(StringComparer.Ordinal);
    private int _documents;
    private readonly string _configurationSignature;
    private string? _model;

    public PrecisionCalibrationBuilder(string configurationSignature = "") =>
        _configurationSignature = configurationSignature;

    public void Add(DocumentOutline outline, AnswerKey key)
    {
        if (_documents == 0) _model = outline.Model;
        else if (!string.Equals(_model, outline.Model, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Không được trộn nhiều model vào cùng một calibration profile.");
        _documents++;
        foreach (var heading in outline.Headings)
        {
            var signature = HeadingAcceptanceSignature.For(heading);
            var expectedLevel = key.LevelOf(heading.Index);
            var correct = key.Contains(heading.Index) &&
                (expectedLevel is null || expectedLevel.Value == heading.Level);
            var old = _counts.GetValueOrDefault(signature);
            _counts[signature] = (old.Samples + 1, old.Correct + (correct ? 1 : 0));
        }
    }

    public PrecisionCalibrationProfile Build() => new()
    {
        Documents = _documents,
        Model = _model,
        ConfigurationSignature = _configurationSignature,
        Buckets = _counts.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new PrecisionCalibrationBucket(
                x.Key,
                x.Value.Samples,
                x.Value.Correct,
                Math.Round((double)x.Value.Correct / x.Value.Samples, 6),
                Math.Round(PrecisionCalibrationProfile.WilsonLowerBound(x.Value.Correct, x.Value.Samples), 6)))
            .ToList(),
    };
}
