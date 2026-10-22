using System.Text.Json.Nodes;
using DocxHeaderExtractor.Contracts.Inference;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.DocumentProcessing.Structure;
using DocxHeaderExtractor.DocumentProcessing.Audit;
using DocxHeaderExtractor.DocumentProcessing.Output;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.Sources.Docx.OpenXmlParser;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.DocumentProcessing.Intake;
using DocxHeaderExtractor.DocumentProcessing.Selection;
using DocxHeaderExtractor.Sources.Docx;
using DocxHeaderExtractor.HeadingAgent.Placement;


namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A heading the placement pass puts under another one keeps both its level and its parent relation. The level comes from the parent, so a
/// lost relation would show as a root heading at a depth it has no parent for.
/// </summary>
public sealed class ParentRelationTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dhx-parent-" + Guid.NewGuid().ToString("N"));

    public ParentRelationTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_heading_placed_under_another_keeps_its_parent_relation_and_level()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "parent.docx"));
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot;
        // Chapter I (atom 0) and GENERAL PROVISIONS (atom 1) are both headings; the placement pass says GENERAL sits under Chapter I.
        var chapter = source.Atoms.OrderBy(a => a.Ordinal).First(a => a.Text == "Chapter I");
        var general = source.Atoms.OrderBy(a => a.Ordinal).First(a => a.Text == "GENERAL PROVISIONS");
        var placement = new FixedPlacement(new JsonObject
        {
            ["placements"] = new JsonArray(new JsonObject { ["alias"] = general.Alias, ["parent"] = chapter.Alias }),
        }.ToJsonString());
        var selector = new FixedSelector(new HashSet<string> { chapter.Alias, general.Alias });
        var authority = new SelectionAuthority(selector, path, "sk-test", new Dictionary<string, string>(StringComparer.Ordinal),
            placement, new HeadingPlacer(), () => { });

        var decided = await authority.DecideAsync(source, CancellationToken.None);
        var assembly = await StructureAssembler.AssembleAsync(source, decided, PrimaryOccurrences.EverySourceOccurrence,
            CancellationToken.None);

        var chapterId = assembly.Structure.Elements.Single(e => e.Sources.Any(s => s.SourceId == chapter.SourceId)).Id;
        var generalId = assembly.Structure.Elements.Single(e => e.Sources.Any(s => s.SourceId == general.SourceId)).Id;
        var parent = assembly.Structure.Relations.SingleOrDefault(r => r.Type == StructuralRelationType.ParentChild && r.ToId == generalId);
        Assert.NotNull(parent);
        Assert.Equal(chapterId, parent!.FromId);
        Assert.Equal(2, assembly.Structure.Elements.Single(e => e.Id == generalId).Level);
    }

    [Fact]
    public async Task A_heading_placed_under_one_that_failed_validation_gets_no_level_and_no_relation()
    {
        var path = DocxTestDocument.Create(Path.Combine(dir, "invalid-parent.docx"));
        var source = DocxSourceAdapter.Build(new OpenXmlDocumentSource(new PipelineOptions().Extraction).Read(path)).Snapshot;
        var chapter = source.Atoms.OrderBy(a => a.Ordinal).First(a => a.Text == "Chapter I");
        var general = source.Atoms.OrderBy(a => a.Ordinal).First(a => a.Text == "GENERAL PROVISIONS");
        var article = source.Atoms.OrderBy(a => a.Ordinal).First(a => a.Text == "Article 1. Scope");
        // GENERAL's span runs past its text, so validation refuses it: it is bound, but it never becomes an element.
        HeadingExtentDecision Decision(SemanticSourceAtom atom, TextOffsetSpan span) =>
            new(atom.SourceId, "pdf-v6-selected-lines-v1", span, "ESTABLISHES_STRUCTURE",
                [new CanonicalSemanticBoundPart(atom.Alias, atom.SourceId, atom.Ordinal, atom.Text, 0, atom.Text.Length)]);
        CanonicalSemanticBoundHeading Bound(SemanticSourceAtom atom) =>
            new(atom.Alias, atom.SourceId, atom.Ordinal, atom.Text, "ESTABLISHES_STRUCTURE", "heading", "document_body", [], 0, atom.Text.Length)
            {
                Parts = [new CanonicalSemanticBoundPart(atom.Alias, atom.SourceId, atom.Ordinal, atom.Text, 0, atom.Text.Length)],
            };
        var decisions = new[]
        {
            Decision(chapter, new TextOffsetSpan(0, chapter.Text.Length)),
            Decision(general, new TextOffsetSpan(0, 100_000)),
            Decision(article, new TextOffsetSpan(0, article.Text.Length)),
        };
        var placement = new FixedPlacement(new JsonObject
        {
            ["placements"] = new JsonArray(new JsonObject { ["alias"] = article.Alias, ["parent"] = general.Alias }),
        }.ToJsonString());
        var authority = new SelectionResult(decisions,
            new[] { Bound(chapter), Bound(general), Bound(article) }, placement, (audit, _) => audit, null);

        var assembly = await StructureAssembler.AssembleAsync(source, authority, PrimaryOccurrences.EverySourceOccurrence,
            CancellationToken.None);

        Assert.DoesNotContain(assembly.Validated, v => v.SourceId == general.SourceId);
        var articleId = assembly.Structure.Elements.Single(e => e.Sources.Any(s => s.SourceId == article.SourceId)).Id;
        Assert.Null(assembly.Structure.Elements.Single(e => e.Id == articleId).Level);
        Assert.DoesNotContain(assembly.Structure.Relations, r => r.ToId == articleId);
    }

    private sealed class FixedSelector(IReadOnlySet<string> aliases) : IHeadingSelector
    {
        public string SelectorId => "fixed-parent-test";

        public Task<HeadingSelection> SelectAsync(string pdfPath, string apiKey, CancellationToken ct) =>
            Task.FromResult(new HeadingSelection(aliases, 1, 1, [], ["{}"], 1, 0.0));
    }

    private sealed class FixedPlacement(string reply) : IInferenceTransport
    {
        public string ModelName => "fixed";
        public int ContextSize => 1 << 20;
        public string RuntimeDescription => "parent relation test";
        public int SharedPrefixTokens => 0;

        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default, int expectedItemCount = 0) =>
            Task.FromResult(HierarchyTestProtocol.Reply(userMessage, JsonNode.Parse(reply)!["placements"]!.AsArray()
                .ToDictionary(p => p!["alias"]!.GetValue<string>(), p => p!["parent"]!.GetValue<string>())) ?? reply);

        public void Dispose() { }
    }
}
