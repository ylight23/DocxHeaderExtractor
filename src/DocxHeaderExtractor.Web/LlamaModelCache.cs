using DocxHeaderExtractor.Core.Llm;

namespace DocxHeaderExtractor.Web;

/// <summary>
/// Cache một model gần nhất. Web đã tuần tự hóa suy luận bằng Gate nên không cần giữ nhiều bản
/// weights trong RAM và không có hai executor dùng chung model cùng lúc.
/// </summary>
public sealed class LlamaModelCache : IDisposable
{
    private LlamaHeaderExtractor? _model;
    private ModelLoadKey? _key;

    public async Task<LlamaHeaderExtractor> GetAsync(LlamaOptions options, CancellationToken ct)
    {
        options.ApplyRecommendedModelProfile();
        var key = ModelLoadKey.From(options);
        if (_model is not null && key == _key) return _model;

        // Tránh đỉnh RAM gấp đôi khi đổi model hoặc cấu hình context/GPU.
        _model?.Dispose();
        _model = null;
        _key = null;

        var loaded = await LlamaHeaderExtractor.LoadAsync(options, ct);
        _model = loaded;
        _key = key;
        return loaded;
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _key = null;
    }

    // Extractor giữ cả cấu hình nạp lẫn cấu hình sampling, vì vậy khóa phải bao phủ toàn bộ
    // LlamaOptions để request sau không dùng nhầm giới hạn token/grammar của request trước.
    private sealed record ModelLoadKey(
        string Path,
        uint ContextSize,
        int ChunkTokenBudget,
        int MaxOutputTokens,
        int? Threads,
        int? BatchThreads,
        uint BatchSize,
        int GpuLayerCount,
        float Temperature,
        uint Seed,
        GrammarMode GrammarMode,
        int ChunkOverlap,
        int MaxCandidatesPerChunk,
        bool VerboseNativeLog)
    {
        public static ModelLoadKey From(LlamaOptions o) => new(
            System.IO.Path.GetFullPath(o.ModelPath), o.ContextSize, o.ChunkTokenBudget,
            o.MaxOutputTokens, o.Threads, o.BatchThreads, o.BatchSize, o.GpuLayerCount,
            o.Temperature, o.Seed, o.GrammarMode, o.ChunkOverlap, o.MaxCandidatesPerChunk,
            o.VerboseNativeLog);
    }
}
