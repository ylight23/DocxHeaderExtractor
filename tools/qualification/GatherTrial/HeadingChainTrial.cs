using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.DocumentProcessing.Gather;

/// <summary>
/// The heading-chain contract, without a model: the content of each source heading goes under the target heading with the same chain (parents and
/// own heading, dates ignored), and a heading the target does not have is added under its parent. Writes a copy of the target, never the target.
/// Usage: GatherTrial headings-chain &lt;target.docx&gt; &lt;target-outline.json&gt; &lt;output-directory&gt; &lt;id=source.docx=outline.json&gt;...
/// </summary>
static class HeadingChainTrial
{
    public static int Run(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: GatherTrial headings-chain <target.docx> <target-outline.json> <output-directory> <id=source.docx=outline.json>...");
            return 2;
        }
        var target = Path.GetFullPath(args[1]);
        var targetOutline = DocumentTrial.Outline(args[2]);
        var output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output)) throw new IOException("OUTPUT_DIRECTORY_ALREADY_EXISTS");
        var sources = args[4..].Select(a => a.Split('=')).Select(p => new GatherSourceFile(p[0], Path.GetFullPath(p[1]), DocumentTrial.Outline(p[2]))).ToList();
        static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var hashes = new[] { target }.Concat(sources.Select(s => s.Path)).ToDictionary(p => p, Hash);
        Directory.CreateDirectory(output);

        var plan = GatherByHeading.Build(target, targetOutline, sources, addMissingHeadings: true);
        var (sections, excluded) = plan;
        var decided = GatherByHeading.DecidedByChain(sections);
        var copy = Path.Combine(output, Path.GetFileNameWithoutExtension(target) + "_tong_hop.docx");
        var written = GatherWriteback.Apply(target, copy, decided.Where(s => s.Run.Facts.Count > 0 || s.NewHeadingDepth is not null).ToList(),
            DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: hashes[target]);
        foreach (var (path, hash) in hashes)
            if (Hash(path) != hash) throw new InvalidDataException("INPUT_CHANGED:" + path);

        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(output, "sections.json"), JsonSerializer.Serialize(new
        {
            target, sources = sources.Select(s => new { s.Name, s.Path }), hashes, modelCalls = 0, skipped = written.SkippedHeadings,
            sections = decided.Select(s => new
            {
                s.Heading, key = GatherByHeading.Key(s.Heading), s.Note, s.TargetLocation, s.NextHeadingLocation, s.NewHeadingDepth, s.NewHeadingParent,
                s.NewHeadingSource, facts = s.Run.Facts.Select(f => new { text = f.Render(), f.Flags, sources = f.Parts.Select(p => p.SourceId + " " + p.Location) }),
            }),
            excluded = excluded.Select(e => new { e.Heading, text = e.Fact.Render(), sources = e.Fact.Parts.Select(p => p.SourceId + " " + p.Location), e.Reason }),
            uncertain = plan.Uncertain.Select(u => new { u.Kind, u.SourceHeading, u.Source, u.SourceParent, u.Reason,
                candidates = u.Candidates.Select(c => new { c.Heading, c.Location }), facts = u.Facts.Select(f => f.Render()) }),
        }, options));
        File.WriteAllText(Path.Combine(output, "REPORT.md"), Report(target, sources, hashes, decided, excluded, plan.Uncertain, written, copy));
        Console.WriteLine($"merged={decided.Where(s => s.NewHeadingDepth is null).Sum(s => s.Run.Facts.Count)} newHeadings={decided.Count(s => s.NewHeadingDepth is not null)} " +
            $"newFacts={decided.Where(s => s.NewHeadingDepth is not null).Sum(s => s.Run.Facts.Count)} excluded={excluded.Count} skipped={written.SkippedHeadings.Count} output={copy}");
        return written.SkippedHeadings.Count == 0 ? 0 : 1;
    }

    static string Report(string target, IReadOnlyList<GatherSourceFile> sources, Dictionary<string, string> hashes, IReadOnlyList<GatherSection> sections,
        IReadOnlyList<GatherExcluded> excluded, IReadOnlyList<GatherUncertain> uncertain, GatherWriteResult written, string copy)
    {
        static string Src(GatheredFact f) => string.Join("; ", f.Parts.Select(p => p.SourceId + " " + p.Location).Distinct());
        static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
        var md = new StringBuilder();
        md.AppendLine("# Tổng hợp theo chuỗi heading");
        md.AppendLine();
        md.AppendLine($"- File đích (không bị sửa): `{target}` — sha256 `{hashes[target]}`");
        foreach (var s in sources) md.AppendLine($"- Nguồn `{s.Name}`: `{s.Path}` — sha256 `{hashes[s.Path]}`");
        md.AppendLine($"- File tổng hợp: `{copy}` (mọi phần thêm là Track Changes, tác giả \"{GatherWriteback.Author}\")");
        md.AppendLine("- Quyết định chỉ dựa vào chuỗi heading của outline; 0 lần gọi model.");
        md.AppendLine();
        var merged = sections.Where(s => s.NewHeadingDepth is null && s.Run.Facts.Count > 0).ToList();
        md.AppendLine($"## 1. Gộp vào heading có sẵn ({merged.Sum(s => s.Run.Facts.Count)} đoạn, {merged.Count} heading)");
        md.AppendLine();
        md.AppendLine("| Heading đích | Đoạn thêm | Nguồn | Cờ |");
        md.AppendLine("|---|---|---|---|");
        foreach (var s in merged)
            foreach (var f in s.Run.Facts)
                md.AppendLine($"| {Cell(s.Heading)} ({s.TargetLocation}) | {Cell(f.Render())} | {Src(f)} | {Cell(string.Join(", ", f.Flags))} |");
        md.AppendLine();
        var added = sections.Where(s => s.NewHeadingDepth is not null).ToList();
        md.AppendLine($"## 2. Heading mới ({added.Count} heading, {added.Sum(s => s.Run.Facts.Count)} đoạn)");
        md.AppendLine();
        md.AppendLine("| Heading mới | Đặt dưới | Lấy từ | Số đoạn |");
        md.AppendLine("|---|---|---|---|");
        foreach (var s in added)
            md.AppendLine($"| {Cell(s.Heading)} | {Cell(s.NewHeadingParent ?? "(đầu văn bản)")} | {s.NewHeadingSource} | {s.Run.Facts.Count} |");
        md.AppendLine();
        md.AppendLine("| Heading mới | Đoạn | Nguồn | Cờ |");
        md.AppendLine("|---|---|---|---|");
        foreach (var s in added)
            foreach (var f in s.Run.Facts)
                md.AppendLine($"| {Cell(s.Heading)} | {Cell(f.Render())} | {Src(f)} | {Cell(string.Join(", ", f.Flags))} |");
        md.AppendLine();
        md.AppendLine($"## 3. Không chắc — chưa ghi vào file ({uncertain.Count} heading, {uncertain.Sum(u => u.Facts.Count)} đoạn)");
        md.AppendLine();
        md.AppendLine("| Loại | Heading nguồn | Lấy từ | Có thể thuộc heading đích | Lý do | Đoạn |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var u in uncertain)
            md.AppendLine($"| {u.Kind} | {Cell(u.SourceHeading)} | {u.Source} | {Cell(string.Join("; ", u.Candidates.Select(c => c.Heading + " (" + c.Location + ")")))} | {Cell(u.Reason)} | {Cell(string.Join(" / ", u.Facts.Select(f => f.Render())))} |");
        md.AppendLine();
        md.AppendLine($"## 3b. Không thêm vào file ({excluded.Count} đoạn) — liệt kê đủ");
        md.AppendLine();
        md.AppendLine("| Lý do | Heading nguồn / đích | Đoạn | Nguồn |");
        md.AppendLine("|---|---|---|---|");
        foreach (var e in excluded.OrderBy(e => e.Reason, StringComparer.Ordinal))
            md.AppendLine($"| {Cell(e.Reason)} | {Cell(e.Heading)} | {Cell(e.Fact.Render())} | {Src(e.Fact)} |");
        md.AppendLine();
        var empty = sections.Where(s => s.NewHeadingDepth is null && s.Run.Facts.Count == 0).ToList();
        md.AppendLine($"## 4. Heading đích không nhận thêm gì ({empty.Count})");
        md.AppendLine();
        foreach (var s in empty) md.AppendLine($"- {s.Heading} ({s.TargetLocation}){(s.Note is null ? "" : " — " + s.Note)}");
        md.AppendLine();
        md.AppendLine($"## 5. Phần không ghi được vào file ({written.SkippedHeadings.Count})");
        md.AppendLine();
        foreach (var s in written.SkippedHeadings) md.AppendLine("- " + s);
        return md.ToString();
    }
}
