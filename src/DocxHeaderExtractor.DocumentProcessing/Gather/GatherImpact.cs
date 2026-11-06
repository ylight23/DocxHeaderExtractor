using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Changes;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>A document in one explicitly named gather history. File names are matching hints, not proof of identity.</summary>
public sealed record GatherSnapshotDocument(string Id, string Name, string Role, DocxVersion Version);
public sealed record GatherSnapshot(DateTimeOffset Created, IReadOnlyList<GatherSnapshotDocument> Documents, GatherHeadingPlan Plan);
public sealed record GatherImpactEntry(string Status, string Heading, GatheredFact Fact);
public sealed record GatherImpactItem(string Kind, GatherImpactEntry? Before, GatherImpactEntry? After, bool NeedsReview = true);
public sealed record GatherImpactModification(string SourceName, string OldLocation, string NewLocation, string OldText, string NewText,
    string Evidence = "unique_para_id_changed_text", bool NeedsReview = true);
public sealed record GatherImpactReorder(string SourceName, ReorderAnalysis Analysis);
public sealed record GatherImpactDocumentDiff(string SourceName, string Scope, IReadOnlyList<Change> Changes,
    IReadOnlyList<string> BeforeUnsupported, IReadOnlyList<string> AfterUnsupported)
{
    /// <summary>Styles whose read definitions changed between the two versions (see ComparisonResult.StyleChanges).</summary>
    public IReadOnlyList<string> StyleChanges { get; init; } = [];

    /// <summary>The changes Word recorded with Track Changes in the newer version, beside the computed ones.</summary>
    public IReadOnlyList<TrackedRevision> Revisions { get; init; } = [];
}
public sealed record GatherImpactResult(DateTimeOffset PreviousAt, bool TargetChanged, IReadOnlyList<string> UnmatchedDocuments,
    IReadOnlyList<GatherImpactItem> Items, IReadOnlyList<GatherImpactModification> PossibleModifications,
    IReadOnlyList<GatherImpactReorder> Reorders, IReadOnlyList<GatherImpactDocumentDiff> DocumentDiffs, string Limitations);

/// <summary>Compare proposed plans; never reuse approvals, infer semantic equivalence or apply any change.</summary>
public static class GatherImpact
{
    static IEnumerable<GatherImpactEntry> Flatten(GatherHeadingPlan p) =>
        p.Sections.SelectMany(s => s.Run.Facts.Select(f => new GatherImpactEntry("PROPOSED", s.Heading, f)))
        .Concat(p.Uncertain.SelectMany(u => u.Facts.Select(f => new GatherImpactEntry("UNCERTAIN", u.SourceHeading, f))))
        .Concat(p.Excluded.Select(e => new GatherImpactEntry("EXCLUDED:" + e.Reason.Split(':')[0], e.Heading, e.Fact)));

    public static GatherImpactResult Compare(GatherSnapshot before, GatherSnapshot after)
    {
        var oldSources = before.Documents.Where(d => d.Role == "source").ToArray();
        var newSources = after.Documents.Where(d => d.Role == "source").ToArray();
        var map = new Dictionary<(string Id, string Location), (string Id, string Location)>();
        var unmatched = new List<string>();
        var modifications = new List<GatherImpactModification>();
        var reorders = new List<GatherImpactReorder>();
        var documentDiffs = new List<GatherImpactDocumentDiff>();
        void AddDocumentDiff(string name, DocxVersion a, DocxVersion b)
        {
            // The review view also compares the formatting this reader takes (direct, from styles, effective) and lists recorded revisions.
            var diff = DocxComparer.Compare(a, b, new ComparerOptions(CompareDirectFormatting: true));
            documentDiffs.Add(new(name, diff.Scope, diff.Changes, diff.TemplateUnsupported, diff.DocumentUnsupported)
            {
                StyleChanges = diff.StyleChanges ?? [],
                Revisions = diff.DocumentRevisions ?? [],
            });
        }
        foreach (var old in oldSources)
        {
            // Names are only provisional hints in this explicit user-named history. Duplicates are never aligned.
            var candidates = newSources.Where(d => string.Equals(d.Name, old.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (oldSources.Count(d => string.Equals(d.Name, old.Name, StringComparison.OrdinalIgnoreCase)) != 1 || candidates.Length != 1)
            { unmatched.Add("Trước: " + old.Name); continue; }
            var current = candidates[0];
            var oldIds = old.Version.Paragraphs.Where(p => !string.IsNullOrEmpty(p.ParaId)).GroupBy(p => p.ParaId!).ToDictionary(g => g.Key, g => g.ToArray());
            var newIds = current.Version.Paragraphs.Where(p => !string.IsNullOrEmpty(p.ParaId)).GroupBy(p => p.ParaId!).ToDictionary(g => g.Key, g => g.ToArray());
            var oldText = old.Version.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.RawText)).GroupBy(p => p.RawText).ToDictionary(g => g.Key, g => g.ToArray());
            var newText = current.Version.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.RawText)).GroupBy(p => p.RawText).ToDictionary(g => g.Key, g => g.ToArray());
            var newByLocation = current.Version.Paragraphs.ToDictionary(p => p.Location);
            foreach (var p in old.Version.Paragraphs)
            {
                if (old.Version.FileSha256 == current.Version.FileSha256 && newByLocation.ContainsKey(p.Location))
                { map[(old.Id, p.Location)] = (current.Id, p.Location); continue; }
                // Changed table/text-box geometry is not safely mapped by text alone.
                if (p.Container != "body" || string.IsNullOrWhiteSpace(p.RawText)) continue;
                ParagraphFingerprint? id = null;
                if (p.ParaId is { Length: > 0 } pid && oldIds[pid].Length == 1 && newIds.TryGetValue(pid, out var found) && found.Length == 1 && found[0].Container == "body") id = found[0];
                if (id is not null && id.RawText != p.RawText)
                { modifications.Add(new(old.Name, p.Location, id.Location, p.RawText, id.RawText)); continue; }
                var text = oldText[p.RawText].Length == 1 && newText.TryGetValue(p.RawText, out var same) && same.Length == 1 && same[0].Container == "body" ? same[0] : null;
                if (id is not null && text is not null && id.Location != text.Location) continue;
                var chosen = id ?? text;
                if (chosen is not null) map[(old.Id, p.Location)] = (current.Id, chosen.Location);
            }
            if (old.Version.FileSha256 != current.Version.FileSha256)
            {
                AddDocumentDiff(old.Name, old.Version, current.Version);
                var reorder = ReorderDetector.Analyze(old.Version, current.Version);
                if (reorder.Blocks.Count > 0) reorders.Add(new(old.Name, reorder));
            }
        }
        foreach (var n in newSources)
            if (newSources.Count(d => string.Equals(d.Name, n.Name, StringComparison.OrdinalIgnoreCase)) != 1 ||
                oldSources.Count(d => string.Equals(d.Name, n.Name, StringComparison.OrdinalIgnoreCase)) != 1) unmatched.Add("Hiện tại: " + n.Name);
        var oldTarget = before.Documents.SingleOrDefault(d => d.Role == "target");
        var newTarget = after.Documents.SingleOrDefault(d => d.Role == "target");
        var targetChanged = oldTarget is null || newTarget is null || oldTarget.Version.FileSha256 != newTarget.Version.FileSha256;
        if (targetChanged && oldTarget is not null && newTarget is not null)
        {
            AddDocumentDiff("Văn bản gốc: " + newTarget.Name, oldTarget.Version, newTarget.Version);
            var reorder = ReorderDetector.Analyze(oldTarget.Version, newTarget.Version);
            if (reorder.Blocks.Count > 0) reorders.Add(new("Văn bản gốc: " + newTarget.Name, reorder));
        }
        string? Key(GatherImpactEntry entry, bool isOld)
        {
            var parts = new List<object>();
            foreach (var p in entry.Fact.Parts)
            {
                var id = p.SourceId; var location = p.Location;
                if (isOld)
                {
                    if (!map.TryGetValue((id, location), out var mapped)) return null;
                    (id, location) = mapped;
                }
                else if (!newSources.Any(d => d.Id == id)) return null;
                parts.Add(new { id, location, p.Role, p.Text, p.Start, p.End });
            }
            return parts.Count == 0 ? null : JsonSerializer.Serialize(parts);
        }
        string? Additional(GatherImpactEntry entry, bool isOld)
        {
            var keys = entry.Fact.AlsoFrom.Select(p => Key(entry with { Fact = new GatheredFact([p], []) }, isOld)).ToArray();
            return keys.Any(k => k is null) ? null : JsonSerializer.Serialize(keys.OrderBy(k => k, StringComparer.Ordinal));
        }
        var left = Flatten(before.Plan).Select(e => (Entry: e, Key: Key(e, true))).ToArray();
        var right = Flatten(after.Plan).Select(e => (Entry: e, Key: Key(e, false))).ToArray();
        var lg = left.Where(x => x.Key is not null).GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.Select(x => x.Entry).ToArray());
        var rg = right.Where(x => x.Key is not null).GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.Select(x => x.Entry).ToArray());
        var items = new List<GatherImpactItem>();
        items.AddRange(left.Where(x => x.Key is null).Select(x => new GatherImpactItem("OLD_EVIDENCE_UNMAPPED", x.Entry, null)));
        items.AddRange(right.Where(x => x.Key is null).Select(x => new GatherImpactItem("NEW_EVIDENCE_UNMAPPED", null, x.Entry)));
        foreach (var key in lg.Keys.Union(rg.Keys))
        {
            var a = lg.GetValueOrDefault(key) ?? []; var b = rg.GetValueOrDefault(key) ?? [];
            if (a.Length > 1 || b.Length > 1)
            {
                items.AddRange(a.Select(x => new GatherImpactItem("AMBIGUOUS_OLD", x, null)));
                items.AddRange(b.Select(x => new GatherImpactItem("AMBIGUOUS_NEW", null, x)));
            }
            else if (a.Length == 0) items.Add(new("NEW_OR_UNMATCHED_EVIDENCE", null, b[0]));
            else if (b.Length == 0) items.Add(new("OLD_EVIDENCE_NOT_IN_NEW_PLAN", a[0], null));
            else
            {
                if (a[0].Status != b[0].Status || a[0].Heading != b[0].Heading) items.Add(new("PLACEMENT_OR_STATUS_CHANGED", a[0], b[0]));
                var x = Additional(a[0], true); var y = Additional(b[0], false);
                if (x is null || y is null) items.Add(new("ADDITIONAL_PROVENANCE_UNVERIFIED", a[0], b[0]));
                else if (x != y) items.Add(new("ADDITIONAL_PROVENANCE_CHANGED", a[0], b[0]));
            }
        }
        return new(before.Created, targetChanged, unmatched, items, modifications, reorders, documentDiffs,
            "So hai plan xem trước; tên file chỉ là tín hiệu ghép. Mục chưa ghép không khẳng định bị xóa hay mới về ngữ nghĩa. Diff đọc các vùng được hỗ trợ, không mang theo quyết định duyệt và không chứng minh đã chọn đủ thông tin.");
    }
}
