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
/// Where a document's headings came from. <see cref="Status"/>: STORED (the outline of this exact file was stored), or EXTRACTED (the pipeline
/// ran; <see cref="Reason"/> says why a stored outline could not be used, null when there was none).
/// </summary>
public sealed record OutlineReuseResult(IReadOnlyList<HeadingRecord> Headings, string Status, string? Reason, IReadOnlyList<string> AddedParagraphs)
{
    /// <summary>The stored outline of this file as used now, when the store holds it.</summary>
    public StoredOutline? Current { get; init; }
    /// <summary>The revision of the same file name stored before <see cref="Current"/>, for review of what changed; null when there is none.</summary>
    public StoredOutline? Previous { get; init; }
}

/// <summary>
/// The DOCX outline of a file, reused when it can be and extracted when it cannot. The key holds the prompts, the model, the build of every
/// assembly that decides headings and the options that change the source, so any change to them is a miss. Changed source bytes always require
/// extraction: paragraph mapping cannot establish that added text is not a heading or that styles and multi-paragraph headings are unchanged.
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

    /// <param name="name">The file name the document is known by, used to retain its version history.</param>
    /// <param name="extract">Runs the pipeline on <paramref name="path"/> when no stored outline can be used.</param>
    public static async Task<OutlineReuseResult> GetAsync(string path, string name, OutlineStore store, string configKey,
        Func<Task<IReadOnlyList<HeadingRecord>>> extract)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (store.Find(sha, configKey) is { } stored)
            return new(stored.Headings, "STORED", null, [])
                { Current = stored, Previous = stored.PreviousSha256 is { } before ? store.Find(before, configKey) : null };

        var version = DocxVersionReader.Read(path);
        var paragraphs = OutlineStore.Paragraphs(version);
        string? reason = null;
        var previous = store.Latest(name, configKey);
        if (previous is not null)
            // An unchanged paragraph signature cannot establish unchanged heading boundaries, inherited styles or document structure.
            // Do not run an expensive diff that cannot authorize reuse: changed bytes always need extraction.
            reason = "SOURCE_CHANGED:OUTLINE_REQUIRES_REEXTRACTION";
        var headings = await extract().ConfigureAwait(false);
        StoredOutline? current = null;
        if (version.FileSha256 == sha)
        {
            // Read back what the store holds, so the outline hash a reviewer sees is the one the store will check.
            store.Store(new(OutlineStore.Version, sha, configKey, name, DateTimeOffset.UtcNow, paragraphs, version.UnsupportedRegions, headings));
            current = store.Find(sha, configKey);
        }
        return new(headings, "EXTRACTED", reason, []) { Current = current, Previous = previous };
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
