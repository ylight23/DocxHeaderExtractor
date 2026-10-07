using DocxHeaderExtractor.DocumentProcessing.Source;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The runtime identity of a DOCX source universe, under
/// <c>a99-docx-runtime-source-universe-v1</c>.
/// <para>
/// The DOCX counterpart of <c>PdfCanonicalSourceUniverseHash</c>, over the same rows: alias,
/// source id, ordinal, text and span, ordered by ordinal. It hashes what the runtime actually
/// produces - <c>OpenXmlDocumentSource.Read</c> into
/// <c>DocumentSourceCatalogBuilder.FromSourceDocument</c> into
/// <c>SemanticSourceAliasCatalog.FromCatalog</c> - rather than re-reading the document through a
/// second parser, and it covers every owned alias rather than only the ones a Gold happens to name.
/// </para>
/// <para>
/// Lifted out of the cross-document preflight test that first defined it. Leaving it private there
/// would have meant writing a second definition to gate DOC-0001, and two definitions of a
/// universe identity is the ambiguity this whole line of work exists to remove. The serialization
/// is unchanged, so every value already frozen against it still holds.
/// </para>
/// </summary>
internal static class DocxSourceUniverseHash
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Compute(string sourceHash, IReadOnlyList<SemanticSourceAlias> aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentNullException.ThrowIfNull(aliases);

        var rows = aliases
            .OrderBy(alias => alias.SourceOrdinal)
            .ThenBy(alias => alias.Alias, StringComparer.Ordinal)
            .Select(alias => new
            {
                sourceAlias = alias.Alias,
                sourceId = alias.SourceId,
                ordinal = alias.SourceOrdinal,
                text = alias.Text,
                sourceStart = alias.SourceSpan.Start,
                sourceEnd = alias.SourceSpan.End,
            })
            .ToArray();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = "a99-docx-runtime-source-universe-v1",
            sourceSha256 = sourceHash,
            rows,
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
