using System.Security.Cryptography;
using System.Text.Json;

namespace DocxHeaderExtractor.V5Qualification.P7;

internal sealed record PilotScoringInput(string Document, EvaluationScope Scope,
    IReadOnlyList<EvaluationAnnotation> Annotations, IReadOnlyList<PilotApprovedRow> ReviewedRows,
    IReadOnlyList<PilotGoldUnit> Units, string PolicySha256);

/// <summary>
/// Qualification scoring boundary: exactly two inputs, Gold and task policy. Does not open
/// manifests/archives or interpretation sidecars. Archive integrity is a separate concern:
/// removing or editing an unscored sidecar cannot invalidate or change task scoring.
/// Source identity and spans still require validation against the pinned snapshot by the caller.
/// </summary>
internal static class P7PilotGoldReader
{
    public const string Version = "P7_ROLE_FREE_GOLD_SCORING_READER_V1";
    public static PilotScoringInput Load(string goldPath, string policyPath) =>
        Read(File.ReadAllBytes(goldPath), File.ReadAllBytes(policyPath));

    public static PilotScoringInput Read(byte[] goldBytes, byte[] policyBytes)
    {
        using var gold = JsonDocument.Parse(goldBytes); var r = gold.RootElement;
        using var policy = JsonDocument.Parse(policyBytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(policyBytes));
        if (r.GetProperty("version").GetString() != P7ApprovedPilotGold.Version ||
            r.GetProperty("status").GetString() != "USER_APPROVED_PILOT_GOLD" ||
            r.GetProperty("offsetConvention").GetString() != P7ApprovedPilotGold.OffsetConvention)
            throw new InvalidOperationException("APPROVED_ROLE_FREE_GOLD_V2_REQUIRED");
        if (r.GetProperty("evaluationPolicySha256").GetString() != hash ||
            policy.RootElement.GetProperty("approvalSha256").GetString() != r.GetProperty("approvalSha256").GetString())
            throw new InvalidOperationException("GOLD_POLICY_BINDING_MISMATCH");
        foreach (var row in r.GetProperty("reviewedRows").EnumerateArray())
            ExactFields(row, "alias", "sourceId", "ordinal", "page", "semanticFunction", "headingMembership", "isDistinctAnchor", "unitId");
        foreach (var a in r.GetProperty("annotations").EnumerateArray())
        {
            ExactFields(a, "alias", "status", "semanticFunction", "headingMembership", "extentParts");
            if (a.GetProperty("extentParts").ValueKind == JsonValueKind.Array)
                foreach (var p in a.GetProperty("extentParts").EnumerateArray()) ExactFields(p, "alias", "start", "length");
        }
        foreach (var u in r.GetProperty("units").EnumerateArray())
        {
            ExactFields(u, "anchor", "parts", "reviewedFirstOutside", "documentEndAttested");
            foreach (var p in u.GetProperty("parts").EnumerateArray()) ExactFields(p, "alias", "start", "length");
        }
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return new(r.GetProperty("document").GetString()!, r.GetProperty("scope").Deserialize<EvaluationScope>(options)!,
            r.GetProperty("annotations").Deserialize<EvaluationAnnotation[]>(options)!,
            r.GetProperty("reviewedRows").Deserialize<PilotApprovedRow[]>(options)!,
            r.GetProperty("units").Deserialize<PilotGoldUnit[]>(options)!, hash);
    }

    private static void ExactFields(JsonElement value, params string[] fields)
    {
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("GOLD_DECISION_FIELDS_INVALID");
    }
}
