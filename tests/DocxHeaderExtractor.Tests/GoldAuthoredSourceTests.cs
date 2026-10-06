using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The one place Gold is written by hand: <c>eval/a99-closed-loop/gold/{ID}.gold.json</c>, one file
/// per document, one format for every document.
/// <para>
/// Everything a consumer reads - <c>gold-current/registry.v1.json</c> and
/// <c>gold-current/documents/{ID}.gold.v1.json</c> - is generated from these files by
/// <see cref="CanonicalGoldConsolidationTests"/>, and nothing else feeds it. The older trees
/// (<c>canonical-semantic-gold-vnext</c>, <c>strict-gold-*</c>) are history: they record how each
/// document's Gold was first derived, and no generator reads them any more.
/// </para>
/// <para>
/// To change a document's Gold: edit its authored file (and append what changed to its
/// <c>provenance</c>), then run <c>A99_FREEZE_UPDATE=1 dotnet test --filter
/// CanonicalGoldConsolidationTests</c>. The checks below fail closed on the mistakes an edit can make:
/// a source that is not the bytes it names, a claim list that does not add up to the approved total,
/// a claim that does not bind to its source.
/// </para>
/// </summary>
public sealed class GoldAuthoredSourceTests
{
    internal const string AuthoredRoot = "eval/a99-closed-loop/gold";
    internal const string AuthoredSchema = "a99-gold-authored-v1";

    [Fact]
    public void Migrate_the_consolidated_view_into_one_authored_file_per_document()
    {
        // One-shot, and never over an existing file: an authored file is the authority, so nothing
        // may regenerate it from a view that is itself derived from it.
        if (Environment.GetEnvironmentVariable("A99_GOLD_MIGRATE") != "1") return;

        foreach (var entry in CanonicalGoldRegistry.Entries)
        {
            var target = TestRepository.Path($"{AuthoredRoot}/{entry.AuthorityId}.gold.json");
            Assert.False(File.Exists(target), $"{target} already exists; refusing to overwrite authored Gold");
            var consumer = JsonNode.Parse(File.ReadAllText(TestRepository.Path(entry.CanonicalGoldPath)))!;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, new UTF8Encoding(false).GetBytes(
                Project(consumer).ToJsonString(FreezeArtifact.Json).ReplaceLineEndings("\n")));
        }
    }

    [Fact]
    public void Every_authored_file_is_exactly_what_its_generated_view_says()
    {
        // Authored -> generated -> projected back must be the identity. It fails when an authored
        // file was edited without regenerating, and when the generator stops being lossless.
        var files = AuthoredFiles();
        Assert.Equal(CanonicalGoldRegistry.Entries.Select(e => e.AuthorityId), files.Select(f => f.Id));
        foreach (var (id, path) in files)
        {
            var authored = JsonNode.Parse(File.ReadAllText(path))!;
            var consumer = JsonNode.Parse(File.ReadAllText(
                TestRepository.Path(CanonicalGoldRegistry.Entry(id).CanonicalGoldPath)))!;
            Assert.True(JsonNode.DeepEquals(authored, Project(consumer)),
                $"{id}: authored Gold and gold-current disagree - regenerate with A99_FREEZE_UPDATE=1");
        }
    }

    [Fact]
    public void Every_authored_source_is_the_bytes_it_names()
    {
        foreach (var (id, path) in AuthoredFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var source = doc.RootElement.GetProperty("source");
            var sourcePath = TestRepository.Path(source.GetProperty("sourcePath").GetString()!);
            Assert.True(File.Exists(sourcePath), $"{id}: source {sourcePath} does not exist");
            Assert.Equal(source.GetProperty("sourceSha256").GetString(), CanonicalArtifactHash.OfBytes(sourcePath));
        }
    }

    [Fact]
    public void Every_itemised_gold_adds_up_to_its_approved_total()
    {
        foreach (var (id, path) in AuthoredFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(AuthoredSchema, root.GetProperty("schemaVersion").GetString());
            if (root.GetProperty("occurrence").ValueKind == JsonValueKind.Null)
            {
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("occurrenceUnavailableReason").GetString()),
                    $"{id}: a count-only Gold must say why it has no occurrence list");
                continue;
            }
            Assert.Equal(root.GetProperty("semanticHeadingTotal").GetInt32(),
                root.GetProperty("occurrence").GetProperty("claims").GetArrayLength());
        }
    }

    [Fact]
    public void Every_structured_claim_binds_through_the_production_binder()
    {
        foreach (var (id, path) in AuthoredFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var occurrence = root.GetProperty("occurrence");
            if (occurrence.ValueKind == JsonValueKind.Null
                || occurrence.GetProperty("coordinateSystem").GetString() != "STRUCTURED_SOURCE_PARTS") continue;

            var atoms = PdfSourceOccurrenceAdapter.Build(
                TestRepository.Path(root.GetProperty("source").GetProperty("sourcePath").GetString()!)).Atoms;
            foreach (var claim in occurrence.GetProperty("claims").EnumerateArray())
            {
                var parts = claim.GetProperty("sourceParts").EnumerateArray().Select(part => new SemanticSourcePart(
                    part.GetProperty("sourceAlias").GetString()!,
                    part.GetProperty("selectionMode").GetString()!,
                    Text(part, "verbatimText"),
                    part.TryGetProperty("occurrence", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : null,
                    Text(part, "leftExactContext"),
                    Text(part, "rightExactContext"))).ToArray();
                var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(parts));
                Assert.True(binding.IsBound, $"{id}: {claim.GetProperty("identity").GetString()} does not bind ({binding.Reason})");
                Assert.Equal(claim.GetProperty("identity").GetString(), binding.Identity);
                // Where a claim records its resolved parts, they are the binder's, part for part.
                if (claim.TryGetProperty("boundParts", out var recorded))
                    Assert.Equal(
                        binding.Parts.Select((part, index) => (part.Alias, part.Start, part.End, part.Text,
                            index == 0 ? null : part.LocalityFromPrevious.ToString())),
                        recorded.EnumerateArray().Select(part => (part.GetProperty("sourceAlias").GetString()!,
                            part.GetProperty("utf16Span").GetProperty("start").GetInt32(),
                            part.GetProperty("utf16Span").GetProperty("end").GetInt32(),
                            part.GetProperty("text").GetString()!, Text(part, "localityFromPrevious"))));
            }
        }
    }

    [Fact]
    public void Every_alias_claim_text_is_in_its_source()
    {
        foreach (var (id, path) in AuthoredFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var occurrence = root.GetProperty("occurrence");
            if (occurrence.ValueKind == JsonValueKind.Null
                || occurrence.GetProperty("coordinateSystem").GetString() != "SOURCE_ALIAS") continue;

            var sourceText = string.Join("\n", Doc0258SourceRegenerationTests.ReadParagraphs(
                TestRepository.Path(root.GetProperty("source").GetProperty("sourcePath").GetString()!)).Select(p => p.Text));
            foreach (var claim in occurrence.GetProperty("claims").EnumerateArray())
            {
                var text = Text(claim, "verbatimText") ?? Text(claim, "exactText");
                Assert.False(string.IsNullOrEmpty(text), $"{id}: an alias claim carries no text");
                Assert.True(sourceText.Contains(text!, StringComparison.Ordinal) || Squash(sourceText).Contains(Squash(text!), StringComparison.Ordinal),
                    $"{id}: '{text}' is not in its source");
            }
        }
    }

    /// <summary>The authored form of a generated view: what a person decides, nothing the generator derives.</summary>
    internal static JsonObject Project(JsonNode consumer)
    {
        var occurrence = consumer["occurrence"]!;
        var evaluable = occurrence["occurrenceEvaluable"]!.GetValue<bool>();
        var capabilities = consumer["capabilities"]!;
        return new JsonObject
        {
            ["schemaVersion"] = AuthoredSchema,
            ["authorityId"] = consumer["authorityId"]!.GetValue<string>(),
            ["source"] = consumer["source"]!.DeepClone(),
            ["approval"] = consumer["approval"]!.DeepClone(),
            ["semanticHeadingTotal"] = consumer["semantic"]!["semanticHeadingTotal"]!.GetValue<int>(),
            ["declaredCapabilities"] = new JsonObject
            {
                ["visualBindingEvaluable"] = capabilities["visualBindingEvaluable"]!.GetValue<bool>(),
                ["hierarchyEvaluable"] = capabilities["hierarchyEvaluable"]!.GetValue<bool>(),
            },
            ["occurrence"] = evaluable
                ? new JsonObject
                {
                    ["coordinateSystem"] = occurrence["bindingCoordinateSystem"]!.GetValue<string>() == "STRUCTURED_SOURCE_PART_TUPLE"
                        ? "STRUCTURED_SOURCE_PARTS"
                        : "SOURCE_ALIAS",
                    ["claims"] = occurrence["claims"]!.DeepClone(),
                }
                : null,
            ["occurrenceUnavailableReason"] = evaluable ? null : occurrence["unavailableReason"]!.GetValue<string>(),
            ["provenance"] = consumer["provenance"]!.DeepClone(),
        };
    }

    internal static IReadOnlyList<(string Id, string Path)> AuthoredFiles() =>
        Directory.EnumerateFiles(TestRepository.Path(AuthoredRoot), "*.gold.json")
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path)[..^".gold.json".Length], path))
            .ToArray();

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Squash(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
