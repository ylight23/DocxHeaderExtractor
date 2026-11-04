using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>Where the admin page keeps stored outlines and their review decisions.</summary>
public sealed record OutlineRoot(string Path);

/// <summary>
/// Review of stored outlines on the admin page. A gather run shows, for each file whose outline came from the store, its review state and the
/// differences from the revision stored before it, with the paragraphs around each place. <c>POST /api/admin/outline/review</c> records the
/// reviewer's verdicts: the client sends only locations and verdicts; the decision is built from the stored outline, and only for the exact
/// outline the reviewer saw.
/// </summary>
internal static class AdminOutlineReview
{
    /// <summary>The review part of one document of a gather run, or null when its outline is not in the store.</summary>
    public static object? Project(OutlineReuseResult reuse, string docxPath, OutlineApprovals approvals)
    {
        if (reuse.Current is not { } current) return null;
        var state = OutlineApprovals.State(current, approvals.Of(current.SourceSha256, current.ConfigKey));
        var paragraphs = DocxVersionReader.Read(docxPath).Paragraphs;
        var at = paragraphs.Select((p, i) => (p.Location, i)).ToDictionary(x => x.Location, x => x.i, StringComparer.Ordinal);
        string Clip(string text) => text.Length > 200 ? text[..200] + "…" : text;
        object? Context(string? location) => location is not null && at.TryGetValue(location, out var i) ? new
        {
            before = i > 0 ? Clip(paragraphs[i - 1].RawText) : null,
            text = Clip(paragraphs[i].RawText),
            after = i + 1 < paragraphs.Count ? Clip(paragraphs[i + 1].RawText) : null,
        } : null;
        OutlineComparison? diff = reuse.Previous is { } previous
            ? OutlineDiff.Compare(previous, current, OutlineApprovals.ApprovedLocations(previous, approvals.Of(previous.SourceSha256, previous.ConfigKey)))
            : null;
        var headingAt = current.Headings.Where(h => h.StableId is not null).Select(h => h.StableId!).ToHashSet(StringComparer.Ordinal);
        return new
        {
            name = current.Name, current.SourceSha256, current.ConfigKey, outlineSha256 = OutlineApprovals.OutlineHash(current),
            revision = current.Revision, previousRevision = reuse.Previous?.Revision, state,
            sameHeadings = diff?.SameHeadings, changedParagraphs = diff?.ChangedParagraphs,
            differences = diff?.Differences.Select(d => new
            {
                d.Kind, d.OldLocation, d.NewLocation, d.OldText, d.NewText, d.OldLevel, d.NewLevel, d.OldParent, d.NewParent,
                d.OldApproved, d.ContextChanged, d.ContextEvidenceVerified, d.Uncertain, d.OldHeadingSpan, d.NewHeadingSpan,
                d.OldInlineBodySpan, d.NewInlineBodySpan, newIsHeading = d.NewLocation is { } l && headingAt.Contains(l),
                context = Context(d.NewLocation),
                details = d.Details.Select(c => new { c.Kind, c.OldLocation, c.NewLocation, c.OldText, c.NewText, c.OldLevel, c.NewLevel,
                    c.OldParent, c.NewParent, c.OldApproved, c.ContextChanged, c.ContextEvidenceVerified, c.Uncertain,
                    c.OldHeadingSpan, c.NewHeadingSpan, c.OldInlineBodySpan, c.NewInlineBodySpan,
                    newIsHeading = c.NewLocation is { } cl && headingAt.Contains(cl), context = Context(c.NewLocation) }),
            }),
            headings = current.Headings.OrderBy(h => h.Index).Select(h => new { h.StableId, h.Level, h.Text, bound = h.ValidatedSourcePartCount > 0 }),
        };
    }

    public sealed record ReviewItem(string? Location, string Kind);
    public sealed record ReviewRequest(string SourceSha256, string ConfigKey, string OutlineSha256, string Reviewer, IReadOnlyList<ReviewItem>? Items, bool Complete);

    public static IResult Record(ReviewRequest request, OutlineRoot root)
    {
        var outline = new OutlineStore(root.Path).Find(request.SourceSha256, request.ConfigKey);
        if (outline is null) return Results.NotFound(new { message = "Không tìm thấy outline đã lưu cho file này." });
        if (OutlineApprovals.OutlineHash(outline) != request.OutlineSha256)
            return Results.Conflict(new { message = "Outline đã thay đổi kể từ khi trang hiển thị; hãy tải lại trước khi duyệt." });
        var now = DateTimeOffset.UtcNow;
        var decisions = new List<OutlineDecision>();
        var refusals = new List<object>();
        foreach (var item in (request.Items ?? []).Concat(request.Complete ? [new ReviewItem(null, OutlineApprovals.OutlineComplete)] : []))
        {
            var (decision, refusal) = OutlineApprovals.Decide(outline, item.Kind, item.Location, request.Reviewer, now);
            if (decision is not null) decisions.Add(decision);
            else refusals.Add(new { item.Location, item.Kind, reason = refusal });
        }
        var approvals = new OutlineApprovals(root.Path);
        if (decisions.Count > 0) approvals.Add(decisions);
        return Results.Json(new
        {
            state = OutlineApprovals.State(outline, approvals.Of(outline.SourceSha256, outline.ConfigKey)),
            recorded = decisions.Count,
            refusals,
        }, WebJson.Options);
    }
}
