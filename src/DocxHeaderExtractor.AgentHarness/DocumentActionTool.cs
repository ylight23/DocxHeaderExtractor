using DocxHeaderExtractor.Application.Capabilities;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Tool có tác dụng phụ ra ngoài tiến trình. Tách khỏi <see cref="IDocumentExtractionTool"/> vì
/// harness đối xử khác hẳn: chỉ chạy sau khi output đã qua validator VÀ qua human-review gate.
/// </summary>
public interface IDocumentActionTool : IDisposable
{
    CapabilityDescriptor Descriptor { get; }

    bool CanExecute(DocumentAgentRequest request);

    Task<AgentWritebackReport> ExecuteAsync(
        DocumentAgentRequest request,
        DocumentOutline outline,
        CancellationToken ct = default);
}

/// <summary>
/// M9.5b. The pdf-first-authority route's writeback: acts on the exact <c>PdfProductOutput</c>
/// the authority pipeline already materialized (<see cref="DocumentOutline.ProductOutput"/>),
/// never a reconstruction through <see cref="HeadingRecord"/>. The mutation itself lives in
/// <see cref="PdfProductWriteback"/>; this tool only carries the harness contract, mirroring
/// <see cref="OutlineWritebackTool"/> for every other route.
/// </summary>
public sealed class PdfProductWritebackTool(ExtractionOptions extraction) : IDocumentActionTool
{
    private readonly ExtractionOptions _extraction = extraction
        ?? throw new ArgumentNullException(nameof(extraction));

    public CapabilityDescriptor Descriptor { get; } = new(
        "write_document_outline",
        "Ghi w:outlineLvl của các heading đã chốt (M9 FinalStructure authority) vào một bản sao .docx; không sửa nội dung.",
        CapabilityRisk.High,
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

        var productOutput = outline.ProductOutput
            ?? throw new InvalidOperationException(
                "Outline không mang PdfProductOutput - route tạo ra nó không phải pdf-first-authority.");
        var target = request.WritebackTargetPath
                     ?? throw new InvalidOperationException("Run không có đích writeback.");

        var conversion = OfficeDocumentConverter.EnsureDocx(request.InputPath);
        try
        {
            var result = PdfProductWriteback.Apply(
                conversion.Path,
                target,
                productOutput,
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
            OfficeDocumentConverter.Cleanup(conversion);
        }
    }

    public void Dispose() { }
}
