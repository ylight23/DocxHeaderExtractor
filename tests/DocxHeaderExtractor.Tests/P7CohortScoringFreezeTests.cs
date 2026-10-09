using DocxHeaderExtractor.V5Qualification.P7;

namespace DocxHeaderExtractor.Tests;

public sealed class P7CohortScoringFreezeTests
{
    private static string H(char c) => new(c, 64);
    private static CohortFreezeInput Ready()
    {
        var sources = new[]
        {
            new CohortSourceIdentity("D1", H('a'), H('b'), H('c'), H('d'), new[] { "L1", "L2" }, "KNOWN_DEVELOPMENT", null),
            new CohortSourceIdentity("D2", H('e'), H('f'), H('0'), H('1'), new[] { "L1", "L2" }, "NOT_SCREENED", null)
        };
        var reviews = P7CohortScoringFreeze.RequiredShapes.Select(shape => new CohortShapeReview(shape,
            "D1", H('a'), H('b'), new[] { "L1" }, H('2'))).ToArray();
        var authorities = sources.Select(s => new CohortScoringAuthority(s.Document,
            s.SourceSha256, s.UniverseSha256, H('3'), H('4'))).ToArray();
        var requests = sources.SelectMany(s => new[] { "F1", "G2A", "H2C" }.SelectMany(stage =>
            new[] { "CONTROL", "B" }.Select(arm => new CohortRequestIdentity(s.Document, stage, arm,
                s.SourceSha256, s.UniverseSha256, s.PackSha256,
                "CALL_001", H('a'), "QUALIFICATION_V1", H('5'), H('6'), H('7'))))).ToArray();
        var coverage = requests.Select(r => new CohortStageCoverage(r.Document, r.Stage, r.Arm, r.IssuedUniverseSha256, 1)).ToArray();
        return new(sources, reviews, authorities, requests, new[] { "CONTROL", "B" }, coverage, new("SCORING_V1", H('8'),
            "INCLUDE_DOCUMENT_TITLES_AND_SUBTITLES", "SOURCE_REVIEWED_EXACT_UNITS_NOT_MODEL_DERIVED", H('9'), "F1_MEMBERSHIP_SEPARATE", "G2A_ANCHOR_SEPARATE",
            "H2_EXACT_BOUNDARY_TRUE_ANCHORS_ONLY", "FALSE_ANCHORS_DIAGNOSTIC_ONLY", "NOT_EVALUABLE",
            "OBSERVATION_ONLY_NO_PRUNING"), false);
    }

    [Fact]
    public void Synthetic_complete_manifest_freezes_without_provider_authorization()
    {
        var input = Ready();
        Assert.Empty(P7CohortScoringFreeze.ReadinessGaps(input));
        var frozen = P7CohortScoringFreeze.Freeze(input);
        Assert.Equal(64, frozen.FreezeSha256.Length);
        Assert.Contains("NOT_GRANTED", System.Text.Encoding.UTF8.GetString(frozen.CanonicalBytes()));
    }

    [Fact]
    public void Frozen_manifest_and_bytes_do_not_alias_mutable_input_or_output()
    {
        var input = Ready(); var sourceAliases = (string[])input.Sources[0].SourceAliases;
        var frozen = P7CohortScoringFreeze.Freeze(input); var before = frozen.FreezeSha256;
        sourceAliases[0] = "Injected";
        var bytes = frozen.CanonicalBytes(); bytes[0] = 0;
        Assert.DoesNotContain("Injected", frozen.Manifest.Sources[0].SourceAliases);
        Assert.Equal(before, frozen.FreezeSha256);
    }

    [Fact]
    public void Freeze_is_byte_identical_under_collection_enumeration_permutation()
    {
        var input = Ready(); var reversed = input with
        {
            Sources = input.Sources.Reverse().Select(s => s with { SourceAliases = s.SourceAliases.Reverse().ToArray() }).ToArray(),
            Shapes = input.Shapes.Reverse().ToArray(), Authorities = input.Authorities.Reverse().ToArray(),
            Requests = input.Requests.Reverse().ToArray(), Coverage = input.Coverage.Reverse().ToArray(), Arms = input.Arms.Reverse().ToArray()
        };
        Assert.Equal(P7CohortScoringFreeze.Freeze(input).CanonicalBytes(), P7CohortScoringFreeze.Freeze(reversed).CanonicalBytes());
    }

    [Theory]
    [InlineData("sourceHash", "SOURCE_IDENTITY_INVALID")]
    [InlineData("duplicateSource", "DUPLICATE_SOURCE_DOCUMENT")]
    [InlineData("shape", "SOURCE_ONLY_SHAPE_REVIEW_MISSING:CROSS_PAGE_HEADING")]
    [InlineData("foreignAlias", "SOURCE_ONLY_SHAPE_REFERENCE_INVALID")]
    [InlineData("shapeSnapshot", "SOURCE_ONLY_SHAPE_REFERENCE_INVALID")]
    [InlineData("goldSnapshot", "SCORING_AUTHORITY_SNAPSHOT_INVALID")]
    [InlineData("missingGold", "INDEPENDENT_SCORING_AUTHORITY_MISSING_OR_DUPLICATE")]
    [InlineData("titlePolicy", "SCORING_POLICY_UNAPPROVED_OR_UNADJUDICATED")]
    [InlineData("groupingPolicy", "SCORING_POLICY_UNAPPROVED_OR_UNADJUDICATED")]
    [InlineData("falseAnchorScore", "SCORING_LANE_POLICY_INVALID")]
    [InlineData("overlapPruning", "SCORING_LANE_POLICY_INVALID")]
    [InlineData("request", "STAGE_REQUEST_MANIFEST_INCOMPLETE")]
    [InlineData("requestSnapshot", "REQUEST_SNAPSHOT_INVALID")]
    [InlineData("coverage", "STAGE_COVERAGE_MANIFEST_INVALID")]
    [InlineData("issuedUniverse", "REQUEST_ISSUED_UNIVERSE_INVALID")]
    [InlineData("duplicateRequest", "DUPLICATE_REQUEST_IDENTITY")]
    [InlineData("heldOut", "HELD_OUT_CLAIM_UNSUPPORTED")]
    public void Missing_or_misbound_authority_cannot_be_named_frozen(string mutation, string code)
    {
        var input = Ready();
        input = mutation switch
        {
            "sourceHash" => input with { Sources = new[] { input.Sources[0] with { SourceSha256 = "bad" }, input.Sources[1] } },
            "duplicateSource" => input with { Sources = new[] { input.Sources[0], input.Sources[0] } },
            "shape" => input with { Shapes = input.Shapes.Where(s => s.Shape != "CROSS_PAGE_HEADING").ToArray() },
            "foreignAlias" => input with { Shapes = input.Shapes.Select(s => s with { SourceAliases = new[] { "L999" } }).ToArray() },
            "shapeSnapshot" => input with { Shapes = input.Shapes.Select(s => s with { SourceSha256 = H('e') }).ToArray() },
            "goldSnapshot" => input with { Authorities = input.Authorities.Select(a => a with { UniverseSha256 = H('0') }).ToArray() },
            "missingGold" => input with { Authorities = [] },
            "titlePolicy" => input with { Policy = input.Policy with { TitleSubtitlePolicy = "OPEN" } },
            "groupingPolicy" => input with { Policy = input.Policy with { TitleSubtitleGroupingPolicy = "USE_MODEL_GROUPING" } },
            "falseAnchorScore" => input with { Policy = input.Policy with { FalseAnchorLane = "INCLUDE_IN_EXACT_DENOMINATOR" } },
            "overlapPruning" => input with { Policy = input.Policy with { OverlapLane = "DELETE_OVERLAPPING" } },
            "request" => input with { Requests = input.Requests.Where(r => r.Stage != "H2C").ToArray() },
            "requestSnapshot" => input with { Requests = input.Requests.Select(r => r with { SourceSha256 = H('0') }).ToArray() },
            "coverage" => input with { Coverage = [] },
            "issuedUniverse" => input with { Requests = input.Requests.Select(r => r with { IssuedUniverseSha256 = H('0') }).ToArray() },
            "duplicateRequest" => input with { Requests = input.Requests.Concat(new[] { input.Requests[0] }).ToArray() },
            "heldOut" => input with { ClaimsHeldOut = true },
            _ => throw new InvalidOperationException()
        };
        Assert.Contains(code, P7CohortScoringFreeze.ReadinessGaps(input));
        Assert.Contains("P7_D23_NOT_FROZEN", Assert.Throws<InvalidOperationException>(() => P7CohortScoringFreeze.Freeze(input)).Message);
    }

    [Fact]
    public void Independently_screened_exposure_is_required_for_every_held_out_document()
    {
        var input = Ready() with { ClaimsHeldOut = true };
        input = input with { Sources = input.Sources.Select(s => s with
        { PriorExposure = "INDEPENDENTLY_SCREENED_UNEXPOSED", ExposureReviewSha256 = H('2') }).ToArray() };
        Assert.Empty(P7CohortScoringFreeze.ReadinessGaps(input));
    }

    [Fact]
    public void Missing_one_of_multiple_expected_H2_calls_is_not_complete()
    {
        var input = Ready();
        input = input with { Coverage = input.Coverage.Select(c => c.Stage == "H2C" ? c with { ExpectedRequestCount = 2 } : c).ToArray() };
        Assert.Contains("STAGE_REQUEST_MANIFEST_INCOMPLETE", P7CohortScoringFreeze.ReadinessGaps(input));
        Assert.Throws<InvalidOperationException>(() => P7CohortScoringFreeze.Freeze(input));
    }

    [Fact]
    public void Downstream_declared_zero_count_with_issued_universe_identity_is_allowed()
    {
        var input = Ready();
        input = input with
        {
            Requests = input.Requests.Where(r => r.Stage != "H2C").ToArray(),
            Coverage = input.Coverage.Select(c => c.Stage == "H2C" ? c with { ExpectedRequestCount = 0 } : c).ToArray()
        };
        Assert.Empty(P7CohortScoringFreeze.ReadinessGaps(input));
        var frozen = P7CohortScoringFreeze.Freeze(input);
        Assert.DoesNotContain(frozen.Manifest.Requests, r => r.Stage == "H2C");
    }

    [Fact]
    public void Nonempty_F1_source_universe_cannot_declare_zero_requests()
    {
        var input = Ready();
        input = input with
        {
            Requests = input.Requests.Where(r => r.Stage != "F1").ToArray(),
            Coverage = input.Coverage.Select(c => c.Stage == "F1" ? c with { ExpectedRequestCount = 0 } : c).ToArray()
        };
        Assert.Contains("NONEMPTY_F1_UNIVERSE_REQUIRES_REQUESTS", P7CohortScoringFreeze.ReadinessGaps(input));
        Assert.Throws<InvalidOperationException>(() => P7CohortScoringFreeze.Freeze(input));
    }
}
