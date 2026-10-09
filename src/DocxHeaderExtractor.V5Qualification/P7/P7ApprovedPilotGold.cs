using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record PilotSourceAtom(string Alias, string SourceId, int Ordinal, int Page, string Text);
internal sealed record PilotPolicyResolution(string Document, string Alias, string SemanticFunction,
    bool HeadingMembership, bool IsDistinctAnchor);
internal sealed record PilotUserApproval(bool UserApproved, bool ScopeApproved, string ProposalSha256,
    IReadOnlyList<PilotPolicyResolution> Resolutions);
internal sealed record PilotApprovedRow(string Alias, string SourceId, int Ordinal, int Page,
    string SemanticFunction, bool HeadingMembership, bool IsDistinctAnchor, string? UnitId);
// Optional, unscored reviewer observations; not parser facts or a model role ontology.
internal sealed record PilotReviewerInterpretation(string Alias, string? ProposalRoleNote, string? Explanation);
internal sealed record PilotApprovedDocument(string Document, EvaluationScope Scope,
    IReadOnlyList<EvaluationAnnotation> Annotations, IReadOnlyList<PilotApprovedRow> ReviewedRows,
    IReadOnlyList<PilotGoldUnit> Units, IReadOnlyList<PilotReviewerInterpretation> ReviewerInterpretations);

/// <summary>
/// Serializes explicitly user-approved source review. No PDF parsing, inference, label prediction,
/// text normalization, implicit negative outside scope, or changes to production Gold.
/// Character offsets are .NET string indices (UTF-16 code units), never Unicode scalar counts.
/// </summary>
internal static class P7ApprovedPilotGold
{
    public const string Version = "P7_USER_APPROVED_PILOT_GOLD_V2";
    public const string OffsetConvention = "DOTNET_UTF16_CODE_UNITS";

    public static PilotApprovedDocument Build(JsonElement proposal, string proposalSha256,
        PilotUserApproval approval, string document, EvaluationScope scope, IReadOnlyList<PilotSourceAtom> source)
    {
        Require(approval.UserApproved && approval.ScopeApproved && approval.ProposalSha256 == proposalSha256,
            "EXPLICIT_USER_APPROVAL_REQUIRED");
        Require(proposal.GetProperty("status").GetString() == "PROPOSAL_NOT_APPROVED_GOLD" &&
            !proposal.GetProperty("modelPredictionsUsed").GetBoolean(), "SOURCE_ONLY_PROPOSAL_REQUIRED");
        Require(source.Select(a => a.Alias).Distinct().Count() == source.Count &&
            source.Select(a => a.Ordinal).Distinct().Count() == source.Count &&
            source.Select(a => a.Ordinal).SequenceEqual(source.Select(a => a.Ordinal).Order()), "SOURCE_ORDER_INVALID");
        var byAlias = source.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var index = source.Select((a, i) => (a.Alias, i)).ToDictionary(a => a.Alias, a => a.i);
        var rows = proposal.GetProperty("worksheetRows").EnumerateArray()
            .Where(r => r.GetProperty("document").GetString() == document).ToArray();
        Require(rows.Select(r => S(r, "alias")).Order().SequenceEqual(
            source.Where(a => scope.Pages.Contains(a.Page)).Select(a => a.Alias).Order()), "SCOPE_ROW_PARTITION_MISMATCH");
        Require(approval.Resolutions.Select(r => (r.Document, r.Alias)).Distinct().Count() == approval.Resolutions.Count,
            "DUPLICATE_POLICY_RESOLUTION");
        foreach (var resolution in approval.Resolutions.Where(r => r.Document == document))
            Require(rows.Any(r => S(r, "alias") == resolution.Alias), "POLICY_REFERENCE_INVALID");

        var units = new List<PilotGoldUnit>(); var unitIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var memberUnits = new Dictionary<string, PilotGoldUnit>(StringComparer.Ordinal);
        foreach (var u in proposal.GetProperty("proposedUnits").EnumerateArray().Where(u => S(u, "document") == document))
        {
            var id = S(u, "unitId"); var anchor = S(u, "primaryAlias");
            Require(unitIds.TryAdd(anchor, id) && !unitIds.Values.Where(x => x == id).Skip(1).Any(), "DUPLICATE_UNIT_ID_OR_ANCHOR");
            var parts = u.GetProperty("parts").EnumerateArray().Select(p =>
            {
                var alias = S(p, "alias"); Require(byAlias.ContainsKey(alias), "PART_REFERENCE_INVALID"); var a = byAlias[alias];
                Require(S(p, "sourceId") == a.SourceId && p.GetProperty("ordinal").GetInt32() == a.Ordinal &&
                    S(p, "text") == a.Text && scope.Pages.Contains(a.Page) && a.Text.Length > 0, "PART_SOURCE_MISMATCH");
                return new ReviewedPart(alias, 0, a.Text.Length);
            }).ToArray();
            Require(parts.Length > 0 && parts[0].Alias == anchor &&
                u.GetProperty("primaryOrdinal").GetInt32() == byAlias[anchor].Ordinal &&
                parts.Select((p, i) => index[p.Alias] - i).Distinct().Count() == 1, "UNIT_NOT_CONTIGUOUS");
            var exit = u.GetProperty("reviewedFirstOutside");
            Require(!u.GetProperty("documentEndAttested").GetBoolean() && exit.ValueKind == JsonValueKind.Object,
                "PILOT_EXIT_WITNESS_REQUIRED");
            var outside = S(exit, "alias");
            Require(byAlias.TryGetValue(outside, out var firstOutside) && scope.Pages.Contains(firstOutside.Page) &&
                index[outside] == index[parts[^1].Alias] + 1 && S(exit, "sourceId") == firstOutside.SourceId &&
                exit.GetProperty("ordinal").GetInt32() == firstOutside.Ordinal && exit.GetProperty("page").GetInt32() == firstOutside.Page,
                "EXIT_WITNESS_MISMATCH");
            var unit = new PilotGoldUnit(anchor, parts, outside, false); units.Add(unit);
            foreach (var p in parts) Require(memberUnits.TryAdd(p.Alias, unit), "OVERLAPPING_GOLD_UNITS");
        }
        var reviewed = new List<PilotApprovedRow>();
        var interpretations = new List<PilotReviewerInterpretation>();
        foreach (var r in rows)
        {
            var alias = S(r, "alias"); var atom = byAlias[alias];
            Require(S(r, "sourceId") == atom.SourceId && S(r, "textForReview") == atom.Text &&
                r.GetProperty("ordinal").GetInt32() == atom.Ordinal && r.GetProperty("page").GetInt32() == atom.Page,
                "ROW_SOURCE_MISMATCH");
            var resolution = approval.Resolutions.SingleOrDefault(x => x.Document == document && x.Alias == alias);
            var function = resolution?.SemanticFunction ?? r.GetProperty("semanticFunction").GetString();
            var member = resolution?.HeadingMembership ?? NullableBool(r, "headingMembership");
            var anchor = resolution?.IsDistinctAnchor ?? NullableBool(r, "isDistinctAnchor");
            Require(function is "ESTABLISHES_STRUCTURE" or "REPRESENTS_STRUCTURE" or "OTHER" && member is not null && anchor is not null,
                "UNRESOLVED_ADJUDICATION");
            var partOfUnit = memberUnits.TryGetValue(alias, out var unit);
            Require(member == partOfUnit && anchor == (partOfUnit && unit!.Anchor == alias), "MEMBERSHIP_ANCHOR_UNIT_MISMATCH");
            var unitId = partOfUnit ? unitIds[unit!.Anchor] : null;
            Require(r.GetProperty("unitId").GetString() == unitId, "ROW_UNIT_ID_MISMATCH");
            reviewed.Add(new(alias, atom.SourceId, atom.Ordinal, atom.Page, function!, member!.Value, anchor!.Value, unitId));
            interpretations.Add(new(alias, OptionalNote(r, "unitRole"), OptionalNote(r, "sourceReviewNote")));
        }
        var reviewedByAlias = reviewed.ToDictionary(r => r.Alias);
        var annotations = source.Select(a => reviewedByAlias.TryGetValue(a.Alias, out var r)
            ? new EvaluationAnnotation(a.Alias, "ADJUDICATED", r.SemanticFunction, r.HeadingMembership,
                memberUnits.TryGetValue(a.Alias, out var u) ? u.Parts : Array.Empty<ReviewedPart>())
            : new EvaluationAnnotation(a.Alias, "OUT_OF_EVALUATION_SCOPE", null, null, null)).ToArray();
        var readiness = P7PilotScorer.Score(scope, source.Select(a => new ReviewOccurrence(a.Alias, a.Page, a.Text.Length)).ToArray(),
            annotations, units, []);
        Require(readiness.ReadinessGaps.Count == 0, "GOLD_SCORER_READINESS_FAILED:" + string.Join(",", readiness.ReadinessGaps));
        return new(document, scope, annotations, reviewed.OrderBy(r => r.Ordinal).ToArray(), units, interpretations);
    }

    // Serialization boundary used by representation-parity tests. No interpretation can enter
    // this projection or the scorer inputs, regardless of role vocabulary or missing notes.
    public static byte[] ScoringProjection(PilotApprovedDocument gold) => JsonSerializer.SerializeToUtf8Bytes(
        new { gold.Document, gold.Scope, gold.Annotations, gold.ReviewedRows, gold.Units },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static string? OptionalNote(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static bool? NullableBool(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetBoolean();
    private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
}
