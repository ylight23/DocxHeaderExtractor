using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.DocumentProcessing.Semantics.HeadingAuthority.Protocols;

/// <summary>Wire-stable anchor-existence protocol. Domain names are intentionally independent of
/// its historical qualification label; prompt and provider-visible bytes remain unchanged.</summary>
internal static class HeadingAnchorProtocolV1
{
    internal const string Version = "v5-function-conditioned-anchor-existence-1";

    internal static readonly string SystemPrompt = QualifiedPromptText.Canonicalize("""
        Decide anchor existence only. Each issued primary occurrence has an upstream ESTABLISHES_STRUCTURE eligibility signal, but that signal is not proof that a valid local structural heading extent begins at this primary.

        For every issued O#, return exactly one anchor: HAS_STRUCTURAL_EXTENT if at least one valid local structural heading extent begins at that primary; otherwise NO_STRUCTURAL_EXTENT. Do not choose or describe any extent. Do not infer an answer from context-only items.

        Return exactly one JSON object with this shape: {"decisions":[{"primary":"O27","anchor":"HAS_STRUCTURAL_EXTENT"},{"primary":"O28","anchor":"NO_STRUCTURAL_EXTENT"}]}. Each decision has exactly primary and anchor. Do not output source text, candidate IDs, coordinates, aliases, locators, relations, hierarchy, rationale, confidence, or extra properties.
        """);

    internal static string ComposeUserMessage(
        IReadOnlyList<SemanticSourceAtom> owned,
        IReadOnlyDictionary<string, string> idByAlias,
        IReadOnlyList<(string Id, SemanticSourceAtom Atom)> establishes)
    {
        var indexByAlias = owned.Select((atom, index) => (atom.Alias, index)).ToDictionary(value => value.Alias, value => value.index, StringComparer.Ordinal);
        var occurrences = establishes.Select(value =>
        {
            object? Neighbor(int index)
            {
                if (index < 0 || index >= owned.Count) return null;
                var adjacent = owned[index];
                return new { occurrence = idByAlias[adjacent.Alias], page = adjacent.Page, text = adjacent.Text, selectable = false };
            }

            var index = indexByAlias[value.Atom.Alias];
            return new { primary = value.Id, page = value.Atom.Page, text = value.Atom.Text,
                upstreamFunction = "ESTABLISHES_STRUCTURE", previous = Neighbor(index - 1), next = Neighbor(index + 1) };
        }).ToArray();
        return JsonSerializer.Serialize(new { protocolVersion = Version, occurrences });
    }

    internal static HashSet<string> Parse(string raw, IEnumerable<string> issued)
    {
        using var document = JsonDocument.Parse(raw); var root = document.RootElement; var expected = issued.ToHashSet(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("decisions", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != expected.Count) throw new InvalidOperationException("g2a-cardinality-invalid");
        var result = new HashSet<string>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 || !row.TryGetProperty("primary", out var primaryProperty) || primaryProperty.ValueKind != JsonValueKind.String || !row.TryGetProperty("anchor", out var anchorProperty) || anchorProperty.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("g2a-schema-invalid");
            var primary = primaryProperty.GetString()!; var anchor = anchorProperty.GetString()!;
            if (!expected.Contains(primary) || !seen.Add(primary) || (anchor is not "HAS_STRUCTURAL_EXTENT" and not "NO_STRUCTURAL_EXTENT")) throw new InvalidOperationException("g2a-ledger-invalid");
            if (anchor == "HAS_STRUCTURAL_EXTENT") result.Add(primary);
        }
        return result;
    }
}
