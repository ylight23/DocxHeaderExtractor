using System.Diagnostics;
using System.Text.Json;
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
    LmStudio,
}

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();

    /// <summary>
    /// Cách cắt khối — thuộc pipeline, không thuộc backend. Xem <see cref="ChunkingOptions"/> để
    /// biết bốn lỗi đã đo được hồi ba giá trị này còn nằm trong <see cref="LlamaOptions"/>.
    /// </summary>
    public ChunkingOptions Chunking { get; set; } = new();

    public LlamaOptions Llama { get; set; } = new();
    public OpenRouterOptions OpenRouter { get; set; } = OpenRouterOptions.FromEnvironment();
    public LmStudioOptions LmStudio { get; set; } = LmStudioOptions.FromEnvironment();
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

    /// <summary>
    /// Chuẩn hoá cấp để không nhảy cóc (1 → 3 thành 1 → 2).
    /// <para>
    /// MẶC ĐỊNH TẮT từ khi cấp do cấu trúc quyết định. Bộ chuẩn hoá gán cấp theo ĐỘ SÂU NGĂN XẾP,
    /// nên heading đầu tiên còn sống luôn bị ép về cấp 1 — mất một heading cha là mọi con của nó
    /// tụt theo. Tra tay trên 01-style-chuan (đáp án 0→1, 2→2, 4→2, 6→1, 8→2) với đoạn 0 bị đánh
    /// rơi: nó gán 2→1 và 4→1 rồi để 6, 8 đúng, khớp từng dòng với báo cáo eval. Toàn bộ lỗi cấp
    /// đo được đều một chiều "trả về 1, đáp án 2" — dấu vân tay của chính phép ép này, không phải
    /// của mô hình đoán bừa. Khi cấp đến từ w:lvl/w:pStyle, style built-in hay chuỗi đánh số đã
    /// xác thực, chuẩn hoá lại chỉ có thể làm hỏng thứ vốn đã đúng.
    /// </para>
    /// </summary>
    public bool NormalizeLevels { get; set; }

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

    /// <summary>Ghi XML tinh gọn từ canonical model ra file để debug/đối chiếu source.</summary>
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

    /// <summary>
    /// Áp profile của model GGUF lên NGÂN SÁCH THẬT mà pipeline dùng để chia khối, rồi chép sang
    /// backend cục bộ để nó tự nới context cho vừa.
    /// <para>
    /// Phải gọi trước khi chia khối. Bản đầu của refactor tách chunking để
    /// <c>LlamaHeaderExtractor.LoadAsync</c> tự áp profile lên một <see cref="ChunkingOptions"/>
    /// TẠM — cú nâng "qwen thì 2200 → 5000" rơi vào vật thể tạm rồi bị vứt, pipeline vẫn chia khối
    /// bằng 2200. Đo được ngay ở dòng log "ngân sách … token thật/khối": 5000 tụt về 2200.
    /// </para>
    /// </summary>
    public void PrepareLocalModelProfile()
    {
        if (DisableLlm || Backend != InferenceBackend.Local) return;
        if (string.IsNullOrWhiteSpace(Llama.ModelPath)) return;
        Llama.ApplyRecommendedModelProfile(Chunking);
    }
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

    public Task<DocumentOutline> RunAsync(string inputPath, CancellationToken ct = default) =>
        RunAsync(inputPath, quarantinedIndexes: null, ct);

    /// <summary>
    /// <paramref name="quarantinedIndexes"/> là các đoạn bị lượt trước loại khỏi vòng phân tích
    /// (ví dụ deterministic validator đã bác). Chúng bị gỡ khỏi tập ứng viên TRƯỚC khi hỏi model,
    /// nên toàn bộ cây, cấp và evidence được dựng lại chứ không phải lọc kết quả cũ.
    /// </summary>
    public async Task<DocumentOutline> RunAsync(
        string inputPath,
        IReadOnlySet<int>? quarantinedIndexes,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var quarantined = quarantinedIndexes ?? new HashSet<int>();

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
            var candidates = slim.Candidates.Where(p => !quarantined.Contains(p.Index)).ToList();
            if (quarantined.Count > 0)
                Log($"Cách ly {quarantined.Count} đoạn theo yêu cầu của lượt trước: " +
                    $"{slim.Candidates.Count(p => quarantined.Contains(p.Index))} ứng viên bị gỡ khỏi vòng phân tích.");
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
                : await RunModelAsync(slim, candidates, quarantined, ct);

            // Lưới cuối: TrustStyles, StructuralRecovery và OutlineStructureResolver đều có thể
            // kéo lại một đoạn theo luật cấu trúc. Đoạn đang bị cách ly thì không được quay lại
            // bằng bất kỳ đường nào.
            if (quarantined.Count > 0) headings.RemoveAll(h => quarantined.Contains(h.Index));

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
        IReadOnlySet<int> quarantined,
        CancellationToken ct)
    {
        var review = _options.ReviewAllParagraphs
            ? slim.Paragraphs
                .Where(p => p.Role != ParagraphRole.Empty && !quarantined.Contains(p.Index)).ToList()
            : candidates.ToList();
        if (review.Count == 0) return [];

        var reviewIndexes = review.Select(p => p.Index).ToHashSet();
        var lines = NeutralDocumentViewSerializer.BuildLines(slim, _options.Extraction, reviewIndexes);

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

        // `_model is not null` nghĩa là lượt trước đã nạp và weights vẫn nằm trong RAM. Không tách
        // hai trường hợp này thì mỗi tài liệu trong một lượt eval đều in "Đang nạp mô hình", và
        // người đọc log sẽ kết luận nhầm rằng 4,4 GB weights bị nạp lại bảy lần.
        Log(_usesPreloadedModel || _model is not null
            ? $"Dùng backend suy luận: {_model?.ModelName}"
            : _options.Backend switch
            {
                InferenceBackend.OpenRouter =>
                    $"Kết nối OpenRouter: {_options.OpenRouter.Model} (ZDR, cấm thu thập dữ liệu)…",
                InferenceBackend.LmStudio =>
                    $"Kết nối LM Studio local: {_options.LmStudio.Model} tại {_options.LmStudio.Endpoint.Authority}…",
                _ => $"Đang nạp mô hình: {Path.GetFileName(_options.Llama.ModelPath)} …",
            });
        var llm = await GetModelAsync(ct);
        Log($"Mô hình sẵn sàng. Ngữ cảnh {llm.ContextSize} token, {llm.RuntimeDescription}.");
        AdoptBackendContextBudget(llm);

        if (llm is LlamaHeaderExtractor && _options.Llama.ReusePromptPrefix)
            Log(llm.SharedPrefixTokens > 0
                ? $"Tái dùng prefill: {llm.SharedPrefixTokens} token phần chung nạp một lần cho mọi khối."
                : "Không cắt được prompt thành phần chung — quay về nạp lại từng khối.");

        var passA = await RunPassAsync(llm, lines, _options.Chunking.TokenBudget,
            _options.Chunking.MaxCandidatesPerChunk, _options.TwoPass ? "lượt 1" : null, shouldAsk, ct);

        // Lượt 2 cắt khối nhỏ hơn hẳn ⇒ mép khối rơi vào chỗ khác, mỗi ứng viên có lân cận khác.
        // Khi không chặn theo số ứng viên (0), việc halve ngân sách token đã đủ dịch mép khối.
        var passB = _options.TwoPass
            ? await RunPassAsync(llm, lines, Math.Max(400, _options.Chunking.TokenBudget / 2),
                _options.Chunking.MaxCandidatesPerChunk > 0
                    ? Math.Max(4, _options.Chunking.MaxCandidatesPerChunk / 2)
                    : 0,
                "lượt 2", shouldAsk, ct)
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

        // "document_title" ở LƯỢT PHÂN LOẠI cũng không phải lời bác, y như ở critic. Chỗ này mới là
        // chỗ chí mạng: `accepted` chỉ dựng từ Votes, nên đoạn bị gán "d" ngay lượt đầu rơi thẳng
        // vào ExplicitNonHeadings và không bao giờ tới được critic để lưới an toàn ở đó cứu. Đo
        // trên bench: vá riêng tầng critic chỉ kéo recall 70,6% → 73,5% vì đoạn 0 chết từ lượt một.
        // Chỉ cứu khi đoạn có bằng chứng cấu trúc: style heading, cấp do danh sách đa cấp khai, hoặc
        // ký hiệu đánh số đứng đầu. Đo được lý do: ở 04-bia-muc-luc-chu-thich, hai dòng bìa không
        // đánh số bị cứu về thành false positive (thừa 0, 1) và kéo precision toàn bộ bench từ 100%
        // xuống 93,8%. Dòng bìa không đánh số thì "đây là tiêu đề văn bản" nhiều khả năng đúng —
        // để nguyên phán quyết của mô hình. Còn "Chương 1.", "9.", "II." thì có chuỗi để đối chiếu.
        var titleFromPass = passA.RejectedRoles
            .Concat(passB?.RejectedRoles ?? [])
            .Where(kv => kv.Value == SemanticRole.DocumentTitle)
            .Select(kv => kv.Key)
            .Distinct()
            .Where(i => !accepted.ContainsKey(i))
            .Where(i => slim.ByIndex(i) is { } p && HasStructuralEvidence(p))
            .ToList();
        foreach (var index in titleFromPass)
        {
            var p = slim.ByIndex(index);
            if (p is null) continue;
            accepted[index] = new HeadingRecord
            {
                Index = p.Index,
                StableId = p.StableId,
                Level = ResolveLevel(p, []),
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Model,
                ModelConfirmed = false,
                // Không tự nhận: mô hình nói đây là tiêu đề của cả văn bản, mà tiêu đề văn bản
                // không đương nhiên thuộc cây mục lục. Để người duyệt quyết định.
                Confidence = 0.5,
                Disputed = true,
            };
        }
        if (titleFromPass.Count > 0)
            Log($"Giữ {titleFromPass.Count} đoạn bị lượt phân loại gọi là document_title ở trạng thái " +
                "cần duyệt thay vì loại bỏ.");

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

        // Không hỏi critic về đoạn mà CẤU TRÚC đã khai báo là heading (cấp lấy từ w:lvl/w:pStyle
        // hoặc style Heading built-in). Ba lý do, không phải để đi tắt:
        //  - Phán quyết của critic không đổi được số phận của chúng: chúng đã được bảo vệ khỏi bị
        //    xoá, và cấp thì lấy từ cấu trúc chứ không từ mô hình. Hỏi xong cũng không dùng.
        //  - Mỗi mục thừa trong batch làm đổi câu trả lời cho các mục CÒN LẠI — đo được: thêm 2
        //    mục vào batch đưa critic của một tài liệu từ "giữ 3, loại 0" sang "giữ 3, loại 3".
        //  - Đây là khoản thời gian lớn nhất của pipeline: đo trên bench, critic 227 s so với
        //    hierarchy 53 s và phân loại ~60–130 s cho mỗi tài liệu.
        // Cấu trúc đóng luôn vai trò xác nhận để chúng không bị tụt confidence vì thiếu phiếu critic.
        var structurallyDeclared = criticIndexes
            .Where(i => slim.ByIndex(i) is { } p && (p.NumberingStyleLevel is not null || p.HasBuiltInHeadingStyle))
            .ToList();
        foreach (var index in structurallyDeclared)
        {
            criticIndexes.Remove(index);
            if (accepted.TryGetValue(index, out var declared)) declared.CriticConfirmed = true;
        }
        if (structurallyDeclared.Count > 0)
            Log($"Bỏ qua critic cho {structurallyDeclared.Count} đoạn cấu trúc đã khai báo " +
                "(danh sách đa cấp / style Heading built-in) — hỏi lại không đổi được kết quả.");
        // Mục được cứu vì bị gọi là document_title KHÔNG đi qua critic. Chúng đã mang cờ cần duyệt
        // và không được tự nhận, nên hỏi critic không đổi được số phận của chúng — nhưng lại đổi
        // THÀNH PHẦN KHỐI của lượt critic, và mô hình trả lời khác đi cho những mục không liên quan.
        // Đo được: thêm 2 mục kiểu này vào batch làm critic của cùng tài liệu chuyển từ "giữ 3,
        // loại 0" sang "giữ 3, loại 3", kéo recall toàn bộ bench từ 73,5% xuống 70,6%.
        criticIndexes.ExceptWith(titleFromPass);
        if (criticIndexes.Count > 0)
        {
            var criticLines = NeutralDocumentViewSerializer.BuildLines(slim, _options.Extraction, criticIndexes);
            // Lượt critic dùng CÙNG ngân sách khối với lượt phân loại. Trần cứng 3000 trước đây là
            // khoản tốn kém lớn nhất trên tài liệu thật không có style Heading — nơi mọi ứng viên
            // đều phải qua critic: đo trên công văn 344 đoạn, lượt phân loại chia 7 khối còn lượt
            // critic chia 10, mỗi khối 280–350 s, tức riêng critic tốn nhiều hơn cả lượt chính.
            // Chụp lại danh sách heading tại thời điểm này: vòng lặp bên dưới sẽ sửa `accepted`,
            // còn anchor phải mô tả cùng một trạng thái cho mọi khối của lượt critic.
            var acceptedAtCritic = accepted.Values.ToList();
            var critic = await RunPassAsync(llm, criticLines,
                _options.Chunking.TokenBudget,
                _options.Chunking.MaxCandidatesPerChunk,
                _options.HighPrecisionMode ? "critic precision-first" : "phản biện model-only yếu",
                null, ct, useCritic: true,
                anchorsFor: asked => BuildCriticAnchorContext(acceptedAtCritic, slim, asked));

            // "document_title" KHÔNG PHẢI lời bác. Nó nói "đoạn này là tiêu đề, chỉ khác là tiêu đề
            // của cả văn bản" — khác hẳn "n"/"f"/"t" vốn khẳng định đoạn không phải tiêu đề. Xoá nó
            // là vứt một dòng mà chính critic vừa công nhận có vai trò tiêu đề.
            //
            // Đo trên bộ bench 7 tài liệu: đoạn 0 bị mất ở 6/7 tài liệu, đều vì heading mở đầu
            // trông giống tiêu đề chính. Bản trước chỉ chặn khi có nhiều hơn một "d" (mâu thuẫn tự
            // thân); trường hợp một "d" duy nhất — đúng trường hợp phổ biến nhất — vẫn xoá thẳng.
            // Giờ mọi "d" đều hạ xuống trạng thái cần duyệt: heading vẫn hiện ra để người dùng
            // xác nhận, nhưng không được tự nhận nếu chưa qua cổng precision.
            var titleClaims = critic.RejectedRoles
                .Where(kv => kv.Value == SemanticRole.DocumentTitle && criticIndexes.Contains(kv.Key))
                .Select(kv => kv.Key)
                .ToHashSet();

            var removed = 0;
            var confirmed = 0;
            var unresolved = 0;
            var disputedTitles = 0;
            var protectedStyles = 0;
            foreach (var index in criticIndexes)
            {
                if (!accepted.TryGetValue(index, out var heading)) continue;
                var builtInStyle = slim.ByIndex(index)?.HasBuiltInHeadingStyle == true;
                if (titleClaims.Contains(index))
                {
                    heading.CriticConfirmed = false;
                    heading.Confidence = Math.Min(heading.Confidence, 0.5);
                    heading.Disputed = true;
                    disputedTitles++;
                }
                else if (critic.ExplicitNonHeadings.Contains(index) && builtInStyle)
                {
                    // Style Heading built-in là tuyên bố tường minh của người soạn: "đoạn này LÀ
                    // tiêu đề". Chính pipeline này coi nó đủ mạnh để khôi phục vô điều kiện ở tầng
                    // heuristic (TrustStyles), nên để một phán quyết của model 7B xoá thẳng nó ở
                    // tầng critic là tự mâu thuẫn. Đo trên bench: ở 01-style-chuan — tài liệu dùng
                    // toàn style chuẩn — critic loại 3 mục và đó đúng là 3 mục bị thiếu.
                    // Không tự nhận, nhưng cũng không xoá: đẩy sang cần duyệt.
                    heading.CriticConfirmed = false;
                    heading.Confidence = Math.Min(heading.Confidence, 0.5);
                    heading.Disputed = true;
                    protectedStyles++;
                }
                else if (critic.ExplicitNonHeadings.Contains(index))
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
                else if (builtInStyle)
                {
                    // Critic im lặng KHÔNG phải lời bác — càng không đủ để lật một style built-in.
                    // Nhánh này chạy khi model không trả lời cho ID đó (hết lượt retry, JSON hỏng);
                    // xoá vì thiếu câu trả lời là xoá vì thiếu bằng chứng, ngược hẳn với xoá vì có
                    // bằng chứng phản bác.
                    heading.CriticConfirmed = false;
                    heading.Confidence = Math.Min(heading.Confidence, 0.5);
                    heading.Disputed = true;
                    protectedStyles++;
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
            if (protectedStyles > 0)
                Log($"  ⚠ Critic bác {protectedStyles} đoạn mang style Heading built-in của Word. " +
                    "Tuyên bố của người soạn không bị một phán quyết model xoá: giữ ở trạng thái cần duyệt.");
            if (disputedTitles > 0)
                Log($"  ⚠ Critic gọi {disputedTitles} đoạn là document_title" +
                    (disputedTitles > 1 ? " — nhiều hơn một tiêu đề chính là mâu thuẫn tự thân" : "") +
                    ". Đó không phải lời bác nên không xoá: giữ ở trạng thái cần duyệt.");
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
        // Điều kiện mà chính ghi chú trên đặt ra — "phải có benchmark mới" — nay đã thoả: trên bộ
        // bench, cổng này chạy khi precision đang là 100% còn recall chỉ 70,6%, tức nó siết một
        // chỗ vốn đã không có false positive nào để siết. Và thứ nó xoá không phải suy đoán của
        // mô hình mà là chuỗi đánh số của chính tài liệu (3.1 → 3.2). Nên đổi từ XOÁ sang HẠ
        // XUỐNG CẦN DUYỆT: critic vẫn thắng ở chỗ mục không được tự nhận, nhưng người dùng còn
        // nhìn thấy nó trong bảng Review thay vì mất trắng.
        var reintroduced = semanticRejectedIndexes.Where(accepted.ContainsKey).ToList();
        foreach (var index in reintroduced)
        {
            var heading = accepted[index];
            heading.CriticConfirmed = false;
            heading.Confidence = Math.Min(heading.Confidence, 0.5);
            heading.Disputed = true;
        }
        if (reintroduced.Count > 0)
            Log($"Giữ {reintroduced.Count} mục ở trạng thái cần duyệt: critic đã bác nhưng chuỗi " +
                "đánh số của tài liệu khẳng định lại. Không tự nhận, cũng không xoá.");

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
            var verifyLines = NeutralDocumentViewSerializer.BuildLines(slim, _options.Extraction, structureIndexes);
            var verification = await RunPassAsync(llm, verifyLines,
                _options.Chunking.TokenBudget,
                _options.Chunking.MaxCandidatesPerChunk,
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
        // Chỉ hỏi về heading mà cấu trúc CHƯA quyết được cấp. Đoạn có style Heading built-in vốn
        // đã bị bỏ qua ở khâu ÁP kết quả bên dưới, nhưng vẫn bị gửi đi trong batch — trả tiền
        // prefill cho một câu trả lời chắc chắn không dùng. Cấp do danh sách đa cấp khai cũng vậy.
        // Chúng vẫn nằm trong `ordered` để làm neo, chỉ không nằm trong phần được hỏi.
        var askable = ordered
            .Where(h => slim.ByIndex(h.Index) is { } p &&
                        p.NumberingStyleLevel is null &&
                        !(p.HasBuiltInHeadingStyle && _options.LevelFromOutline))
            .ToList();
        if (askable.Count == 0)
        {
            Log("Bỏ qua lượt gán cấp toàn cục: cấu trúc đã quyết cấp cho mọi heading.");
            return;
        }

        // 0 = không chặn: hỏi cấp cho toàn bộ danh sách trong một lượt. Mỗi mục ở đây chỉ là một
        // dòng tiêu đề nên khối không phình như lượt phân loại vốn mang theo cả đoạn lân cận.
        var batchSize = _options.Chunking.MaxCandidatesPerChunk > 0
            ? _options.Chunking.MaxCandidatesPerChunk
            : Math.Max(1, askable.Count);
        const int anchorCount = 6;

        for (var start = 0; start < askable.Count; start += batchSize)
        {
            var batch = askable.Skip(start).Take(batchSize).ToList();
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
        bool useCritic = false,
        Func<IReadOnlyList<int>, string>? anchorsFor = null)
    {
        // Đếm bằng tokenizer của chính mô hình: ngân sách chỉ có nghĩa khi đơn vị của nó trùng
        // với đơn vị mà cửa sổ ngữ cảnh dùng. Chỉ backend local mới có tokenizer trong tiến trình;
        // backend từ xa rơi về ước lượng theo ký tự.
        Func<string, int>? countTokens = llm is LlamaHeaderExtractor local ? local.CountTokens : null;

        var chunks = SlimXmlChunker.Split(
            lines, chunkTokens, _options.Chunking.Overlap, maxCandidatesPerChunk, shouldAsk,
            countTokens);
        var prefix = passLabel is null ? "" : passLabel + " — ";
        var unit = countTokens is null ? "token ước lượng" : "token thật";
        Log($"{prefix}chia thành {chunks.Count} khối context trung lập (ngân sách {chunkTokens} {unit}/khối)");

        // Dựng view của mọi khối TRƯỚC, vẫn tuần tự: FindExamples đọc danh sách correction không
        // khoá, và view phải giống hệt bản tuần tự thì kết quả mới không đổi. Bước này không gọi
        // mạng nên không phải chỗ tốn thời gian.
        var views = new string[chunks.Count];
        var memoryNotes = new string?[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var documentView = NeutralDocumentViewSerializer.WrapChunk(chunks[i].Lines, chunks[i].Number, chunks.Count);
            // Anchor dựng theo từng khối: mốc phải nằm quanh đúng các đoạn khối này đang hỏi.
            var anchors = anchorsFor?.Invoke(chunks[i].CandidateIndexes);
            if (!string.IsNullOrWhiteSpace(anchors))
                documentView += "\n" + anchors;
            if (!useCritic && _correctionMemory is not null &&
                _options.Backend is InferenceBackend.Local or InferenceBackend.LmStudio)
            {
                var examples = _correctionMemory.FindExamples(documentView);
                if (examples.Count > 0)
                {
                    documentView = CorrectionMemory.InjectExamples(documentView, examples);
                    memoryNotes[i] = $"    ↳ memory: {examples.Count} correction tương tự đã xác nhận (chỉ làm ví dụ, không ép kết quả)";
                }
            }
            views[i] = documentView;
        }

        var votes = new Dictionary<int, List<int>>();
        var explicitNonHeadings = new HashSet<int>();
        var rejectedRoles = new Dictionary<int, SemanticRole>();
        var degree = Math.Clamp(ChunkParallelism, 1, 16);
        if (degree > 1 && chunks.Count > 1)
            Log($"{prefix}gửi tối đa {degree} khối song song — nội dung từng request không đổi, chỉ bớt thời gian chờ.");

        using var gate = new SemaphoreSlim(degree, degree);
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Dev log phải giữ nguyên thứ tự khối, nếu không thì dump request của khối 7 chen vào giữa
        // khối 1. Mỗi khối ghi vào bộ đệm riêng, xả ra đúng lúc tới lượt nó.
        var buffers = new List<string>[chunks.Count];
        var directDebug = _options.LmStudio.DebugLog;
        if (degree > 1 && directDebug is not null) _options.LmStudio.DebugLog = BufferDebug;

        var pending = new Task<ChunkResult>[chunks.Count];
        try
        {
            for (var i = 0; i < chunks.Count; i++) pending[i] = RunChunkAsync(i);

            for (var i = 0; i < chunks.Count; i++)
            {
                var result = await pending[i];
                var chunk = chunks[i];

                if (memoryNotes[i] is { } note) Log(note);
                string[] buffered;
                lock (buffers[i]) buffered = [.. buffers[i]];
                foreach (var line in buffered) Log(line);
                Log($"  {prefix}khối {chunk.Number}/{chunks.Count}: {chunk.CandidateIndexes.Count} ứng viên → " +
                    $"{result.Headings.Count} tiêu đề ({result.ElapsedMs} ms" +
                    (result.RejectedIndexes > 0 ? $", loại {result.RejectedIndexes} chỉ số bịa" : "") + ")");
                if (_options.ShowRawOutput) Log($"    ↳ {result.RawOutput.ReplaceLineEndings(" ").Trim()}");

                explicitNonHeadings.UnionWith(result.ExplicitNonHeadings);
                if (result.RejectedRoles is { } roles)
                    foreach (var (paragraph, role) in roles) rejectedRoles[paragraph] = role;

                foreach (var h in result.Headings)
                {
                    if (!votes.TryGetValue(h.Index, out var list)) votes[h.Index] = list = [];
                    list.Add(h.Level);
                }
            }
        }
        catch
        {
            // Khối lỗi thì các khối đang bay còn lại vô nghĩa; huỷ rồi chờ để không còn task nào
            // ném exception không ai quan sát sau khi lỗi gốc đã nổi lên.
            failFast.Cancel();
            try { await Task.WhenAll(pending.Where(t => t is not null)); } catch { /* lỗi gốc mới là lỗi cần báo */ }
            throw;
        }
        finally
        {
            _options.LmStudio.DebugLog = directDebug;
        }

        return new PassResult(votes, explicitNonHeadings, rejectedRoles);

        async Task<ChunkResult> RunChunkAsync(int index)
        {
            var buffer = buffers[index] = [];
            ChunkDebugSink.Value = buffer;
            await gate.WaitAsync(failFast.Token);
            try
            {
                return useCritic
                    ? await llm.CritiqueAsync(views[index], chunks[index].CandidateIndexes, failFast.Token)
                    : await llm.ClassifyAsync(views[index], chunks[index].CandidateIndexes, failFast.Token);
            }
            finally
            {
                gate.Release();
            }
        }

        void BufferDebug(string message)
        {
            var sink = ChunkDebugSink.Value;
            if (sink is null) { directDebug!(message); return; }
            lock (sink) sink.Add(message);
        }
    }

    /// <summary>
    /// Bộ đệm dev log của khối đang chạy trên logical context hiện tại. AsyncLocal chứ không phải
    /// biến thường: hook DebugLog nằm sâu trong extractor và chỉ có ngữ cảnh async nối nó về đúng khối.
    /// </summary>
    private static readonly AsyncLocal<List<string>?> ChunkDebugSink = new();

    /// <summary>
    /// Bằng chứng cấu trúc do CHÍNH tài liệu khai, không phải suy đoán về hình thức: cấp do danh
    /// sách đa cấp khai qua <c>w:lvl/w:pStyle</c>, style Heading built-in, hoặc ký hiệu đánh số
    /// đứng đầu đoạn (kể cả gõ tay — "3.1", "II.", "a)"). Không tính cỡ chữ, in đậm, căn giữa:
    /// đó là hình thức, và bìa văn bản nào cũng có.
    /// </summary>
    private static bool HasStructuralEvidence(SlimParagraph p) =>
        p.NumberingStyleLevel is not null ||
        p.HasBuiltInHeadingStyle ||
        p.PrecedesTableOfContents ||
        NumberingAudit.Parse(p.Text) is not null;

    /// <summary>
    /// Mốc cấu trúc cho một khối critic. Hai điều kiện làm nên tính đúng của nó:
    /// <list type="number">
    /// <item>Anchor phải là heading GẦN đoạn đang bị phản biện. Bản đầu lấy 12 heading đầu tài
    /// liệu theo index, nên với tài liệu 344 đoạn thì đoạn ở index 147 được phản biện bằng mốc
    /// của phần mở đầu — coi như không có ngữ cảnh.</item>
    /// <item>Anchor KHÔNG được chứa chính các đoạn đang bị hỏi. Đưa lại giả thuyết cũ kèm cấp của
    /// nó là mớm đáp án cho đúng cái prompt được giao nhiệm vụ đi phản bác.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Lấy ngân sách khối từ context mà chính backend khai báo, thay cho hằng số đoán sẵn.
    /// <para>
    /// Chỉ áp cho backend RPC: bản GGUF cục bộ đã tự suy ngân sách từ context của nó trong
    /// <see cref="LlamaOptions.ApplyRecommendedModelProfile"/>, còn nhánh RPC thì đang dùng hằng
    /// 5000 — con số đo cho Qwen 7B chạy cục bộ với context 8192, rồi đem dùng cho mọi server.
    /// LM Studio khai 16384 mà pipeline vẫn cắt theo 5000, tức chia tài liệu nhỏ hơn mức cần
    /// trong khi mỗi khối là một lượt RPC.
    /// </para>
    /// <para>Người dùng đặt tay thì giữ nguyên; chỉ NÂNG, không tự hạ.</para>
    /// </summary>
    private void AdoptBackendContextBudget(IHeaderClassifier llm)
    {
        if (_options.Chunking.TokenBudgetExplicit) return;
        if (_options.Backend is not (InferenceBackend.LmStudio or InferenceBackend.OpenRouter)) return;
        if (llm.ContextSize <= 0) return;

        var maxOutput = _options.Backend == InferenceBackend.LmStudio
            ? _options.LmStudio.MaxOutputTokens
            : _options.OpenRouter.MaxOutputTokens;
        var derived = ChunkingOptions.DeriveTokenBudget(
            llm.ContextSize, maxOutput, LlamaOptions.FixedPromptTokens);
        if (derived <= _options.Chunking.TokenBudget) return;

        Log($"Ngân sách khối theo context backend khai báo: {_options.Chunking.TokenBudget} → {derived} " +
            $"token (context {llm.ContextSize}, đầu ra {maxOutput}, dự trữ prompt {LlamaOptions.FixedPromptTokens}).");
        _options.Chunking.TokenBudget = derived;
    }

    private static string BuildCriticAnchorContext(
        IEnumerable<HeadingRecord> accepted,
        SlimDocument slim,
        IReadOnlyList<int> askedIndexes)
    {
        if (askedIndexes.Count == 0) return string.Empty;
        var asked = askedIndexes.ToHashSet();
        var from = askedIndexes.Min();
        var to = askedIndexes.Max();

        var anchors = accepted
            .Where(h => !asked.Contains(h.Index))
            .OrderBy(h => DistanceToWindow(h.Index, from, to))
            .ThenBy(h => h.Index)
            .Take(12)
            .OrderBy(h => h.Index)
            .Select(h => new
            {
                i = h.Index,
                level = h.Level,
                text = SlimXmlSerializer.Truncate(slim.ByIndex(h.Index)?.Text ?? h.Text, 180),
                numbering = slim.ByIndex(h.Index)?.NumberLabel,
            })
            .ToArray();
        return anchors.Length == 0
            ? string.Empty
            : "CRITIC_ANCHORS (mốc cấu trúc, không cần trả quyết định):\n" +
              JsonSerializer.Serialize(anchors) + "\nEND_CRITIC_ANCHORS";
    }

    private static int DistanceToWindow(int index, int from, int to) =>
        index < from ? from - index : index > to ? index - to : 0;

    /// <summary>
    /// Chỉ backend RPC mới song song được. Model local chạy trong tiến trình trên một context duy
    /// nhất nên gửi chồng lên nhau không nhanh hơn, chỉ tranh nhau.
    /// </summary>
    private int ChunkParallelism => _options.Backend switch
    {
        InferenceBackend.LmStudio => _options.LmStudio.MaxParallelRequests,
        _ => 1,
    };

    private sealed record PassResult(
        Dictionary<int, List<int>> Votes,
        HashSet<int> ExplicitNonHeadings,
        Dictionary<int, SemanticRole> RejectedRoles);

    /// <summary>
    /// Cấp cuối cùng cho một đoạn. Chỉ built-in heading style được lấy cấp trực tiếp từ OOXML;
    /// outline level tự đặt là evidence có thể sai nên lấy phiếu mô hình làm chính.
    /// </summary>
    /// <summary>
    /// Thứ tự quyền lực khi gán cấp, mạnh trước yếu sau:
    /// <list type="number">
    /// <item>Danh sách đa cấp khai báo cấp này gắn với style Heading N (<c>w:lvl/w:pStyle</c>) —
    /// người soạn cấu hình một lần cho cả tài liệu, không dính thao tác định dạng lẻ.</item>
    /// <item>Style Heading built-in trên chính đoạn.</item>
    /// <item>Phiếu mô hình.</item>
    /// </list>
    /// Mô hình KHÔNG quyết cấp khi cấu trúc đã khai báo — nó chỉ còn việc xác nhận ngữ nghĩa.
    /// </summary>
    private int ResolveLevel(SlimParagraph p, List<int> modelLevels)
    {
        if (p.NumberingStyleLevel is { } fromList) return fromList;

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

        // Profile của model (ví dụ Qwen 7B dùng ngân sách 5K) phải áp lên CHÍNH ngân sách pipeline
        // dùng để chia khối, không phải lên một bản sao. Sau đó backend cục bộ tự nới context.
        _options.PrepareLocalModelProfile();
        _options.Llama.ChunkTokenBudget = _options.Chunking.TokenBudget;

        _model = _options.Backend switch
        {
            InferenceBackend.OpenRouter => OpenRouterHeaderExtractor.CreateOwned(_options.OpenRouter),
            InferenceBackend.LmStudio => LmStudioHeaderExtractor.CreateOwned(_options.LmStudio),
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
