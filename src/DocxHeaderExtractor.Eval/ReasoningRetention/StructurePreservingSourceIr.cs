using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Eval.ReasoningRetention;

/// <summary>
/// Evaluation-only source representation that preserves the parser's canonical UTF-16 offsets
/// while retaining the OOXML events which produced them. It is deliberately subordinate to the
/// existing SourceDocument contract: production extraction and the frozen source bytes do not
/// change.
/// </summary>
public sealed record StructurePreservingSourceIr
{
    public required string DocumentId { get; init; }
    public required string SourcePath { get; init; }
    public required IReadOnlyList<StructurePreservingOccurrence> Occurrences { get; init; }
    public required IReadOnlyList<LogicalSourceLine> Lines { get; init; }
    public required string CanonicalTextSha256 { get; init; }

    public string ReconstructCanonicalText() =>
        string.Join("\n", Occurrences.Select(item => item.CanonicalText));
}

public sealed record StructurePreservingOccurrence
{
    public required string SourceOccurrenceId { get; init; }
    public required string SourceId { get; init; }
    public required int SourceOrdinal { get; init; }
    public required string CanonicalText { get; init; }
    public required IReadOnlyList<SourceIrAtom> Atoms { get; init; }
    public required IReadOnlyList<LogicalSourceLine> Lines { get; init; }
}

public sealed record SourceIrAtom
{
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
    [JsonPropertyName("sourceOccurrenceId")] public required string SourceOccurrenceId { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("canonicalStart")] public required int CanonicalStart { get; init; }
    [JsonPropertyName("canonicalEnd")] public required int CanonicalEnd { get; init; }
    [JsonPropertyName("rawText")] public required string RawText { get; init; }
    [JsonPropertyName("canonicalText")] public required string CanonicalText { get; init; }
}

/// <summary>Stable address-only model alias. Source identity is retained only in the harness.</summary>
public sealed record LogicalSourceLine
{
    [JsonPropertyName("alias")] public required string Alias { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("canonicalStart")] public required int CanonicalStart { get; init; }
    [JsonPropertyName("canonicalEnd")] public required int CanonicalEnd { get; init; }
    [JsonPropertyName("breakAfter")] public required bool BreakAfter { get; init; }
    [JsonPropertyName("paragraphBoundaryAfter")] public required bool ParagraphBoundaryAfter { get; init; }
    [JsonIgnore] public required string SourceOccurrenceId { get; init; }
    [JsonIgnore] public required string SourceId { get; init; }
    [JsonIgnore] public required int SourceOrdinal { get; init; }
}

public sealed record StructurePreservingPacket
{
    [JsonPropertyName("lines")] public required IReadOnlyList<StructurePreservingPacketLine> Lines { get; init; }
}

public sealed record StructurePreservingPacketLine
{
    [JsonPropertyName("alias")] public required string Alias { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("breakAfter")] public required bool BreakAfter { get; init; }
    [JsonPropertyName("paragraphBoundaryAfter")] public required bool ParagraphBoundaryAfter { get; init; }
}

public sealed record StructurePreservingPacketResult
{
    public required StructurePreservingPacket Packet { get; init; }
    public required string SerializedJson { get; init; }
    public required IReadOnlyDictionary<string, LogicalSourceLine> LinesByAlias { get; init; }
    public required int SourceTextCharacters { get; init; }
    public required int PacketCharacters { get; init; }
}

public static class StructurePreservingSourceIrBuilder
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static StructurePreservingSourceIr Build(SourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!File.Exists(source.SourcePath)) throw new FileNotFoundException("source-docx-missing", source.SourcePath);

        using var archive = ZipFile.OpenRead(source.SourcePath);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("word-document-xml-missing");
        using var stream = entry.Open();
        var xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var xmlParagraphs = xml.Descendants(W + "p").ToArray();
        var sourceParagraphs = source.Paragraphs.OrderBy(item => item.SourceOrdinal).ToArray();
        if (xmlParagraphs.Length != sourceParagraphs.Length)
            throw new InvalidDataException($"SOURCE_IR_PARAGRAPH_COUNT_MISMATCH:{xmlParagraphs.Length}:{sourceParagraphs.Length}");

        var allLines = new List<LogicalSourceLine>();
        var occurrences = new List<StructurePreservingOccurrence>(sourceParagraphs.Length);
        var aliasOrdinal = 1;
        for (var index = 0; index < sourceParagraphs.Length; index++)
        {
            var paragraph = sourceParagraphs[index];
            var occurrenceId = $"{source.DocumentId}:{paragraph.SourceId}:{paragraph.SourceOrdinal}:{paragraph.Text.Length}";
            var built = BuildParagraph(xmlParagraphs[index], paragraph, occurrenceId);
            if (!string.Equals(built.CanonicalText, paragraph.Text, StringComparison.Ordinal))
                throw new InvalidDataException($"SOURCE_IR_CANONICAL_MISMATCH:{paragraph.SourceId}:{built.CanonicalText.Length}:{paragraph.Text.Length}");

            var localLines = new List<LogicalSourceLine>();
            var lineStart = 0;
            foreach (var lineBreak in built.LineBreaks)
            {
                var line = NewLine(aliasOrdinal++, occurrenceId, paragraph, built.CanonicalText, lineStart, lineBreak.Before,
                    breakAfter: true, paragraphBoundaryAfter: false);
                localLines.Add(line);
                allLines.Add(line);
                lineStart = Math.Min(built.CanonicalText.Length, lineBreak.After);
            }
            var finalLine = NewLine(aliasOrdinal++, occurrenceId, paragraph, built.CanonicalText, lineStart,
                built.CanonicalText.Length, breakAfter: false, paragraphBoundaryAfter: true);
            localLines.Add(finalLine);
            allLines.Add(finalLine);
            occurrences.Add(new StructurePreservingOccurrence
            {
                SourceOccurrenceId = occurrenceId,
                SourceId = paragraph.SourceId,
                SourceOrdinal = paragraph.SourceOrdinal,
                CanonicalText = built.CanonicalText,
                Atoms = built.Atoms,
                Lines = localLines,
            });
        }

        var canonical = string.Join("\n", sourceParagraphs.Select(item => item.Text));
        return new StructurePreservingSourceIr
        {
            DocumentId = source.DocumentId,
            SourcePath = source.SourcePath,
            Occurrences = occurrences,
            Lines = allLines,
            CanonicalTextSha256 = Sha256(canonical),
        };
    }

    public static StructurePreservingPacketResult BuildPacket(StructurePreservingSourceIr ir)
    {
        ArgumentNullException.ThrowIfNull(ir);
        var lines = ir.Lines.Select(line => new StructurePreservingPacketLine
        {
            Alias = line.Alias,
            Text = line.Text,
            BreakAfter = line.BreakAfter,
            ParagraphBoundaryAfter = line.ParagraphBoundaryAfter,
        }).ToArray();
        var packet = new StructurePreservingPacket { Lines = lines };
        var serialized = JsonSerializer.Serialize(packet, JsonOptions);
        return new StructurePreservingPacketResult
        {
            Packet = packet,
            SerializedJson = serialized,
            LinesByAlias = ir.Lines.ToDictionary(line => line.Alias, StringComparer.Ordinal),
            SourceTextCharacters = ir.Occurrences.Sum(item => item.CanonicalText.Length),
            PacketCharacters = serialized.Length,
        };
    }

    private static LogicalSourceLine NewLine(int aliasOrdinal, string occurrenceId, SourceParagraph paragraph,
        string canonical, int start, int end, bool breakAfter, bool paragraphBoundaryAfter) => new()
    {
        Alias = $"L{aliasOrdinal:000000}", Text = canonical[start..end], CanonicalStart = start,
        CanonicalEnd = end, BreakAfter = breakAfter, ParagraphBoundaryAfter = paragraphBoundaryAfter,
        SourceOccurrenceId = occurrenceId, SourceId = paragraph.SourceId, SourceOrdinal = paragraph.SourceOrdinal,
    };

    private static BuiltParagraph BuildParagraph(XElement paragraph, SourceParagraph source, string occurrenceId)
    {
        var canonical = new StringBuilder();
        var breaks = new List<(int Before, int After)>();
        var atoms = new List<PendingAtom>();
        foreach (var run in paragraph.Descendants(W + "r"))
        {
            if (run.Ancestors(W + "txbxContent").Any() || run.Ancestors(W + "del").Any()) continue;
            foreach (var element in run.Descendants())
            {
                if (element.Ancestors(W + "txbxContent").Any() || element.Ancestors(W + "del").Any()) continue;
                var kind = element.Name == W + "t" ? "w:t" : element.Name == W + "br" ? "w:br" :
                    element.Name == W + "tab" ? "w:tab" : element.Name == W + "noBreakHyphen" ? "w:noBreakHyphen" : null;
                if (kind is null) continue;
                var raw = kind == "w:t" ? element.Value : kind switch
                {
                    "w:br" => "",
                    "w:tab" => "\t",
                    "w:noBreakHyphen" => "-",
                    _ => "",
                };
                var start = canonical.Length;
                var emitted = kind == "w:br" ? " " : raw;
                foreach (var character in emitted)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        if (canonical.Length == 0 || canonical[^1] == ' ') continue;
                        canonical.Append(' ');
                    }
                    else canonical.Append(character);
                }
                if (kind == "w:br") breaks.Add((start, canonical.Length));
                atoms.Add(new PendingAtom(kind, raw, start, canonical.Length));
            }
        }
        while (canonical.Length > 0 && canonical[^1] == ' ') canonical.Length--;
        var finalText = canonical.ToString();
        var finalized = atoms.Select(atom => new SourceIrAtom
        {
            SourceId = source.SourceId, SourceOccurrenceId = occurrenceId, Kind = atom.Kind,
            CanonicalStart = Math.Min(atom.Start, finalText.Length),
            CanonicalEnd = Math.Min(atom.End, finalText.Length), RawText = atom.RawText,
            CanonicalText = finalText[Math.Min(atom.Start, finalText.Length)..Math.Min(atom.End, finalText.Length)],
        }).ToArray();
        return new BuiltParagraph(finalText, breaks.Where(item => item.Before <= finalText.Length).Distinct().ToArray(), finalized);
    }

    private sealed record PendingAtom(string Kind, string RawText, int Start, int End);
    private sealed record BuiltParagraph(string CanonicalText, IReadOnlyList<(int Before, int After)> LineBreaks, IReadOnlyList<SourceIrAtom> Atoms);

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class StructurePreservingLineBinder
{
    public static IReadOnlyList<StructurePreservingBoundHeading> Bind(
        IReadOnlyList<StructurePreservingHeadingAddress> headings,
        StructurePreservingPacketResult packet)
    {
        ArgumentNullException.ThrowIfNull(headings);
        ArgumentNullException.ThrowIfNull(packet);
        var result = new List<StructurePreservingBoundHeading>();
        foreach (var heading in headings)
        {
            if (!CeilingSemanticRole.IsAllowed(heading.Role) ||
                !packet.LinesByAlias.TryGetValue(heading.StartLineAlias, out var startLine) ||
                !packet.LinesByAlias.TryGetValue(heading.EndLineAlias, out var endLine) ||
                !string.Equals(startLine.SourceOccurrenceId, endLine.SourceOccurrenceId, StringComparison.Ordinal) ||
                heading.StartOffset < 0 || heading.StartOffset >= startLine.Text.Length ||
                heading.EndOffset <= 0 || heading.EndOffset > endLine.Text.Length ||
                startLine.CanonicalStart + heading.StartOffset >= endLine.CanonicalStart + heading.EndOffset)
                continue;

            var start = startLine.CanonicalStart + heading.StartOffset;
            var end = endLine.CanonicalStart + heading.EndOffset;
            result.Add(new StructurePreservingBoundHeading(startLine.SourceId, startLine.SourceOccurrenceId,
                start, end, heading.Role));
        }
        return result;
    }
}

public sealed record StructurePreservingHeadingAddress(
    [property: JsonPropertyName("startLineAlias")] string StartLineAlias,
    [property: JsonPropertyName("startOffset")] int StartOffset,
    [property: JsonPropertyName("endLineAlias")] string EndLineAlias,
    [property: JsonPropertyName("endOffset")] int EndOffset,
    [property: JsonPropertyName("role")] string Role);

public sealed record StructurePreservingBoundHeading(
    string SourceId, string SourceOccurrenceId, int Start, int End, string Role);

public sealed record StructurePreservingSemanticResponse(IReadOnlyList<StructurePreservingHeadingAddress> Headings);

public static class StructurePreservingSemanticResponseParser
{
    public static StructurePreservingSemanticResponse Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new FormatException("structure-ir-response-empty");
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < start) throw new FormatException("structure-ir-response-json-incomplete");
        using var document = JsonDocument.Parse(raw[start..(end + 1)]);
        if (!document.RootElement.TryGetProperty("headings", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new FormatException("structure-ir-response-headings-missing");
        var headings = new List<StructurePreservingHeadingAddress>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("startLineAlias", out var sla) || sla.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("startOffset", out var so) || !so.TryGetInt32(out var startOffset) || startOffset < 0 ||
                !item.TryGetProperty("endLineAlias", out var ela) || ela.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("endOffset", out var eo) || !eo.TryGetInt32(out var endOffset) || endOffset < 1 ||
                !item.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                !CeilingSemanticRole.IsAllowed(role.GetString()))
                throw new FormatException("structure-ir-response-heading-schema-invalid");
            headings.Add(new StructurePreservingHeadingAddress(sla.GetString()!, startOffset, ela.GetString()!, endOffset, role.GetString()!));
        }
        return new StructurePreservingSemanticResponse(headings);
    }
}

public static class StructurePreservingSemanticPrompt
{
    public const string ProtocolVersion = "a99-structure-preserving-source-ir-v1";

    public const string System = """
You identify every structurally real document heading or structural label in the supplied
document text. Formatting, numbering, and layout are evidence, not rules. Include document
titles, parts, chapters, sections, articles, clauses, annex headings, and other structural
labels; navigation, TOC, captions, list items, running headers, body fragments, and other
non-task labels may be represented with their role and are filtered later. Return exact UTF-16
half-open spans. The supplied lines contain all document text in source order. Line aliases are
address-only and have no semantic meaning. Do not invent source identities, aliases, or text.
Do not return chain-of-thought or hierarchy. Return only the JSON schema response.

For a heading contained in one line, use that line alias for both endpoints. For a heading that
crosses a preserved line break, use the start and end line aliases and local offsets. Offsets are
UTF-16 offsets into the corresponding line text, end exclusive. Empty lines cannot be endpoints.
""";

    public static string BuildUser(string packetJson, string route) => $"TASK={ProtocolVersion}\nroute={route}\n{packetJson}";

    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            headings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        startLineAlias = new { type = "string", pattern = "^L[0-9]{6}$" },
                        startOffset = new { type = "integer", minimum = 0 },
                        endLineAlias = new { type = "string", pattern = "^L[0-9]{6}$" },
                        endOffset = new { type = "integer", minimum = 1 },
                        role = new { type = "string", @enum = CeilingSemanticRole.AllowedRoles },
                    },
                    required = new[] { "startLineAlias", "startOffset", "endLineAlias", "endOffset", "role" },
                },
            },
        },
        required = new[] { "headings" },
    };
}
