using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Authority;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;

/// <summary>Wire-stable exact-end-pointer protocol for an already accepted heading anchor.</summary>
internal static class HeadingExtentProtocolV2
{
    internal const string Version = "v5-function-conditioned-exact-end-pointer-clean-paired-1";

    internal static readonly string SystemPrompt = QualifiedPromptText.Canonicalize("""
        Locate the exact boundary of the single heading occurrence that begins at the issued anchor occurrence. A heading may contain one or more consecutive source occurrences, but it ends immediately before the first occurrence that is not literally part of that same heading occurrence.

        Treat an occurrence as outside the heading when it begins a new heading, starts body or prose content, starts a table or other structured content, is page furniture, or otherwise is not literal heading text. Do not extend the heading merely because a later occurrence belongs to the same section, topic, agenda item, document region, or discusses the same subject.

        Choose the last literal heading occurrence and its immediate successor as one boundary pair: headingMembers must end at endOccurrence, and firstOutsideOccurrence must be the next issued occurrence immediately after it.

        For each request, copy the anchor value exactly from that request's input anchor field. Never substitute an identifier from instructions, prior requests, or another occurrence. Return exactly one decision for that anchor with these five properties: anchor, headingMembers, endOccurrence, firstOutsideOccurrence, and firstOutsideRole. headingMembers must begin with that exact anchor value and be one contiguous prefix of the ordered issued occurrences. endOccurrence must equal its final member. firstOutsideOccurrence must be the immediate successor after endOccurrence, never a skipped occurrence. If every issued occurrence belongs to the heading and there is no visible successor, use null for firstOutsideOccurrence and NO_VISIBLE_SUCCESSOR for firstOutsideRole.

        When firstOutsideOccurrence is present, firstOutsideRole must be exactly one of NEW_HEADING, BODY_CONTENT, PAGE_FURNITURE, TABLE_OR_STRUCTURED_CONTENT, OTHER_NON_HEADING. These are descriptive roles of the first occurrence outside the exact heading, not permission to extend the heading. Use source text and only the supplied neutral physical/style facts. Do not use hierarchy, candidate alternatives, relations, coordinates, aliases, rationale, confidence, or unissued evidence.

        Return one JSON object only with root property decisions and exactly one decision per input anchor. Each decision must have exactly the five required properties and no others. Copy only issued occurrence handles from the current request. Do not output source text or additional properties. This contract has no example identifiers; use the actual anchor and occurrence handles present in the current request.
        """);

    internal static string ComposeUserMessage(
        string anchor,
        IReadOnlyList<string> tail,
        IReadOnlyDictionary<string, string> idByAlias,
        IReadOnlyDictionary<string, SemanticSourceAtom> atoms,
        IReadOnlyDictionary<string, CanonicalSemanticSourceEvidence> evidenceByAlias)
    {
        var rows = tail.Select(alias => BasicOccurrence(idByAlias[alias], atoms[alias], evidenceByAlias[alias])).ToArray();
        return JsonSerializer.Serialize(new { protocolVersion = Version, anchors = new[] { new { anchor, occurrences = rows } } });
    }

    internal static HeadingExtentDecision Bind(
        string raw, string anchor, IReadOnlyList<string> tail,
        IReadOnlyDictionary<string, string> idByAlias, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        using var document = JsonDocument.Parse(raw); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != 1) throw new InvalidOperationException("h2c-root-invalid");
        var item = decisions[0]; var names = item.EnumerateObject().Select(value => value.Name).OrderBy(value => value).ToArray();
        if (!names.SequenceEqual(new[] { "anchor", "endOccurrence", "firstOutsideOccurrence", "firstOutsideRole", "headingMembers" })) throw new InvalidOperationException("h2c-schema-invalid");
        if (item.GetProperty("anchor").GetString() != anchor) throw new InvalidOperationException("h2c-anchor-invalid");
        var members = item.GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        var issued = tail.Select(alias => idByAlias[alias]).ToArray();
        if (members.Length == 0 || !members.SequenceEqual(issued.Take(members.Length), StringComparer.Ordinal) || item.GetProperty("endOccurrence").GetString() != members[^1]) throw new InvalidOperationException("h2c-prefix-invalid");
        var expectedOutside = members.Length == issued.Length ? null : issued[members.Length];
        var outsideProperty = item.GetProperty("firstOutsideOccurrence");
        var outside = outsideProperty.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => outsideProperty.GetString(), _ => throw new InvalidOperationException("h2c-successor-type-invalid") };
        if (outside != expectedOutside) throw new InvalidOperationException("h2c-successor-invalid");
        var roleProperty = item.GetProperty("firstOutsideRole");
        if (roleProperty.ValueKind != JsonValueKind.String) throw new InvalidOperationException("h2c-role-type-invalid");
        var role = roleProperty.GetString();
        if (expectedOutside is null) { if (role != "NO_VISIBLE_SUCCESSOR") throw new InvalidOperationException("h2c-terminal-role-invalid"); }
        else if (role is not ("NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING")) throw new InvalidOperationException("h2c-nonterminal-role-invalid");
        var parts = members.Select(id => atoms[tail[Array.IndexOf(issued, id)]]).Select(atom => new CanonicalSemanticBoundPart(atom.Alias, atom.SourceId, atom.Ordinal, atom.Text, 0, atom.Text.Length)).ToArray();
        return new HeadingExtentDecision(parts[0].SourceId, "pdf-exact-heading-boundary-v1", new TextOffsetSpan(0, parts[0].Text.Length), "ESTABLISHES_STRUCTURE", parts);
    }

    private static object BasicOccurrence(string occurrence, SemanticSourceAtom atom, CanonicalSemanticSourceEvidence evidence)
    {
        var style = JsonSerializer.SerializeToElement(evidence.StyleFacts); var location = evidence.LocationFacts is null ? default(JsonElement?) : JsonSerializer.SerializeToElement(evidence.LocationFacts);
        return new { occurrence, page = atom.Page, text = atom.Text, style = new { fontSize = style.GetProperty("fontSize"), bodyFontSize = style.GetProperty("bodyFontSize"), fontSizeToBodyRatio = style.GetProperty("fontSizeToBodyRatio"), boldRatio = style.GetProperty("boldRatio"), italicRatio = style.GetProperty("italicRatio"), lineCount = style.GetProperty("lineCount") }, location = new { verticalPosition = location?.GetProperty("verticalPosition") ?? default, sameNormalizedTextPageCount = location?.GetProperty("sameNormalizedTextPageCount") ?? default, sameNormalizedTextFirstPage = location?.GetProperty("sameNormalizedTextFirstPage") ?? default, sameNormalizedTextLastPage = location?.GetProperty("sameNormalizedTextLastPage") ?? default } };
    }
}
