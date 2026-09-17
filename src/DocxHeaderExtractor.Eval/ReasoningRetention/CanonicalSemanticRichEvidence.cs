using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>Builds source-backed evidence and attention metadata without consulting Gold or
/// treating the attention signal as a recall gate.</summary>
public static class CanonicalSemanticRichEvidenceBuilder
{
    public static IReadOnlyList<CanonicalSemanticSourceEvidence> Build(
        SourceDocument source,
        DocumentSourceCatalog catalog,
        DocxPolicyState policy)
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(catalog);
        var paragraphs = source.Paragraphs
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .OrderBy(paragraph => paragraph.SourceOrdinal)
            .ToArray();
        var policyById = policy.Paragraphs.ToDictionary(item => item.Source.StableId, StringComparer.Ordinal);
        var paragraphIndex = paragraphs
            .Select((paragraph, index) => (paragraph, index))
            .ToDictionary(item => item.paragraph.SourceId, item => item.index, StringComparer.Ordinal);
        var evidence = new List<CanonicalSemanticSourceEvidence>(aliases.Count);
        foreach (var alias in aliases)
        {
            var paragraph = source.Paragraphs.Single(item => item.SourceId == alias.SourceId);
            var policyParagraph = policyById.GetValueOrDefault(alias.SourceId);
            var structuralSignals = new List<string>();
            if (policyParagraph?.IsCandidate == true) structuralSignals.Add("policy_candidate");
            if (paragraph.Style.BuiltInHeadingStyleLevel is not null) structuralSignals.Add("built_in_heading_style");
            if (paragraph.Style.OutlineLevel is not null) structuralSignals.Add("outline_level");
            if (paragraph.Numbering.NumberingId is not null) structuralSignals.Add("numbering_id");
            if (paragraph.Numbering.NumberingLevel is not null) structuralSignals.Add("numbering_level");
            if (paragraph.Layout.KeepNext) structuralSignals.Add("keep_next");
            if (paragraph.Layout.PageBreakBefore) structuralSignals.Add("page_break_before");
            if (paragraph.Layout.TableDepth > 0) structuralSignals.Add("table_depth");
            if (paragraph.Layout.InContentControl) structuralSignals.Add("content_control");
            if (paragraph.InTableOfContents) structuralSignals.Add("table_of_contents");
            var attention = new SemanticCandidateAttentionHint(
                alias.Alias,
                structuralSignals.Count > 0,
                structuralSignals.Count == 0 ? "NO_OBSERVED_STRUCTURAL_SIGNAL" : string.Join('|', structuralSignals));
            var index = paragraphIndex[paragraph.SourceId];
            var scope = paragraph.InTableOfContents ? "table_of_contents" :
                paragraph.Layout.TableDepth > 0 ? "table" :
                paragraph.Layout.InContentControl ? "content_control" : "document_body";
            var markerFacts = new List<string>();
            if (paragraph.Numbering.NumberingId is { } numberingId) markerFacts.Add($"numbering_id:{numberingId}");
            if (paragraph.Numbering.NumberingLevel is { } numberingLevel) markerFacts.Add($"numbering_level:{numberingLevel}");
            if (!string.IsNullOrWhiteSpace(paragraph.Numbering.NumberLabel)) markerFacts.Add($"number_label:{paragraph.Numbering.NumberLabel}");
            if (!string.IsNullOrWhiteSpace(paragraph.Numbering.NumberingFormat)) markerFacts.Add($"numbering_format:{paragraph.Numbering.NumberingFormat}");
            var containerFacts = new[]
            {
                $"source_kind:{source.SourceKind}",
                $"section_index:{paragraph.Layout.SectionIndex}",
                $"table_depth:{paragraph.Layout.TableDepth}",
                $"source_segment_count:{paragraph.SourceSegments.Count}",
                $"line_break_count:{paragraph.LineBreakOffsets.Count}",
            };
            evidence.Add(new CanonicalSemanticSourceEvidence(
                alias.Alias,
                alias.SourceId,
                alias.SourceOrdinal,
                alias.Text,
                scope,
                paragraph.Layout.TableDepth,
                paragraph.Layout.SectionIndex,
                paragraph.Layout.InContentControl,
                paragraph.InTableOfContents,
                containerFacts,
                new
                {
                    paragraph.Style.StyleId,
                    paragraph.Style.StyleName,
                    paragraph.Style.BuiltInHeadingStyleLevel,
                    paragraph.Style.OutlineLevel,
                    paragraph.Style.Bold,
                    paragraph.Style.Italic,
                    paragraph.Style.Underline,
                    paragraph.Style.AllCaps,
                    paragraph.Style.FontSizePt,
                    paragraph.Style.Alignment,
                },
                new
                {
                    paragraph.Numbering.NumberingId,
                    paragraph.Numbering.NumberingLevel,
                    paragraph.Numbering.NumberLabel,
                    paragraph.Numbering.NumberingFormat,
                    paragraph.Numbering.NumberingStyleHeadingLevel,
                },
                paragraph.TextSpans.Select(span => (object)new
                {
                    span.Start,
                    span.End,
                    span.Bold,
                    span.Italic,
                    span.Underline,
                    span.FontSizePt,
                }).ToArray(),
                markerFacts,
                structuralSignals,
                paragraphs.Skip(Math.Max(0, index - 2)).Take(Math.Min(2, index)).Select(item => Excerpt(item.Text)).ToArray(),
                paragraphs.Skip(index + 1).Take(2).Select(item => Excerpt(item.Text)).ToArray(),
                attention));
        }
        return evidence;
    }

    private static string Excerpt(string text) => text.Length <= 180 ? text : text[..180];
}

/// <summary>Single serializer shared by the offline request freeze and the live adapter. This is
/// the byte-fidelity boundary for the canonical rich-evidence request.</summary>
public static class CanonicalSemanticRequestMaterializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildPacket(
        CanonicalSemanticProductionInput input,
        SemanticContextPacket packedContext)
    {
        var aliases = SemanticSourceAliasCatalog.FromCatalog(input.SourceCatalog);
        return BuildPacket(aliases, input.SourceEvidence ?? [], input.CandidateHints, packedContext);
    }

    public static string BuildPacket(
        IReadOnlyList<SemanticSourceAlias> aliases,
        IReadOnlyList<CanonicalSemanticSourceEvidence> sourceEvidence,
        IReadOnlyList<SemanticCandidateAttentionHint> candidateHints,
        SemanticContextPacket packedContext)
    {
        var evidenceByAlias = sourceEvidence
            .ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
        var hintByAlias = candidateHints.ToDictionary(item => item.SourceAlias, StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            sourceAliases = aliases.Select(alias => new
            {
                alias = alias.Alias,
                text = alias.Text,
                sourceOrdinal = alias.SourceOrdinal,
                candidateAttention = hintByAlias.GetValueOrDefault(alias.Alias),
                sourceEvidence = evidenceByAlias.GetValueOrDefault(alias.Alias),
            }).ToArray(),
            context = new
            {
                targetEvidence = packedContext.TargetEvidence,
                localContext = packedContext.LocalContext,
                globalContext = packedContext.GlobalContext,
            },
            semanticReachability = new
            {
                canonicalOccurrenceCount = aliases.Count,
                semanticallyReachableOccurrenceCount = aliases.Count,
                candidateGating = false,
            },
        }, JsonOptions);
    }
}
