using DocxHeaderExtractor.DocumentProcessing.Inference;
using System.Text;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace DocxHeaderExtractor.Infrastructure.AI;

/// <summary>
/// Chạy suy luận với phần prompt dùng chung được nạp MỘT LẦN.
/// <para>
/// Prompt mỗi khối = [phần chung: system + luật + ví dụ one-shot + mở đầu user] + [document view] +
/// [đuôi: câu lệnh trả lời + mở lượt assistant]. Phần chung chiếm phần lớn và giống hệt nhau ở
/// mọi khối, nhưng <see cref="StatelessExecutor"/> nạp lại toàn bộ ở từng khối.
/// </para>
/// <para>
/// Ở đây phần chung được nạp vào một <see cref="Conversation"/> gốc, rồi mỗi khối
/// <see cref="Conversation.Fork"/> ra một nhánh dùng lại nguyên KV cache của phần chung.
/// Nhánh bị huỷ sau mỗi khối nên các khối vẫn độc lập với nhau — không nhiễm ngữ cảnh chéo.
/// </para>
/// </summary>
internal sealed class PrefixCachedRunner : IDisposable
{
    /// <summary>Chuỗi đánh dấu chỗ chèn document view, để cắt template thành phần chung và phần riêng.</summary>
    private const string Sentinel = "«DHX-CHUNK»";

    private readonly BatchedExecutor _executor;
    private readonly Conversation _root;
    private readonly LLamaWeights _weights;
    private readonly string _suffix;

    public int SharedPrefixTokens { get; }

    private PrefixCachedRunner(BatchedExecutor executor, Conversation root, LLamaWeights weights,
        string suffix, int prefixTokens)
    {
        _executor = executor;
        _root = root;
        _weights = weights;
        _suffix = suffix;
        SharedPrefixTokens = prefixTokens;
    }

    /// <summary>
    /// Dựng runner và nạp sẵn phần chung. Trả null khi không cắt được template — khi đó gọi
    /// phải quay về đường StatelessExecutor thay vì chạy sai.
    /// </summary>
    public static async Task<PrefixCachedRunner?> CreateAsync(
        LLamaWeights weights,
        ModelParams modelParams,
        Func<string, string, string> buildPrompt,
        CancellationToken ct)
    {
        var full = buildPrompt(HeaderPrompt.System, HeaderPrompt.BuildUser(Sentinel));
        var at = full.IndexOf(Sentinel, StringComparison.Ordinal);
        if (at < 0) return null;

        var prefix = full[..at];
        var suffix = full[(at + Sentinel.Length)..];
        if (prefix.Length == 0) return null;

        var executor = new BatchedExecutor(weights, modelParams);
        try
        {
            var root = executor.Create();
            root.Prompt(prefix, addBos: true, special: true);
            await InferAllAsync(executor, root, ct);

            return new PrefixCachedRunner(executor, root, weights, suffix, root.TokenCount);
        }
        catch
        {
            executor.Dispose();
            throw;
        }
    }

    /// <summary>Sinh đáp án cho một khối. Phần chung không được nạp lại.</summary>
    public async Task<string> RunAsync(string chunkXml, ISamplingPipeline pipeline, int maxTokens, CancellationToken ct)
    {
        var fork = _root.Fork();
        try
        {
            fork.Prompt(chunkXml + _suffix, addBos: false, special: true);
            await InferAllAsync(_executor, fork, ct);

            var decoder = new StreamingTokenDecoder(_executor.Context);
            var sb = new StringBuilder();

            for (int n = 0; n < maxTokens; n++)
            {
                ct.ThrowIfCancellationRequested();

                var token = pipeline.Sample(_executor.Context.NativeHandle, fork.GetSampleIndex());
                if (_weights.Vocab.EOS == token || _weights.Vocab.EOT == token) break;

                decoder.Add(token);
                sb.Append(decoder.Read());

                fork.Prompt(token);
                await InferAllAsync(_executor, fork, ct);
            }

            return sb.ToString();
        }
        finally
        {
            fork.Dispose();
        }
    }

    /// <summary>
    /// Một lần Prompt có thể xếp nhiều batch khi số token vượt BatchSize, nên phải chạy tới khi
    /// hội thoại hết cần suy luận. Bỏ vòng lặp này thì khối dài bị nạp thiếu và mô hình trả lời
    /// dựa trên prompt cụt.
    /// </summary>
    private static async Task InferAllAsync(BatchedExecutor executor, Conversation conversation, CancellationToken ct)
    {
        while (conversation.RequiresInference)
        {
            var result = await executor.Infer(ct);
            if (result != DecodeResult.Ok)
                throw new InvalidOperationException($"llama.cpp decode thất bại: {result}");
        }
    }

    public void Dispose()
    {
        _root.Dispose();
        _executor.Dispose();
    }
}
