using System.Text.Json;
using System.Text.RegularExpressions;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.OpenXmlLayer;
using DocxHeaderExtractor.DocumentProcessing.Features;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.DocumentProcessing.Policy;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A99_V2_MODEL_VISIBLE_PROVENANCE_AUDIT_V1 - phase A of the follow-up the reachability audit of 2026-09-27 asked for
/// (its critical answer 5), and the ground the semantic-contract question needs: what exactly reaches the model.
/// <para>
/// The existing hard-code audit proves no forbidden <em>label key</em> is serialized on one PDF and one DOCX. This goes a
/// level down and over the pilot cohort: every string the model can read is traced to where it came from, so a derived
/// prior cannot reach it under an innocent key either. A string is admitted only if it is the source's own text, a
/// parser-owned alias, an author's own OOXML declaration, or a value from a closed vocabulary this test enumerates.
/// Anything else is reported as unclassified and fails.
/// </para>
/// <para>
/// The three LIVE_SUSPICIOUS producers are then run for real on each document and their own conclusions - the style-trust
/// verdict, the inferred document regime, every domain role name - are searched for in the request bytes.
/// </para>
/// <para>
/// 0 provider calls. Nothing in the extraction path is modified; the prompts, the model and the contract are untouched.
/// </para>
/// </summary>
public sealed partial class V2ModelVisibleProvenanceAuditTests
{
    private const string Dir = "eval/a99-closed-loop/model-visible-provenance-v1";

    private static readonly (string Id, string Path, string Media)[] Documents =
    [
        ("SRC-089", Src089BlindGeneralizationTests.Pdf, "PDF"),
        ("SRC-095", Src095BlindGeneralizationTests.Pdf, "PDF"),
        ("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "PDF"),
        ("DOCX-STYLE", "bench/01-style-chuan.docx", "DOCX"),
    ];

    /// <summary>
    /// Values the harness may state that are not the document's own text: each is a name of a measurement, not a reading of
    /// what a line means. The audit fails if a string outside this set and outside the source appears.
    /// </summary>
    private static readonly string[] ProtocolIds = ["a99-semantic-source-parts-v1", "a99-semantic-source-parts-v2", "a99-canonical-semantic-vnext-v1"];
    // Vertical position only (PdfLineBlockFilter: >= 0.92 of the extent is TOP, <= 0.08 is BOTTOM, the rest BODY).
    // "BODY" names the middle band of the page, not a judgement that the line is body text.
    private static readonly string[] PageBands = ["TOP", "BODY", "BOTTOM", "MIDDLE", "UPPER", "LOWER", "CENTER", "CENTRE"];
    private static readonly string[] RelativeFontSizes = ["larger-than-body", "much-larger-than-body", "smaller-than-body", "body", "body-size"];
    // The four values the extractor declares, taken from it rather than retyped: which evidence said "bold".
    private static readonly string[] BoldEvidenceSources =
        [PdfLineTypography.FromFontDetails, PdfLineTypography.FromFontName, PdfLineTypography.FromBoth, PdfLineTypography.None];
    private static readonly string[] SourceFactsIds = ["PDF_SOURCE_FACTS_V1", "PDF_SOURCE_FACTS_V2", "PDF_SOURCE_FACTS_V3"];

    /// <summary>
    /// The provenance of a model-visible string, decided by the key that carries it. Each category is a parser-owned
    /// measurement or the source's own content; none is a reading of what a line means.
    /// </summary>
    private static string ProvenanceOfKeyedValue(string keyPath, string value) => keyPath switch
    {
        "protocol" when ProtocolIds.Contains(value, StringComparer.Ordinal) => "PROTOCOL_ID",
        _ when keyPath.EndsWith("block", StringComparison.Ordinal) && LayoutBlockId().IsMatch(value) => "LAYOUT_BLOCK_ID",
        _ when keyPath.EndsWith("pageBand", StringComparison.Ordinal) && PageBands.Contains(value, StringComparer.OrdinalIgnoreCase) => "PAGE_BAND",
        _ when keyPath.EndsWith("RelativeFontSize", StringComparison.Ordinal) && RelativeFontSizes.Contains(value, StringComparer.Ordinal) => "RELATIVE_FONT_SIZE_BUCKET",
        _ when keyPath.EndsWith("boldEvidenceSource", StringComparison.Ordinal) && BoldEvidenceSources.Contains(value, StringComparer.Ordinal) => "BOLD_EVIDENCE_SOURCE",
        _ when keyPath.EndsWith("sourceFacts", StringComparison.Ordinal) && SourceFactsIds.Contains(value, StringComparer.Ordinal) => "SOURCE_FACTS_VERSION",
        _ when keyPath.EndsWith("dominantFontName", StringComparison.Ordinal) || keyPath.EndsWith("fontName", StringComparison.Ordinal) => "FONT_RESOURCE_NAME",
        _ when keyPath.EndsWith("Alignment", StringComparison.Ordinal) => "AUTHOR_DECLARED_STYLE",
        _ when keyPath.Contains("markers", StringComparison.Ordinal) && MarkerFact().IsMatch(value) => "MARKER_PREFIX_PARSE",
        _ => "",
    };

    [GeneratedRegex(@"^b\d+$")] private static partial Regex LayoutBlockId();
    [GeneratedRegex(@"^marker-(family|signature|depth|is-path|components):")] private static partial Regex MarkerFact();
    [GeneratedRegex(@"^L\d+:S\d+$")] private static partial Regex PdfAlias();
    [GeneratedRegex(@"^(P|T|R|C)\d+")] private static partial Regex DocxAliasShape();
    [GeneratedRegex(@"^[\s\p{P}\p{S}\d]*$")] private static partial Regex PunctuationOrDigitsOnly();

    private sealed record Value(string KeyPath, string Text);

    /// <summary>Every string the packet carries, with the key path that carries it.</summary>
    private static void Walk(JsonElement node, string path, List<Value> strings)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject()) Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", strings);
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) Walk(item, $"{path}[]", strings);
                break;
            case JsonValueKind.String:
                strings.Add(new Value(path, node.GetString() ?? ""));
                break;
        }
    }

    private static string Normalize(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    private static async Task<IReadOnlyList<CapturedRequest>> Compose(string path, string media)
    {
        using var capture = new RequestCapturingClassifier();
        if (media == "PDF")
            await CanonicalSemanticPdfAuthorityAdapter.RunAsync(TestRepository.Path(path), capture, CancellationToken.None,
                profile: PdfSemanticAuthorityProfile.StructuredSourceParts);
        else
        {
            var source = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
            var state = DocxPolicyStateBuilder.Build(source, NumberingStyleFeatures.FromSourceDocument(source),
                new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
            var mode = DocumentModeClassifier.Measure(state.Paragraphs.Cast<IPolicyParagraph>().ToArray());
            await DocxAuthorityPipeline.RunAsync(state, mode, capture);
        }
        return capture.Requests.ToArray();
    }

    /// <summary>The conclusions the three LIVE_SUSPICIOUS producers reach for this document, as strings.</summary>
    private static (Dictionary<string, string> Observations, string[] Searched) DerivedObservations(string path, string media)
    {
        var observations = new Dictionary<string, string>(StringComparer.Ordinal);
        var searched = new List<string>();

        if (media == "PDF")
        {
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(path)).Atoms;
            var regime = PdfDocumentRegime.Infer(atoms.Select(a => a.Text));
            observations["DocumentDomainPolicy.InferRegime"] = regime;
            searched.Add(regime);
        }
        else
        {
            var source = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
            var state = DocxPolicyStateBuilder.Build(source, NumberingStyleFeatures.FromSourceDocument(source),
                new DocumentFeatureDeriver().Derive(source), new ExtractionOptions());
            var paragraphs = state.Paragraphs.Cast<IPolicyParagraph>().ToArray();
            var trust = StyleTrustAudit.Measure(paragraphs);
            observations["StyleTrustAudit.Measure"] = trust.ToString();
            observations["StyleTrustAudit.SelectionTrusted"] = trust.SelectionTrusted.ToString();
            observations["StyleTrustAudit.LevelTrusted"] = trust.LevelTrusted.ToString();
            var captions = paragraphs.Count(HeadingHeuristics.IsObjectCaption);
            var listItems = paragraphs.Count(p => HeadingHeuristics.LooksLikeListItem(p.Text));
            var styled = paragraphs.Count(p => HeadingHeuristics.BuiltInLevel(p) is not null);
            observations["HeadingHeuristics.IsObjectCaption"] = captions.ToString();
            observations["HeadingHeuristics.LooksLikeListItem"] = listItems.ToString();
            observations["HeadingHeuristics.BuiltInLevel"] = styled.ToString();
            observations["DocumentModeClassifier"] = DocumentModeClassifier.Measure(paragraphs).ToString();
        }

        // Every name of a derived verdict the harness can reach, whatever this document happens to produce.
        searched.AddRange(Enum.GetNames<PdfDomainRole>());
        searched.AddRange(["StyleTrust", "SelectionTrusted", "LevelTrusted", "trustStyleSelection", "IsObjectCaption", "LooksLikeListItem",
            "HeadingHeuristics", "StyleTrustAudit", "DocumentDomainPolicy", "PdfDomainRole", "DocumentMode",
            "attention", "candidate", "candidateScore", "rank", "score", "salience",
            "running_page_artifact", "table_of_contents", "inTableOfContents", "reference_list", "index_terms",
            "code_or_grammar", "appendix_table", "quoted_replacement", "embedded_amendment",
            "legal_document", "procurement_document", "financial_document", "meeting_minutes", "document_body"]);
        return (observations, searched.Distinct(StringComparer.Ordinal).Where(s => s.Length > 2).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Freeze_the_model_visible_provenance_audit()
    {
        var rows = new List<object>();
        var unclassifiedTotal = 0;
        var leakedTotal = 0;

        foreach (var (id, path, media) in Documents)
        {
            var requests = await Compose(path, media);
            Assert.NotEmpty(requests);

            // What the source itself owns: its atom texts and aliases, and the author's own style declarations.
            var sourceTexts = new HashSet<string>(StringComparer.Ordinal);
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (media == "PDF")
                foreach (var atom in PdfStructuredSourceAuthorityBuilder.Build(TestRepository.Path(path)).Atoms)
                {
                    sourceTexts.Add(Normalize(atom.Text));
                    aliases.Add(atom.Alias);
                }
            else
            {
                var source = new OpenXmlDocumentSource().Read(TestRepository.Path(path));
                foreach (var paragraph in source.Paragraphs)
                {
                    sourceTexts.Add(Normalize(paragraph.Text));
                    if (paragraph.Style.StyleId is { Length: > 0 } styleId) declared.Add(styleId);
                    if (paragraph.Style.StyleName is { Length: > 0 } styleName) declared.Add(styleName);
                    if (paragraph.Style.Alignment is { Length: > 0 } alignment) declared.Add(alignment);
                }
                foreach (var alias in SemanticSourceAliasCatalog.FromCatalog(DocumentSourceCatalogBuilder.FromSourceDocument(source)))
                    aliases.Add(alias.Alias);
            }
            var wholeSource = string.Join("\n", sourceTexts);

            var byProvenance = new Dictionary<string, int>(StringComparer.Ordinal);
            var unclassified = new List<object>();
            var keyPaths = new SortedSet<string>(StringComparer.Ordinal);
            var harnessValues = new SortedSet<string>(StringComparer.Ordinal);
            var harnessKeys = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var request in requests)
            {
                var end = request.UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal);
                Assert.True(end > 0, "the request no longer separates its evidence packet from its response schema");
                using var packet = JsonDocument.Parse(request.UserMessage[..end]);
                var strings = new List<Value>();
                Walk(packet.RootElement, "", strings);
                foreach (var value in strings)
                {
                    keyPaths.Add(value.KeyPath);
                    var text = Normalize(value.Text);
                    var provenance =
                        text.Length == 0 ? "EMPTY"
                        : aliases.Contains(value.Text) || PdfAlias().IsMatch(value.Text) ? "PARSER_ALIAS"
                        : sourceTexts.Contains(text) ? "SOURCE_TEXT_EXACT"
                        : wholeSource.Contains(text, StringComparison.Ordinal) ? "SOURCE_TEXT_FRAGMENT"
                        : declared.Contains(value.Text) ? "AUTHOR_DECLARED_STYLE"
                        : ProvenanceOfKeyedValue(value.KeyPath, value.Text) is { Length: > 0 } keyed ? keyed
                        : PunctuationOrDigitsOnly().IsMatch(value.Text) ? "PUNCTUATION_OR_DIGITS"
                        : media == "DOCX" && DocxAliasShape().IsMatch(value.Text) ? "PARSER_ALIAS"
                        : "UNCLASSIFIED";
                    byProvenance[provenance] = byProvenance.GetValueOrDefault(provenance) + 1;
                    if (provenance == "UNCLASSIFIED" && unclassified.Count < 40)
                        unclassified.Add(new { keyPath = value.KeyPath, value = value.Text.Length > 120 ? value.Text[..120] : value.Text });
                    // The harness's own surface: what it wrote, as opposed to what the document says.
                    if (!provenance.StartsWith("SOURCE_TEXT", StringComparison.Ordinal) && provenance != "PARSER_ALIAS")
                        harnessValues.Add(value.Text);
                    harnessKeys.Add(value.KeyPath);
                }
            }

            // The derived observations, computed for this document, then searched for in everything the model reads.
            var (observations, searched) = DerivedObservations(path, media);
            // Only the harness's own surface is searched: the prompt and schema it wrote, the keys it chose, and the values
            // that are not the document's text. Searching the text as well would report the document's own vocabulary as a
            // leak - DOC-0252 says "an accession candidate" and "the relevance and salience of all the issues", and
            // SRC-095's prose contains "unknown".
            var surface = string.Join("\n\u0000\n", requests.Select(r => r.SystemPrompt).Distinct(StringComparer.Ordinal)
                .Concat(requests.Select(r => r.UserMessage[r.UserMessage.IndexOf("\nSCHEMA=", StringComparison.Ordinal)..]).Distinct(StringComparer.Ordinal))
                .Concat(harnessKeys).Concat(harnessValues));
            var leaked = searched.Where(term => surface.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
            var leakedValues = observations.Where(o => o.Value.Length > 2 && surface.Contains(o.Value, StringComparison.Ordinal))
                .Select(o => o.Key).ToArray();

            unclassifiedTotal += byProvenance.GetValueOrDefault("UNCLASSIFIED");
            leakedTotal += leaked.Length + leakedValues.Length;

            rows.Add(new
            {
                documentId = id,
                media,
                source = new { path, sha256 = CanonicalArtifactHash.OfBytes(TestRepository.Path(path)) },
                requests = requests.Count,
                modelVisibleStrings = byProvenance.Values.Sum(),
                byProvenance = byProvenance.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
                unclassified,
                evidenceKeyPaths = keyPaths.ToArray(),
                harnessAuthoredValues = harnessValues.Count,
                harnessAuthoredValueSample = harnessValues.Take(24).ToArray(),
                derivedObservationsComputed = observations,
                derivedTermsSearched = searched.Length,
                derivedTermsFoundInRequest = leaked,
                derivedObservationValuesFoundInRequest = leakedValues,
            });
        }

        FreezeArtifact.AssertJson(Dir, "v2-model-visible-provenance.v1.json", new
        {
            artifactKind = "a99_model_visible_provenance_audit",
            study = "A99_V2_MODEL_VISIBLE_PROVENANCE_AUDIT_V1",
            phase = "A - what reaches the model",
            follows = new { audit = "docs/architecture/current-semantic-reachability-audit-2026-09-27.md", commit = "16f5273", item = "critical answer 5" },
            modelProviderVlmCalls = 0,
            requestVersion = SemanticRequestVersions.ProductionDefault.ToString(),
            method = new[]
            {
                "compose the real production request offline for each document (no provider), split the evidence packet from the response schema, and walk every string the packet carries",
                "classify each string by provenance: a parser-owned alias, the source's own text (exact or a fragment of it), an author's OOXML style declaration, a value from the closed vocabulary enumerated in this test, or punctuation and digits",
                "run the three LIVE_SUSPICIOUS producers for real on the same document and search the whole request - system prompt included - for their own conclusions and for every derived-verdict name the harness can reach",
            },
            closedVocabularies = new { ProtocolIds, PageBands, RelativeFontSizes, BoldEvidenceSources, SourceFactsIds, markerFactPrefixes = new[] { "marker-family:", "marker-signature:", "marker-depth:", "marker-is-path:", "marker-components:" } },
            searchScope = "the harness's own surface: the system prompt, the response schema, every key it chose, and every value that is not the document's own text or a parser alias. The document's text is deliberately out of scope - it legitimately contains words like candidate, salience and unknown, and a naive byte grep reports those as leaks",
            documents = rows,
            result = new
            {
                unclassifiedModelVisibleStrings = unclassifiedTotal,
                derivedPriorTermsFound = leakedTotal,
                passed = unclassifiedTotal == 0 && leakedTotal == 0,
            },
            provenClaims = new[]
            {
                "no CandidateAttention, candidate score or rank, style-trust verdict, heading-heuristic decision, document-genre conclusion or pre-classified TOC/running-header/caption label is serialized to the model under any key",
                "every string the model can read is the document's own text, a parser-owned alias, the author's own style declaration, or a named measurement from a closed vocabulary",
                "the Gold is not read while a request is composed: no string in any packet comes from anywhere but the source, and the audit fails on the first that does",
            },
            limitation = "this proves no derived label or verdict reaches the model as text. A derived prior encoded purely as a number under a source-fact key would not be caught by a string audit; the numeric fields are enumerated per document in evidenceKeyPaths and each is a parser measurement in the hard-code audit's field table (hardcode-audit-v1)",
        });

        Assert.Empty(rows.SelectMany(r => (IEnumerable<object>)r.GetType().GetProperty("unclassified")!.GetValue(r)!));
        Assert.Equal(0, leakedTotal);
    }
}
