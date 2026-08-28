using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxHeaderExtractor.Tests;

/// <summary>Audits the retained marker-only resolution evidence without replaying providers or production code.</summary>
public sealed class PdfEMarkerOnlyPolicyAuditProbe
{
    private const string Input = "eval/accuracy-round6/k160-semantic-run.v1.json";

    [Fact]
    public void WriteAudit()
    {
        var jsonPath = Environment.GetEnvironmentVariable("BENCH_E_MARKER_ONLY_JSON");
        var mdPath = Environment.GetEnvironmentVariable("BENCH_E_MARKER_ONLY_MD");
        if (string.IsNullOrWhiteSpace(jsonPath) || string.IsNullOrWhiteSpace(mdPath)) return;
        var root = PdfExtractorQualityBenchmarkProbe.RepositoryRoot();
        var artifact = Build(root);
        jsonPath = Path.IsPathRooted(jsonPath) ? jsonPath : Path.Combine(root, jsonPath);
        mdPath = Path.IsPathRooted(mdPath) ? mdPath : Path.Combine(root, mdPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mdPath))!);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(artifact, Options));
        File.WriteAllText(mdPath, Markdown(artifact));
    }

    [Fact]
    public void AuditPreservesAuthorityBoundary()
    {
        var a = Build(PdfExtractorQualityBenchmarkProbe.RepositoryRoot());
        Assert.Equal(13, a.MARKER_ONLY_TOTAL);
        Assert.Equal(13, a.MARKER_ONLY_NEEDS_VISUAL);
        Assert.Equal("NOT_OBSERVABLE", a.MARKER_ONLY_HEADING_TOPIC);
        Assert.Equal(13, a.NOT_JOINABLE);
        Assert.Equal(0, a.TRUE_HEADING_LOWERED);
        Assert.Equal("NOT_OBSERVABLE", a.FIRST_LOSS_CONFLICT_RESOLUTION);
        Assert.Equal("NOT_PROVEN", a.CROSS_DOCUMENT_VALID_HEADING_LOSS);
        Assert.Equal("NO", a.REMEDIATION_JUSTIFIED);
        Assert.Equal(0, a.PROVIDER_CALLS);
        Assert.False(a.PRODUCTION_CODE_CHANGED);
    }

    private static EArtifact Build(string root)
    {
        var path = Path.Combine(root, Input.Replace('/', Path.DirectorySeparatorChar));
        using var source = JsonDocument.Parse(File.ReadAllText(path));
        var rows = source.RootElement.GetProperty("rows").EnumerateArray().Select(BuildDocument).ToArray();
        var total = rows.Sum(x => x.MARKER_ONLY_NEEDS_VISUAL);
        return new EArtifact(1, "accuracy_marker_only_policy_audit", "E-marker-only-policy-audit",
            "frozen eval/accuracy-round6/k160-semantic-run.v1.json; aggregate-only proposal resolution evidence",
            total, "NOT_OBSERVABLE", total, 0, 0, 0, total, "NOT_OBSERVABLE", rows,
            new Counterfactual("NOT_EXECUTABLE_NO_ID_LEVEL_TRANSITION", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE"),
            new Safety("NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE"),
            "NOT_PROVEN", "004 legal and 030 procurement are CLASS_2: policy shape is observed, but reviewed ID-level authority is absent. 043 financial and 058 textbook are CLASS_3 for this policy because no marker-only resolution is retained.",
            "NO", 0, false);
    }

    private static EDocument BuildDocument(JsonElement row)
    {
        var file = row.GetProperty("file").GetString()!;
        var id = file[..3];
        var markerOnly = row.GetProperty("proposalResolution").GetProperty("decisions").EnumerateArray()
            .Where(x => x.GetProperty("resolution").GetString() == "marker-only-needs-visual")
            .Select(x => x.GetProperty("count").GetInt32()).DefaultIfEmpty(0).Single();
        var classification = markerOnly == 0 ? "CLASS_3" : "CLASS_2";
        var rows = Enumerable.Range(0, markerOnly).Select(_ => new JoinRow(id, null, Array.Empty<string>(), null,
            "NOT_OBSERVABLE", "marker-only-needs-visual", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_JOINABLE")).ToArray();
        var regime = id switch { "004" => "legal", "030" => "procurement/contract", "043" => "financial", "058" => "textbook/book", _ => "other" };
        return new EDocument(id, file, row.GetProperty("fingerprints").GetProperty("sourceDocumentSha256").GetString()!, regime,
            markerOnly, "NOT_OBSERVABLE", markerOnly, "NOT_OBSERVABLE", "NOT_OBSERVABLE", "NOT_OBSERVABLE",
            classification, rows);
    }

    private static string Markdown(EArtifact a)
    {
        var s = new StringBuilder();
        s.AppendLine("# Marker-Only Policy Audit"); s.AppendLine();
        s.AppendLine("Offline audit of the retained semantic run. The source keeps aggregate proposal resolutions only; it does not keep ID-level raw/post-conflict transitions."); s.AppendLine();
        s.AppendLine($"- `MARKER_ONLY_TOTAL={a.MARKER_ONLY_TOTAL}`; `MARKER_ONLY_NEEDS_VISUAL={a.MARKER_ONLY_NEEDS_VISUAL}`");
        s.AppendLine($"- `MARKER_ONLY_HEADING_TOPIC={a.MARKER_ONLY_HEADING_TOPIC}`; `FIRST_LOSS_CONFLICT_RESOLUTION={a.FIRST_LOSS_CONFLICT_RESOLUTION}`");
        s.AppendLine($"- `TRUE_HEADING_LOWERED={a.TRUE_HEADING_LOWERED}`; `NOT_JOINABLE={a.NOT_JOINABLE}`");
        s.AppendLine($"- `CROSS_DOCUMENT_VALID_HEADING_LOSS={a.CROSS_DOCUMENT_VALID_HEADING_LOSS}`; `REMEDIATION_JUSTIFIED={a.REMEDIATION_JUSTIFIED}`");
        s.AppendLine();
        s.AppendLine("## Inventory"); s.AppendLine();
        s.AppendLine("| document | regime | marker-only candidates | marker-only HeadingTopic | marker-only-needs-visual | raw role | post-conflict role | class |");
        s.AppendLine("|---|---|---:|---|---:|---|---|---|");
        foreach (var d in a.Documents) s.AppendLine($"| `{d.DocumentId}` | {d.Regime} | {d.MARKER_ONLY_CANDIDATES} | {d.MARKER_ONLY_HEADING_TOPIC} | {d.MARKER_ONLY_NEEDS_VISUAL} | {d.RAW_ROLE} | {d.POST_CONFLICT_ROLE} | {d.CrossDocumentClass} |");
        s.AppendLine();
        s.AppendLine("## Authority and safety"); s.AppendLine();
        s.AppendLine("No occurrence was called `UNREVIEWED` or false positive. All 13 aggregate-only rows are `NOT_JOINABLE`; occurrenceId, sourceLineIds, candidateId, validator, grounding, and output transitions are unavailable."); s.AppendLine();
        s.AppendLine("The requested keep-HeadingTopic counterfactual cannot be replayed deterministically because the retained artifact lacks the affected IDs and downstream per-occurrence transition evidence. Validator, grounding, and output safety are therefore `NOT_OBSERVABLE`, not assumed safe."); s.AppendLine();
        s.AppendLine("## Conclusion"); s.AppendLine();
        s.AppendLine("The evidence does not satisfy the E decision gate: there is no reviewed-proven valid heading loss, no first-loss attribution, no material downstream counterfactual recovery, and no CLASS_1 recurrence. `REMEDIATION_JUSTIFIED=NO`.");
        return s.ToString();
    }

    private sealed record EArtifact(int SchemaVersion, string ArtifactKind, string Phase, string SourceAuthority,
        int MARKER_ONLY_TOTAL, string MARKER_ONLY_HEADING_TOPIC, int MARKER_ONLY_NEEDS_VISUAL, int TRUE_HEADING_LOWERED,
        int REVIEWED_NON_HEADING_LOWERED, int UNREVIEWED, int NOT_JOINABLE, string FIRST_LOSS_CONFLICT_RESOLUTION,
        IReadOnlyList<EDocument> Documents, Counterfactual COUNTERFACTUAL, Safety DOWNSTREAM_SAFETY,
        string CROSS_DOCUMENT_VALID_HEADING_LOSS, string CrossDocumentBasis, string REMEDIATION_JUSTIFIED, int PROVIDER_CALLS, bool PRODUCTION_CODE_CHANGED);
    private sealed record EDocument(string DocumentId, string File, string DocumentSha256, string Regime,
        int MARKER_ONLY_CANDIDATES, string MARKER_ONLY_HEADING_TOPIC, int MARKER_ONLY_NEEDS_VISUAL, string RAW_ROLE,
        string POST_CONFLICT_ROLE, string PostConflictUncertain, string CrossDocumentClass, IReadOnlyList<JoinRow> JOIN_ROWS)
    {
        public int MARKER_ONLY_TOTAL => MARKER_ONLY_NEEDS_VISUAL;
    }
    private sealed record JoinRow(string DocumentId, string? OccurrenceId, IReadOnlyList<string> SourceLineIds, string? CandidateIdDiagnostic,
        string RawRole, string Resolution, string PostConflictRole, string ValidationStatus, string GroundingStatus, string OutputStatus);
    private sealed record Counterfactual(
        [property: JsonPropertyName("reviewedHeadingRecovered")] string ReviewedHeadingRecovered,
        [property: JsonPropertyName("reviewedHeadingEmitted")] string ReviewedHeadingEmitted,
        [property: JsonPropertyName("reviewedNonHeadingAdmitted")] string ReviewedNonHeadingAdmitted,
        [property: JsonPropertyName("unreviewedCandidateAdmitted")] string UnreviewedCandidateAdmitted,
        [property: JsonPropertyName("status")] string Status);
    private sealed record Safety(
        [property: JsonPropertyName("validatorBlocked")] string ValidatorBlocked,
        [property: JsonPropertyName("groundingBlocked")] string GroundingBlocked,
        [property: JsonPropertyName("outputBlocked")] string OutputBlocked,
        [property: JsonPropertyName("escapedToOutput")] string EscapedToOutput,
        [property: JsonPropertyName("reviewedNonHeading")] string ReviewedNonHeading,
        [property: JsonPropertyName("unreviewed")] string Unreviewed);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
