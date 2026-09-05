using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// Last hierarchy stage for source-grounded headings that marker/structural evidence could not
/// relate. The model proposes only a parent source id; deterministic validation owns the final
/// parent and level, and every accepted semantic relation remains review-only.
/// </summary>
internal static class PdfSemanticHierarchyFallback
{
    private const string SystemPrompt =
        "You resolve parent links for an existing PDF outline candidate list.\n" +
        "For each requested id, return one earlier allowed_parent_id or null.\n" +
        "Do not create ids, titles, headings, levels, or text. If the relationship is unclear, return null.\n" +
        "Return strict JSON: {\"items\":[{\"id\":\"child\",\"parent_id\":\"earlier-id-or-null\"}]}.";

    public static async Task<PdfSemanticHierarchyResult> ResolveAsync(
        IHeaderClassifier classifier,
        IReadOnlyList<PdfValidatedHeading> headings,
        IReadOnlyList<PdfValidatedStructure> structures,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        CancellationToken ct = default)
    {
        var headingIds = headings.Select(heading => heading.SourceId).ToHashSet(StringComparer.Ordinal);
        var unresolved = structures.Where(structure => structure.ParentResolution == "unresolved" &&
                contexts.ContainsKey(structure.SourceId))
            .OrderBy(structure => PositionOf(contexts[structure.SourceId]))
            .ToArray();
        if (unresolved.Length == 0) return new PdfSemanticHierarchyResult(structures, [], [], []);

        var allowedByChild = unresolved.ToDictionary(
            structure => structure.SourceId,
            structure => contexts[structure.SourceId].AllowedParentIds.Where(headingIds.Contains)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var prompt = BuildPrompt(unresolved, contexts, allowedByChild);
        var requestId = RequestId(unresolved.Select(item => item.SourceId));
        var modelRequest = new RouteModelRequestAudit(
            requestId, "hierarchy", unresolved.Select(item => item.SourceId).ToArray(),
            ProviderCallAttempted: true, ResponseObserved: false, Status: "STARTED");
        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(SystemPrompt, prompt, ct);
            modelRequest = modelRequest with { ResponseObserved = true, Status = "COMPLETED" };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            modelRequest = modelRequest with { Status = "FAILED" };
            return new PdfSemanticHierarchyResult(structures, [], [], ["hierarchy-model-call-failed"])
            {
                ModelRequests = [modelRequest],
            };
        }

        var proposals = Parse(raw, unresolved.Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal));
        var position = headings.ToDictionary(heading => heading.SourceId, heading => PositionOf(contexts[heading.SourceId]), StringComparer.Ordinal);
        var resolved = structures.Select(structure =>
        {
            if (structure.ParentResolution != "unresolved" || !proposals.TryGetValue(structure.SourceId, out var parentId) || parentId is null)
                return structure;
            if (!allowedByChild[structure.SourceId].Contains(parentId) || !position.TryGetValue(parentId, out var parentPosition) ||
                ComparePosition(parentPosition, position[structure.SourceId]) >= 0) return structure;

            var parent = structures.FirstOrDefault(candidate => candidate.SourceId == parentId);
            var level = parent is null ? 1 : Math.Clamp(parent.Level + 1, 1, 9);
            return structure with
            {
                ParentId = parentId,
                Level = level,
                ParentResolution = "semantic-proposal-validated",
                Decision = "requires_review",
            };
        }).ToArray();

        var audit = unresolved.Select(item =>
        {
            var parentId = proposals.GetValueOrDefault(item.SourceId);
            var final = resolved.Single(structure => structure.SourceId == item.SourceId);
            var status = final.ParentResolution == "semantic-proposal-validated"
                ? "accepted-review-only"
                : parentId is null ? "unresolved" : "rejected-parent-pointer";
            return new PdfHierarchyProposalAudit(item.SourceId, parentId, final.ParentId, status);
        }).ToArray();
        return new PdfSemanticHierarchyResult(resolved, audit, [raw], [prompt])
        {
            ModelRequests = [modelRequest],
        };
    }

    internal static string BuildPrompt(
        IReadOnlyList<PdfValidatedStructure> unresolved,
        IReadOnlyDictionary<string, PdfCandidateContext> contexts,
        IReadOnlyDictionary<string, HashSet<string>> allowedByChild) => JsonSerializer.Serialize(new
    {
        items = unresolved.Select(item => new
        {
            id = item.SourceId,
            source_text = contexts[item.SourceId].Source.RawText,
            active_heading_stack = contexts[item.SourceId].ActiveHeadingStack,
            allowed_parent_ids = allowedByChild[item.SourceId].OrderBy(id => id, StringComparer.Ordinal),
        }),
    });

    internal static IReadOnlyDictionary<string, string?> Parse(string raw, IReadOnlySet<string> allowedIds)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            using var document = JsonDocument.Parse(start >= 0 && end > start ? raw[start..(end + 1)] : raw);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (id is null || !allowedIds.Contains(id)) continue;
                var parentId = item.TryGetProperty("parent_id", out var parentElement) && parentElement.ValueKind == JsonValueKind.String
                    ? parentElement.GetString()
                    : null;
                result.TryAdd(id, parentId);
            }
        }
        catch (JsonException) { }
        return result;
    }

    private static (int Page, double InvertedY, string Id) PositionOf(PdfCandidateContext context) =>
        (context.Source.Page, -context.Source.TopY, context.Source.SourceId);

    private static int ComparePosition((int Page, double InvertedY, string Id) left,
        (int Page, double InvertedY, string Id) right)
    {
        var page = left.Page.CompareTo(right.Page);
        if (page != 0) return page;
        var y = left.InvertedY.CompareTo(right.InvertedY);
        return y != 0 ? y : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static string RequestId(IEnumerable<string> candidateIds)
    {
        var payload = Encoding.UTF8.GetBytes("hierarchy|" + string.Join("|", candidateIds.OrderBy(id => id, StringComparer.Ordinal)));
        return $"hierarchy:{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()[..16]}";
    }
}

internal sealed record PdfSemanticHierarchyResult(
    IReadOnlyList<PdfValidatedStructure> Structures,
    IReadOnlyList<PdfHierarchyProposalAudit> Audit,
    IReadOnlyList<string> RawResponses,
    IReadOnlyList<string> InputContracts)
{
    public IReadOnlyList<RouteModelRequestAudit> ModelRequests { get; init; } = [];
}

public sealed record PdfHierarchyProposalAudit(
    string Id,
    string? ProposedParentId,
    string? ResolvedParentId,
    string Resolution);
