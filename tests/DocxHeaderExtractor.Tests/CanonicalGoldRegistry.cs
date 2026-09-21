using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

/// <summary>One authority as the registry lists it.</summary>
public sealed record CanonicalGoldEntry(
    string AuthorityId,
    string CanonicalGoldPath,
    string SourceSha256,
    int SemanticHeadingTotal,
    bool SemanticEvaluable,
    bool OccurrenceEvaluable,
    bool CharacterSpanEvaluable,
    bool VisualBindingEvaluable,
    string GoldSha256,
    string ApprovalAuthority,
    bool UserFinalApproval);

/// <summary>
/// The one place Gold is discovered, and the only one.
/// <para>
/// Before this there were four active roots - strict-gold-v4, the semantic vNext freezes, the
/// occurrence artifacts, and the DOC-0252 review pack - and which one a piece of evaluation code
/// happened to read decided what it believed the truth was. Two tasks could disagree about how many
/// headings a document has and both be reading a real file. DOC-0205 is 72 in the semantic authority
/// and 71 in an older occurrence artifact; DOC-0258 is 37 against an older 24; DOC-0264 is 159
/// against an older 158. Picking a root was picking an answer.
/// </para>
/// <para>
/// Resolution fails closed. An authority that is not registered, a file that is missing, and a file
/// whose content no longer matches its recorded hash are all errors - there is deliberately no
/// fallback to a legacy root, because a fallback is how the older answer comes back.
/// </para>
/// <para>
/// One root does not mean one coordinate system. A registered authority says what may be scored
/// against it: a scanned source can carry semantic truth with no occurrence bindings at all, and
/// the capability flags are what an evaluator checks before running rather than something it
/// assumes.
/// </para>
/// </summary>
public static class CanonicalGoldRegistry
{
    public const string Root = "eval/a99-closed-loop/gold-current";
    public const string RegistryRelativePath = Root + "/registry.v1.json";

    private static readonly Lazy<IReadOnlyList<CanonicalGoldEntry>> Loaded = new(Read);

    public static IReadOnlyList<CanonicalGoldEntry> Entries => Loaded.Value;

    /// <summary>The registry row, or an error naming the authority that is not registered.</summary>
    public static CanonicalGoldEntry Entry(string authorityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityId);
        return Entries.SingleOrDefault(entry =>
                   string.Equals(entry.AuthorityId, authorityId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"'{authorityId}' is not a canonical Gold authority. Registered: " +
                   string.Join(", ", Entries.Select(entry => entry.AuthorityId)) + ".");
    }

    /// <summary>
    /// The canonical Gold document, after checking that what is on disk is what the registry
    /// recorded. A mismatch is a corrupted authority, not something to read anyway.
    /// </summary>
    public static JsonDocument Resolve(string authorityId)
    {
        var entry = Entry(authorityId);
        var path = TestRepository.Path(entry.CanonicalGoldPath);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Canonical Gold for {authorityId} is registered at {entry.CanonicalGoldPath} but missing.", path);

        var text = File.ReadAllText(path);
        var actual = CanonicalArtifactHash.OfText(text);
        if (!string.Equals(actual, entry.GoldSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Canonical Gold for {authorityId} does not match its registered hash." +
                $" registry {entry.GoldSha256}, file {actual}.");

        return JsonDocument.Parse(text);
    }

    /// <summary>The semantic total a source is held to, from the registry rather than from a file scan.</summary>
    public static int SemanticHeadingTotal(string authorityId) => Entry(authorityId).SemanticHeadingTotal;

    /// <summary>
    /// Gate an evaluator on the capability it needs. Refusing here is the point: a source with no
    /// occurrence bindings is not a source that scores zero, it is one that cannot be scored on
    /// that axis at all, and reporting a zero would be an invented measurement.
    /// </summary>
    public static void RequireCapability(string authorityId, GoldCapability capability)
    {
        var entry = Entry(authorityId);
        var granted = capability switch
        {
            GoldCapability.Semantic => entry.SemanticEvaluable,
            GoldCapability.Occurrence => entry.OccurrenceEvaluable,
            GoldCapability.CharacterSpan => entry.CharacterSpanEvaluable,
            GoldCapability.VisualBinding => entry.VisualBindingEvaluable,
            _ => false,
        };

        if (!granted)
            throw new InvalidOperationException(
                $"{authorityId} is not {capability}-evaluable. Its canonical Gold does not carry that axis, " +
                "so a score on it would be a measurement nobody made.");
    }

    /// <summary>
    /// The occurrence Gold for an authority whose claims are alias-addressed, as the PDF scoring
    /// code consumes it.
    /// <para>
    /// The capability check happens first and throws, so a source with no bindings cannot quietly
    /// produce an empty Gold that scores every prediction as spurious. The coordinate system is
    /// checked too: DOC-0001 binds with UTF-16 spans and DOC-0252 by alias and selection mode, and
    /// reading one as the other would silently drop what the reviewer actually recorded.
    /// </para>
    /// </summary>
    public static PdfGoldDocument ResolveOccurrenceGold(string authorityId)
    {
        RequireCapability(authorityId, GoldCapability.Occurrence);
        var entry = Entry(authorityId);
        using var gold = Resolve(authorityId);
        var root = gold.RootElement;
        var occurrence = root.GetProperty("occurrence");

        var system = occurrence.GetProperty("bindingCoordinateSystem").GetString();
        if (!string.Equals(system, "SOURCE_ALIAS_PLUS_SELECTION_MODE", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{authorityId} binds as {system}; it cannot be read as alias-addressed occurrence Gold.");

        var headings = occurrence.GetProperty("claims").EnumerateArray()
            .Select(claim => JsonSerializer.Deserialize<PdfGoldHeading>(claim.GetRawText())!)
            .ToArray();

        var approval = root.GetProperty("approval");
        return new PdfGoldDocument(authorityId, entry.SourceSha256, headings)
        {
            SemanticHeadingTotal = entry.SemanticHeadingTotal,
            FinalAuthority = entry.ApprovalAuthority,
            // Carried through, not defaulted: a Gold that claims occurrence truth has to name the
            // review that produced it, and PdfGoldValidator refuses one that does not.
            SemanticHeadingTotalAuthority = new PdfGoldAuthorityRecord(
                entry.ApprovalAuthority, approval.GetProperty("approvedAt").GetString() ?? string.Empty),
            OccurrenceAuthority = new PdfGoldAuthorityRecord(
                entry.ApprovalAuthority, approval.GetProperty("approvedAt").GetString() ?? string.Empty)
            {
                SourceUniverseSha256 = occurrence.TryGetProperty("sourceUniverseSha256", out var universe)
                    ? universe.GetString()
                    : null,
            },
            Capabilities = new PdfGoldCapabilities
            {
                SemanticEvaluable = entry.SemanticEvaluable,
                OccurrenceEvaluable = entry.OccurrenceEvaluable,
            },
        };
    }

    private static IReadOnlyList<CanonicalGoldEntry> Read()
    {
        var path = TestRepository.Path(RegistryRelativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No canonical Gold registry at {RegistryRelativePath}.", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("authorities").EnumerateArray()
            .Select(item => new CanonicalGoldEntry(
                item.GetProperty("authorityId").GetString()!,
                item.GetProperty("canonicalGoldPath").GetString()!,
                item.GetProperty("sourceSha256").GetString()!,
                item.GetProperty("semanticHeadingTotal").GetInt32(),
                item.GetProperty("semanticEvaluable").GetBoolean(),
                item.GetProperty("occurrenceEvaluable").GetBoolean(),
                item.GetProperty("characterSpanEvaluable").GetBoolean(),
                item.GetProperty("visualBindingEvaluable").GetBoolean(),
                item.GetProperty("goldSha256").GetString()!,
                item.GetProperty("approvalAuthority").GetString()!,
                item.GetProperty("userFinalApproval").GetBoolean()))
            .ToArray();
    }
}

public enum GoldCapability
{
    Semantic,
    Occurrence,
    CharacterSpan,
    VisualBinding,
}
