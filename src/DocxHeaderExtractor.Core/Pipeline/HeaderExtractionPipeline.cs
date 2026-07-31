using System.Diagnostics;
using DocxHeaderExtractor.Core.Chunking;
using DocxHeaderExtractor.Core.Llm;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.Core.Pipeline;

public sealed class PipelineOptions
{
    public ExtractionOptions Extraction { get; set; } = new();
    public LlamaOptions Llama { get; set; } = new();

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

    /// <summary>Ghi XML tinh gọn đã gửi cho mô hình ra file (debug).</summary>
    public string? DumpXmlPath { get; set; }

    /// <summary>In nguyên văn đầu ra của mô hình cho từng khối (debug prompt/grammar).</summary>
    public bool ShowRawOutput { get; set; }

    public Action<string>? Log { get; set; }
}

public sealed class HeaderExtractionPipeline(PipelineOptions options)
{
    private readonly PipelineOptions _options = options;

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
            Log($"OpenXML: {slim.Paragraphs.Count} đoạn → {candidates.Count} ứng viên " +
                $"({styled} theo style, {candidates.Count - styled} theo heuristic)");

            if (_options.DumpXmlPath is { } dump)
            {
                await File.WriteAllTextAsync(dump, SlimXmlSerializer.ToFullXml(slim, _options.Extraction), ct);
                Log($"Đã ghi XML tinh gọn: {dump}");
            }

            List<HeadingRecord> headings = _options.DisableLlm
                ? HeuristicOnly(candidates)
                : await RunModelAsync(slim, candidates, ct);

            if (_options.NormalizeLevels) NormalizeLevels(headings);

            sw.Stop();

            return new DocumentOutline
            {
                File = Path.GetFileName(inputPath),
                ParagraphCount = slim.Paragraphs.Count,
                CandidateCount = candidates.Count,
                Headings = headings,
                ElapsedMs = sw.ElapsedMilliseconds,
                Model = _options.DisableLlm ? null : Path.GetFileName(_options.Llama.ModelPath),
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
        if (candidates.Count == 0) return [];

        var lines = SlimXmlSerializer.BuildLines(slim, _options.Extraction);

        // Chỉ bỏ qua được khi TrustStyles bật — tắt nó đi thì câu trả lời của mô hình mới có trọng lượng.
        var skipStyled = _options.SkipStyledCandidates && _options.TrustStyles;
        var styled = candidates.Where(p => p.Role == ParagraphRole.StyledHeading).Select(p => p.Index).ToHashSet();
        Func<int, bool>? shouldAsk = skipStyled ? i => !styled.Contains(i) : null;

        if (skipStyled && styled.Count > 0)
            Log($"Bỏ qua {styled.Count} ứng viên đã có style/outlineLvl (giữ nguyên làm ngữ cảnh) — " +
                $"chỉ hỏi mô hình {candidates.Count - styled.Count} đoạn còn lại.");

        if (candidates.Count == styled.Count && skipStyled)
        {
            Log("Không còn đoạn nào cần hỏi — toàn bộ tiêu đề đã xác định bằng style.");
            return [.. candidates.Select(p => new HeadingRecord
            {
                Index = p.Index,
                Level = p.GuessedLevel ?? 1,
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Style,
                Confidence = 1.0,
            })];
        }

        Log($"Đang nạp mô hình: {Path.GetFileName(_options.Llama.ModelPath)} …");
        using var llm = await LlamaHeaderExtractor.LoadAsync(_options.Llama, ct);
        Log($"Đã nạp. Ngữ cảnh {llm.ContextSize} token, CPU {_options.Llama.Threads ?? LlamaHeaderExtractor.DefaultThreads()} luồng.");

        var passA = await RunPassAsync(llm, lines, _options.Llama.ChunkTokenBudget,
            _options.Llama.MaxCandidatesPerChunk, _options.TwoPass ? "lượt 1" : null, shouldAsk, ct);

        // Lượt 2 cắt khối nhỏ hơn hẳn ⇒ mép khối rơi vào chỗ khác, mỗi ứng viên có lân cận khác.
        var passB = _options.TwoPass
            ? await RunPassAsync(llm, lines, Math.Max(400, _options.Llama.ChunkTokenBudget / 2),
                Math.Max(4, _options.Llama.MaxCandidatesPerChunk / 2), "lượt 2", shouldAsk, ct)
            : null;

        var accepted = new Dictionary<int, HeadingRecord>();
        var keep = passB is null ? passA.Keys : passA.Keys.Union(passB.Keys);

        foreach (var index in keep)
        {
            var p = slim.ByIndex(index);
            if (p is null) continue;

            var levels = new List<int>();
            if (passA.TryGetValue(index, out var la)) levels.AddRange(la);
            if (passB is not null && passB.TryGetValue(index, out var lb)) levels.AddRange(lb);

            accepted[index] = new HeadingRecord
            {
                Index = p.Index,
                Level = ResolveLevel(p, levels),
                Text = p.Text,
                StyleId = p.StyleId,
                Source = HeadingSource.Model,
                Confidence = p.Role == ParagraphRole.StyledHeading ? 1.0 : Math.Max(0.5, p.Score),
                // Giữ lại vì MỘT lượt nhận, lượt kia loại ⇒ mô hình không ổn định ở đây.
                Disputed = passB is not null && passA.ContainsKey(index) != passB.ContainsKey(index),
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
            foreach (var p in candidates.Where(p => p.Role == ParagraphRole.StyledHeading))
            {
                if (accepted.ContainsKey(p.Index)) continue;
                accepted[p.Index] = new HeadingRecord
                {
                    Index = p.Index,
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

        return [.. accepted.Values.OrderBy(h => h.Index)];
    }

    /// <summary>
    /// Một lượt quét toàn tài liệu. Trả về: chỉ số đoạn → các cấp mô hình đã gán cho nó
    /// (nhiều giá trị khi đoạn nằm trong phần chồng lấn của hai khối liên tiếp).
    /// </summary>
    private async Task<Dictionary<int, List<int>>> RunPassAsync(
        LlamaHeaderExtractor llm,
        IReadOnlyList<XmlLine> lines,
        int chunkTokens,
        int maxCandidatesPerChunk,
        string? passLabel,
        Func<int, bool>? shouldAsk,
        CancellationToken ct)
    {
        var chunks = SlimXmlChunker.Split(
            lines, chunkTokens, _options.Llama.ChunkOverlap, maxCandidatesPerChunk, shouldAsk);
        var prefix = passLabel is null ? "" : passLabel + " — ";
        Log($"{prefix}chia thành {chunks.Count} khối XML (ngân sách {chunkTokens} token/khối)");

        var votes = new Dictionary<int, List<int>>();

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var xml = SlimXmlSerializer.WrapChunk(chunk.Lines, chunk.Number, chunks.Count);
            var allowed = chunk.CandidateIndexes;

            var result = await llm.ClassifyAsync(xml, allowed, ct);
            Log($"  {prefix}khối {chunk.Number}/{chunks.Count}: {allowed.Count} ứng viên → " +
                $"{result.Headings.Count} tiêu đề ({result.ElapsedMs} ms" +
                (result.RejectedIndexes > 0 ? $", loại {result.RejectedIndexes} chỉ số bịa" : "") + ")");

            if (_options.ShowRawOutput) Log($"    ↳ {result.RawOutput.ReplaceLineEndings(" ").Trim()}");

            foreach (var h in result.Headings)
            {
                if (!votes.TryGetValue(h.Index, out var list)) votes[h.Index] = list = [];
                list.Add(h.Level);
            }
        }

        return votes;
    }

    /// <summary>
    /// Cấp cuối cùng cho một đoạn. Ưu tiên <c>w:outlineLvl</c> đọc thẳng từ file: đó là cấp do
    /// người soạn đặt, còn cấp mô hình trả về chỉ là suy luận từ hình thức. Không có outlineLvl
    /// thì lấy cấp mô hình gán nhiều lần nhất (các khối chồng lấn có thể cho hai giá trị khác nhau).
    /// </summary>
    private int ResolveLevel(SlimParagraph p, List<int> modelLevels)
    {
        if (_options.LevelFromOutline && p.OutlineLevel is not null && p.GuessedLevel is { } fromFile)
            return fromFile;

        if (modelLevels.Count == 0) return p.GuessedLevel ?? 1;

        return modelLevels.GroupBy(l => l)
                          .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                          .First().Key;
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
}
