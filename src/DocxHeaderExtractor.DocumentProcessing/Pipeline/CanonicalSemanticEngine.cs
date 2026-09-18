using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Authority;
using DocxHeaderExtractor.DocumentProcessing.Inference;

namespace DocxHeaderExtractor.DocumentProcessing.Pipeline;

/// <summary>
/// The semantic stage, shared by every source format.
/// <para>
/// Nothing here knows whether the occurrences came from OOXML paragraphs or PDF text blocks. That
/// is the point: the prompt, the segmentation, the contract, the binder, the hierarchy resolver and
/// the placement pass must be identical across formats, or the two lanes will quietly disagree
/// about level derivation and parent wiring and the difference will surface only as an
/// unexplainable metric gap between a DOCX and its own PDF.
/// </para>
/// </summary>
internal static class CanonicalSemanticEngine
{
    internal const string SystemPrompt = """
        You are the primary semantic reasoning stage of the A99 canonical document pipeline.
        Decide semantic meaning only for the supplied parser-owned source aliases. Return strict
        JSON matching the supplied schema. sourceAlias/sourceAliases and verbatimText are the only
        source references allowed. Do not return offsets, spans, pages, boxes, coordinates, or
        generated text. Formatting and numbering are evidence, never deterministic truth.

        sourceEvidence is in document order. Evaluate every alias in ownedSourceAliases and return
        one entry for each heading you find among them. Entries marked "owned": false are shown
        only so you can read the surrounding document; never return one of them as a heading.
        The "attention" flag is a hint, not the set of allowed headings: any owned occurrence may
        be a heading. Neighbouring entries are the local context; no context is repeated per item.

        REQUIRED for every heading: exactly one relationHints entry saying where it sits.
          "parent-node:<sourceAlias>" - it belongs under that earlier heading.
          "parent-node:ROOT"          - it is a top-level section of this document.
          "parent-node:NONE"          - it is a heading but holds NO position in the section tree.
        Use NONE for the document's own title and subtitle, running headers and footers, table and
        figure labels, form labels and signature labels. They are real headings and you should
        still report them, but they are not sections: they have no level and nothing is filed
        under them. Putting a title at ROOT instead pushes every real section one level deeper.
        Omit the entry entirely only when the evidence genuinely does not let you decide - that is
        an open question for a human, not the same as NONE, which is your decision.
        Never emit a numeric level: the harness derives level from the relations you return.

        OPTIONAL, and only in addition to the parent hint: when a heading is another occurrence of
        a section you already reported — the same section shown again, a continued table header, a
        running title — add "same-node:<key>", giving every occurrence of that one section the same
        short key. Identical wording is NOT enough on its own: two different forms may both be
        titled "CURRICULUM VITAE" and are then different sections, so give them different keys.
        Never let this hint displace the parent hint.
        """;

    internal static IReadOnlyList<string> MarkerFactsOf(PdfSourceFacts source)
    {
        if (source.Marker is not { } marker) return [];
        var facts = new List<string>
        {
            $"marker-family:{marker.Family}",
            $"marker-signature:{marker.Signature}",
            $"marker-depth:{marker.Depth}",
            $"marker-is-path:{(marker.IsPath ? "true" : "false")}",
        };
        if (!marker.Components.IsDefaultOrEmpty)
            facts.Add($"marker-components:{string.Join('.', marker.Components)}");
        return facts;
    }

    internal const string PlacementPrompt = """
        You are the structural stage of the A99 canonical document pipeline. The heading list below
        is already settled: do not add, remove, rename or re-judge any entry. Decide one thing only
        — where each heading in "toPlace" sits relative to the others.

        Answer with {"placements":[{"alias":"<alias>","parent":"<alias>|ROOT|NONE"}]}.
          "<alias>" - it belongs under that heading, which must appear earlier in the list.
          "ROOT"    - it is a top-level section of this document.
          "NONE"    - it is a heading but holds no position in the section tree: the document's own
                      title or subtitle, a meeting date or venue line, a running header, a table or
                      figure label, a form label, an annex label.
        Omit an alias entirely if the evidence still does not let you decide. Never return a level:
        the harness derives depth from the relations you give.
        """;

    /// <summary>
    /// Re-asks only about headings the first pass left unplaced, and only about placement. The
    /// reply may add a parent relation to those headings and nothing else: a heading that was
    /// already placed keeps its relation, and no heading is added or dropped here.
    /// </summary>
    internal static async Task<IReadOnlyList<CanonicalSemanticBoundHeading>> PlaceUnresolvedHeadingsAsync(
        IReadOnlyList<CanonicalSemanticBoundHeading> bound,
        IHeaderClassifier classifier,
        CancellationToken cancellationToken)
    {
        var derived = ModelRelationHierarchyResolver.DeriveHierarchyFromModelRelations(bound);
        var unplaced = derived
            .Where(item => item.Resolution == ModelRelationHierarchyResolver.Unresolved)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        if (unplaced.Count == 0) return bound;

        var ordered = bound.OrderBy(item => item.SourceOrdinal).ThenBy(item => item.Start).ToArray();
        var packet = JsonSerializer.Serialize(new
        {
            headings = ordered.Select(item => new { alias = item.Alias, text = item.Text }).ToArray(),
            toPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
                .Select(item => item.Alias).ToArray(),
        });

        string raw;
        try
        {
            raw = await classifier.BoundaryCutAsync(
                PlacementPrompt, packet, cancellationToken, expectedItemCount: unplaced.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Placement is an improvement pass. If it cannot run, the headings stay unresolved,
            // which is exactly what they already were.
            return bound;
        }

        Dictionary<string, string> parentByAlias;
        try
        {
            using var document = JsonDocument.Parse(raw);
            parentByAlias = document.RootElement.TryGetProperty("placements", out var placements)
                ? placements.EnumerateArray()
                    .Where(item => item.TryGetProperty("alias", out _) && item.TryGetProperty("parent", out _))
                    .GroupBy(item => item.GetProperty("alias").GetString() ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().GetProperty("parent").GetString() ?? string.Empty,
                        StringComparer.Ordinal)
                : [];
        }
        catch (JsonException)
        {
            return bound;
        }

        var aliasesToPlace = ordered.Where(item => unplaced.Contains(item.SourceId))
            .Select(item => item.Alias).ToHashSet(StringComparer.Ordinal);
        return bound.Select(item =>
        {
            // Only a heading that was actually unresolved may gain a relation here, so a second
            // pass can never overwrite what the semantic pass already decided.
            if (!aliasesToPlace.Contains(item.Alias)) return item;
            if (!parentByAlias.TryGetValue(item.Alias, out var parent) || string.IsNullOrWhiteSpace(parent))
                return item;
            return item with { RelationHints = [.. item.RelationHints, $"parent-node:{parent}"] };
        }).ToArray();
    }

    internal static PdfSemanticRole ParseSemanticRole(string? role) =>
        Enum.TryParse<PdfSemanticRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : PdfSemanticRole.SectionHeading;

    internal sealed class HeaderClassifierCanonicalTextModel(IHeaderClassifier classifier) : ICanonicalSemanticTextModel
    {
        public List<string> RawResponses { get; } = [];

        /// <summary>
        /// Request-shaped view of one owned occurrence. LocalBefore/LocalAfter are deliberately
        /// dropped: within a segment the neighbouring evidence entries already are that context,
        /// and repeating them was 45% of the payload. Per-run formatting spans are dropped too;
        /// style, numbering and marker facts carry the same signal far more compactly.
        /// </summary>
        private static object OwnedEvidence(CanonicalSemanticSourceEvidence item) => new
        {
            alias = item.SourceAlias,
            text = item.ExactSourceText,
            owned = true,
            scope = item.StructuralScope,
            tableDepth = item.TableDepth,
            inTableOfContents = item.InTableOfContents,
            style = item.StyleFacts,
            numbering = item.NumberingFacts,
            markers = item.MarkerFacts,
            attention = item.CandidateAttention.HeuristicMatch,
        };

        /// <summary>Owned occurrences evaluated per request. Keeps one document bounded.</summary>
        internal const int OwnedPerSegment = 120;

        /// <summary>Neighbouring occurrences a segment may read but never claim.</summary>
        internal const int VisibleMargin = 20;

        public async Task<CanonicalSemanticTextInferenceResult> InferAsync(
            CanonicalSemanticProductionInput input,
            SemanticContextPacket packedContext,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            var evidence = input.SourceEvidence ?? [];
            var proposals = new List<CanonicalSemanticProposal>();
            var issues = new List<SemanticContractIssue>();
            for (var start = 0; start < evidence.Count; start += OwnedPerSegment)
            {
                var owned = evidence.Skip(start).Take(OwnedPerSegment).ToArray();
                if (owned.Length == 0) break;
                var from = Math.Max(0, start - VisibleMargin);
                var to = Math.Min(evidence.Count, start + owned.Length + VisibleMargin);
                var visible = evidence.Skip(from).Take(to - from).ToArray();
                var ownedAliases = owned.Select(item => item.SourceAlias).ToHashSet(StringComparer.Ordinal);
                var packet = JsonSerializer.Serialize(new
                {
                    protocol = CanonicalSemanticContract.ProtocolVersion,
                    ownedSourceAliases = owned.Select(item => item.SourceAlias).ToArray(),
                    // Evidence is already in document order, so a neighbour IS the local context.
                    // Owned entries carry the decision facts; margin entries carry text only.
                    sourceEvidence = visible.Select(item => ownedAliases.Contains(item.SourceAlias)
                        ? OwnedEvidence(item)
                        : (object)new { alias = item.SourceAlias, text = item.ExactSourceText, owned = false })
                        .ToArray(),
                });
                var raw = await classifier.BoundaryCutAsync(
                    SystemPrompt,
                    packet + "\nSCHEMA=" + JsonSerializer.Serialize(CanonicalSemanticContract.Schema()),
                    cancellationToken,
                    expectedItemCount: owned.Length);
                RawResponses.Add(raw);
                // A reply that is not JSON at all - truncated mid-object, wrapped in prose, empty -
                // costs this segment. It used to throw out of the segment loop and end the document,
                // so one bad reply among sixteen discarded the other fifteen with no record of why.
                JsonDocument? parsed = null;
                try
                {
                    parsed = JsonDocument.Parse(raw);
                }
                catch (JsonException error)
                {
                    issues.Add(new SemanticContractIssue("UNPARSEABLE_REPLY", null,
                        $"The reply for this segment was not valid JSON: {error.Message}"));
                }

                if (parsed is null) continue;
                using var document = parsed;
                var segmentIssues = CanonicalSemanticContractValidator.ValidateJson(document.RootElement);
                if (segmentIssues.Count > 0)
                {
                    issues.AddRange(segmentIssues);
                    continue;
                }
                // Ownership is enforced here as well as in the contract validator: a segment may
                // read its neighbours for context but may never claim an occurrence it does not own.
                // One malformed entry must cost that entry, not the document. The contract
                // validator rejects what it can describe; a reply missing a required field cannot
                // even be read, so it is dropped here and recorded as a contract issue.
                if (!document.RootElement.TryGetProperty("headings", out var headings) ||
                    headings.ValueKind != JsonValueKind.Array)
                {
                    issues.Add(new SemanticContractIssue("MISSING_HEADINGS_ARRAY", null,
                        "The reply carried no headings array."));
                    continue;
                }
                foreach (var element in headings.EnumerateArray())
                {
                    if (ParseProposal(element) is not { } proposal)
                    {
                        issues.Add(new SemanticContractIssue("UNREADABLE_PROPOSAL", null,
                            "A heading entry omitted a required field and was dropped."));
                        continue;
                    }
                    if (ownedAliases.Contains(proposal.SourceAlias)) proposals.Add(proposal);
                }
            }
            return new(proposals, new CanonicalSemanticInferenceTelemetry(classifier.ModelName))
            {
                ContractIssues = issues,
            };
        }

        /// <summary>
        /// Null when the entry cannot be read. Absent optional fields are fine; a field that is
        /// present with the wrong JSON type is not, and costs this entry alone.
        /// <para>
        /// Every read goes through a typed accessor rather than <c>GetString</c>/<c>GetInt32</c>
        /// directly. Those throw on type confusion, and the throw escaped this method, the entry
        /// loop and the segment loop, so a single reply with <c>"occurrence": "1"</c> or
        /// <c>"sourceAliases": "S0123"</c> ended the whole document with nothing recorded.
        /// </para>
        /// </summary>
        private static CanonicalSemanticProposal? ParseProposal(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (Text(element, "sourceAlias") is not { Length: > 0 } sourceAlias) return null;
            if (!element.TryGetProperty("isHeading", out var isHeadingValue) ||
                isHeadingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            if (!TryTextArray(element, "sourceAliases", out var sourceAliases)) return null;
            if (!TryTextArray(element, "relationHints", out var relationHints)) return null;
            if (!TryOrdinal(element, "occurrence", out var occurrence)) return null;
            if (!TryText(element, "verbatimText", out var verbatimText)) return null;
            if (!TryTextArray(element, "verbatimParts", out var verbatimParts)) return null;
            if (!TryText(element, "semanticRole", out var semanticRole)) return null;
            if (!TryText(element, "structuralType", out var structuralType)) return null;
            if (!TryText(element, "scope", out var scope)) return null;
            if (!TryText(element, "leftExactContext", out var left)) return null;
            if (!TryText(element, "rightExactContext", out var right)) return null;
            if (!TryText(element, "selectionMode", out var mode)) return null;

            return new CanonicalSemanticProposal(
                sourceAlias,
                isHeadingValue.GetBoolean(),
                verbatimText,
                verbatimParts,
                semanticRole,
                structuralType,
                scope,
                relationHints,
                sourceAliases,
                occurrence,
                left,
                right,
                mode);
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>False only when the field is present and is not a string or null.</summary>
        private static bool TryText(JsonElement element, string name, out string? text)
        {
            text = null;
            if (!element.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String) return false;
            text = value.GetString();
            return true;
        }

        /// <summary>False only when the field is present and is not an array of strings.</summary>
        private static bool TryTextArray(JsonElement element, string name, out string[]? items)
        {
            items = null;
            if (!element.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.Array) return false;
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                // A null or non-string element makes the list unreadable: silently substituting an
                // empty string would shift every later alias-to-part mapping by one.
                if (item.ValueKind != JsonValueKind.String) return false;
                result.Add(item.GetString()!);
            }
            items = [.. result];
            return true;
        }

        /// <summary>False only when the field is present and is not a 32-bit integer.</summary>
        private static bool TryOrdinal(JsonElement element, string name, out int? ordinal)
        {
            ordinal = null;
            if (!element.TryGetProperty(name, out var value)) return true;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return false;
            ordinal = number;
            return true;
        }
    }
}
