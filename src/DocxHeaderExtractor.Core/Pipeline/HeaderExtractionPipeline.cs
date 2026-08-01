using System.Diagnostics;
using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Eval;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Learning;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

public enum InferenceBackend
{
    Local,
    OpenRouter,
}

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();
    public LlamaOptions Llama { get; set; } = new();
    public OpenRouterOptions OpenRouter { get; set; } = OpenRouterOptions.FromEnvironment();
    public InferenceBackend Backend { get; set; }

    /// <summary>Bỏ qua LLM, chỉ dùng luật (nhanh, để đối chiếu).</summary>
    public bool DisableLlm { get; set; }

    /// <summary>Luôn giữ đoạn có style heading kể cả khi mô hình bỏ sót.</summary>
    public bool TrustStyles { get; set; } = true;

    /// <summary>
    /// Không hỏi mô hình về đoạn đã có style heading / <c>w:outlineLvl</c> — chúng vẫn nằm trong
    /// XML làm ngữ cảnh, chỉ là không bị hỏi.
    /// <para>
    /// MẶC ĐỊNH TẮT — nghe thì có vẻ miễn phí nhưng ĐO ĐƯỢC LÀ KHÔNG. Lập luận "câu trả lời cho
    /// nhóm có style không đổi được kết quả vì TrustStyles khôi phục hết" chỉ đúng khi câu trả
    /// lời của mô hình là cố định. Thực tế bỏ 32 câu hỏi ra khỏi khối làm đổi thành phần khối,
    /// và mô hình trả lời những đoạn CÒN LẠI khác đi: trên tài liệu thật, precision tụt từ 100%
    /// xuống 94,1% (nhận nhầm hai ô tiêu đề bảng) để đổi lấy 24% thời gian.
    /// Tiêu đề có style nằm xen kẽ đóng vai trò neo cho chuỗi sinh tự hồi quy.
    /// </para>
    /// </summary>
    public bool SkipStyledCandidates { get; set; }

    /// <summary>Chuẩn hoá cấp để không nhảy cóc (1 → 3 thành 1 → 2).</summary>
    public bool NormalizeLevels { get; set; } = true;

    /// <summary>
    /// Đoạn có <c>w:outlineLvl</c> thì lấy cấp từ đó, không dùng cấp mô hình đoán.
    /// outlineLvl là đặc tả OOXML do chính người soạn đặt — chính xác hơn mọi suy luận.
    /// </summary>
    public bool LevelFromOutline { get; set; } = true;

    /// <summary>
    /// Quét hai lượt với cách cắt khối khác nhau rồi đối chiếu. Grammar liệt kê buộc mô hình
    /// sinh một chữ số cho mỗi ứng viên theo thứ tự, nên một dãy 0 kéo chữ số sau nó về 0 —
    /// lỗi bám theo vị trí trong khối. Đổi mép khối thì mỗi ứng viên rơi vào lân cận khác;
    /// chỗ nào hai lượt lệch nhau là chỗ mô hình lung lay, đánh dấu để trọng tài xem lại.
    /// </summary>
    public bool TwoPass { get; set; }

    /// <summary>
    /// Hậu kiểm bằng ký hiệu đánh số của chính tài liệu: cùng dạng đánh số phải cùng cấp, và
    /// dãy anh em phải liên tục từ 1. Không tốn giây suy luận nào và bắt được cả lỗi trượt cấp
    /// của mô hình lẫn tiêu đề bị tầng lọc đánh rơi — xem <see cref="NumberingAudit"/>.
    /// </summary>
    public bool AuditNumbering { get; set; } = true;

    /// <summary>
    /// Cứu heading bị mô hình loại hẳn khi đánh số của tài liệu khẳng định nó là em kế tiếp của
    /// một heading đã nhận (3.1 → 3.2). Bộ sắp cấp chỉ sửa được cấp của heading ĐÃ chọn, không
    /// kéo lại được mục đã bị loại — xem <see cref="StructuralRecovery"/>.
    /// </summary>
    public bool RecoverNumberedSiblings { get; set; } = true;

    /// <summary>Ghi XML tinh gọn đã gửi cho mô hình ra file (debug).</summary>
    public string? DumpXmlPath { get; set; }

    /// <summary>In nguyên văn đầu ra của mô hình cho từng khối (debug prompt/grammar).</summary>
    public bool ShowRawOutput { get; set; }

    /// <summary>
    /// Chỉ bật để audit/thu thập nhãn: gửi mọi paragraph không rỗng cho model. Mặc định pipeline
    /// production chỉ hỏi các ứng viên mơ hồ; style/rule và hậu kiểm cấu trúc xử lý phần chắc chắn.
    /// </summary>
    public bool ReviewAllParagraphs { get; set; }

    /// <summary>
    /// Sau khi chọn heading theo từng cửa sổ, chạy một lượt riêng để gán lại cấp trên danh sách
    /// heading theo thứ tự toàn tài liệu. Tránh lỗi chunk cắt giữa heading cha và heading con.
    /// </summary>
    public bool GlobalHierarchy { get; set; } = true;

    public Action<string>? Log { get; set; }

    /// <summary>JSONL correction đã được người dùng sửa thật sự; null thì không dùng memory.</summary>
    public string? CorrectionMemoryPath { get; set; }

    /// <summary>
    /// Precision-first: mọi heading do model/style đề xuất phải qua prompt critic độc lập.
    /// Đổi lại thêm một lượt RPC nhỏ chỉ chứa các mục đã chọn, không quét lại toàn tài liệu.
    /// </summary>
    public bool HighPrecisionMode { get; set; } = true;

    /// <summary>Ngưỡng precision mong muốn cho selective auto-accept.</summary>
    public double TargetPrecision { get; set; } = 0.93;

    /// <summary>Số dự đoán holdout tối thiểu trong đúng evidence bucket.</summary>
    public int MinimumCalibrationSamples { get; set; } = 52;

    /// <summary>Profile sinh từ `dhx eval ... --calibration-out`; null = evidence chưa calibration.</summary>
    public string? CalibrationProfilePath { get; set; } =
        Environment.GetEnvironmentVariable("DHX_CALIBRATION_PROFILE");
}

public sealed class HeaderExtractionPipeline : IDisposable
{
    private readonly PipelineOptions _options;
    private IHeaderClassifier? _model;
    private readonly bool _ownsModel;
    private readonly bool _usesPreloadedModel;
    private readonly CorrectionMemory? _correctionMemory;
    private readonly PrecisionCalibrationProfile? _calibrationProfile;

    public HeaderExtractionPipeline(PipelineOptions options)
    {
        _options = options;
        _ownsModel = true;
        if (!string.IsNullOrWhiteSpace(options.CorrectionMemoryPath))
            _correctionMemory = new CorrectionMemory(options.CorrectionMemoryPath);
        _calibrationProfile = LoadCalibration(options.CalibrationProfilePath);
    }

    /// <summary>
    /// Dùng model đã nạp sẵn. Pipeline không sở hữu và không giải phóng model này, nhờ đó máy
    /// chủ có thể tái sử dụng weights giữa nhiều request tuần tự.
    /// </summary>
    public HeaderExtractionPipeline(PipelineOptions options, IHeaderClassifier model)
    {
        _options = options;
        _model = model;
        _ownsModel = false;
        _usesPreloadedModel = true;
        if (!string.IsNullOrWhiteSpace(options.CorrectionMemoryPath))
            _correctionMemory = new CorrectionMemory(options.CorrectionMemoryPath);
        _calibrationProfile = LoadCalibration(options.CalibrationProfilePath);
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    public async Task<DocumentOutline> RunAsync(string inputPath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. .doc / .rtf / .odt → .docx (OpenXML SDK không đọc được nhị phân đời cũ).
        var conversion = LegacyDocConverter.EnsureDocx(inputPath);
        if (conversion.Converter is not null)
            Log($"Đã chuyển đổi sang .docx bằng {conversion.Converter}");

        try
        {
            // 2. OpenXML → cấu trúc tinh gọn.
            var extractor = new DocxSlimExtractor(_options.Extraction);
            var slim = extractor.Extract(conversion.Path);

            var styled = slim.Paragraphs.Count(p => p.Role == ParagraphRole.StyledHeading);
            var candidates = slim.Candidates.ToList();
            // Khi tắt LLM, pipeline chỉ xét danh sách ứng viên heuristic. Không được báo toàn bộ
            // paragraph là "đã review": Stats sẽ lấy CandidateCount - Headings để tính số bị loại.
            var reviewCount = _options.DisableLlm
                ? candidates.Count
                : _options.ReviewAllParagraphs
                    ? slim.Paragraphs.Count(p => p.Role != ParagraphRole.Empty)
                    : candidates.Count;
            var reviewSummary = _options.DisableLlm
                ? $"luật xét {reviewCount} ứng viên."
                : $"LLM review {reviewCount} đoạn.";
            Log($"OpenXML: {slim.Paragraphs.Count} đoạn → {candidates.Count} ứng viên " +
                $"({styled} theo style, {candidates.Count - styled} theo heuristic); " +
                reviewSummary);

            if (_options.DumpXmlPath is { } dump)
            {
                await File.WriteAllTextAsync(dump, SlimXmlSerializer.ToFullXml(slim, _options.Extraction), ct);
                Log($"Đã ghi XML tinh gọn: {dump}");
            }

            List<HeadingRecord> headings = _options.DisableLlm
                ? HeuristicOnly(candidates)
                : await RunModelAsync(slim, candidates, ct);

            if (_options.NormalizeLevels) NormalizeLevels(headings);

            // Correction do người dùng xác nhận cho đúng file/stableId/text là ground truth cục bộ.
            // Áp dụng sau normalize để cấp người dùng chọn không bị thuật toán đổi lại.
            if (_correctionMemory is not null)
            {
                var corrected = _correctionMemory.ApplyExact(Path.GetFileName(inputPath), slim, headings);
                if (corrected > 0) Log($"Áp dụng {corrected} correction khớp chính xác file + stable ID + nội dung.");
            }

            // Sau chuẩn hoá: cấp đem đối chiếu phải là cấp người dùng thực sự nhìn thấy.
            var auditConflicts = new HashSet<int>();
            if (_options.AuditNumbering)
            {
                var warnings = NumberingAudit.Run(headings);
                auditConflicts.UnionWith(warnings.SelectMany(w => w.Indexes));
                foreach (var w in warnings) Log($"  ⚠ {w.Message}");
                if (warnings.Count > 0)
                    Log($"Hậu kiểm đánh số: {warnings.Count} điểm đáng ngờ, " +
                        $"{headings.Count(h => h.Disputed)} tiêu đề được đánh dấu xem lại.");
            }

            var calibrated = EvidenceConfidenceCalibrator.Apply(headings, slim, auditConflicts);
            if (calibrated > 0)
                Log($"Tự đánh giá evidence: {calibrated} heading Structure; " +
                    $"{headings.Count(h => h.Source == HeadingSource.Structure && h.Confidence >= 0.95)} đạt đủ 5 kiểm tra.");

            PrecisionAcceptanceGate.Apply(headings, _calibrationProfile,
                _options.TargetPrecision, _options.MinimumCalibrationSamples,
                _model?.ModelName, PrecisionCalibrationProfile.ConfigurationFor(_options));
            Log($"Cổng precision {_options.TargetPrecision:P0}: " +
                $"{headings.Count(h => h.DecisionStatus is not HeadingDecisionStatus.RequiresReview)} tự nhận, " +
                $"{headings.Count(h => h.DecisionStatus == HeadingDecisionStatus.RequiresReview)} cần duyệt" +
                (_calibrationProfile is null ? " (evidence chưa calibration bằng holdout)." :
                    $" (profile {_calibrationProfile.Documents} tài liệu holdout)."));

            sw.Stop();

            return new DocumentOutline
            {
                File = Path.GetFileName(inputPath),
                ParagraphCount = slim.Paragraphs.Count,
                CandidateCount = reviewCount,
                Headings = headings,
                ElapsedMs = sw.ElapsedMilliseconds,
                Model = _options.DisableLlm ? null : _model?.ModelName,
            };
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }

    private static List<HeadingRecord> HeuristicOnly(IReadOnlyList<SlimParagraph> candidates) =>
    [
        .. candidates.Select(p => new HeadingRecord
        {
            Index = p.Index,
            StableId = p.StableId,
            Level = p.GuessedLevel ?? 1,
            Text = p.Text,
            StyleId = p.StyleId,
            Source = p.Role == ParagraphRole.StyledHeading ? HeadingSource.Style : HeadingSource.Heuristic,
            Confidence = p.Role == ParagraphRole.StyledHeading ? 1.0 : p.Score,
        })
    ];

    private async Task<List<HeadingRecord>> RunModelAsync(
        SlimDocument slim,
        IReadOnlyList<SlimParagraph> candidates,
        CancellationToken ct)
    {
        var review = _options.ReviewAllParagraphs
            ? slim.Paragraphs.Where(p => p.Role != ParagraphRole.Empty).ToList()
            : candidates.ToList();
        if (review.Count == 0) return [];

        var reviewIndexes = review.Select(p => p.Index).ToHashSet();
        var lines = SlimXmlSerializer.BuildLines(slim, _options.Extraction, reviewIndexes);

        // Chỉ bỏ qua được khi TrustStyles bật — tắt nó đi thì câu trả lời của mô hình mới có trọng lượng.
        var skipStyled = _options.SkipStyledCandidates && _options.TrustStyles;
        var styled = candidates.Where(p => p.Role == ParagraphRole.StyledHeading).Select(p => p.Index).ToHashSet();
        Func<int, bool>? shouldAsk = skipStyled ? i => !styled.Contains(i) : null;

        if (skipStyled && styled.Count > 0)
            Log($"Bỏ qua {styled.Count} heading built-in đã xác định (vẫn giữ làm ngữ cảnh) — " +
                $"chỉ hỏi mô hình {review.Count - styled.Count} đoạn còn lại.");

        if (review.All(p => styled.Contains(p.Index)) && skipStyled)
        {
            Log("Không còn đoạn nào cần hỏi — toàn bộ tiêu đề đã xác định bằng style.");
            return [.. candidates.Select(p => new HeadingRecord
            {
                Index = p.Index,
                StableId = p.StableId,
                Level = p.GuessedLevel ?? 1,
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Style,
                Confidence = 1.0,
            })];
        }

        Log(_usesPreloadedModel
            ? $"Dùng backend suy luận: {_model?.ModelName}"
            : _options.Backend == InferenceBackend.OpenRouter
                ? $"Kết nối OpenRouter: {_options.OpenRouter.Model} (ZDR, cấm thu thập dữ liệu)…"
                : $"Đang nạp mô hình: {Path.GetFileName(_options.Llama.ModelPath)} …");
        var llm = await GetModelAsync(ct);
        Log($"Mô hình sẵn sàng. Ngữ cảnh {llm.ContextSize} token, {llm.RuntimeDescription}.");

        if (llm is LlamaHeaderExtractor && _options.Llama.ReusePromptPrefix)
            Log(llm.SharedPrefixTokens > 0
                ? $"Tái dùng prefill: {llm.SharedPrefixTokens} token phần chung nạp một lần cho mọi khối."
                : "Không cắt được prompt thành phần chung — quay về nạp lại từng khối.");

        var passA = await RunPassAsync(llm, lines, _options.Llama.ChunkTokenBudget,
            _options.Llama.MaxCandidatesPerChunk, _options.TwoPass ? "lượt 1" : null, shouldAsk, ct);

        // Lượt 2 cắt khối nhỏ hơn hẳn ⇒ mép khối rơi vào chỗ khác, mỗi ứng viên có lân cận khác.
        var passB = _options.TwoPass
            ? await RunPassAsync(llm, lines, Math.Max(400, _options.Llama.ChunkTokenBudget / 2),
                Math.Max(4, _options.Llama.MaxCandidatesPerChunk / 2), "lượt 2", shouldAsk, ct)
            : null;

        var accepted = new Dictionary<int, HeadingRecord>();
        var keep = passB is null ? passA.Votes.Keys : passA.Votes.Keys.Union(passB.Votes.Keys);

        foreach (var index in keep)
        {
            var p = slim.ByIndex(index);
            if (p is null) continue;

            var inPassA = passA.Votes.ContainsKey(index);
            var inPassB = passB?.Votes.ContainsKey(index) ?? false;

            var levels = new List<int>();
            if (passA.Votes.TryGetValue(index, out var la)) levels.AddRange(la);
            if (passB is not null && passB.Votes.TryGetValue(index, out var lb)) levels.AddRange(lb);

            accepted[index] = new HeadingRecord
            {
                Index = p.Index,
                StableId = p.StableId,
                Level = ResolveLevel(p, levels),
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Model,
                ModelConfirmed = true,
                Confidence = ModelConfidenceCalibrator.FromPasses(
                    p.Role == ParagraphRole.StyledHeading, passB is not null, inPassA, inPassB),
                // Giữ lại vì MỘT lượt nhận, lượt kia loại ⇒ mô hình không ổn định ở đây.
                Disputed = passB is not null && inPassA != inPassB,
            };
        }

        if (passB is not null)
        {
            var disputed = accepted.Values.Count(h => h.Disputed);
            Log($"Đối chiếu hai lượt: {accepted.Count} tiêu đề, {disputed} đoạn bất đồng cần xem lại.");
        }

        // Lưới an toàn: style Word đã khẳng định là heading thì không để mô hình làm mất.
        if (_options.TrustStyles)
        {
            int restored = 0;
            foreach (var p in candidates.Where(p => p.HasBuiltInHeadingStyle))
            {
                if (accepted.ContainsKey(p.Index)) continue;
                if (passA.ExplicitNonHeadings.Contains(p.Index) ||
                    (passB?.ExplicitNonHeadings.Contains(p.Index) ?? false))
                {
                    Log($"Không khôi phục heading style ở đoạn {p.Index}: model đã chủ động bác từ ngữ cảnh.");
                    continue;
                }
                accepted[p.Index] = new HeadingRecord
                {
                    Index = p.Index,
                    StableId = p.StableId,
                    Level = p.GuessedLevel ?? 1,
                    Text = p.Text,
                    StyleId = p.StyleId,
                    Source = HeadingSource.Style,
                    Confidence = 1.0,
                };
                restored++;
            }
            if (restored > 0) Log($"Khôi phục {restored} tiêu đề theo style mà mô hình bỏ sót.");
        }

        // Precision-first: mọi heading Model/Style phải qua prompt đối nghịch. Khi tắt chế độ này,
        // vẫn phản biện model-only yếu để chặn false positive rõ ràng.
        // prompt đối nghịch, không tiết lộ kết luận cũ, để chủ động tìm vai trò hành chính/title/
        // normal. Đây là kiểm tra ngữ nghĩa tổng quát; không hardcode văn bản hay từ khóa cụ thể.
        var semanticRejectedIndexes = new HashSet<int>();
        var weakModelIndexes = accepted.Values
            .Where(h => slim.ByIndex(h.Index) is { } p && ModelHeadingCriticGate.NeedsCritique(h, p))
            .Select(h => h.Index)
            .ToHashSet();
        var criticIndexes = _options.HighPrecisionMode
            ? accepted.Values.Where(h => h.Source is HeadingSource.Model or HeadingSource.Style)
                .Select(h => h.Index).ToHashSet()
            : weakModelIndexes;
        if (criticIndexes.Count > 0)
        {
            var criticLines = SlimXmlSerializer.BuildLines(slim, _options.Extraction, criticIndexes);
            var critic = await RunPassAsync(llm, criticLines,
                Math.Min(_options.Llama.ChunkTokenBudget, 3000),
                Math.Min(_options.Llama.MaxCandidatesPerChunk, 12),
                _options.HighPrecisionMode ? "critic precision-first" : "phản biện model-only yếu",
                null, ct, useCritic: true);

            var removed = 0;
            var confirmed = 0;
            var unresolved = 0;
            foreach (var index in criticIndexes)
            {
                if (!accepted.TryGetValue(index, out var heading)) continue;
                if (critic.ExplicitNonHeadings.Contains(index))
                {
                    accepted.Remove(index);
                    semanticRejectedIndexes.Add(index);
                    removed++;
                }
                else if (critic.Votes.ContainsKey(index))
                {
                    heading.CriticConfirmed = true;
                    heading.Confidence = ModelConfidenceCalibrator.CriticConfirmed;
                    confirmed++;
                }
                else
                {
                    // Với model-only yếu, "không chắc" không phải bằng chứng đủ để đưa vào cây.
                    // Ưu tiên precision: chỉ giữ khi critic độc lập cũng trả heading.
                    accepted.Remove(index);
                    semanticRejectedIndexes.Add(index);
                    unresolved++;
                }
            }
            Log($"Critic ngữ nghĩa: giữ {confirmed} được xác nhận; " +
                $"loại {removed} bị bác và {unresolved} không đủ bằng chứng.");
        }

        // Bộ phục hồi cấu trúc đọc toàn tài liệu, không chỉ tập model được hỏi. Đây là rule
        // xác định theo chuỗi đánh số nên không cần đốt token LLM cho từng paragraph.
        var structuralParagraphs = slim.Paragraphs.Where(p => p.Role != ParagraphRole.Empty).ToList();
        var outlineFix = OutlineStructureResolver.Apply(structuralParagraphs, accepted);
        if (outlineFix is { Recovered: > 0 } or { Removed: > 0 } or { LevelsFixed: > 0 })
            Log($"Cấu trúc La Mã → số → chữ: khôi phục {outlineFix.Recovered}, " +
                $"loại {outlineFix.Removed} gạch đầu dòng, sửa cấp {outlineFix.LevelsFixed}.");

        // Trước khi sắp lại cấp: cứu những mục mà đánh số khẳng định là anh em của heading đã nhận.
        // Phải chạy TRƯỚC ReconcileHierarchy/StructuralHierarchyResolver để phần cứu được cũng
        // đi qua bộ sắp cấp, thay vì mang cấp của neo mãi mãi.
        if (_options.RecoverNumberedSiblings && accepted.Count > 0)
        {
            foreach (var r in StructuralRecovery.Find(review, accepted))
            {
                accepted[r.Paragraph.Index] = new HeadingRecord
                {
                    Index = r.Paragraph.Index,
                    StableId = r.Paragraph.StableId,
                    Level = r.Level,
                    Text = r.Paragraph.Text,
                    StyleId = r.Paragraph.StyleId,
                    Source = HeadingSource.Structure,
                    Confidence = 0.5,
                    // Cứu theo cấu trúc là suy luận, không phải khẳng định — luôn cần người xem lại.
                    Disputed = true,
                };
                Log($"  ↺ Cứu đoạn {r.Paragraph.Index} theo cấu trúc: {r.Reason}");
            }
        }

        // Structural rule không được âm thầm kéo lại đoạn mà semantic critic vừa bác. Nếu muốn
        // cứu trường hợp đó phải có nhãn người dùng hoặc benchmark/profile mới, không vote vòng.
        var reintroduced = semanticRejectedIndexes.Count(accepted.ContainsKey);
        foreach (var index in semanticRejectedIndexes) accepted.Remove(index);
        if (reintroduced > 0)
            Log($"Cổng precision loại lại {reintroduced} mục bị Structure kéo vào sau khi critic đã bác.");

        // Xác định span trước để lượt cross-verification không bị phần số liệu inline làm nhiễu.
        var inlineSplits = InlineHeadingSplitter.Apply(accepted.Values, slim);
        if (inlineSplits > 0)
            Log($"Tách {inlineSplits} heading có nội dung cùng dòng theo ranh giới kiểm chứng được.");

        // Cross-verification: Structure chỉ đề xuất ứng viên; hỏi model lại trong một batch tập
        // trung trước khi cho phép evidence score đạt mức verified.
        var structureIndexes = accepted.Values.Where(h => h.Source == HeadingSource.Structure)
            .Select(h => h.Index).ToHashSet();
        if (structureIndexes.Count > 0)
        {
            var verifyLines = SlimXmlSerializer.BuildLines(slim, _options.Extraction, structureIndexes);
            var verification = await RunPassAsync(llm, verifyLines,
                Math.Min(_options.Llama.ChunkTokenBudget, 3000),
                Math.Min(_options.Llama.MaxCandidatesPerChunk, 16),
                "xác minh Structure", null, ct);
            foreach (var index in structureIndexes)
                if (accepted.TryGetValue(index, out var heading))
                    heading.ModelConfirmed = verification.Votes.ContainsKey(index);
        }

        if (_options.GlobalHierarchy && accepted.Count > 0)
            await ReconcileHierarchyAsync(llm, accepted, slim, ct);

        var result = accepted.Values.OrderBy(h => h.Index).ToList();
        var structuralFixes = StructuralHierarchyResolver.Apply(result, slim);
        if (structuralFixes > 0) Log($"Hậu xử lý hierarchy: sửa {structuralFixes} cấp theo quan hệ numbering cha–con/anh–em.");
        return result;
    }

    /// <summary>
    /// Lượt hai chỉ nhìn danh sách heading đã chọn nên có thể dùng quan hệ giữa các mục ở hai
    /// đầu chunk. Tài liệu dài được chia batch nhưng luôn mang vài heading trước làm mốc; không
    /// có keyword hay danh sách biểu mẫu đặc thù nào trong logic này.
    /// </summary>
    private async Task ReconcileHierarchyAsync(
        IHeaderClassifier llm,
        Dictionary<int, HeadingRecord> accepted,
        SlimDocument slim,
        CancellationToken ct)
    {
        var ordered = accepted.Values.OrderBy(h => h.Index).ToList();
        var batchSize = Math.Clamp(_options.Llama.MaxCandidatesPerChunk, 6, 32);
        const int anchorCount = 6;

        for (var start = 0; start < ordered.Count; start += batchSize)
        {
            var batch = ordered.Skip(start).Take(batchSize).ToList();
            var anchors = ordered.Skip(Math.Max(0, start - anchorCount)).Take(start - Math.Max(0, start - anchorCount))
                .Select(ToHierarchyItem).ToList();
            var asked = batch.Select(ToHierarchyItem).ToList();
            var result = await llm.ClassifyHierarchyAsync(anchors, asked, ct);
            var levels = result.Headings.ToDictionary(h => h.Index, h => h.Level);

            foreach (var heading in batch)
            {
                // Style built-in là tín hiệu cấu trúc rõ; các dạng outline/custom style đã được
                // để model phản biện ngay từ lượt một, nên không khoá cấp của chúng ở đây.
                var p = slim.ByIndex(heading.Index);
                if (p?.HasBuiltInHeadingStyle == true) continue;
                if (levels.TryGetValue(heading.Index, out var level)) heading.Level = level;
            }

            Log($"  hierarchy {start / batchSize + 1}: {batch.Count} heading → gán cấp toàn cục ({result.ElapsedMs} ms)");
        }

        HierarchyItem ToHierarchyItem(HeadingRecord heading)
        {
            var p = slim.ByIndex(heading.Index)!;
            var styleLevel = p.HasBuiltInHeadingStyle ? p.GuessedLevel : null;
            var numbering = p.NumberLabel ?? (p.NumberingId is { } id ? $"{id}.{p.NumberingLevel ?? 0}" : null);
            return new HierarchyItem(heading.Index, heading.Text, styleLevel, p.OutlineLevel,
                heading.Level, numbering);
        }
    }

    /// <summary>
    /// Một lượt quét toàn tài liệu. Trả về: chỉ số đoạn → các cấp mô hình đã gán cho nó
    /// (nhiều giá trị khi đoạn nằm trong phần chồng lấn của hai khối liên tiếp).
    /// </summary>
    private async Task<PassResult> RunPassAsync(
        IHeaderClassifier llm,
        IReadOnlyList<XmlLine> lines,
        int chunkTokens,
        int maxCandidatesPerChunk,
        string? passLabel,
        Func<int, bool>? shouldAsk,
        CancellationToken ct,
        bool useCritic = false)
    {
        // Đếm bằng tokenizer của chính mô hình: ngân sách chỉ có nghĩa khi đơn vị của nó trùng
        // với đơn vị mà cửa sổ ngữ cảnh dùng. Chỉ backend local mới có tokenizer trong tiến trình;
        // backend từ xa rơi về ước lượng theo ký tự.
        Func<string, int>? countTokens = llm is LlamaHeaderExtractor local ? local.CountTokens : null;

        var chunks = SlimXmlChunker.Split(
            lines, chunkTokens, _options.Llama.ChunkOverlap, maxCandidatesPerChunk, shouldAsk,
            countTokens);
        var prefix = passLabel is null ? "" : passLabel + " — ";
        var unit = countTokens is null ? "token ước lượng" : "token thật";
        Log($"{prefix}chia thành {chunks.Count} khối XML (ngân sách {chunkTokens} {unit}/khối)");

        var votes = new Dictionary<int, List<int>>();
        var explicitNonHeadings = new HashSet<int>();

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var xml = SlimXmlSerializer.WrapChunk(chunk.Lines, chunk.Number, chunks.Count);
            if (!useCritic && _correctionMemory is not null && _options.Backend == InferenceBackend.Local)
            {
                var examples = _correctionMemory.FindExamples(xml);
                if (examples.Count > 0)
                {
                    xml = CorrectionMemory.InjectExamples(xml, examples);
                    Log($"    ↳ memory: {examples.Count} correction tương tự đã xác nhận (chỉ làm ví dụ, không ép kết quả)");
                }
            }
            var allowed = chunk.CandidateIndexes;

            var result = useCritic
                ? await llm.CritiqueAsync(xml, allowed, ct)
                : await llm.ClassifyAsync(xml, allowed, ct);
            Log($"  {prefix}khối {chunk.Number}/{chunks.Count}: {allowed.Count} ứng viên → " +
                $"{result.Headings.Count} tiêu đề ({result.ElapsedMs} ms" +
                (result.RejectedIndexes > 0 ? $", loại {result.RejectedIndexes} chỉ số bịa" : "") + ")");

            if (_options.ShowRawOutput) Log($"    ↳ {result.RawOutput.ReplaceLineEndings(" ").Trim()}");

            explicitNonHeadings.UnionWith(result.ExplicitNonHeadings);

            foreach (var h in result.Headings)
            {
                if (!votes.TryGetValue(h.Index, out var list)) votes[h.Index] = list = [];
                list.Add(h.Level);
            }
        }

        return new PassResult(votes, explicitNonHeadings);
    }

    private sealed record PassResult(
        Dictionary<int, List<int>> Votes,
        HashSet<int> ExplicitNonHeadings);

    /// <summary>
    /// Cấp cuối cùng cho một đoạn. Chỉ built-in heading style được lấy cấp trực tiếp từ OOXML;
    /// outline level tự đặt là evidence có thể sai nên lấy phiếu mô hình làm chính.
    /// </summary>
    private int ResolveLevel(SlimParagraph p, List<int> modelLevels)
    {
        if (_options.LevelFromOutline && p.HasBuiltInHeadingStyle && p.GuessedLevel is { } fromFile)
            return fromFile;

        if (modelLevels.Count == 0) return p.GuessedLevel ?? 1;

        return modelLevels.GroupBy(l => l)
                          .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                          .First().Key;
    }

    private static PrecisionCalibrationProfile? LoadCalibration(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Không tìm thấy precision calibration profile.", full);
        return PrecisionCalibrationProfile.Load(full);
    }

    /// <summary>Ép dãy cấp thành liên tục: cấp đầu tiên là 1 và không tăng quá 1 bậc mỗi lần.</summary>
    public static void NormalizeLevels(List<HeadingRecord> headings)
    {
        if (headings.Count == 0) return;

        var stack = new List<int>();   // các cấp gốc đang mở
        foreach (var h in headings.OrderBy(x => x.Index))
        {
            var raw = h.Level;

            while (stack.Count > 0 && stack[^1] >= raw) stack.RemoveAt(stack.Count - 1);
            stack.Add(raw);
            h.Level = stack.Count;
        }
    }

    private async Task<IHeaderClassifier> GetModelAsync(CancellationToken ct)
    {
        if (_model is not null) return _model;
        _model = _options.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterHeaderExtractor.CreateOwned(_options.OpenRouter),
            _ => await LlamaHeaderExtractor.LoadAsync(_options.Llama, ct),
        };
        return _model;
    }

    /// <summary>Giữ weights trong RAM khi một Pipeline xử lý nhiều file CLI/eval.</summary>
    public void Dispose()
    {
        if (_ownsModel) _model?.Dispose();
        _model = null;
    }
}
