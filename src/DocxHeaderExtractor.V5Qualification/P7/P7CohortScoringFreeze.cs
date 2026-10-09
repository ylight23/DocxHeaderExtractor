namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record CohortSourceIdentity(string Document, string SourceSha256,
    string UniverseSha256, string StoreSha256, string PackSha256,
    IReadOnlyList<string> SourceAliases, string PriorExposure, string? ExposureReviewSha256);
internal sealed record CohortShapeReview(string Shape, string Document, string SourceSha256,
    string UniverseSha256, IReadOnlyList<string> SourceAliases, string SourceOnlyReviewSha256);
internal sealed record CohortScoringAuthority(string Document, string SourceSha256,
    string UniverseSha256, string GoldSha256, string IndependentApprovalSha256);
internal sealed record CohortRequestIdentity(string Document, string Stage, string Arm,
    string SourceSha256, string UniverseSha256, string PackSha256,
    string CallHandle, string IssuedUniverseSha256, string ProtocolVersion,
    string PromptSha256, string BodySha256, string UserMessageSha256);
internal sealed record CohortStageCoverage(string Document, string Stage, string Arm,
    string IssuedUniverseSha256, int ExpectedRequestCount);
internal sealed record CohortScoringPolicy(string Version, string ScorerSha256,
    string TitleSubtitlePolicy, string TitleSubtitleGroupingPolicy, string PolicyApprovalSha256,
    string MembershipLane, string AnchorLane, string BoundaryLane, string FalseAnchorLane,
    string UnadjudicatedLane, string OverlapLane);
internal sealed record CohortFreezeInput(IReadOnlyList<CohortSourceIdentity> Sources,
    IReadOnlyList<CohortShapeReview> Shapes, IReadOnlyList<CohortScoringAuthority> Authorities,
    IReadOnlyList<CohortRequestIdentity> Requests, IReadOnlyList<string> Arms,
    IReadOnlyList<CohortStageCoverage> Coverage, CohortScoringPolicy Policy, bool ClaimsHeldOut);

/// <summary>
/// Qualification-only manifest gate. Checks identity and readiness, not annotation truth.
/// Approval hashes must come from independently approved records, never predictions.
/// A successful freeze grants neither provider authorization nor production promotion.
/// </summary>
internal sealed class P7CohortScoringFreeze
{
    public const string Version = "P7_D23_COHORT_SCORING_FREEZE_V1";
    public static IReadOnlyList<string> RequiredShapes { get; } = Array.AsReadOnly(new[]
    {
        "ADMINISTRATIVE_DOCUMENT", "MULTICOLUMN_TABLE", "BORDERLESS_TABLE",
        "MULTILINE_HEADING", "CROSS_PAGE_HEADING", "TWO_COLUMN_DOCUMENT",
        "NO_TABLE_DOCUMENT", "TRUE_DOCUMENT_HEADING_INSIDE_TABLE_CELL"
    });
    public CohortFreezeInput Manifest { get; }
    private readonly byte[] bytes;
    public string FreezeSha256 => SpatialCanonical.Hash(bytes);
    public byte[] CanonicalBytes() => bytes.ToArray();

    private P7CohortScoringFreeze(CohortFreezeInput input)
    {
        Manifest = Copy(input);
        bytes = SpatialCanonical.Bytes(new { protocolVersion = Version,
            status = "FROZEN", providerAuthorization = "NOT_GRANTED",
            runtimePromotion = "FORBIDDEN", manifest = Manifest });
    }

    public static P7CohortScoringFreeze Freeze(CohortFreezeInput input)
    {
        var snapshot = Copy(input);
        var gaps = ReadinessGaps(snapshot);
        if (gaps.Count != 0) throw new InvalidOperationException("P7_D23_NOT_FROZEN:" + string.Join("|", gaps));
        return new(snapshot);
    }

    public static IReadOnlyList<string> ReadinessGaps(CohortFreezeInput input)
    {
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        void Gap(string code) => gaps.Add(code);
        static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static bool Distinct(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Count() == values.Count();
        if (input.Sources.Count < 2) Gap("MULTI_DOCUMENT_COHORT_MISSING");
        if (!Distinct(input.Sources.Select(s => s.Document)) || !Distinct(input.Sources.Select(s => s.SourceSha256))) Gap("DUPLICATE_SOURCE_DOCUMENT");
        if (input.Arms.Count == 0 || !Distinct(input.Arms) || input.Arms.Any(string.IsNullOrWhiteSpace)) Gap("ARM_MANIFEST_INVALID");
        foreach (var source in input.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Document) || !new[] { source.SourceSha256, source.UniverseSha256, source.StoreSha256, source.PackSha256 }.All(Hash)) Gap("SOURCE_IDENTITY_INVALID");
            if (source.SourceAliases.Count == 0 || source.SourceAliases.Any(string.IsNullOrWhiteSpace) || !Distinct(source.SourceAliases)) Gap("SOURCE_ALIAS_UNIVERSE_INVALID");
            if (source.PriorExposure is not ("KNOWN_DEVELOPMENT" or "NOT_SCREENED" or "INDEPENDENTLY_SCREENED_UNEXPOSED")) Gap("PRIOR_EXPOSURE_LABEL_INVALID");
            if (input.ClaimsHeldOut && (source.PriorExposure != "INDEPENDENTLY_SCREENED_UNEXPOSED" || !Hash(source.ExposureReviewSha256))) Gap("HELD_OUT_CLAIM_UNSUPPORTED");
            var authorities = input.Authorities.Where(x => x.Document == source.Document).ToArray();
            if (authorities.Length != 1) Gap("INDEPENDENT_SCORING_AUTHORITY_MISSING_OR_DUPLICATE");
            else if (authorities[0].SourceSha256 != source.SourceSha256 || authorities[0].UniverseSha256 != source.UniverseSha256 || !Hash(authorities[0].GoldSha256) || !Hash(authorities[0].IndependentApprovalSha256)) Gap("SCORING_AUTHORITY_SNAPSHOT_INVALID");
            foreach (var stage in new[] { "F1", "G2A", "H2C" })
                foreach (var arm in input.Arms)
                {
                    var coverage = input.Coverage.Where(c => c.Document == source.Document && c.Stage == stage && c.Arm == arm).ToArray();
                    if (coverage.Length != 1 || coverage[0].ExpectedRequestCount < 0 || !Hash(coverage[0].IssuedUniverseSha256)) Gap("STAGE_COVERAGE_MANIFEST_INVALID");
                    else if (stage == "F1" && source.SourceAliases.Count > 0 && coverage[0].ExpectedRequestCount == 0) Gap("NONEMPTY_F1_UNIVERSE_REQUIRES_REQUESTS");
                    else if (input.Requests.Count(r => r.Document == source.Document && r.Stage == stage && r.Arm == arm) != coverage[0].ExpectedRequestCount) Gap("STAGE_REQUEST_MANIFEST_INCOMPLETE");
                }
        }
        foreach (var shape in RequiredShapes)
            if (!input.Shapes.Any(x => x.Shape == shape)) Gap("SOURCE_ONLY_SHAPE_REVIEW_MISSING:" + shape);
        foreach (var review in input.Shapes)
        {
            var source = input.Sources.SingleOrDefaultSafe(s => s.Document == review.Document);
            if (source is null || !RequiredShapes.Contains(review.Shape) || source.SourceSha256 != review.SourceSha256 || source.UniverseSha256 != review.UniverseSha256 || !Hash(review.SourceOnlyReviewSha256) || review.SourceAliases.Count == 0 || !Distinct(review.SourceAliases) || review.SourceAliases.Any(a => !source.SourceAliases.Contains(a))) Gap("SOURCE_ONLY_SHAPE_REFERENCE_INVALID");
        }
        if (input.Authorities.Any(a => !input.Sources.Any(s => s.Document == a.Document))) Gap("UNISSUED_SCORING_AUTHORITY");
        foreach (var request in input.Requests)
        {
            if (!input.Sources.Any(s => s.Document == request.Document) || !input.Arms.Contains(request.Arm) || request.Stage is not ("F1" or "G2A" or "H2C") || string.IsNullOrWhiteSpace(request.ProtocolVersion) || string.IsNullOrWhiteSpace(request.CallHandle) || !new[] { request.PromptSha256, request.BodySha256, request.UserMessageSha256, request.IssuedUniverseSha256 }.All(Hash)) Gap("REQUEST_IDENTITY_INVALID");
            var source = input.Sources.SingleOrDefaultSafe(s => s.Document == request.Document);
            if (source is null || source.SourceSha256 != request.SourceSha256 || source.UniverseSha256 != request.UniverseSha256 || source.PackSha256 != request.PackSha256) Gap("REQUEST_SNAPSHOT_INVALID");
            var coverage = input.Coverage.SingleOrDefaultSafe(c => c.Document == request.Document && c.Stage == request.Stage && c.Arm == request.Arm);
            if (coverage is null || coverage.IssuedUniverseSha256 != request.IssuedUniverseSha256) Gap("REQUEST_ISSUED_UNIVERSE_INVALID");
        }
        if (input.Coverage.Any(c => !input.Sources.Any(s => s.Document == c.Document) || !input.Arms.Contains(c.Arm) || c.Stage is not ("F1" or "G2A" or "H2C"))) Gap("STAGE_COVERAGE_MANIFEST_INVALID");
        // Multiple H2-C calls are allowed; exact body identities cannot be repeated.
        if (!Distinct(input.Requests.Select(r => r.Document + "|" + r.Stage + "|" + r.Arm + "|" + r.BodySha256))) Gap("DUPLICATE_REQUEST_IDENTITY");
        if (!Distinct(input.Requests.Select(r => r.Document + "|" + r.Stage + "|" + r.Arm + "|" + r.CallHandle))) Gap("DUPLICATE_REQUEST_IDENTITY");
        var p = input.Policy;
        if (string.IsNullOrWhiteSpace(p.Version) || !Hash(p.ScorerSha256) || !Hash(p.PolicyApprovalSha256) || p.TitleSubtitlePolicy is not ("INCLUDE_DOCUMENT_TITLES_AND_SUBTITLES" or "INCLUDE_TITLES_EXCLUDE_SUBTITLES" or "EXCLUDE_DOCUMENT_TITLES_AND_SUBTITLES") || p.TitleSubtitleGroupingPolicy != "SOURCE_REVIEWED_EXACT_UNITS_NOT_MODEL_DERIVED") Gap("SCORING_POLICY_UNAPPROVED_OR_UNADJUDICATED");
        if (p.MembershipLane != "F1_MEMBERSHIP_SEPARATE" || p.AnchorLane != "G2A_ANCHOR_SEPARATE" || p.BoundaryLane != "H2_EXACT_BOUNDARY_TRUE_ANCHORS_ONLY" || p.FalseAnchorLane != "FALSE_ANCHORS_DIAGNOSTIC_ONLY" || p.UnadjudicatedLane != "NOT_EVALUABLE" || p.OverlapLane != "OBSERVATION_ONLY_NO_PRUNING") Gap("SCORING_LANE_POLICY_INVALID");
        return Array.AsReadOnly(gaps.ToArray());
    }

    private static CohortFreezeInput Copy(CohortFreezeInput input) => input with
    {
        Sources = Array.AsReadOnly(input.Sources.OrderBy(x => x.Document, StringComparer.Ordinal).Select(x => x with { SourceAliases = Sorted(x.SourceAliases) }).ToArray()),
        Shapes = Array.AsReadOnly(input.Shapes.OrderBy(x => x.Shape, StringComparer.Ordinal).ThenBy(x => x.Document, StringComparer.Ordinal).ThenBy(x => x.SourceOnlyReviewSha256, StringComparer.Ordinal).Select(x => x with { SourceAliases = Sorted(x.SourceAliases) }).ToArray()),
        Authorities = Array.AsReadOnly(input.Authorities.OrderBy(x => x.Document, StringComparer.Ordinal).ToArray()),
        Requests = Array.AsReadOnly(input.Requests.OrderBy(x => x.Document, StringComparer.Ordinal).ThenBy(x => x.Stage, StringComparer.Ordinal).ThenBy(x => x.Arm, StringComparer.Ordinal).ThenBy(x => x.BodySha256, StringComparer.Ordinal).ToArray()),
        Coverage = Array.AsReadOnly(input.Coverage.OrderBy(x => x.Document, StringComparer.Ordinal).ThenBy(x => x.Stage, StringComparer.Ordinal).ThenBy(x => x.Arm, StringComparer.Ordinal).ToArray()),
        Arms = Sorted(input.Arms)
    };
    private static IReadOnlyList<string> Sorted(IEnumerable<string> values) => Array.AsReadOnly(values.OrderBy(x => x, StringComparer.Ordinal).ToArray());
}

internal static class CohortSequenceExtensions
{
    public static T? SingleOrDefaultSafe<T>(this IEnumerable<T> values, Func<T, bool> predicate) where T : class
    {
        var matches = values.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
