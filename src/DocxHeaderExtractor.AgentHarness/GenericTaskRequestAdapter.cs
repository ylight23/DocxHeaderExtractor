using DocxHeaderExtractor.Application.Tasks;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Compatibility adapter from the existing DOCX host request to the generic application request.
/// The locator is opaque to Application; local file access remains an outer-boundary concern.
/// </summary>
internal static class GenericTaskRequestAdapter
{
    public static AgentTaskRequest FromDocumentRequest(DocumentAgentRequest request) =>
        new(
            string.IsNullOrWhiteSpace(request.UserPrompt)
                ? "Extract the document structure."
                : request.UserPrompt,
            [new InputResource(
                "input-0",
                InputResourceKind.Document,
                Path.GetFileName(request.InputPath),
                MediaTypeFor(request.InputPath),
                request.InputPath)],
            new AgentTaskPermissions(
                request.AllowExternalDataTransfer,
                request.WantsAction),
            request.WantsAction ? "writeback" : null,
            "outline");

    private static string MediaTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".docm" => "application/vnd.ms-word.document.macroEnabled.12",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
}
