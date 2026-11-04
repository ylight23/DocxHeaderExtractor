using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.DocumentProcessing.Gather;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.Sources.Docx.Changes;

namespace DocxHeaderExtractor.App;

/// <summary>
/// Where a document's headings came from. <see cref="Status"/>: STORED (the outline of this exact file was stored), EXACT (a stored version of
/// this file name has the same paragraphs), MAPPED (the stored headings moved onto a version with added or removed paragraphs; the added ones in
/// <see cref="AddedParagraphs"/> were not checked for headings), EXTRACTED (the pipeline ran; <see cref="Reason"/> says why a stored outline could
/// not be used, null when there was none).
/// </summary>
public sealed record OutlineReuseResult(IReadOnlyList<HeadingRecord> Headings, string Status, string? Reason, IReadOnlyList<string> AddedParagraphs);

/// <summary>
/// The DOCX outline of a file, reused when it can be and extracted when it cannot. The key holds the prompts, the model, the build of every
/// assembly that decides headings and the options that change the source, so any change to them is a miss. A MAPPED outline is not stored:
/// its added paragraphs stay unchecked until the file is extracted again.
/// </summary>
public static class OutlineReuse
{
    public static string ConfigKey(PipelineOptions options) => OutlineStore.ConfigKey(
        PromptCatalog.Docx.Instruction, PromptCatalog.Docx.SystemPrompt, PromptCatalog.Docx.GroupPrompt ?? "", PromptCatalog.Docx.GroupInstruction ?? "",
        OpenRouterWire.ChatModel,
        typeof(PromptCatalog).Module.ModuleVersionId.ToString(), typeof(OpenRouterWire).Module.ModuleVersionId.ToString(),
        typeof(OutlineStore).Module.ModuleVersionId.ToString(), typeof(DocxVersionReader).Module.ModuleVersionId.ToString(),
        typeof(OutlineReuse).Module.ModuleVersionId.ToString(),
        JsonSerializer.Serialize(options.Extraction), options.DocxBoundarySnapParagraphs.ToString());

    /// <param name="name">The file name the document is known by; a new version under the same name is mapped onto the stored one.</param>
    /// <param name="extract">Runs the pipeline on <paramref name="path"/> when no stored outline can be used.</param>
    public static async Task<OutlineReuseResult> GetAsync(string path, string name, OutlineStore store, string configKey,
        Func<Task<IReadOnlyList<HeadingRecord>>> extract)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (store.Find(sha, configKey) is { } stored) return new(stored.Headings, "STORED", null, []);

        var version = DocxVersionReader.Read(path);
        var paragraphs = OutlineStore.Paragraphs(version);
        string? reason = null;
        if (store.Latest(name, configKey) is { } previous)
        {
            var mapped = OutlineMap.Map(previous, paragraphs, version.UnsupportedRegions);
            if (mapped.Status == "EXACT")
            {
                store.Store(new(OutlineStore.Version, sha, configKey, name, DateTimeOffset.UtcNow, paragraphs, version.UnsupportedRegions, mapped.Headings));
                return new(mapped.Headings, "EXACT", null, []);
            }
            if (mapped.Status == "MAPPED") return new(mapped.Headings, "MAPPED", null, mapped.AddedParagraphs);
            reason = mapped.Reason;
        }
        var headings = await extract().ConfigureAwait(false);
        if (version.FileSha256 == sha)
            store.Store(new(OutlineStore.Version, sha, configKey, name, DateTimeOffset.UtcNow, paragraphs, version.UnsupportedRegions, headings));
        return new(headings, "EXTRACTED", reason, []);
    }

    /// <summary>
    /// Marks every gathered fact that comes from a paragraph added since the stored outline: nobody decided whether that paragraph is a heading,
    /// so the section it was placed in is not established.
    /// </summary>
    public static GatherHeadingPlan FlagAdded(GatherHeadingPlan plan, IReadOnlyDictionary<string, IReadOnlyList<string>> addedBySource)
    {
        if (addedBySource.Count == 0) return plan;
        var added = addedBySource.SelectMany(kv => kv.Value.Select(l => (kv.Key, l))).ToHashSet();
        GatheredFact Flag(GatheredFact f) => f.Parts.Any(p => added.Contains((p.SourceId, p.Location)))
            ? f with { Flags = [.. f.Flags, "UNVERIFIED_STRUCTURE: đoạn mới so với outline đã lưu, chưa kiểm tra có phải heading"] } : f;
        return plan with
        {
            Sections = plan.Sections.Select(s => s with { Run = s.Run with { Facts = s.Run.Facts.Select(Flag).ToArray() } }).ToArray(),
            Uncertain = plan.Uncertain.Select(u => u with { Facts = u.Facts.Select(Flag).ToArray() }).ToArray(),
        };
    }
}
