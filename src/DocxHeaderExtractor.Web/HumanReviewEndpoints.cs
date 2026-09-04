using System.Text.Json;
using DocxHeaderExtractor.Application.Review;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Review;

namespace DocxHeaderExtractor.Web;

public static class HumanReviewEndpoints
{
    public static void MapHumanReviewEndpoints(
        this WebApplication app,
        JsonSerializerOptions json)
    {
        app.MapGet("/api/review/{documentId}", async (
            string documentId,
            HumanReviewService service,
            CancellationToken ct) =>
        {
            var review = await service.GetReviewAsync(documentId, ct);
            return review is null
                ? Results.NotFound(new { message = "review-session-not-found" })
                : Results.Json(review, json);
        });

        app.MapGet("/api/review/{documentId}/decisions", async (
            string documentId,
            HumanReviewService service,
            CancellationToken ct) =>
        {
            var review = await service.GetReviewAsync(documentId, ct);
            if (review is null)
                return Results.NotFound(new { message = "review-session-not-found" });
            return Results.Json(await service.GetRecordsAsync(documentId, ct), json);
        });

        app.MapPost("/api/review/{documentId}/decisions", async (
            string documentId,
            HttpRequest request,
            HumanReviewService service,
            CancellationToken ct) =>
        {
            HumanReviewDecision? decision;
            try
            {
                decision = await request.ReadFromJsonAsync<HumanReviewDecision>(json, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "review-request-malformed" });
            }

            if (decision is null)
                return Results.BadRequest(new { code = "review-request-malformed" });

            try
            {
                return Results.Json(await service.RecordAsync(documentId, decision, ct), json);
            }
            catch (InvalidOperationException ex) when (ex.Message == "review-session-not-found")
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = ex.Message });
            }
        });

        app.MapPost("/api/review/{documentId}/writeback-plan", async (
            string documentId,
            HumanReviewService service,
            CancellationToken ct) =>
        {
            var plan = await service.BuildWritebackPlanAsync(documentId, cancellationToken: ct);
            return plan is null
                ? Results.NotFound(new { message = "review-session-not-found" })
                : Results.Json(plan, json);
        });

        app.MapPost("/api/review/{documentId}/writeback", async (
            string documentId,
            HttpRequest request,
            HumanReviewService service,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { code = "writeback-source-required" });

            var form = await request.ReadFormAsync(ct);
            var upload = form.Files["file"];
            if (upload is null || upload.Length == 0)
                return Results.BadRequest(new { code = "writeback-source-required" });

            var work = Path.Combine(Path.GetTempPath(), "dhx-human-review-writeback",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var sourcePath = Path.Combine(work, "source.docx");
            var targetPath = Path.Combine(work, "approved.outline.docx");
            try
            {
                await using (var stream = File.Create(sourcePath))
                    await upload.CopyToAsync(stream, ct);

                var extraction = new ExtractionOptions();
                var source = new AuthorityDocumentSourceReader(extraction).Read(sourcePath).Document;
                var plan = await service.BuildWritebackPlanAsync(
                    documentId, source, allowSourceDocumentIdAlias: true, cancellationToken: ct);
                if (plan is null)
                    return Results.NotFound(new { message = "review-session-not-found" });
                if (!plan.IsReady)
                    return Results.Conflict(plan);

                var result = ApprovedWritebackExecutor.Apply(
                    sourcePath, targetPath, plan, extraction,
                    explicitApproval: true, allowSourceDocumentIdAlias: true);
                var content = await File.ReadAllBytesAsync(result.OutputPath, ct);
                return Results.File(
                    content,
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    "approved-outline.docx");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = ex.Message });
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }
        });
    }
}
