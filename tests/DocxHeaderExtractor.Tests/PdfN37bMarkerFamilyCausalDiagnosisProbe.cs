using System.Text.Json;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfN37bMarkerFamilyCausalDiagnosisProbe
{
    [Fact]
    public void Frozen_report_is_diagnosis_only_and_preserves_marker_family_boundary()
    {
        var path = FindRepoFile("eval/benchmark-n3/n3.4/reports/004-n3.7b-role-only-causal-diagnosis.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal("n3_7b_role_only_causal_diagnosis", root.GetProperty("artifactKind").GetString());
        Assert.False(root.GetProperty("usesModel").GetBoolean());
        Assert.False(root.GetProperty("productionChangeMade").GetBoolean());
        Assert.False(root.GetProperty("providerCallsMade").GetBoolean());
        Assert.Equal("ROLE_ERROR_PROVEN_REMEDIATION_NOT_JUSTIFIED",
            root.GetProperty("conclusion").GetProperty("finalStatus").GetString());

        var counts = root.GetProperty("004Counts");
        Assert.Equal(92, counts.GetProperty("canonicalOutputs").GetInt32());
        Assert.Equal(78, counts.GetProperty("labelled").GetInt32());
        Assert.Equal(8, counts.GetProperty("arabic").GetInt32());
        Assert.Equal(6, counts.GetProperty("letter").GetInt32());
        Assert.Equal(14, counts.GetProperty("arabicOrLetter").GetInt32());
        Assert.Equal(14, counts.GetProperty("classificationCounts").GetProperty("UNMATCHED_NON_HEADING").GetInt32());

        var items = root.GetProperty("items");
        Assert.Equal(14, items.GetArrayLength());
        Assert.Contains(items.EnumerateArray(), item => item.GetProperty("sourceFactId").GetString() == "b208");
        Assert.Contains(items.EnumerateArray(), item => item.GetProperty("sourceFactId").GetString() == "b483");

        var audit = root.GetProperty("discriminatorAudit");
        Assert.Equal(6, audit.GetProperty("TP_ownerB").GetInt32());
        Assert.Equal(6, audit.GetProperty("TP_ownerA").GetInt32());
        Assert.True(audit.GetProperty("availableBeforeRoleDecision").GetBoolean());
        Assert.Equal("not_safe_to_promote_from_004_alone", audit.GetProperty("decision").GetString());

        var controls = root.GetProperty("crossDocumentControls");
        Assert.Contains(controls.EnumerateArray(), item => item.GetProperty("documentId").GetString() == "057" &&
            item.GetProperty("status").GetString() == "control_refutes_generic_marker_family_rejection_but_not_a_joined_counterfactual");
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
