using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.Core.Models;

public enum CanonicalSemanticModality
{
    Text,
    Hybrid,
    VisualOnly,
}

public sealed record CanonicalSemanticPageEvidence(
    string PageId,
    bool HasUsableText,
    int EmbeddedImageCount,
    string EvidenceSource);

public sealed record CanonicalSemanticPageRoute(
    string PageId,
    CanonicalSemanticModality Modality,
    bool UseTextEvidence,
    bool UseVisualRecovery);

public sealed record CanonicalSemanticModalityProfile(
    CanonicalSemanticModality DocumentModality,
    IReadOnlyList<CanonicalSemanticPageRoute> Pages);

/// <summary>Profiles each page/region before occurrences are finalized; a document-wide label is not enough.</summary>
public static class CanonicalSemanticModalityProfiler
{
    public static CanonicalSemanticModalityProfile Profile(IReadOnlyList<CanonicalSemanticPageEvidence> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        var routes = pages.Select(page =>
        {
            var visual = page.EmbeddedImageCount > 0 && !page.HasUsableText;
            var hybrid = page.EmbeddedImageCount > 0 && page.HasUsableText;
            return new CanonicalSemanticPageRoute(
                page.PageId,
                visual ? CanonicalSemanticModality.VisualOnly : hybrid ? CanonicalSemanticModality.Hybrid : CanonicalSemanticModality.Text,
                page.HasUsableText,
                visual || hybrid);
        }).ToArray();
        var documentMode = routes.All(route => route.Modality == CanonicalSemanticModality.VisualOnly)
            ? CanonicalSemanticModality.VisualOnly
            : routes.Any(route => route.UseVisualRecovery)
                ? CanonicalSemanticModality.Hybrid
                : CanonicalSemanticModality.Text;
        return new(documentMode, routes);
    }
}

/// <summary>Short runtime facade used by the control plane before occurrence finalization.</summary>
public static class ModalityProfiler
{
    public static CanonicalSemanticModalityProfile Profile(IReadOnlyList<CanonicalSemanticPageEvidence> pages) =>
        CanonicalSemanticModalityProfiler.Profile(pages);
}

public sealed record CanonicalSemanticVisualBoundingBox(double Left, double Top, double Width, double Height)
{
    public bool IsValid => Left >= 0 && Top >= 0 && Width > 0 && Height > 0;
}

/// <summary>Visual geometry is parser/render-owned evidence, never model-supplied coordinates.</summary>
public sealed record CanonicalSemanticVisualBlock(
    string PageId,
    int BlockOrdinal,
    string ImageSha256,
    CanonicalSemanticVisualBoundingBox BoundingBox,
    string Transcript);

/// <summary>Runtime-owned page image evidence. The bytes are transient request input; the
/// persisted identity is the page/image hash, never a model-supplied coordinate.</summary>
public sealed record CanonicalSemanticVisualPageEvidence(
    string PageId,
    string ImageSha256,
    byte[] ImageBytes,
    int Width,
    int Height,
    string MimeType = "image/png");

public sealed record CanonicalSemanticVisualOccurrence(
    string VisualAlias,
    string PageId,
    int BlockOrdinal,
    string ImageSha256,
    CanonicalSemanticVisualBoundingBox BoundingBox,
    string Transcript,
    string RegionSha256,
    string TranscriptSha256);

public static class CanonicalSemanticVisualRecovery
{
    public static IReadOnlyList<CanonicalSemanticVisualOccurrence> Recover(
        IReadOnlyList<CanonicalSemanticVisualBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.OrderBy(block => block.PageId, StringComparer.Ordinal).ThenBy(block => block.BlockOrdinal)
            .Select((block, index) =>
            {
                if (!block.BoundingBox.IsValid) throw new InvalidOperationException("VISUAL_GEOMETRY_INVALID");
                var transcriptHash = HashText(block.Transcript);
                var regionHash = HashText($"{block.ImageSha256}\n{block.BoundingBox}\n{block.Transcript}");
                return new CanonicalSemanticVisualOccurrence(
                    $"V{index + 1:0000}", block.PageId, block.BlockOrdinal, block.ImageSha256,
                    block.BoundingBox, block.Transcript, regionHash, transcriptHash);
            }).ToArray();
    }

    internal static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class VisualRecovery
{
    public static IReadOnlyList<CanonicalSemanticVisualOccurrence> Recover(IReadOnlyList<CanonicalSemanticVisualBlock> blocks) =>
        CanonicalSemanticVisualRecovery.Recover(blocks);
}

public sealed record CanonicalSemanticVisualProposal(
    string VisualAlias,
    bool IsHeading,
    string VerbatimTranscript,
    string? SemanticRole = null,
    string? StructuralType = null,
    string? Scope = null,
    IReadOnlyList<string>? VisualAliases = null,
    IReadOnlyList<string>? VerbatimTranscripts = null);

public sealed record CanonicalSemanticVisualBinding(
    string VisualAlias,
    string PageId,
    string ImageSha256,
    CanonicalSemanticVisualBoundingBox BoundingBox,
    string RecoveredTranscript,
    string RegionSha256,
    string TranscriptHash,
    string CoordinateSystem = "VISUAL_REGION");

public sealed record CanonicalSemanticVisualBoundHeading(
    CanonicalSemanticVisualBinding Binding,
    string SemanticRole,
    string StructuralType,
    string Scope,
    bool IsHeading = true)
{
    public IReadOnlyList<CanonicalSemanticVisualBinding> Bindings { get; init; } = [];
}

public static class CanonicalSemanticVisualBinder
{
    public static IReadOnlyList<CanonicalSemanticVisualBoundHeading> Bind(
        IReadOnlyList<CanonicalSemanticVisualProposal> proposals,
        IReadOnlyList<CanonicalSemanticVisualOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(occurrences);
        var byAlias = occurrences.ToDictionary(item => item.VisualAlias, StringComparer.Ordinal);
        var bound = new List<CanonicalSemanticVisualBoundHeading>();
        foreach (var proposal in proposals.Where(item => item.IsHeading))
        {
            var names = proposal.VisualAliases is { Count: > 0 } ? proposal.VisualAliases : [proposal.VisualAlias];
            var transcripts = proposal.VerbatimTranscripts is { Count: > 0 } ? proposal.VerbatimTranscripts : [proposal.VerbatimTranscript];
            if (names.Count != transcripts.Count) continue;
            var bindings = new List<CanonicalSemanticVisualBinding>(names.Count);
            var valid = true;
            foreach (var (name, transcript) in names.Zip(transcripts))
            {
                if (!byAlias.TryGetValue(name, out var occurrence) ||
                    !string.Equals(transcript, occurrence.Transcript, StringComparison.Ordinal))
                {
                    valid = false;
                    break;
                }
                bindings.Add(new CanonicalSemanticVisualBinding(occurrence.VisualAlias, occurrence.PageId, occurrence.ImageSha256,
                    occurrence.BoundingBox, occurrence.Transcript, occurrence.RegionSha256, occurrence.TranscriptSha256));
            }
            if (!valid || bindings.Count == 0) continue;
            bound.Add(new(bindings[0], proposal.SemanticRole ?? "OTHER_STRUCTURAL_LABEL",
                proposal.StructuralType ?? "Heading", proposal.Scope ?? "document_body") { Bindings = bindings });
        }
        return bound;
    }
}

/// <summary>Explicit union boundary: text uses the existing UTF-16 binder; visual uses region identity.</summary>
public static class ModalityAwareExactBinder
{
    public static IReadOnlyList<CanonicalSemanticBoundHeading> BindText(
        IReadOnlyList<CanonicalSemanticProposal> proposals,
        IReadOnlyList<SemanticSourceAlias> aliases,
        out IReadOnlyList<CanonicalSemanticBindingObservation> observations) =>
        CanonicalSemanticExactBinder.Bind(proposals, aliases, out observations);

    public static IReadOnlyList<CanonicalSemanticVisualBoundHeading> BindVisual(
        IReadOnlyList<CanonicalSemanticVisualProposal> proposals,
        IReadOnlyList<CanonicalSemanticVisualOccurrence> occurrences) =>
        CanonicalSemanticVisualBinder.Bind(proposals, occurrences);
}

public static class CanonicalSemanticVisualBindingValidator
{
    public static bool IsValid(CanonicalSemanticVisualBoundHeading heading,
        IReadOnlyList<CanonicalSemanticVisualOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(heading);
        ArgumentNullException.ThrowIfNull(occurrences);
        var bindings = heading.Bindings.Count == 0 ? [heading.Binding] : heading.Bindings;
        return bindings.All(binding =>
        {
            var expected = occurrences.SingleOrDefault(item => item.VisualAlias == binding.VisualAlias);
            return expected is not null &&
                string.Equals(expected.ImageSha256, binding.ImageSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(expected.RegionSha256, binding.RegionSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(expected.TranscriptSha256, binding.TranscriptHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(expected.Transcript, binding.RecoveredTranscript, StringComparison.Ordinal) &&
                expected.BoundingBox == binding.BoundingBox;
        });
    }
}

public sealed record CanonicalSemanticTextEvidenceBinding(string SourceId, int Start, int End, string Text);

public sealed record CanonicalSemanticUnifiedOccurrence(
    string UnifiedId,
    string Text,
    IReadOnlyList<CanonicalSemanticTextEvidenceBinding> TextEvidence,
    IReadOnlyList<CanonicalSemanticVisualBinding> VisualEvidence);

/// <summary>Reconciles evidence for one physical heading without letting one modality duplicate it.</summary>
public static class CanonicalSemanticCrossModalReconciler
{
    public static IReadOnlyList<CanonicalSemanticUnifiedOccurrence> Reconcile(
        IReadOnlyList<CanonicalSemanticTextEvidenceBinding> text,
        IReadOnlyList<CanonicalSemanticVisualBinding> visual)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(visual);
        var groups = new Dictionary<string, (List<CanonicalSemanticTextEvidenceBinding> Text, List<CanonicalSemanticVisualBinding> Visual)>(StringComparer.Ordinal);
        foreach (var item in text)
        {
            var key = $"text:{item.Text}";
            if (!groups.TryGetValue(key, out var group)) group = ([], []);
            group.Text.Add(item);
            groups[key] = group;
        }
        foreach (var item in visual)
        {
            var key = $"text:{item.RecoveredTranscript}";
            if (!groups.TryGetValue(key, out var group)) group = ([], []);
            group.Visual.Add(item);
            groups[key] = group;
        }
        return groups.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select((item, index) => new CanonicalSemanticUnifiedOccurrence(
                $"U{index + 1:0000}", item.Value.Text.FirstOrDefault()?.Text ?? item.Value.Visual[0].RecoveredTranscript,
                item.Value.Text, item.Value.Visual)).ToArray();
    }
}

public sealed record CanonicalSemanticDocxMediaEvidence(
    string MediaPath,
    string ImageSha256,
    IReadOnlyList<string> RelationshipFiles);

/// <summary>Offline OOXML media inventory preserves the image/hash lineage needed by visual recovery.</summary>
public static class CanonicalSemanticDocxMediaInventory
{
    public static IReadOnlyList<CanonicalSemanticDocxMediaEvidence> Inspect(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var media = archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal).ToArray();
        var relationships = archive.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).ToArray();
        return media.Select(entry =>
        {
            using var stream = entry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var relFiles = relationships.Where(rel =>
            {
                using var reader = new StreamReader(rel.Open());
                return reader.ReadToEnd().Contains(entry.FullName[(entry.FullName.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase);
            }).Select(rel => rel.FullName).OrderBy(item => item, StringComparer.Ordinal).ToArray();
            return new CanonicalSemanticDocxMediaEvidence(entry.FullName, hash, relFiles);
        }).ToArray();
    }
}
