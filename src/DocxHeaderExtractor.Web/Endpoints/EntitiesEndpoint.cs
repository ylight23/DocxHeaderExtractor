using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Harness;

namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>
/// <c>POST /api/entities</c>: runs the entity problem on one uploaded file and answers with its envelope and output as JSON. The provider is
/// the server's own (OPENROUTER_API_KEY); the uploaded copy is removed after the response.
/// </summary>
internal static class EntitiesEndpoint
{
    public static async Task<IResult> HandleAsync(HttpRequest req, ModelProviderFactory modelProvider, CancellationToken ct)
    {
        if (!req.HasFormContentType) return Results.BadRequest(new { message = "Cần multipart/form-data." });
        string? work = null;
        try
        {
            var form = await req.ReadFormAsync(ct);
            var upload = form.Files["file"];
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { message = "Chưa chọn file." });
            var provider = modelProvider();
            if (string.IsNullOrWhiteSpace(provider.CreateRoute().ApiKey))
                return Results.Json(new { message = $"Server chưa có {OpenRouterProvider.ApiKeyVariable}." }, statusCode: 503);
            work = Path.Combine(Path.GetTempPath(), "dhx-entities", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var path = Path.Combine(work, Uploads.SafeName(upload.FileName));
            await using (var stream = File.Create(path)) await upload.CopyToAsync(stream, ct);
            var run = await EntityExtraction.RunAsync(path, new PipelineOptions(), provider, ct);
            return Results.Json(new { envelope = run.Envelope, output = run.Output }, WebJson.Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.BadRequest(new { message = "Không chạy được bài toán thực thể cho file này." });
        }
        finally
        {
            if (work is not null) { try { Directory.Delete(work, recursive: true); } catch (IOException) { } }
        }
    }
}
