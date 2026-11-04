using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Output;

namespace DocxHeaderExtractor.Web.Endpoints;

/// <summary>
/// <c>POST /api/admin/gather</c>: the admin page. Extracts the headings of one target DOCX and its source DOCX files (or takes uploaded outlines),
/// gathers the sources into a copy of the target by heading chain, asks the model to classify what the chain could not place, and streams the
/// progress and the result as NDJSON. Uncertain sections are shown with the model's advice and never written. The uploaded files are not changed.
/// </summary>
internal static class AdminGatherEndpoint
{
    public static async Task HandleAsync(HttpRequest req, HttpResponse res, WritebackStore writebackStore, AdminRunStore runs,
        ModelProviderFactory modelProvider, OutlineRoot outlineRoot, CancellationToken ct)
    {
        if (!req.HasFormContentType)
        {
            res.StatusCode = StatusCodes.Status400BadRequest;
            await res.WriteAsync("Cần multipart/form-data.", ct);
            return;
        }
        IFormCollection form;
        try { form = await req.ReadFormAsync(ct); }
        catch (BadHttpRequestException ex)
        {
            res.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await res.WriteAsync($"File quá lớn (trần {Uploads.MaxBytes / (1024 * 1024)} MB): {ex.Message}", ct);
            return;
        }
        catch (InvalidDataException)
        {
            res.StatusCode = StatusCodes.Status400BadRequest;
            await res.WriteAsync("Multipart upload không hợp lệ.", ct);
            return;
        }

        res.ContentType = "application/x-ndjson; charset=utf-8";
        res.Headers.CacheControl = "no-store";
        async Task EmitAsync(object payload)
        {
            await res.WriteAsync(JsonSerializer.Serialize(payload, WebJson.Options) + "\n", Encoding.UTF8, ct);
            await res.Body.FlushAsync(ct);
        }

        var targetUpload = form.Files.GetFile("target");
        var sourceUploads = form.Files.GetFiles("sources").Where(f => f.Length > 0).ToList();
        if (targetUpload is null || targetUpload.Length == 0 || sourceUploads.Count == 0)
        {
            await EmitAsync(new { type = "error", message = "Cần một văn bản đích và ít nhất một văn bản nguồn." });
            return;
        }
        if (new[] { targetUpload }.Concat(sourceUploads).FirstOrDefault(f => !IsWord(f.FileName)) is { } wrong)
        {
            await EmitAsync(new { type = "error", message = $"Tổng hợp hiện chỉ đọc file Word .docx/.docm: \"{Uploads.SafeName(wrong.FileName)}\"." });
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "dhx-ui", "admin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var events = Channel.CreateUnbounded<object>();
        var runId = Guid.NewGuid();
        void Stage(string stage, string kind, string message) =>
            events.Writer.TryWrite(new { type = "agent", runId, timestamp = DateTimeOffset.UtcNow, stage, kind, message });
        try
        {
            async Task<string> SaveAsync(IFormFile file, string folder)
            {
                var dir = Path.Combine(work, folder);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, Uploads.SafeName(file.FileName));
                await using var fs = File.Create(path);
                await file.CopyToAsync(fs, ct);
                return path;
            }
            var targetPath = await SaveAsync(targetUpload, "target");
            var sourcePaths = new List<string>();
            for (var i = 0; i < sourceUploads.Count; i++) sourcePaths.Add(await SaveAsync(sourceUploads[i], "source-" + (i + 1)));
            var outlines = new List<(string Name, string Stem, HeadingRecord[] Headings, string? File)>();
            foreach (var file in form.Files.GetFiles("outlines").Where(f => f.Length > 0))
            {
                using var reader = new StreamReader(file.OpenReadStream());
                try { outlines.Add(ReadOutline(Uploads.SafeName(file.FileName), await reader.ReadToEndAsync(ct))); }
                catch (JsonException) { await EmitAsync(new { type = "error", message = $"Outline \"{Uploads.SafeName(file.FileName)}\" không phải JSON outline hợp lệ." }); return; }
            }

            var provider = modelProvider();
            var noLlm = Flag(form, "noLlm");
            var classify = Flag(form, "classify") && !noLlm;
            var outlineCache = Flag(form, "outlineCache");
            var apiKey = provider.CreateRoute().ApiKey;
            var ids = SourceIds(sourcePaths);
            var documents = new[] { (Role: "target", Id: "target", Path: targetPath) }
                .Concat(sourcePaths.Select((p, i) => (Role: "source", Id: ids[i], Path: p))).ToList();
            var missing = documents.Where(d => FindOutline(outlines, d.Path) is null).ToList();
            if (missing.Count > 0 && (noLlm || string.IsNullOrWhiteSpace(apiKey)))
            {
                await EmitAsync(new { type = "error", message = (noLlm ? "Đã tắt mô hình" : "Server chưa có OPENROUTER_API_KEY") +
                    "; cần tải outline JSON cho: " + string.Join(", ", missing.Select(d => Path.GetFileName(d.Path))) + "." });
                return;
            }
            var hashes = documents.ToDictionary(d => d.Path, d => Hash(d.Path));

            var run = Task.Run(async () =>
            {
                // 1. Headings of every document: an uploaded outline when one names the file, otherwise the V6 heading pipeline (in parallel).
                var options = RequestOptions.Build(form, provider, out _);
                options.Log = _ => { };
                // Opt-in: an outline stored for the same file (or mapped from a stored version of the same file name) is used instead of a run.
                var store = outlineCache ? new OutlineStore(outlineRoot.Path) : null;
                var configKey = OutlineReuse.ConfigKey(options);
                var extracted = await Task.WhenAll(documents.Select(async d =>
                {
                    var name = Path.GetFileName(d.Path);
                    if (FindOutline(outlines, d.Path) is { } given)
                    {
                        Stage("extract", "Completed", $"{name}: dùng outline tải lên ({given.Headings.Length} heading).");
                        return (d.Role, d.Id, d.Path, Headings: (IReadOnlyList<HeadingRecord>)given.Headings, From: "outline tải lên", Reuse: (OutlineReuseResult?)null);
                    }
                    async Task<IReadOnlyList<HeadingRecord>> Extract()
                    {
                        Stage("extract", "Started", $"{name}: trích heading bằng pipeline V6.");
                        var execution = await new DocumentRunner(options, provider).ExtractAsync(d.Path, ct: ct);
                        return execution.Outline.Headings;
                    }
                    if (store is null)
                    {
                        var headings = await Extract();
                        Stage("extract", "Completed", $"{name}: {headings.Count} heading.");
                        return (d.Role, d.Id, d.Path, Headings: headings, From: "pipeline V6", Reuse: null);
                    }
                    var reuse = await OutlineReuse.GetAsync(d.Path, name, store, configKey, Extract);
                    var from = reuse.Status switch
                    {
                        "STORED" => "outline đã lưu (file không đổi)",
                        "EXACT" => "outline đã lưu (nội dung không đổi)",
                        "MAPPED" => $"outline đã lưu, ánh xạ qua diff ({reuse.AddedParagraphs.Count} đoạn mới chưa kiểm tra heading)",
                        _ => reuse.Reason is null ? "pipeline V6" : $"pipeline V6 (không dùng được outline đã lưu: {reuse.Reason})",
                    };
                    Stage("extract", "Completed", $"{name}: {reuse.Headings.Count} heading, {from}.");
                    return (d.Role, d.Id, d.Path, Headings: reuse.Headings, From: from, Reuse: (OutlineReuseResult?)reuse);
                }));
                var target = extracted.Single(e => e.Role == "target");
                var sources = extracted.Where(e => e.Role == "source").Select(e => new GatherSourceFile(e.Id, e.Path, e.Headings)).ToList();

                // 2. Placement by heading chain; what it cannot decide is held as uncertain.
                Stage("gather", "Started", "Ghép nội dung theo chuỗi heading.");
                var plan = OutlineReuse.FlagAdded(GatherByHeading.Build(target.Path, target.Headings, sources, addMissingHeadings: true),
                    extracted.Where(e => e.Reuse is { Status: "MAPPED" }).ToDictionary(e => e.Id, e => e.Reuse!.AddedParagraphs));
                Stage("gather", "Completed", $"{plan.Uncertain.Count} mục không chắc, {plan.Excluded.Count} đoạn không thêm.");

                // 3. The model classifies the uncertain sections; its verdict is advice shown next to them, never applied.
                GatherUncertainReviewResult? review = null;
                if (classify && plan.Uncertain.Count > 0 && !string.IsNullOrWhiteSpace(apiKey))
                {
                    Stage("classify", "Started", $"Model phân loại {plan.Uncertain.Count} mục không chắc.");
                    using var model = new GatherOpenRouterModel(apiKey!, maxCalls: 2, ceilingUsd: 0.5);
                    review = await GatherUncertainReview.ReviewAsync(plan.Uncertain, model, ct);
                    Stage("classify", review.Error is null ? "Completed" : "Partial",
                        review.Error is null ? "Model đã phân loại." : "Model không phân loại được: " + review.Error);
                }

                // 4. A copy of the target with the decided content as tracked changes. The uploaded files stay as they were.
                var output = Path.Combine(work, Path.GetFileNameWithoutExtension(target.Path) + "_tong_hop.docx");
                var decided = GatherByHeading.DecidedByChain(plan.Sections);
                var written = GatherWriteback.Apply(target.Path, output, decided.Where(s => s.Run.Facts.Count > 0 || s.NewHeadingDepth is not null).ToList(),
                    DateTime.UtcNow, targetOutlineCoverageVerified: true, verifiedTargetSha256: hashes[target.Path]);
                Stage("writeback", "Completed", $"Đã ghi bản tổng hợp; {written.SkippedHeadings.Count} mục không ghi được.");
                foreach (var (path, hash) in hashes)
                    if (Hash(path) != hash) throw new InvalidDataException("INPUT_CHANGED");
                return (Extracted: extracted, Plan: plan, Decided: decided, Review: review, Written: written);
            }, ct);
            _ = run.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);
            await foreach (var evt in events.Reader.ReadAllAsync(ct)) await EmitAsync(evt);
            var result = await run;

            writebackStore.Put(runId, await File.ReadAllBytesAsync(result.Written.OutputPath, ct), Path.GetFileName(result.Written.OutputPath));
            runs.Put(runId, new AdminRunStore.Entry(await File.ReadAllBytesAsync(targetPath, ct), Path.GetFileName(targetPath), result.Decided,
                result.Plan.Uncertain, DateTimeOffset.UtcNow));
            var approvals = new OutlineApprovals(outlineRoot.Path);
            await EmitAsync(new
            {
                type = "result",
                runId,
                download = $"/api/outline/{runId}.docx",
                modelClassified = result.Review is not null,
                documents = result.Extracted.Select(e => new
                {
                    e.Role, e.Id, file = Path.GetFileName(e.Path), from = e.From, reuse = e.Reuse?.Status, addedParagraphs = e.Reuse?.AddedParagraphs,
                    headings = e.Headings.OrderBy(h => h.Index).Select(h => new { h.Level, h.Text, h.StableId }),
                    outlineReview = e.Reuse is { } reuse ? AdminOutlineReview.Project(reuse, e.Path, approvals) : null,
                }),
                merged = result.Decided.Where(s => s.NewHeadingDepth is null && s.Run.Facts.Count > 0).Select(s => new
                {
                    s.Heading, location = s.TargetLocation, facts = s.Run.Facts.Select(Fact),
                }),
                added = result.Decided.Where(s => s.NewHeadingDepth is not null).Select(s => new
                {
                    s.Heading, parent = s.NewHeadingParent, source = s.NewHeadingSource, facts = s.Run.Facts.Select(Fact),
                }),
                uncertain = result.Plan.Uncertain.Select((u, i) => new
                {
                    u.Kind, u.SourceHeading, u.Source, u.SourceParent, u.Reason, u.NewHeadingParent,
                    candidates = u.Candidates.Select(c => new { c.Heading, c.Location, c.Content }),
                    facts = u.Facts.Select(Fact),
                    verdict = result.Review?.Verdicts.FirstOrDefault(v => v.Index == i),
                }),
                excluded = result.Plan.Excluded.Select(e => new { e.Reason, e.Heading, text = e.Fact.Render(), sources = Sources(e.Fact) }),
                unchanged = result.Decided.Where(s => s.NewHeadingDepth is null && s.Run.Facts.Count == 0).Select(s => new { s.Heading, s.Note }),
                skipped = result.Written.SkippedHeadings,
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var message = ex is NotSupportedException or FileNotFoundException or InvalidDataException
                ? ex.Message
                : $"Tổng hợp thất bại ({ex.GetType().Name}). Nội dung nguồn và phản hồi nhà cung cấp không được hiển thị.";
            try { await EmitAsync(new { type = "error", message }); } catch (Exception) { }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The body of <c>POST /api/admin/gather/{runId}/apply</c>: the reader's decision on each uncertain section.</summary>
    public sealed record ApplyRequest(IReadOnlyList<GatherDecision>? Decisions);

    /// <summary>
    /// <c>POST /api/admin/gather/{runId}/apply</c>: writes the summary again with the reader's decisions on the uncertain sections. The decisions
    /// come from the person on the page; the model's classification is never applied by the server.
    /// </summary>
    public static async Task<IResult> ApplyAsync(Guid runId, ApplyRequest request, AdminRunStore runs, WritebackStore writebackStore, CancellationToken ct)
    {
        if (runs.Get(runId) is not { } run)
            return Results.NotFound(new { message = "Lượt tổng hợp đã hết hạn (giữ 1 giờ). Hãy chạy tổng hợp lại." });
        var applied = GatherDecisions.Apply(run.Decided, run.Uncertain, request.Decisions ?? []);
        var work = Path.Combine(Path.GetTempPath(), "dhx-ui", "admin-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var target = Path.Combine(work, run.TargetName);
            await File.WriteAllBytesAsync(target, run.Target, ct);
            var output = Path.Combine(work, Path.GetFileNameWithoutExtension(run.TargetName) + "_tong_hop_da_duyet.docx");
            var written = GatherWriteback.Apply(target, output,
                applied.Sections.Where(s => s.Run.Facts.Count > 0 || s.NewHeadingDepth is not null).ToList(), DateTime.UtcNow,
                targetOutlineCoverageVerified: true, verifiedTargetSha256: Convert.ToHexStringLower(SHA256.HashData(run.Target)));
            var downloadId = Guid.NewGuid();
            writebackStore.Put(downloadId, await File.ReadAllBytesAsync(output, ct), Path.GetFileName(output));
            return Results.Json(new
            {
                download = $"/api/outline/{downloadId}.docx",
                outcomes = applied.Outcomes,
                undecided = applied.Undecided,
                skipped = written.SkippedHeadings,
            }, WebJson.Options);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    static object Fact(GatheredFact f) => new { text = f.Render(), sources = Sources(f), f.Flags };

    static IEnumerable<string> Sources(GatheredFact f) => f.Parts.Select(p => p.SourceId + " " + p.Location).Distinct();

    static bool IsWord(string name) => Path.GetExtension(name).ToLowerInvariant() is ".docx" or ".docm";

    static bool Flag(IFormCollection form, string key) => form[key].ToString() is "1" or "true" or "on";

    static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A short, distinct id per source from its file name: "08", "09"; a repeated name gets its position.</summary>
    static IReadOnlyList<string> SourceIds(IReadOnlyList<string> paths)
    {
        var stems = paths.Select(p => Path.GetFileNameWithoutExtension(p)).Select(s => s.Length > 24 ? s[..24] : s).ToList();
        return stems.Select((s, i) => stems.Count(x => x == s) > 1 ? $"{s}#{i + 1}" : s).ToList();
    }

    /// <summary>An outline JSON as the CLI writes it (an object with "headings", or the array itself), with the source file name it names.</summary>
    static (string Name, string Stem, HeadingRecord[] Headings, string? File) ReadOutline(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? file = null;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String) file = f.GetString();
            foreach (var key in new[] { "Outline", "outline" })
                if (root.TryGetProperty(key, out var outline) && outline.TryGetProperty("headings", out var inner)) root = inner;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("headings", out var direct)) root = direct;
        }
        var headings = root.Deserialize<HeadingRecord[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("OUTLINE_NOT_ARRAY");
        return (name, Path.GetFileNameWithoutExtension(name), headings, file);
    }

    /// <summary>The uploaded outline for a document: the one whose "file" is its name, or whose file name stem is its stem.</summary>
    static (string Name, string Stem, HeadingRecord[] Headings, string? File)? FindOutline(
        IReadOnlyList<(string Name, string Stem, HeadingRecord[] Headings, string? File)> outlines, string path)
    {
        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var o in outlines)
            if (string.Equals(o.File, name, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Stem, stem, StringComparison.OrdinalIgnoreCase))
                return o;
        return null;
    }
}
