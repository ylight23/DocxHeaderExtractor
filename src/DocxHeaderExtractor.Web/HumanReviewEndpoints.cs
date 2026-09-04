using System.Text.Json;
using DocxHeaderExtractor.Application.Review;

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
    }
}
