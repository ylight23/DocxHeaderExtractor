using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.Tests;

/// <summary>Current shapes after explicit API retirement, not byte parity with retired DTOs.</summary>
public sealed class CleanupContractCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_facts_and_source_selection_have_explicit_current_shapes(bool web)
    {
        var facts = new SourceFacts
        {
            SourceId = "p0", RawText = "Heading", RawSpan = new SourceTextSpan(0, 7),
            Source = new SourceAnchor { SourceType = "docx", ParagraphIndex = 0 },
        };
        AssertShape(facts, web, "SourceId", "RawText", "Source", "RawSpan", "ParserBoundaries");
        var occurrence = new StructuralSourceOccurrence
        {
            SourceOccurrenceId = "o0", ObservedSourceFacts = [facts],
        };
        Assert.Equal("p0", Assert.Single(occurrence.ObservedSources).SourceId);
        // This internal authority input was already required+JsonIgnore before cleanup.
        // Default/Web serialization is unsupported; do not invent a wire-parity claim.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(occurrence, Options(web)));
        Assert.Contains(web ? "observedSourceFacts" : "ObservedSourceFacts", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Context_packet_has_three_evidence_layers_without_computed_duplicate(bool web) =>
        AssertShape(new SemanticContextPacket(["target"], ["local"], ["global"]), web,
            "TargetEvidence", "LocalContext", "GlobalContext");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_occurrence_has_explicit_current_shape_without_binding_mode(bool web) =>
        AssertShape(new CanonicalSemanticGraphOccurrence("o0", "n0", "O1", "p0", 0,
            "Heading", "HEADING", "Heading", "document", 0, 7, "text", null), web,
            "OccurrenceId", "SemanticNodeId", "SourceAlias", "SourceId", "SourceOrdinal", "Text",
            "SemanticRole", "StructuralType", "Scope", "Start", "End", "OccurrenceKind",
            "ParentOccurrenceId", "Level");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outline_with_no_legacy_outcome_keeps_existing_emitted_payload(bool web)
    {
        var options = Options(web);
        var outline = new DocumentOutline { File = "sample.docx", Headings = [] };
        // Previously Outcome was null and WhenWritingNull omitted it. Existing outline bytes
        // therefore remain unchanged for this fixture, unlike the removed non-ignored DTO fields.
        Assert.Equal("{\"file\":\"sample.docx\",\"paragraphCount\":0,\"sourceCount\":0,\"headings\":[],\"elapsedMs\":0,\"model\":null,\"disputedCount\":0}",
            JsonSerializer.Serialize(outline, options));
    }

    private static JsonSerializerOptions Options(bool web) => web
        ? new(JsonSerializerDefaults.Web) : new();

    private static void AssertShape<T>(T value, bool web, params string[] names)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Options(web)));
        Assert.Equal(names.Select(name => web ? JsonNamingPolicy.CamelCase.ConvertName(name) : name),
            json.RootElement.EnumerateObject().Select(property => property.Name));
    }
}
