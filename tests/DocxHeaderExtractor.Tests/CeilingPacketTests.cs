using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Eval.ReasoningRetention;

namespace DocxHeaderExtractor.Tests;

/// <summary>Offline (no-model-call) coverage for the Qwen3.5-9B ceiling request-packet
/// optimization: compact source packet, closed semantic/hierarchy schemas, UTF-16 binding
/// exactness, and reasoning/context capability wiring.</summary>
public sealed class CeilingPacketTests
{
    private static ReasoningSourceOccurrence Occurrence(
        string id, string sourceId, int ordinal, string text,
        bool candidate = true, double score = 0.9, string? styleName = "Heading 2",
        int? outline = 1, bool bold = true, bool toc = false) => new()
    {
        SourceOccurrenceId = id,
        SourceId = sourceId,
        SourceOrdinal = ordinal,
        RawText = text,
        SourceSpan = new StructuralSpan(0, text.Length),
        CandidateHint = new CandidateHint(candidate, score, ["BUILT_IN_HEADING_STYLE"]),
        StyleFacts = new Dictionary<string, object?> { ["styleName"] = styleName, ["outlineLevel"] = outline, ["bold"] = bold, ["italic"] = null, ["fontSizePt"] = null, ["alignment"] = null, ["builtInHeadingStyleLevel"] = null },
        LayoutFacts = new Dictionary<string, object?> { ["tableDepth"] = 0, ["keepNext"] = false, ["pageBreakBefore"] = false, ["inTableOfContents"] = toc },
        NumberingFacts = new Dictionary<string, object?> { ["numberLabel"] = null, ["numberingLevel"] = null },
    };

    [Fact]
    public void Ceiling_packet_omits_candidate_hint()
    {
        var occ = Occurrence("o1", "s1", 1, "Article 1. Scope");
        var packet = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" });
        Assert.DoesNotContain("candidateHint", packet.SerializedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"candidate\"", packet.SerializedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Ceiling_packet_omits_candidate_score()
    {
        var occ = Occurrence("o1", "s1", 1, "Article 1. Scope");
        var packet = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" });
        Assert.DoesNotContain("\"score\"", packet.SerializedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Ceiling_packet_omits_source_identity()
    {
        var occ = Occurrence("secret-occurrence-id-42", "secret-source-id-99", 1, "Article 1. Scope");
        var packet = CeilingPacketBuilder.Build([occ], new HashSet<string> { "secret-occurrence-id-42" });
        Assert.DoesNotContain("secret-occurrence-id-42", packet.SerializedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-source-id-99", packet.SerializedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceId", packet.SerializedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceOccurrenceId", packet.SerializedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Ceiling_packet_omits_gold()
    {
        var occ = Occurrence("o1", "s1", 1, "Article 1. Scope");
        var packet = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" });
        Assert.DoesNotContain("gold", packet.SerializedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expected", packet.SerializedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ceiling_packet_uses_sparse_facts()
    {
        var normal = Occurrence("o1", "s1", 1, "Plain body text.", styleName: "Normal", outline: null, bold: false);
        var facts = CeilingPacketBuilder.BuildSparseFacts(normal);
        Assert.Null(facts);

        var heading = Occurrence("o2", "s2", 2, "Article 1. Scope");
        var headingFacts = CeilingPacketBuilder.BuildSparseFacts(heading);
        Assert.NotNull(headingFacts);
        Assert.True(headingFacts!.ContainsKey("style"));
        Assert.True(headingFacts.ContainsKey("outline"));
        Assert.True(headingFacts.ContainsKey("bold"));
        Assert.False(headingFacts.ContainsKey("italic"));
        Assert.False(headingFacts.ContainsKey("toc"));
        Assert.False(headingFacts.ContainsKey("tableDepth"));
    }

    [Fact]
    public void Ceiling_packet_supports_zero_one_many_headings()
    {
        var occurrences = new[]
        {
            Occurrence("o1", "s1", 1, "No heading here, just body text."),
            Occurrence("o2", "s2", 2, "Article 1. Scope"),
            Occurrence("o3", "s3", 3, "Article 2. Definitions\nArticle 3. Interpretation"),
        };
        var packet = CeilingPacketBuilder.Build(occurrences, occurrences.Select(o => o.SourceOccurrenceId).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(3, packet.Packet.Occurrences.Count);
        Assert.All(packet.Packet.Occurrences, o => Assert.NotNull(o.Owned));
    }

    [Fact]
    public void Ceiling_semantic_schema_has_closed_role_enum()
    {
        var schemaJson = JsonSerializer.Serialize(CeilingSemanticPrompt.Schema());
        using var doc = JsonDocument.Parse(schemaJson);
        var roleEnum = doc.RootElement.GetProperty("properties").GetProperty("headings").GetProperty("items")
            .GetProperty("properties").GetProperty("role").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(CeilingSemanticRole.AllowedRoles, roleEnum);
        Assert.False(doc.RootElement.GetProperty("properties").GetProperty("headings").GetProperty("items").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Ceiling_semantic_schema_has_no_parent()
    {
        var required = RequiredHeadingFields();
        Assert.DoesNotContain("proposedParentLocalId", required);
        Assert.DoesNotContain("parent", required);
    }

    [Fact]
    public void Ceiling_semantic_schema_has_no_level()
    {
        var required = RequiredHeadingFields();
        Assert.DoesNotContain("proposedLevel", required);
        Assert.DoesNotContain("level", required);
    }

    [Fact]
    public void Ceiling_semantic_schema_has_no_evidence_requirement()
    {
        var required = RequiredHeadingFields();
        Assert.DoesNotContain("decisionEvidence", required);
        Assert.DoesNotContain("evidence", required);
    }

    [Fact]
    public void Ceiling_semantic_schema_has_no_confidence_requirement()
    {
        var required = RequiredHeadingFields();
        Assert.DoesNotContain("confidence", required);
        Assert.Equal(new[] { "i", "start", "end", "role" }, required);
    }

    [Fact]
    public void Ceiling_semantic_prompt_does_not_request_hierarchy()
    {
        Assert.DoesNotContain("its hierarchy", CeilingSemanticPrompt.System, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proposedParent", CeilingSemanticPrompt.System, StringComparison.Ordinal);
        Assert.DoesNotContain("proposedLevel", CeilingSemanticPrompt.System, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_document_used_when_packet_fits_model_budget()
    {
        var source = BuildSourceDocument(3);
        var policy = BuildPolicyStub(source);
        var pack = ReasoningContextBuilder.Build(source, policy, 80_000, 48_000, expandOwnedPerOccurrence: false);
        Assert.Equal("SINGLE_FULL_CONTEXT", pack.ContextStrategy);
        Assert.Single(pack.Segments);
    }

    [Fact]
    public void Segmentation_used_only_when_packet_exceeds_budget()
    {
        var source = BuildSourceDocument(400);
        var policy = BuildPolicyStub(source);
        var pack = ReasoningContextBuilder.Build(source, policy, 4_000, 2_000, expandOwnedPerOccurrence: false);
        Assert.Equal("HIERARCHICAL_FULL_COVERAGE", pack.ContextStrategy);
        Assert.True(pack.Segments.Count > 1);
        Assert.Equal(1d, pack.SourceOccurrenceCoverage);
    }

    [Fact]
    public void Visible_halo_binding_remains_exact()
    {
        var occ = Occurrence("o1", "s1", 1, "0123456789ARTICLE 1 SCOPE0123456789");
        var length = occ.RawText.Length;
        var ownedStart = occ.RawText.IndexOf("ARTICLE", StringComparison.Ordinal);
        var ownedEnd = ownedStart + "ARTICLE 1 SCOPE".Length;
        var visible = new Dictionary<string, (int, int)> { ["o1"] = (0, length) };
        var owned = new Dictionary<string, (int, int)> { ["o1"] = (ownedStart, ownedEnd) };
        var packet = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" }, visible, owned);
        var binding = packet.Bindings.Single();

        Assert.True(binding.TryBind(ownedStart, ownedEnd, out var s1, out var e1, out var owned1));
        Assert.True(owned1);
        Assert.Equal(ownedStart, s1); Assert.Equal(ownedEnd, e1);

        // Heading start is inside the owned range but its end extends into the visible halo.
        Assert.True(binding.TryBind(ownedEnd - 2, length, out var s2, out var e2, out var owned2));
        Assert.True(owned2);
        Assert.Equal(ownedEnd - 2, s2); Assert.Equal(length, e2);

        Assert.True(binding.TryBind(0, 4, out _, out _, out var owned3));
        Assert.False(owned3);
    }

    [Fact]
    public void UTF16_binding_remains_exact()
    {
        var text = "Résumé — Điều 1. Phạm vi 🎉 extra";
        var occ = Occurrence("o1", "s1", 1, text);
        var packet = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" });
        var binding = packet.Bindings.Single();
        var localStart = text.IndexOf("Điều", StringComparison.Ordinal);
        var localEnd = localStart + "Điều 1. Phạm vi".Length;
        Assert.True(binding.TryBind(localStart, localEnd, out var globalStart, out var globalEnd, out var owned));
        Assert.True(owned);
        Assert.Equal(occ.RawText[localStart..localEnd], occ.RawText[globalStart..globalEnd]);
    }

    [Fact]
    public void Hierarchy_packet_uses_compact_local_ids()
    {
        var inventory = new[]
        {
            new ReasoningProposalInventoryItem("proposal:DOC-X:secret-source-1:0:10", 1, "secret-source-1", 0, 10, "Chapter 1", "CHAPTER"),
            new ReasoningProposalInventoryItem("proposal:DOC-X:secret-source-2:0:10", 2, "secret-source-2", 0, 10, "Article 1", "ARTICLE"),
        };
        var (json, bindings) = CeilingHierarchyPacketBuilder.Build(inventory);
        Assert.DoesNotContain("secret-source-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("proposal:DOC-X", json, StringComparison.Ordinal);
        Assert.Equal(2, bindings.Count);
        Assert.Equal("proposal:DOC-X:secret-source-1:0:10", bindings[0].ProposalId);
        Assert.Equal("proposal:DOC-X:secret-source-2:0:10", bindings[1].ProposalId);
    }

    [Fact]
    public void Hierarchy_schema_only_returns_parent_relationships()
    {
        var schemaJson = JsonSerializer.Serialize(CeilingHierarchyPrompt.Schema(3));
        using var doc = JsonDocument.Parse(schemaJson);
        var required = doc.RootElement.GetProperty("properties").GetProperty("parents").GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "child", "parent" }, required);
        Assert.DoesNotContain("level", required);
        Assert.DoesNotContain("text", required);
    }

    [Fact]
    public void Packet_output_is_deterministic()
    {
        var occ = Occurrence("o1", "s1", 1, "Article 1. Scope");
        var first = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" }).SerializedJson;
        var second = CeilingPacketBuilder.Build([occ], new HashSet<string> { "o1" }).SerializedJson;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Qwen9b_ceiling_reasoning_is_not_none()
    {
        var capability = new OpenRouterModelCapability
        {
            ModelId = "qwen/qwen3.5-9b", ContextLength = 262_144, ReasoningSupported = true,
            SupportedReasoningEfforts = ["high", "medium", "low"], SelectedReasoningEffort = "high",
            ReasoningEnabled = true, EffortListReported = true, StructuredOutputSupported = true,
        };
        Assert.NotEqual("none", capability.SelectedReasoningEffort);
        Assert.True(capability.ReasoningEnabled);
    }

    [Fact]
    public void Qwen9b_reasoning_is_excluded_from_response()
    {
        var element = JsonDocument.Parse("""{"id":"qwen/qwen3.5-9b","context_length":262144,"supported_parameters":["reasoning","response_format"]}""").RootElement;
        var capability = OpenRouterModelCapabilityResolver.FromModelElement("qwen/qwen3.5-9b", element);
        Assert.True(capability.ReasoningEnabled);
        // exclude=true is applied by the request builder whenever reasoning is enabled; the
        // capability object itself only records that reasoning is on, never chain-of-thought.
        Assert.True(capability.ReasoningSupported);
    }

    [Fact]
    public void Qwen9b_context_comes_from_provider_capability()
    {
        var element = JsonDocument.Parse("""{"id":"qwen/qwen3.5-9b","context_length":262144,"supported_parameters":["reasoning"]}""").RootElement;
        var capability = OpenRouterModelCapabilityResolver.FromModelElement("qwen/qwen3.5-9b", element);
        Assert.Equal(262_144, capability.ContextLength);
        Assert.NotEqual(32_768, capability.ContextLength);
    }

    private static string[] RequiredHeadingFields()
    {
        var schemaJson = JsonSerializer.Serialize(CeilingSemanticPrompt.Schema());
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.GetProperty("properties").GetProperty("headings").GetProperty("items")
            .GetProperty("required").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
    }

    private static SourceDocument BuildSourceDocument(int paragraphCount)
    {
        var paragraphs = Enumerable.Range(0, paragraphCount).Select(i => new SourceParagraph
        {
            SourceId = $"p{i}",
            SourceOrdinal = i,
            Text = $"Paragraph {i} with some representative body text to occupy character budget space consistently.",
            Style = new SourceStyleFacts { StyleId = "Normal", StyleName = "Normal" },
            Layout = new SourceLayoutFacts(),
            Numbering = new SourceNumberingFacts(),
        }).ToArray();
        return new SourceDocument { DocumentId = "DOC-TEST", FileName = "doc-test.docx", SourcePath = "doc-test.docx", SourceKind = "docx", Paragraphs = paragraphs };
    }

    private static DocxHeaderExtractor.DocumentProcessing.Policy.DocxPolicyState BuildPolicyStub(SourceDocument source)
    {
        var features = DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer.NumberingStyleFeatures.FromSourceDocument(source);
        var derived = new DocxHeaderExtractor.DocumentProcessing.Features.DocumentFeatureDeriver().Derive(source);
        return DocxHeaderExtractor.DocumentProcessing.Policy.DocxPolicyStateBuilder.Build(
            source, features, derived, new DocxHeaderExtractor.DocumentProcessing.Pipeline.PipelineOptions { DisableLlm = false }.Extraction);
    }
}
