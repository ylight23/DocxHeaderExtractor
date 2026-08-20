using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;
using DocxHeaderExtractor.Core.Pipeline;
using DocxHeaderExtractor.Core.Repair;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Tool có tác dụng phụ ra ngoài tiến trình. Tách khỏi <see cref="IDocumentExtractionTool"/> vì
/// harness đối xử khác hẳn: chỉ chạy sau khi output đã qua validator VÀ qua human-review gate.
/// </summary>
public interface IDocumentActionTool : IDisposable
{
    AgentToolDescriptor Descriptor { get; }

    bool CanExecute(DocumentAgentRequest request);

    Task<AgentWritebackReport> ExecuteAsync(
        DocumentAgentRequest request,
        DocumentOutline outline,
        CancellationToken ct = default);
}

/// <summary>
/// Ghi cấp heading đã chốt vào một bản sao .docx. Bản thân việc ghi nằm ở
/// <see cref="OutlineWriteback"/> trong Core; tool này chỉ lo phần hợp đồng của harness và
/// việc chuyển đổi định dạng đời cũ trước khi ghi.
/// </summary>
public sealed class OutlineWritebackTool(ExtractionOptions extraction) : IDocumentActionTool
{
    private readonly ExtractionOptions _extraction = extraction
        ?? throw new ArgumentNullException(nameof(extraction));

    public AgentToolDescriptor Descriptor { get; } = new(
        "write_document_outline",
        "Ghi w:outlineLvl của các heading đã chốt vào một bản sao .docx; không sửa nội dung.",
        AgentToolRisk.High,
        SendsDataExternally: false,
        MutatesExternalState: true);

    public bool CanExecute(DocumentAgentRequest request) => request.WantsWriteback;

    public Task<AgentWritebackReport> ExecuteAsync(
        DocumentAgentRequest request,
        DocumentOutline outline,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outline);
        ct.ThrowIfCancellationRequested();

        var target = request.WritebackTargetPath
                     ?? throw new InvalidOperationException("Run không có đích writeback.");

        var conversion = LegacyDocConverter.EnsureDocx(request.InputPath);
        try
        {
            var result = OutlineWriteback.Apply(
                conversion.Path,
                target,
                outline,
                _extraction,
                new OutlineWritebackOptions
                {
                    ApplyHeadingStyles = request.ApplyHeadingStyles,
                    Overwrite = request.AllowWritebackOverwrite,
                });

            return Task.FromResult(new AgentWritebackReport(
                result.OutputPath, result.Applied, result.Skipped.Count));
        }
        finally
        {
            LegacyDocConverter.Cleanup(conversion);
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Ghi partial key package để người duyệt tạo đáp án cho các file gate-pass nhưng thiếu key.
/// Tool này không tạo nhãn vàng tự động: file .key luôn có marker partial_human và cần duyệt.
/// </summary>
public sealed class PartialKeyPackageActionTool(PipelineOptions options) : IDocumentActionTool
{
    private readonly PartialKeyPackage _packager = new(options);

    public AgentToolDescriptor Descriptor { get; } = new(
        "create_partial_key_package",
        "Ghi current outline, CSV review và partial_human .key draft cho người duyệt.",
        AgentToolRisk.Medium,
        SendsDataExternally: false,
        MutatesExternalState: true);

    public bool CanExecute(DocumentAgentRequest request) => request.WantsKeyPackage;

    public async Task<AgentWritebackReport> ExecuteAsync(
        DocumentAgentRequest request,
        DocumentOutline outline,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outline);

        var outputDirectory = request.KeyPackageOutputDirectory
            ?? throw new InvalidOperationException("Run không có thư mục key package.");

        var result = await _packager.RunAsync(
            request.InputPath,
            outline,
            new PartialKeyPackageOptions(
                outputDirectory,
                request.KeyPackageLimit,
                request.KeyPackageStart,
                request.KeyPackageDistributedSample),
            ct);

        return new AgentWritebackReport(
            result.Directory,
            result.SelectedHeadings,
            Math.Max(0, result.TotalHeadings - result.SelectedHeadings));
    }

    public void Dispose() { }
}
