using System.Security.Cryptography;
using System.Text;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// What kind of accepted claim this is. Deliberately closed, and deliberately not a placement.
/// <para>
/// The relation field currently carries three unrelated meanings at once - a document label outside
/// the section tree, a structural claim whose parent is unresolved, and text that is no heading at
/// all - and the captured runs show all three in the same responses. Separating them starts here:
/// membership says what a claim is, and placement, when it exists, will say only where it sits.
/// </para>
/// </summary>
public enum Stage1MembershipDisposition
{
    /// <summary>The document's own accepted label. Accepted without being in the section tree.</summary>
    DocumentLabel,

    /// <summary>A claim that opens and names a structural unit.</summary>
    StructuralUnit,
}

/// <summary>One harness-resolved coordinate, in the order the claim occupies the source.</summary>
public sealed record Stage1SourceCoordinate(string Alias, int Start, int End);

/// <summary>
/// The authoritative identity of an accepted claim.
/// <para>
/// SHA-256 over a domain separator, the document's source hash and the ordered coordinate tuple.
/// Every field is length-prefixed, which is the part that actually matters: concatenating an alias
/// ending in a digit with an offset beginning with one would otherwise let two different tuples
/// serialize to the same bytes, and a wider hash would not have helped.
/// </para>
/// <para>
/// The short form exists for logs and is not an identity. Eight hex characters is 32 bits, where
/// collisions become likely well below the size of this corpus, so it is never accepted without
/// resolving the full value and comparing the tuple it came from.
/// </para>
/// </summary>
public sealed record Stage1AuthorityClaimId
{
    /// <summary>Changing this changes every id, which is the point of versioning it.</summary>
    public const string DomainSeparator = "a99-semantic-membership-claim-v1";

    private Stage1AuthorityClaimId(string value) => Value = value;

    /// <summary>The full 256-bit digest, hex encoded, prefixed <c>C:</c>.</summary>
    public string Value { get; }

    /// <summary>A short form for diagnostics. Never an identity on its own.</summary>
    public string Display => "C" + Value[2..10].ToUpperInvariant();

    public static Stage1AuthorityClaimId For(
        string documentSourceSha256, IReadOnlyList<Stage1SourceCoordinate> coordinates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentSourceSha256);
        ArgumentNullException.ThrowIfNull(coordinates);
        if (coordinates.Count == 0)
            throw new InvalidOperationException("STAGE1_CLAIM_REQUIRES_AT_LEAST_ONE_COORDINATE");

        var builder = new StringBuilder();
        Frame(builder, DomainSeparator);
        Frame(builder, documentSourceSha256);
        foreach (var coordinate in coordinates)
        {
            Frame(builder, coordinate.Alias);
            Frame(builder, coordinate.Start.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Frame(builder, coordinate.End.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new Stage1AuthorityClaimId("C:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant());
    }

    private static void Frame(StringBuilder target, string field) =>
        target.Append(field.Length).Append(':').Append(field).Append('\u001f');

    public override string ToString() => Value;
}

/// <summary>
/// An accepted claim, carrying only what membership and identity need.
/// <para>
/// No relation, no role, no model-chosen offsets or selection mode. Not because those are
/// unimportant, but because a membership result that can be moved by any of them is not a
/// membership result - which is exactly what made the last two experiments hard to read.
/// </para>
/// </summary>
public sealed record Stage1AcceptedClaim(
    Stage1AuthorityClaimId ClaimId,
    IReadOnlyList<Stage1SourceCoordinate> Coordinates,
    string DocumentSourceSha256,
    Stage1MembershipDisposition Disposition)
{
    /// <summary><c>alias:start-end</c> per coordinate, in source order - the binder's own form.</summary>
    public string CanonicalIdentity =>
        string.Join("|", Coordinates.Select(item => $"{item.Alias}:{item.Start}-{item.End}"));
}

/// <summary>
/// Turns bound structured-v2 claims into accepted Stage-1 claims, and nothing else.
/// <para>
/// Projection begins only after validation, decoding, canonicalization and deterministic binding
/// have finished, so its input is a harness-owned coordinate rather than anything the model wrote.
/// It accepts every bound claim and rejects nothing: a projection that dropped one would improve
/// false-positive counts by discarding evidence, and the architecture it justifies would be resting
/// on a policy change nobody authorized.
/// </para>
/// </summary>
public static class Stage1Projection
{
    /// <summary>Projects one bound claim. Returns null when the binding did not succeed.</summary>
    public static Stage1AcceptedClaim? Project(
        string documentSourceSha256,
        SemanticSourcePartsBinding binding,
        Stage1MembershipDisposition disposition = Stage1MembershipDisposition.StructuralUnit)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.IsBound) return null;

        var coordinates = binding.Parts
            .Select(part => new Stage1SourceCoordinate(part.Alias, part.Start, part.End)).ToArray();

        return new Stage1AcceptedClaim(
            Stage1AuthorityClaimId.For(documentSourceSha256, coordinates),
            coordinates,
            documentSourceSha256,
            disposition);
    }

    /// <summary>Projects a production bound heading.</summary>
    public static Stage1AcceptedClaim Project(
        string documentSourceSha256,
        CanonicalSemanticBoundHeading heading,
        Stage1MembershipDisposition disposition = Stage1MembershipDisposition.StructuralUnit)
    {
        ArgumentNullException.ThrowIfNull(heading);
        var coordinates = heading.Parts.Count > 0
            ? heading.Parts.Select(part => new Stage1SourceCoordinate(part.Alias, part.Start, part.End)).ToArray()
            : [new Stage1SourceCoordinate(heading.Alias, heading.Start, heading.End)];

        return new Stage1AcceptedClaim(
            Stage1AuthorityClaimId.For(documentSourceSha256, coordinates),
            coordinates,
            documentSourceSha256,
            disposition);
    }

    /// <summary>
    /// Collects an accepted set, refusing rather than merging when one id arrives with two
    /// different tuples. SHA-256 makes that improbable; it does not make checking it optional, and
    /// silently merging two claims is worse than stopping.
    /// </summary>
    public static IReadOnlyList<Stage1AcceptedClaim> Collect(IEnumerable<Stage1AcceptedClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var accepted = new Dictionary<string, Stage1AcceptedClaim>(StringComparer.Ordinal);

        foreach (var claim in claims)
        {
            if (!accepted.TryGetValue(claim.ClaimId.Value, out var existing))
            {
                accepted[claim.ClaimId.Value] = claim;
                continue;
            }
            if (!string.Equals(existing.CanonicalIdentity, claim.CanonicalIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException("AUTHORITY_CLAIM_ID_COLLISION");
        }

        return [.. accepted.Values];
    }
}

/// <summary>How a Gold claim fared, kept apart from why.</summary>
public enum Stage1ProposalState
{
    /// <summary>The model never named it.</summary>
    NotProposed,

    /// <summary>It was named, and the harness would not accept the coordinates.</summary>
    ProposedButRefused,

    /// <summary>It resolved to exact harness-owned coordinates.</summary>
    Bound,
}

/// <summary>What membership scored, and which claims made up each number.</summary>
public sealed record Stage1MembershipScore(
    int TruePositive,
    int FalsePositive,
    int FalseNegative,
    IReadOnlyList<string> TruePositiveIdentities,
    IReadOnlyList<string> FalsePositiveIdentities,
    IReadOnlyList<string> FalseNegativeIdentities);

/// <summary>
/// Scores membership from accepted claims and Gold, and from nothing else.
/// <para>
/// It cannot consume a relation, a role or a placement graph, because it is never given one. That
/// is the guarantee: a placement error can never be counted as a membership miss, and a membership
/// error can never be hidden by placement declining to place it.
/// </para>
/// </summary>
public static class Stage1MembershipEvaluator
{
    public static Stage1MembershipScore Score(
        IReadOnlyList<Stage1AcceptedClaim> accepted,
        IReadOnlySet<string> goldTargetIdentities,
        IReadOnlySet<string> goldAllIdentities)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(goldTargetIdentities);
        ArgumentNullException.ThrowIfNull(goldAllIdentities);

        var acceptedIdentities = accepted
            .Select(claim => claim.CanonicalIdentity).ToHashSet(StringComparer.Ordinal);

        var truePositives = goldTargetIdentities
            .Where(acceptedIdentities.Contains).Order(StringComparer.Ordinal).ToArray();

        // A claim outside Gold entirely is a false positive. Gold membership is the whole authority
        // here - not the target subset, or a claim approved elsewhere in the document would be
        // counted as an error for being outside the packs under test.
        var falsePositives = acceptedIdentities
            .Where(identity => !goldAllIdentities.Contains(identity))
            .Order(StringComparer.Ordinal).ToArray();

        var falseNegatives = goldTargetIdentities
            .Where(identity => !acceptedIdentities.Contains(identity))
            .Order(StringComparer.Ordinal).ToArray();

        return new Stage1MembershipScore(
            truePositives.Length, falsePositives.Length, falseNegatives.Length,
            truePositives, falsePositives, falseNegatives);
    }

    /// <summary>
    /// The diagnostic state of one Gold claim. Separate from the score on purpose: a claim the
    /// model never named and one it named but grounded wrongly are different failures with
    /// different remedies, and summing them hides which one happened.
    /// </summary>
    public static Stage1ProposalState StateOf(
        string goldIdentity,
        IReadOnlySet<string> acceptedIdentities,
        IReadOnlySet<string> refusedAliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goldIdentity);
        ArgumentNullException.ThrowIfNull(acceptedIdentities);
        ArgumentNullException.ThrowIfNull(refusedAliases);

        if (acceptedIdentities.Contains(goldIdentity)) return Stage1ProposalState.Bound;

        var first = goldIdentity.Split('|')[0];
        var alias = first[..first.LastIndexOf(':')];
        return refusedAliases.Contains(alias)
            ? Stage1ProposalState.ProposedButRefused
            : Stage1ProposalState.NotProposed;
    }
}
