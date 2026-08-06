using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.OpenXmlLayer;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Tool có tác dụng phụ ra ngoài tiến trình. Tách khỏi <see cref="IDocumentExtractionTool"/> vì
/// harness đối xử khác hẳn: chỉ chạy sau khi output đã qua validator VÀ qua human-review gate.
/// </summary>
public interface IDocumentActionTool : IDisposable
{
    AgentToolDescriptor Descriptor { get; }

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
