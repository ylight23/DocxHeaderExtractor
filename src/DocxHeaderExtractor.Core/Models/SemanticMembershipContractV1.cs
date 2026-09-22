using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// The Stage-1 contract: which source spans are accepted claims, and nothing else.
/// <para>
/// Every structured contract so far has asked one generation to decide membership, name a role and
/// place the claim in a tree at the same time. Two prompt interventions tried to change what it
/// accepts and neither moved it, while its placement field kept carrying meanings that belong to
/// membership. This contract removes the other two tasks from the request rather than instructing
/// around them: the model is not told that placement exists.
/// </para>
/// <para>
/// The source-grounding half is unchanged and deliberately so - the model names occurrences and
/// quotes words, the harness derives the mode and the offsets. That seam is the one part of the
/// pipeline that has been proven against every approved claim in the corpus, and Stage 1 reuses it
/// rather than restating it.
/// </para>
/// </summary>
public static class SemanticMembershipContractV1
{
    public const string ProtocolVersion = "a99-semantic-membership-v1";

    /// <summary>
    /// Schema shown to the model. There is no role, no relation, no selection mode and no offset -
    /// not as fields the model should leave blank, but as fields that do not exist, so no reply can
    /// express a placement and no placement can admit a claim.
    /// </summary>
    public static object Schema() => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            claims = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        membership = new
                        {
                            type = "string",
                            @enum = new[] { "DOCUMENT_LABEL", "STRUCTURAL_UNIT" },
                        },
                        sourceParts = new
                        {
                            type = "array",
                            minItems = 1,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    sourceAlias = new { type = "string", minLength = 1 },
                                    verbatimText = new { type = "string" },
                                    occurrence = new { type = "integer", minimum = 1 },
                                    leftExactContext = new { type = "string" },
                                    rightExactContext = new { type = "string" },
                                },
                                required = new[] { "sourceAlias" },
                            },
                        },
                    },
                    required = new[] { "sourceParts", "membership" },
                },
            },
        },
        required = new[] { "claims" },
    };

    /// <summary>Hashed exactly as the source-parts contracts hash theirs, so all three compare.</summary>
    public static string SchemaHash()
    {
        var json = JsonSerializer.Serialize(Schema(), new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(json.ReplaceLineEndings("\n"))));
    }
}

/// <summary>
/// What Stage 1 teaches the model about naming source. Identical in substance to the v2 clause,
/// because the source-grounding rule is not what this experiment changes.
/// </summary>
public static class SemanticMembershipV1PromptClause
{
    public const string Text = """

        Name each piece of a claim by the sourceAlias it occupies, in reading order.

        When a claim is the whole of an occurrence, give the alias alone. When it is only part of
        one - a label run into the text that follows it - also give verbatimText: that exact
        substring, copied character for character, with nothing normalized, repaired or shortened.
        Do not say how much of the occurrence you mean in any other way; the harness works that out
        from the words you quote.

        If the quoted text appears more than once inside that occurrence, say which one you mean by
        adding "occurrence" as its 1-based ordinal, or "leftExactContext"/"rightExactContext" copied
        exactly from the characters beside it. An unmarked duplicate is refused rather than guessed.

        A claim that wraps across occurrences is several parts, each quoted from its own; one that
        occupies part of a single occurrence is one part. Never return offsets, spans or positions.
        """;
}

/// <summary>One decoded Stage-1 claim, before the harness has resolved where it is.</summary>
public sealed record SemanticMembershipClaim(
    IReadOnlyList<SemanticSourcePart> SourceParts,
    Stage1MembershipDisposition Disposition);

/// <summary>What a Stage-1 reply yielded, and what it could not.</summary>
public sealed record SemanticMembershipDecodeResult(
    IReadOnlyList<SemanticMembershipClaim> Claims,
    IReadOnlyList<string> Failures);

/// <summary>What the harness accepted from a Stage-1 reply, and every refusal, by reason.</summary>
public sealed record SemanticMembershipAcceptance(
    IReadOnlyList<Stage1AcceptedClaim> Accepted,
    IReadOnlyList<SemanticMembershipRefusal> Refusals);

/// <summary>A claim the model named that the harness would not ground.</summary>
public sealed record SemanticMembershipRefusal(string Alias, string Reason);

/// <summary>
/// Reading a Stage-1 reply and turning it into accepted claims.
/// <para>
/// There is no second representation here: a decoded claim goes through the same canonicalizer and
/// the same binder the structured contract uses, and comes out as the
/// <see cref="Stage1AcceptedClaim"/> the offline seam already validates. The transport form is the
/// only thing new.
/// </para>
/// </summary>
public static class SemanticMembershipV1
{
    public static SemanticMembershipDecodeResult Decode(JsonElement response)
    {
        var claims = new List<SemanticMembershipClaim>();
        var failures = new List<string>();

        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("claims", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return new SemanticMembershipDecodeResult([], ["A reply must be an object carrying 'claims'."]);
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                failures.Add("A claim must be an object.");
                continue;
            }
            if (!entry.TryGetProperty("sourceParts", out var partsElement)
                || partsElement.ValueKind != JsonValueKind.Array || partsElement.GetArrayLength() == 0)
            {
                failures.Add("A claim must name at least one source part.");
                continue;
            }
            if (!entry.TryGetProperty("membership", out var membershipElement)
                || membershipElement.ValueKind != JsonValueKind.String
                || !TryParseDisposition(membershipElement.GetString(), out var disposition))
            {
                failures.Add("A claim must state membership as DOCUMENT_LABEL or STRUCTURAL_UNIT.");
                continue;
            }

            var parts = new List<SemanticSourcePart>();
            var malformed = false;
            foreach (var part in partsElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object
                    || !part.TryGetProperty("sourceAlias", out var alias)
                    || alias.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(alias.GetString()))
                {
                    failures.Add("A source part must name a sourceAlias.");
                    malformed = true;
                    break;
                }

                parts.Add(new SemanticSourcePart(
                    alias.GetString()!,
                    // The mode is the harness's, exactly as under v2. Nothing between here and the
                    // canonicalizer may hand this to the binder.
                    SemanticSourcePartCanonicalizer.PendingSelectionMode,
                    Text(part, "verbatimText"),
                    part.TryGetProperty("occurrence", out var occurrence)
                        && occurrence.ValueKind == JsonValueKind.Number ? occurrence.GetInt32() : null,
                    Text(part, "leftExactContext"),
                    Text(part, "rightExactContext")));
            }

            if (!malformed) claims.Add(new SemanticMembershipClaim(parts, disposition));
        }

        return new SemanticMembershipDecodeResult(claims, failures);

        static string? Text(JsonElement part, string name) =>
            part.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Grounds decoded claims against the source and returns the immutable accepted set. A claim
    /// the harness cannot ground is refused by name and never widened to the whole occurrence.
    /// </summary>
    public static SemanticMembershipAcceptance Accept(
        IReadOnlyList<SemanticSourceAtom> atoms,
        string documentSourceSha256,
        SemanticMembershipDecodeResult decoded,
        IReadOnlySet<string>? ownedAliases = null)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        ArgumentNullException.ThrowIfNull(decoded);

        var accepted = new List<Stage1AcceptedClaim>();
        var refusals = new List<SemanticMembershipRefusal>();

        foreach (var claim in decoded.Claims)
        {
            var alias = claim.SourceParts[0].SourceAlias;
            if (ownedAliases is not null && claim.SourceParts.Any(part => !ownedAliases.Contains(part.SourceAlias)))
            {
                refusals.Add(new SemanticMembershipRefusal(alias, "OutOfOwnedSegment"));
                continue;
            }

            var canonical = SemanticSourcePartCanonicalizer.Canonicalize(atoms, claim.SourceParts);
            if (!canonical.IsCanonical)
            {
                refusals.Add(new SemanticMembershipRefusal(alias, canonical.Status.ToString()));
                continue;
            }

            var bound = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal(canonical.Parts));
            if (!bound.IsBound)
            {
                refusals.Add(new SemanticMembershipRefusal(alias, bound.Status.ToString()));
                continue;
            }

            accepted.Add(Stage1Projection.Project(documentSourceSha256, bound, claim.Disposition)!);
        }

        return new SemanticMembershipAcceptance(Stage1Projection.Collect(accepted), refusals);
    }

    /// <summary>
    /// Checks a whole Stage-1 reply's shape. The envelope is "claims", not "headings": a reply read
    /// by the wrong contract's validator does not fail loudly, it comes back empty, and an empty
    /// measurement cannot be told apart from a model that accepted nothing.
    /// </summary>
    public static IReadOnlyList<SemanticContractIssue> ValidateJson(JsonElement payload)
    {
        var issues = new List<SemanticContractIssue>();
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("claims", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("MISSING_CLAIMS", null, "A membership reply must carry a 'claims' array."));
            return issues;
        }

        var ordinal = 0;
        foreach (var entry in array.EnumerateArray())
        {
            ordinal++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("MALFORMED_CLAIM", ordinal.ToString(), "A claim must be an object."));
                continue;
            }
            if (!entry.TryGetProperty("membership", out var membership)
                || membership.ValueKind != JsonValueKind.String
                || !TryParseDisposition(membership.GetString(), out _))
            {
                issues.Add(new("INVALID_MEMBERSHIP", ordinal.ToString(),
                    "membership must be DOCUMENT_LABEL or STRUCTURAL_UNIT."));
            }
            if (!entry.TryGetProperty("sourceParts", out var parts)
                || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
            {
                issues.Add(new("MISSING_SOURCE_PARTS", ordinal.ToString(),
                    "A claim must name at least one source part."));
                continue;
            }

            // Fields the schema does not offer. Seeing one means the reply was written against a
            // different contract, which is worth saying rather than silently ignoring.
            foreach (var forbidden in new[] { "relationHints", "semanticRole", "parentClaimId", "selectionMode" })
            {
                if (entry.TryGetProperty(forbidden, out _))
                    issues.Add(new("FIELD_NOT_IN_CONTRACT", forbidden,
                        "Stage 1 does not ask for this, and will not read it."));
            }
        }

        return issues;
    }

    /// <summary>
    /// Reads one entry of the claims array into proposals, for the generic contract seam. The
    /// disposition has nowhere to live in a proposal, so the Stage-1 path uses
    /// <see cref="Decode(JsonElement)"/> and <see cref="Accept"/> instead; this exists so schema,
    /// validator, decoder and binder remain one agreement rather than three and a gap.
    /// </summary>
    public static SemanticProposalDecodeResult DecodeEntry(JsonElement claim)
    {
        using var wrapper = JsonDocument.Parse(
            JsonSerializer.Serialize(new { claims = new[] { JsonSerializer.Deserialize<JsonElement>(claim.GetRawText()) } }));
        var decoded = Decode(wrapper.RootElement);
        var failures = decoded.Failures
            .Select(detail => new SemanticProposalDecodeFailure("MEMBERSHIP_CLAIM_UNREADABLE", detail))
            .ToArray();
        if (decoded.Claims.Count == 0)
            return new SemanticProposalDecodeResult([], failures);

        var parts = decoded.Claims[0].SourceParts;
        return new SemanticProposalDecodeResult(
            [new CanonicalSemanticProposal(
                parts[0].SourceAlias,
                IsHeading: true,
                VerbatimText: null,
                VerbatimParts: null,
                SemanticRole: null,
                StructuralType: null,
                Scope: null,
                RelationHints: null,
                SourceAliases: parts.Select(part => part.SourceAlias).ToArray(),
                Occurrence: null,
                LeftExactContext: null,
                RightExactContext: null,
                SelectionMode: null,
                SourceParts: parts)],
            failures);
    }

    /// <summary>
    /// The same canonicalizing binder v2 uses, under this contract's name. Reusing it is the point:
    /// a second implementation of where a claim is would be a second answer to that question.
    /// </summary>
    public static readonly SemanticCoordinateBinding Binding =
        SemanticSourcePartsV2.Binding with { BindingId = "SEMANTIC_MEMBERSHIP_V1" };

    private static bool TryParseDisposition(string? value, out Stage1MembershipDisposition disposition)
    {
        switch (value)
        {
            case "DOCUMENT_LABEL":
                disposition = Stage1MembershipDisposition.DocumentLabel;
                return true;
            case "STRUCTURAL_UNIT":
                disposition = Stage1MembershipDisposition.StructuralUnit;
                return true;
            default:
                disposition = default;
                return false;
        }
    }
}
